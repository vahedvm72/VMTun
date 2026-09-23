using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace VMTun
{
    /// <summary>
    /// A minimal SOCKS5 client. Used to test the upstream proxy for real before the whole
    /// machine is pointed at it, which is the difference between "it says connected" and
    /// knowing whether anything will actually work.
    /// </summary>
    static class Socks5
    {
        /// <summary>Opens a TCP connection to target through the SOCKS5 proxy.</summary>
        public static TcpClient ConnectTcp(string proxyHost, int proxyPort, string targetHost, int targetPort, int timeoutMs)
        {
            TcpClient tcp = new TcpClient();
            try
            {
                IAsyncResult ar = tcp.BeginConnect(proxyHost, proxyPort, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) throw new IOException("proxy connect timed out");
                tcp.EndConnect(ar);
                tcp.ReceiveTimeout = timeoutMs;
                tcp.SendTimeout = timeoutMs;

                NetworkStream s = tcp.GetStream();
                Greet(s);

                // CONNECT, domain name form, so the proxy does the resolving.
                byte[] host = Encoding.ASCII.GetBytes(targetHost);
                byte[] req = new byte[7 + host.Length];
                req[0] = 0x05; req[1] = 0x01; req[2] = 0x00; req[3] = 0x03;
                req[4] = (byte)host.Length;
                Buffer.BlockCopy(host, 0, req, 5, host.Length);
                req[5 + host.Length] = (byte)(targetPort >> 8);
                req[6 + host.Length] = (byte)(targetPort & 0xFF);
                s.Write(req, 0, req.Length);

                byte[] head = ReadExact(s, 4);
                if (head[0] != 0x05) throw new IOException("bad SOCKS reply");
                if (head[1] != 0x00) throw new IOException("SOCKS refused the connection (code " + head[1] + ")");
                SkipAddress(s, head[3]);
                return tcp;
            }
            catch
            {
                try { tcp.Close(); }
                catch { }
                throw;
            }
        }

        static void Greet(NetworkStream s)
        {
            s.Write(new byte[] { 0x05, 0x01, 0x00 }, 0, 3);
            byte[] hello = ReadExact(s, 2);
            if (hello[0] != 0x05) throw new IOException("not a SOCKS5 proxy");
            if (hello[1] == 0xFF) throw new IOException("the proxy requires authentication");
            if (hello[1] != 0x00) throw new IOException("the proxy asked for an unsupported auth method");
        }

        static void SkipAddress(NetworkStream s, byte atyp)
        {
            if (atyp == 0x01) ReadExact(s, 4 + 2);
            else if (atyp == 0x04) ReadExact(s, 16 + 2);
            else if (atyp == 0x03)
            {
                byte[] len = ReadExact(s, 1);
                ReadExact(s, len[0] + 2);
            }
            else throw new IOException("unknown address type in SOCKS reply");
        }

        static byte[] ReadExact(NetworkStream s, int count)
        {
            byte[] buf = new byte[count];
            int got = 0;
            while (got < count)
            {
                int n = s.Read(buf, got, count - got);
                if (n <= 0) throw new IOException("the proxy closed the connection");
                got += n;
            }
            return buf;
        }

        /// <summary>
        /// Asks the proxy for a UDP relay and sends one real DNS query through it.
        /// Many VLESS/VMess-over-WebSocket servers cannot carry UDP at all; knowing that up
        /// front is what lets the tunnel pick a DNS transport that will actually answer.
        /// </summary>
        public static bool UdpWorks(string proxyHost, int proxyPort, string dnsServer, int timeoutMs, out string detail)
        {
            detail = "";
            TcpClient control = null;
            UdpClient udp = null;
            try
            {
                control = new TcpClient();
                IAsyncResult ar = control.BeginConnect(proxyHost, proxyPort, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) { detail = "proxy connect timed out"; return false; }
                control.EndConnect(ar);
                control.ReceiveTimeout = timeoutMs;
                control.SendTimeout = timeoutMs;

                NetworkStream s = control.GetStream();
                Greet(s);

                // UDP ASSOCIATE with an unspecified client address.
                s.Write(new byte[] { 0x05, 0x03, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, 0, 10);
                byte[] head = ReadExact(s, 4);
                if (head[1] != 0x00) { detail = "the proxy refused UDP ASSOCIATE (code " + head[1] + ")"; return false; }
                if (head[3] != 0x01) { detail = "the proxy returned a non-IPv4 relay address"; return false; }
                byte[] addr = ReadExact(s, 4);
                byte[] port = ReadExact(s, 2);
                IPAddress relayIp = new IPAddress(addr);
                int relayPort = (port[0] << 8) | port[1];
                // A relay bound to 0.0.0.0 means "same host as the proxy".
                if (relayIp.Equals(IPAddress.Any)) relayIp = IPAddress.Parse(proxyHost);

                byte[] query = BuildDnsQuery("cloudflare.com");
                IPAddress target = IPAddress.Parse(dnsServer);
                byte[] targetBytes = target.GetAddressBytes();
                if (targetBytes.Length != 4) { detail = "the DNS server is not IPv4"; return false; }

                byte[] packet = new byte[10 + query.Length];
                packet[0] = 0; packet[1] = 0;           // reserved
                packet[2] = 0;                          // no fragmentation
                packet[3] = 0x01;                       // IPv4 destination
                Buffer.BlockCopy(targetBytes, 0, packet, 4, 4);
                packet[8] = 0; packet[9] = 53;
                Buffer.BlockCopy(query, 0, packet, 10, query.Length);

                udp = new UdpClient();
                udp.Client.ReceiveTimeout = timeoutMs;
                udp.Connect(new IPEndPoint(relayIp, relayPort));
                udp.Send(packet, packet.Length);

                IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                byte[] reply = udp.Receive(ref from);
                if (reply.Length > 10)
                {
                    detail = "the proxy relayed UDP (" + reply.Length + " bytes back)";
                    return true;
                }
                detail = "the UDP reply was empty";
                return false;
            }
            catch (Exception ex)
            {
                detail = ex.Message;
                return false;
            }
            finally
            {
                if (udp != null) { try { udp.Close(); } catch { } }
                if (control != null) { try { control.Close(); } catch { } }
            }
        }

        static byte[] BuildDnsQuery(string name)
        {
            List<byte> q = new List<byte>();
            q.AddRange(new byte[] { 0x12, 0x34 });          // transaction id
            q.AddRange(new byte[] { 0x01, 0x00 });          // standard query, recursion desired
            q.AddRange(new byte[] { 0x00, 0x01 });          // one question
            q.AddRange(new byte[] { 0x00, 0x00 });          // no answers
            q.AddRange(new byte[] { 0x00, 0x00 });          // no authority
            q.AddRange(new byte[] { 0x00, 0x00 });          // no additional
            foreach (string label in name.Split('.'))
            {
                byte[] l = Encoding.ASCII.GetBytes(label);
                q.Add((byte)l.Length);
                q.AddRange(l);
            }
            q.Add(0x00);
            q.AddRange(new byte[] { 0x00, 0x01 });          // type A
            q.AddRange(new byte[] { 0x00, 0x01 });          // class IN
            return q.ToArray();
        }

        /// <summary>Fetches the exit IP over HTTPS through the proxy. Null when it does not work.</summary>
        public static string ExternalIpThroughProxy(string proxyHost, int proxyPort, int timeoutMs, out string error)
        {
            error = null;
            TcpClient tcp = null;
            try
            {
                const string Host = "api.ipify.org";
                tcp = ConnectTcp(proxyHost, proxyPort, Host, 443, timeoutMs);
                using (SslStream ssl = new SslStream(tcp.GetStream(), false,
                    delegate { return true; }))   // the proxy path is what is under test, not the CA chain
                {
                    ssl.AuthenticateAsClient(Host, null, SslProtocols.Tls12, false);
                    ssl.ReadTimeout = timeoutMs;
                    ssl.WriteTimeout = timeoutMs;
                    byte[] req = Encoding.ASCII.GetBytes(
                        "GET / HTTP/1.1\r\nHost: " + Host + "\r\nUser-Agent: VMTun\r\nConnection: close\r\n\r\n");
                    ssl.Write(req, 0, req.Length);
                    ssl.Flush();

                    StringBuilder sb = new StringBuilder();
                    byte[] buf = new byte[1024];
                    int n;
                    while ((n = ssl.Read(buf, 0, buf.Length)) > 0)
                    {
                        sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                        if (sb.Length > 8192) break;
                    }
                    string text = sb.ToString();
                    int sep = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (sep < 0) { error = "malformed HTTP response"; return null; }
                    string body = text.Substring(sep + 4).Trim();
                    // Strip a chunked-encoding size line if the server used one.
                    string[] parts = body.Split('\r', '\n');
                    foreach (string p in parts)
                    {
                        string candidate = p.Trim();
                        IPAddress parsed;
                        if (IPAddress.TryParse(candidate, out parsed)) return candidate;
                    }
                    error = "no address in the response";
                    return null;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
            finally { if (tcp != null) { try { tcp.Close(); } catch { } } }
        }
    }

    /// <summary>Everything worth knowing before and after the tunnel comes up.</summary>
    static class Preflight
    {
        /// <summary>Checks run before connecting. Order matters: they read top to bottom in the UI.</summary>
        public static List<CheckResult> Run(Settings s, out bool fatal, out bool udpWorks)
        {
            List<CheckResult> list = new List<CheckResult>();
            fatal = false;
            udpWorks = false;

            // ---- tools -------------------------------------------------------------
            if (File.Exists(AppPaths.SingBox) && File.Exists(AppPaths.Wintun))
            {
                string sout, serr;
                ProcUtil.Run(AppPaths.SingBox, "version", 8000, out sout, out serr);
                System.Text.RegularExpressions.Match m =
                    System.Text.RegularExpressions.Regex.Match(sout + serr, @"version\s+v?([\d.]+)");
                list.Add(new CheckResult(CheckStatus.Ok,
                    Lang.T("هسته تونل", "Tunnel core"),
                    "sing-box " + (m.Success ? m.Groups[1].Value : "?") + " + wintun"));
            }
            else
            {
                fatal = true;
                list.Add(new CheckResult(CheckStatus.Fail,
                    Lang.T("هسته تونل", "Tunnel core"),
                    Lang.T("sing-box.exe یا wintun.dll در پوشه tools نیست",
                           "sing-box.exe or wintun.dll is missing from the tools folder"),
                    Lang.T("این دو فایل را از نصب v2rayN کپی کنید یا build.ps1 را دوباره اجرا کنید.",
                           "Copy both files from a v2rayN installation, or re-run build.ps1.")));
            }

            // ---- proxy core process -------------------------------------------------
            List<ProxyCandidate> found = ProxyDetector.Discover();
            if (found.Count > 0)
            {
                StringBuilder names = new StringBuilder();
                foreach (ProxyCandidate c in found)
                {
                    if (names.Length > 0) names.Append("، ");
                    names.Append(c.ProcessName).Append(":").Append(c.Port);
                }
                list.Add(new CheckResult(CheckStatus.Ok,
                    Lang.T("هسته پروکسی", "Proxy core"), names.ToString()));
            }
            else
            {
                list.Add(new CheckResult(CheckStatus.Warn,
                    Lang.T("هسته پروکسی", "Proxy core"),
                    Lang.T("هیچ هسته شناخته‌شده‌ای در حال اجرا نیست",
                           "no known proxy core is running"),
                    Lang.T("v2rayN را باز کنید و به یک سرور وصل شوید.",
                           "Open v2rayN and connect to a server.")));
            }

            // ---- the port answers ----------------------------------------------------
            string where = s.ProxyHost + ":" + s.ProxyPort;
            string kind = ProxyDetector.Probe(s.ProxyHost, s.ProxyPort);
            if (kind == "unknown")
            {
                fatal = true;
                list.Add(new CheckResult(CheckStatus.Fail,
                    Lang.T("پورت پروکسی", "Proxy port"),
                    where + Lang.T(" پاسخ نمی‌دهد", " is not answering"),
                    Lang.T("در v2rayN پورت ورودی SOCKS را بررسی کنید، یا دکمه شناسایی خودکار را بزنید.",
                           "Check the SOCKS inbound port in v2rayN, or use the auto-detect button.")));
                return list;
            }
            list.Add(new CheckResult(CheckStatus.Ok,
                Lang.T("پورت پروکسی", "Proxy port"), where + "  (" + kind + ")"));

            // ---- does the proxy actually reach the internet? --------------------------
            // This is the check that matters: a tunnel pointed at a dead proxy still comes up
            // and still says "connected", which tells the user nothing.
            if (kind == "socks")
            {
                string ipError;
                string ip = Socks5.ExternalIpThroughProxy(s.ProxyHost, s.ProxyPort, 12000, out ipError);
                if (ip != null)
                {
                    list.Add(new CheckResult(CheckStatus.Ok,
                        Lang.T("اینترنت از مسیر پروکسی", "Internet through the proxy"),
                        Lang.T("IP خروجی: ", "exit IP: ") + ip));
                }
                else
                {
                    fatal = true;
                    list.Add(new CheckResult(CheckStatus.Fail,
                        Lang.T("اینترنت از مسیر پروکسی", "Internet through the proxy"),
                        ipError,
                        Lang.T("خود پروکسی به اینترنت نمی‌رسد. سرور v2rayN را عوض کنید؛ تونل هم درست نخواهد شد.",
                               "The proxy itself cannot reach the internet. Change the server in v2rayN — the tunnel cannot fix this.")));
                    return list;
                }

                // ---- UDP relay ---------------------------------------------------------
                string udpDetail;
                udpWorks = Socks5.UdpWorks(s.ProxyHost, s.ProxyPort, "1.1.1.1", 5000, out udpDetail);
                if (udpWorks)
                {
                    list.Add(new CheckResult(CheckStatus.Ok,
                        Lang.T("عبور UDP از سرور", "UDP through the server"),
                        Lang.T("پشتیبانی می‌شود — QUIC و DNS روی UDP کار می‌کنند",
                               "supported — QUIC and UDP DNS will work")));
                }
                else
                {
                    list.Add(new CheckResult(CheckStatus.Warn,
                        Lang.T("عبور UDP از سرور", "UDP through the server"),
                        Lang.T("پشتیبانی نمی‌شود (", "not supported (") + udpDetail + ")",
                        Lang.T("طبیعی است؛ بیشتر سرورهای VLESS/VMess روی WebSocket، UDP را رد نمی‌کنند. " +
                               "VMTun به همین دلیل DNS را روی TCP می‌فرستد و QUIC را می‌بندد، پس مشکلی پیش نمی‌آید.",
                               "This is common: most VLESS/VMess-over-WebSocket servers cannot relay UDP. " +
                               "VMTun therefore sends DNS over TCP and blocks QUIC, so it still works.")));
                }
            }

            // ---- competing tunnels ------------------------------------------------------
            List<string> rivals = OtherTunnelAdapters();
            if (rivals.Count > 0)
            {
                list.Add(new CheckResult(CheckStatus.Warn,
                    Lang.T("تونل دیگر فعال است", "Another tunnel is active"),
                    string.Join("، ", rivals.ToArray()),
                    Lang.T("این آداپتورها سر مسیر پیش‌فرض با VMTun رقابت می‌کنند و بعضی‌شان " +
                           "(مثل WireGuard و AmneziaVPN) کیل‌سوئیچ داخلی دارند که ترافیک VMTun را می‌بندد. " +
                           "قبل از اتصال قطعشان کنید.",
                           "These compete with VMTun for the default route, and some of them " +
                           "(WireGuard, AmneziaVPN) carry their own kill switch that blocks VMTun's traffic. " +
                           "Disconnect them before connecting.")));
            }

            // ---- IPv6 that could go around the tunnel ------------------------------------
            // With IPv6 switched off the adapter carries no IPv6 address, so IPv6 is not routed
            // into the tunnel at all. That is deliberate (claiming it and refusing it makes
            // applications retry in a tight loop), but it does mean a real IPv6 uplink would
            // bypass the proxy, which is worth saying out loud.
            if (!s.EnableIpv6)
            {
                List<string> v6 = GlobalIpv6Adapters();
                if (v6.Count > 0)
                {
                    list.Add(new CheckResult(CheckStatus.Warn,
                        Lang.T("اینترنت IPv6 فعال است", "The connection has IPv6"),
                        string.Join("، ", v6.ToArray()),
                        Lang.T("چون IPv6 در تونل خاموش است، این ترافیک از کنار تونل رد می‌شود. " +
                               "یا کیل‌سوئیچ را روشن کنید، یا IPv6 را در تنظیمات فعال کنید، " +
                               "یا IPv6 را روی کارت شبکه ویندوز غیرفعال کنید.",
                               "With IPv6 off in the tunnel this traffic goes around it. Either turn " +
                               "the kill switch on, or enable IPv6 in Settings, or disable IPv6 on the " +
                               "Windows adapter.")));
                }
            }

            // ---- leftovers ----------------------------------------------------------------
            if (FirewallGuard.IsActive())
            {
                list.Add(new CheckResult(CheckStatus.Warn,
                    Lang.T("کیل‌سوئیچ جامانده", "Leftover kill switch"),
                    Lang.T("از یک اجرای قبلی باقی مانده است", "left over from an earlier run"),
                    Lang.T("دکمه «بازیابی شبکه» در تب ابزارها آن را پاک می‌کند.",
                           "The \"Repair network\" button on the Tools tab clears it.")));
            }

            return list;
        }

        /// <summary>
        /// Adapters holding a routable IPv6 address, ignoring link-local and unique-local ones
        /// (which never reach the internet) and our own tunnel.
        /// </summary>
        public static List<string> GlobalIpv6Adapters()
        {
            List<string> found = new List<string>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (ni.Name == ConfigBuilder.InterfaceName) continue;

                    foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        IPAddress a = ua.Address;
                        if (a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) continue;
                        if (a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6Teredo) continue;
                        byte first = a.GetAddressBytes()[0];
                        if ((first & 0xFE) == 0xFC) continue;   // fc00::/7, unique local
                        if (!found.Contains(ni.Name)) found.Add(ni.Name);
                        break;
                    }
                }
            }
            catch (Exception ex) { Log.Error("IPv6 scan failed", ex); }
            return found;
        }

        /// <summary>Active VPN-ish adapters other than our own.</summary>
        public static List<string> OtherTunnelAdapters()
        {
            List<string> found = new List<string>();
            string[] markers = { "wireguard", "tap-windows", "openvpn", "wintun", "tun", "tap", "amnezia", "proton", "nord", "express" };
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.Name == ConfigBuilder.InterfaceName) continue;

                    // Our own adapter, identified by address rather than name.
                    bool isOurs = false;
                    foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                        if (ua.Address.Equals(TunAdapter.ExpectedIp)) isOurs = true;
                    if (isOurs) continue;

                    string haystack = (ni.Name + " " + ni.Description).ToLowerInvariant();
                    foreach (string marker in markers)
                    {
                        if (haystack.IndexOf(marker, StringComparison.Ordinal) >= 0)
                        {
                            found.Add(ni.Name + " (" + ni.Description + ")");
                            break;
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Error("Adapter scan failed", ex); }
            return found;
        }

        /// <summary>
        /// Run once the adapter is up: proves that a normal, unproxied request from this machine
        /// now leaves through the tunnel. Without this the app can only claim success.
        /// </summary>
        public static List<CheckResult> Verify(out bool healthy, out string exitIp)
        {
            List<CheckResult> list = new List<CheckResult>();
            healthy = false;
            exitIp = null;

            // ---- DNS ------------------------------------------------------------------
            bool dnsOk = false;
            try
            {
                DateTime t0 = DateTime.UtcNow;
                IPHostEntry he = Dns.GetHostEntry("api.ipify.org");
                int ms = (int)(DateTime.UtcNow - t0).TotalMilliseconds;
                dnsOk = he.AddressList.Length > 0;
                list.Add(new CheckResult(dnsOk ? CheckStatus.Ok : CheckStatus.Fail,
                    Lang.T("ترجمه نام (DNS)", "Name resolution (DNS)"),
                    dnsOk ? (he.AddressList[0] + "  — " + ms + "ms")
                          : Lang.T("پاسخی برنگشت", "no answer")));
            }
            catch (Exception ex)
            {
                list.Add(new CheckResult(CheckStatus.Fail,
                    Lang.T("ترجمه نام (DNS)", "Name resolution (DNS)"), ex.Message,
                    Lang.T("در تنظیمات، «روش DNS» را روی DoH یا TCP بگذارید.",
                           "Set the DNS transport to DoH or TCP in Settings.")));
            }

            // ---- a real request through the tunnel -------------------------------------
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create("https://api.ipify.org");
                req.Proxy = null;           // deliberately no proxy: this must travel via the tunnel
                req.Timeout = 15000;
                req.UserAgent = "VMTun";
                DateTime t0 = DateTime.UtcNow;
                using (WebResponse resp = req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream()))
                {
                    exitIp = sr.ReadToEnd().Trim();
                }
                int ms = (int)(DateTime.UtcNow - t0).TotalMilliseconds;
                healthy = !string.IsNullOrEmpty(exitIp);
                list.Add(new CheckResult(healthy ? CheckStatus.Ok : CheckStatus.Fail,
                    Lang.T("عبور ترافیک از تونل", "Traffic through the tunnel"),
                    healthy ? (Lang.T("IP خروجی: ", "exit IP: ") + exitIp + "  — " + ms + "ms")
                            : Lang.T("پاسخ خالی بود", "empty response")));
            }
            catch (Exception ex)
            {
                list.Add(new CheckResult(CheckStatus.Fail,
                    Lang.T("عبور ترافیک از تونل", "Traffic through the tunnel"),
                    dnsOk ? ex.Message : Lang.T("چون DNS کار نکرد، این هم شکست خورد", "failed because DNS failed"),
                    Lang.T("لاگ را ببینید؛ اگر تونل دیگری (WireGuard/Amnezia) فعال است قطعش کنید.",
                           "Check the log; if another tunnel (WireGuard/Amnezia) is up, disconnect it.")));
            }

            return list;
        }
    }
}
