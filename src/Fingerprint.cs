using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace VMTun
{
    /// <summary>What the outside world can see about the exit address.</summary>
    class ExitInfo
    {
        public string Ip = "";
        public string Country = "";
        public string CountryCode = "";
        public string City = "";
        public string Org = "";
        public string TimeZoneIana = "";
        public int UtcOffsetSeconds;
        public bool HasOffset;
        public string ReverseName = "";     // PTR record, empty when there is none
        // Every STUN server's answer, because they do not have to agree: some destinations
        // can leave outside the tunnel while others do not.
        public List<StunReply> UdpReplies = new List<StunReply>();

        public string Where
        {
            get
            {
                string s = City;
                if (!string.IsNullOrEmpty(Country))
                    s = (s.Length > 0 ? s + ", " : "") + Country;
                return s;
            }
        }

        public string OffsetText
        {
            get
            {
                if (!HasOffset) return "";
                int m = UtcOffsetSeconds / 60;
                string sign = m < 0 ? "-" : "+";
                m = Math.Abs(m);
                return "UTC" + sign + (m / 60).ToString("00", CultureInfo.InvariantCulture) +
                       ":" + (m % 60).ToString("00", CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>Which resolver actually reached the authoritative name server.</summary>
    class ResolverInfo
    {
        public string Ip = "";
        public string Description = "";     // e.g. "Armenia - Cloudflare, Inc."
        public string Country = "";         // the leading part of Description
        public string CountryCode = "";     // looked up from Ip, because the names disagree
    }

    /// <summary>
    /// Looks for the ways a machine still identifies itself after the traffic is tunnelled.
    ///
    /// A tunnel moves the packets; it does not move the computer. The address a site sees comes
    /// from the exit server, but the clock's zone, the region Windows was set up with, the
    /// resolver that answered the lookup and the name attached to the exit address all come from
    /// elsewhere, and a mismatch between them is exactly what a service checks when it decides
    /// that an address does not belong to the person using it.
    ///
    /// Everything here is read-only. Nothing is reported anywhere; the requests go to public
    /// address-lookup services and travel through the tunnel like the rest of the traffic.
    /// </summary>
    static class Fingerprint
    {
        // ------------------------------------------------------------------ lookups

        /// <summary>
        /// Asks a public service what address the connection appears to come from. Two providers
        /// are tried, because free endpoints rate-limit and a diagnostics page that fails on a
        /// 429 is worse than useless — it reads as a leak.
        /// </summary>
        public static ExitInfo LookupExit(Settings s, out string error)
        {
            bool viaProxy;
            return LookupExit(s, out error, out viaProxy);
        }

        /// <summary>
        /// Asks a public service what address the connection appears to come from, through the
        /// proxy when there is one.
        ///
        /// Going through the proxy rather than following the operating system's routing is the
        /// whole point. The question is "which server does my traffic come out of", so the
        /// answer has to be the proxy's exit server. An ordinary request leaves through whatever
        /// adapter currently owns the default route, so on a machine with a second VPN up it
        /// reports that VPN's country no matter which server the proxy is pointed at.
        ///
        /// `viaProxy` says which path produced the answer, so the caller can label a direct
        /// reading as describing the bare connection rather than the tunnel.
        /// </summary>
        public static ExitInfo LookupExit(Settings s, out string error, out bool viaProxy)
        {
            error = null;
            viaProxy = false;

            string proxyError = null;
            if (s != null && s.ProxyType == "socks")
            {
                ExitInfo viaSocks = FromIpWhoIs(s, out proxyError);
                if (viaSocks != null) { viaProxy = true; return viaSocks; }
            }

            // No proxy, or it did not answer: fall back to the ordinary path so the page still
            // says something useful about the connection as it stands.
            ExitInfo info = FromIpWhoIs(null, out error);
            if (info != null) return info;
            string firstError = error;

            info = FromIpInfo(out error);
            if (info != null) return info;

            error = Join(proxyError, firstError, error);
            return null;
        }

        static string Join(params string[] parts)
        {
            List<string> kept = new List<string>();
            foreach (string part in parts)
                if (!string.IsNullOrEmpty(part) && !kept.Contains(part)) kept.Add(part);
            return string.Join("; ", kept.ToArray());
        }

        static ExitInfo FromIpWhoIs(Settings viaProxy, out string error)
        {
            error = null;
            try
            {
                Dictionary<string, object> root = GetJson(viaProxy, "ipwho.is", "/", 15000, out error);
                if (root == null) return null;
                if (!Flag(root, "success")) { error = Str(root, "message"); return null; }

                ExitInfo info = new ExitInfo();
                info.Ip = Str(root, "ip");
                info.Country = Str(root, "country");
                info.CountryCode = Str(root, "country_code");
                info.City = Str(root, "city");

                Dictionary<string, object> conn = Obj(root, "connection");
                if (conn != null)
                {
                    info.Org = Str(conn, "isp");
                    if (info.Org.Length == 0) info.Org = Str(conn, "org");
                }

                Dictionary<string, object> tz = Obj(root, "timezone");
                if (tz != null)
                {
                    info.TimeZoneIana = Str(tz, "id");
                    object off;
                    if (tz.TryGetValue("offset", out off) && off != null)
                    {
                        try { info.UtcOffsetSeconds = Convert.ToInt32(off); info.HasOffset = true; }
                        catch { }
                    }
                }

                if (info.Ip.Length == 0) { error = "no address in the response"; return null; }
                return info;
            }
            catch (Exception ex) { error = ex.Message; return null; }
        }

        static ExitInfo FromIpInfo(out string error)
        {
            error = null;
            try
            {
                Dictionary<string, object> root = GetJson(null, "ipinfo.io", "/json", 15000, out error);
                if (root == null) return null;

                ExitInfo info = new ExitInfo();
                info.Ip = Str(root, "ip");
                info.CountryCode = Str(root, "country");
                info.Country = info.CountryCode;
                info.City = Str(root, "city");
                info.Org = Str(root, "org");
                info.TimeZoneIana = Str(root, "timezone");

                if (info.Ip.Length == 0) { error = "no address in the response"; return null; }
                return info;
            }
            catch (Exception ex) { error = ex.Message; return null; }
        }

        /// <summary>
        /// Names the resolver that reached the authoritative server for a lookup. This is the
        /// only honest DNS-leak test: reading the resolver configured on the adapter says what
        /// Windows intends, not what actually happened to the query.
        /// </summary>
        public static ResolverInfo LookupResolver(Settings s, out string error)
        {
            error = null;
            try
            {
                // Through the proxy as well: the question is which resolver the exit server's
                // lookups come from, not which one this machine happens to use right now.
                Settings via = (s != null && s.ProxyType == "socks") ? s : null;
                Dictionary<string, object> root = GetJson(via, "edns.ip-api.com", "/json", 15000, out error);
                if (root == null && via != null)
                    root = GetJson(null, "edns.ip-api.com", "/json", 15000, out error);
                Dictionary<string, object> dns = root == null ? null : Obj(root, "dns");
                if (dns == null) { error = "unexpected response"; return null; }

                ResolverInfo r = new ResolverInfo();
                r.Ip = Str(dns, "ip");
                r.Description = Str(dns, "geo");
                int dash = r.Description.IndexOf(" - ", StringComparison.Ordinal);
                r.Country = dash > 0 ? r.Description.Substring(0, dash).Trim() : r.Description;

                // The two services spell countries differently — one says "Turkey" where the
                // other says "Türkiye" — and comparing those strings reported a leak on a
                // resolver sitting in exactly the right country. Codes do not have that problem,
                // so the resolver's own address is looked up for one.
                if (r.Ip.Length > 0)
                {
                    string ignored;
                    Dictionary<string, object> geo = GetJson(via, "ipwho.is", "/" + r.Ip, 10000, out ignored);
                    if (geo != null) r.CountryCode = Str(geo, "country_code");
                }
                return r;
            }
            catch (Exception ex) { error = ex.Message; return null; }
        }

        /// <summary>The PTR record for an address, or an empty string when it has none.</summary>
        public static string ReverseName(string ip)
        {
            try
            {
                IPHostEntry entry = Dns.GetHostEntry(ip);
                return entry == null || entry.HostName == null ? "" : entry.HostName;
            }
            catch { return ""; }
        }

        /// <summary>The two-letter country Windows was set up as the user's home.</summary>
        public static string WindowsHomeRegion()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k =
                       Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\International\Geo"))
                {
                    if (k != null)
                    {
                        object name = k.GetValue("Name");
                        if (name != null && name.ToString().Length > 0) return name.ToString();
                    }
                }
            }
            catch { }
            try { return RegionInfo.CurrentRegion.TwoLetterISORegionName; }
            catch { return ""; }
        }

        // ------------------------------------------------------------------ the audit

        /// <summary>
        /// Builds the rows for the Privacy page. Network lookups are already done by the caller
        /// so this stays instant and can be re-rendered without hitting the network again.
        /// </summary>
        public static List<CheckResult> Audit(ExitInfo exit, ResolverInfo resolver, string lookupError)
        {
            List<CheckResult> list = new List<CheckResult>();

            // ---- where the traffic comes out ---------------------------------------------
            if (exit == null)
            {
                list.Add(new CheckResult(CheckStatus.Fail,
                    Lang.T("آدرس خروجی", "Exit address"),
                    string.IsNullOrEmpty(lookupError) ? Lang.T("در دسترس نیست", "not available") : lookupError,
                    Lang.T("بدون آدرس خروجی هیچ‌کدام از مقایسه‌های زیر ممکن نیست. اتصال را برقرار کنید و دوباره بررسی کنید.",
                           "None of the comparisons below are possible without it. Connect and check again.")));
                return list;
            }

            string detail = exit.Ip;
            if (exit.Where.Length > 0) detail += "  —  " + exit.Where;
            if (exit.Org.Length > 0) detail += "  —  " + exit.Org;
            list.Add(new CheckResult(CheckStatus.Info,
                Lang.T("آدرس خروجی", "Exit address"), detail));

            // ---- clock -------------------------------------------------------------------
            list.Add(TimeZoneCheck(exit));

            // ---- the region Windows was set up with --------------------------------------
            string home = WindowsHomeRegion();
            if (home.Length > 0 && exit.CountryCode.Length > 0)
            {
                bool same = string.Equals(home, exit.CountryCode, StringComparison.OrdinalIgnoreCase);
                list.Add(new CheckResult(same ? CheckStatus.Ok : CheckStatus.Info,
                    Lang.T("کشور ویندوز", "Windows home region"),
                    Lang.T("ویندوز: ", "Windows: ") + home +
                    Lang.T("  /  خروجی: ", "  /  exit: ") + exit.CountryCode,
                    same ? "" :
                    Lang.T("این مقدار را سایت‌ها مستقیم نمی‌بینند، ولی فروشگاه ویندوز و بعضی برنامه‌های سیستمی از آن " +
                           "استفاده می‌کنند و ممکن است با کشور IP نخواند. در «زمان و زبان ← زبان و منطقه» قابل تغییر است.",
                           "Sites cannot read this directly, but the Microsoft Store and some system apps use it and " +
                           "may disagree with the country of the address. Change it under Time & language → Language & region.")));
            }

            // ---- the name attached to the exit address -----------------------------------
            list.Add(ReverseDnsCheck(exit));

            // ---- who actually resolved the name ------------------------------------------
            list.Add(ResolverCheck(exit, resolver));

            // ---- traffic that never entered the tunnel -----------------------------------
            List<string> v6 = Preflight.GlobalIpv6Adapters();
            if (v6.Count > 0)
            {
                list.Add(new CheckResult(CheckStatus.Warn,
                    Lang.T("آدرس IPv6 عمومی", "Global IPv6 address"),
                    string.Join("، ", v6.ToArray()),
                    Lang.T("سایتی که از IPv6 استفاده کند آدرس واقعی شما را می‌بیند، چون تونل فقط IPv4 را می‌برد. " +
                           "کیل‌سوئیچ را روشن کنید یا IPv6 را روی کارت شبکه خاموش کنید.",
                           "A site reached over IPv6 sees your real address, because the tunnel only carries IPv4. " +
                           "Turn the kill switch on, or disable IPv6 on the Windows adapter.")));
            }
            else
            {
                list.Add(new CheckResult(CheckStatus.Ok,
                    Lang.T("آدرس IPv6 عمومی", "Global IPv6 address"),
                    Lang.T("هیچ کارت شبکه‌ای آدرس IPv6 عمومی ندارد", "no adapter holds a routable IPv6 address")));
            }

            // ---- the address a browser would publish over WebRTC --------------------------
            list.Add(WebRtcCheck(exit));

            // ---- the part this app cannot reach ------------------------------------------
            list.Add(new CheckResult(CheckStatus.Info,
                Lang.T("اثرانگشت مرورگر", "Browser fingerprint"),
                Lang.T("خارج از دسترس این برنامه", "outside this application's reach"),
                Lang.T("canvas، فهرست فونت‌ها، زبان مرورگر و وضوح صفحه از داخل خود مرورگر خوانده می‌شوند و هیچ " +
                       "تونلی تغییرشان نمی‌دهد؛ برای آن‌ها باید تنظیمات مرورگر را عوض کنید یا از Tor Browser " +
                       "استفاده کنید. (آدرسی که WebRTC اعلام می‌کند جداگانه بالاتر بررسی شده، چون آن یکی " +
                       "مسئلهٔ شبکه است نه مرورگر.) نتیجه را در browserscan.net ببینید.",
                       "Canvas, the font list, the browser's language and the screen size are read inside the " +
                       "browser itself and no tunnel changes them; those need browser settings or Tor Browser. " +
                       "(The address WebRTC publishes is checked separately above, because that one is a " +
                       "network problem rather than a browser one.) browserscan.net shows the rest.")));

            return list;
        }

        static CheckResult TimeZoneCheck(ExitInfo exit)
        {
            string title = Lang.T("ساعت و منطقه زمانی", "Clock and time zone");
            string mine = TimeZoneSync.CurrentIana;
            string mineOffset = TimeZoneSync.CurrentOffset;

            if (string.IsNullOrEmpty(exit.TimeZoneIana))
                return new CheckResult(CheckStatus.Info, title,
                    Lang.T("ویندوز: ", "Windows: ") + mine + " (UTC" + mineOffset + ")",
                    Lang.T("منطقه زمانی سرور خروجی مشخص نشد، پس مقایسه‌ای ممکن نیست.",
                           "The exit server's time zone is unknown, so there is nothing to compare against."));

            string detail = Lang.T("ویندوز: ", "Windows: ") + mine + " (UTC" + mineOffset + ")" +
                            Lang.T("  /  خروجی: ", "  /  exit: ") + exit.TimeZoneIana +
                            (exit.OffsetText.Length > 0 ? " (" + exit.OffsetText + ")" : "");

            bool sameZone = string.Equals(mine, exit.TimeZoneIana, StringComparison.OrdinalIgnoreCase);
            bool sameOffset = sameZone;
            if (!sameOffset && exit.HasOffset)
            {
                try
                {
                    sameOffset = (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow).TotalSeconds
                                 == exit.UtcOffsetSeconds;
                }
                catch { }
            }

            if (sameZone)
                return new CheckResult(CheckStatus.Ok, title, detail);

            if (sameOffset)
                return new CheckResult(CheckStatus.Ok, title, detail,
                    Lang.T("نام منطقه فرق دارد ولی اختلاف ساعت یکی است، و چیزی که سایت‌ها اندازه می‌گیرند همین است.",
                           "The names differ but the offset matches, and the offset is what sites measure."));

            return new CheckResult(CheckStatus.Warn, title, detail,
                Lang.T("قوی‌ترین نشانه‌ای است که می‌ماند: مرورگر ساعت سیستم را به هر سایتی که بخواهد می‌دهد، و " +
                       "اختلافی که با کشور IP بخواند عملاً کشور واقعی را لو می‌دهد. دکمهٔ «اتصال پیشرفته» " +
                       "هنگام اتصال آن را با کشور سرور یکی می‌کند و بعد برمی‌گرداند.",
                       "This is the strongest signal left: the browser hands the system clock's zone to any site that " +
                       "asks, and an offset that disagrees with the address effectively names the real country. " +
                       "Pro Connect matches it to the server's country while you are connected, and puts it back afterwards."));
        }

        /// <summary>
        /// Compares the addresses UDP leaves from with the one the proxy exits from.
        ///
        /// A browser learns its public address by asking STUN servers over UDP, and publishes
        /// what they say to any page that opens a WebRTC connection. When the tunnel carries TCP
        /// but not all UDP, those answers stop agreeing with the exit address — and they need
        /// not even agree with each other, because traffic to one destination can leave through
        /// the tunnel while traffic to another does not.
        ///
        /// So every server is asked and the worst answer decides. Checking one, or stopping at
        /// the first that replies, reports an all-clear over a live leak.
        /// </summary>
        static CheckResult WebRtcCheck(ExitInfo exit)
        {
            string title = Lang.T("آدرس WebRTC (UDP)", "WebRTC address (UDP)");
            List<string> seen = Stun.DistinctAddresses(exit.UdpReplies);

            if (seen.Count == 0)
                return new CheckResult(CheckStatus.Ok, title,
                    Lang.T("هیچ آدرسی بیرون نرفت", "no address got out"),
                    Lang.T("هیچ سرور STUN جوابی نداد، یعنی UDP از این دستگاه بیرون نمی‌رود و مرورگر هم " +
                           "چیزی برای اعلام ندارد. امن‌ترین حالت است.",
                           "No STUN server answered, so UDP is not leaving this machine and a browser has " +
                           "no address to publish. This is the safe outcome."));

            // Anything that is not the exit address is an address a site would see instead of it.
            List<string> stray = new List<string>();
            foreach (string address in seen)
                if (!string.Equals(address, exit.Ip, StringComparison.Ordinal)) stray.Add(address);

            if (stray.Count == 0)
                return new CheckResult(CheckStatus.Ok, title,
                    seen[0] + Lang.T("  —  هر ", "  —  all ") +
                    Stun.ServersReporting(exit.UdpReplies, seen[0]).Count +
                    Lang.T(" سرور همین را دید", " servers agree"),
                    Lang.T("با آدرس خروجی یکی است، پس WebRTC چیز تازه‌ای لو نمی‌دهد.",
                           "The same as the exit address, so WebRTC gives nothing away."));

            StringBuilder detail = new StringBuilder();
            foreach (string address in stray)
            {
                if (detail.Length > 0) detail.Append("   ");
                detail.Append(address).Append(" (")
                      .Append(string.Join(", ", Stun.ServersReporting(exit.UdpReplies, address).ToArray()))
                      .Append(")");
            }
            detail.Append(Lang.T("   /   خروجی: ", "   /   exit: ")).Append(exit.Ip);

            return new CheckResult(CheckStatus.Fail, title, detail.ToString(),
                Lang.T("بخشی از UDP بیرون از تونل می‌رود و سایتی که اتصال WebRTC باز کند همین آدرس را " +
                       "می‌بیند. چون همهٔ سرورها یک جواب نداده‌اند، نشت کلی نیست بلکه به مقصد بستگی دارد، " +
                       "و علت تقریباً همیشه قوانین مسیریابی خود v2rayN است: گزینه‌هایی مثل «دور زدن چین» " +
                       "بعضی مقصدها را مستقیم می‌فرستند، و آن بسته‌ها با IP واقعی شما بیرون می‌روند. در " +
                       "v2rayN مسیریابی را روی Global بگذارید. تا آن موقع، خاموش کردن WebRTC در مرورگر " +
                       "این مسیر را کامل می‌بندد.",
                       "Some UDP is leaving outside the tunnel, and a site that opens a WebRTC connection " +
                       "sees that address. Since the servers did not all agree, this is not a total leak " +
                       "but a per-destination one, and the cause is almost always the routing rules inside " +
                       "v2rayN itself: options such as \"bypass mainland China\" send some destinations " +
                       "direct, and those packets leave with your real address. Set routing to Global in " +
                       "v2rayN. Until then, turning WebRTC off in the browser closes this path entirely."));
        }

        static CheckResult ReverseDnsCheck(ExitInfo exit)
        {
            string title = Lang.T("نام معکوس آدرس خروجی", "Reverse DNS of the exit address");
            if (exit.ReverseName.Length == 0)
                return new CheckResult(CheckStatus.Ok, title,
                    Lang.T("آدرس خروجی نام معکوس ندارد", "the exit address has no PTR record"));

            string ptr = exit.ReverseName;

            // A PTR carrying the operator's own name is a straight line from the address back to a
            // person, and it is set on the server, so nothing on this machine can hide it.
            string token = PersonalTokenIn(ptr);
            if (token != null)
                return new CheckResult(CheckStatus.Fail, title, ptr,
                    Lang.T("این نام شامل «" + token + "» است. هر سایتی که آدرس خروجی را معکوس جست‌وجو کند آن را می‌بیند؛ " +
                           "این یک پیوند مستقیم به هویت شماست و هیچ تونلی جلویش را نمی‌گیرد. نام معکوس روی سرور " +
                           "تنظیم می‌شود، پس باید از پنل سرویس‌دهندهٔ سرور عوضش کنید.",
                           "This name contains \"" + token + "\". Any site that reverse-looks-up the exit address sees it; " +
                           "it links the address straight back to you and no tunnel can mask it. A PTR record is set on " +
                           "the server, so it has to be changed in your hosting provider's panel."));

            return new CheckResult(CheckStatus.Info, title, ptr,
                Lang.T("نام معکوس را سرویس‌دهندهٔ سرور تعیین می‌کند. اگر چیزی شخصی در آن باشد، از پنل همان سرویس‌دهنده عوض می‌شود.",
                       "The hosting provider sets this. If it contains anything personal, change it in their panel."));
        }

        /// <summary>
        /// Whether a host name carries something that points back at this machine's owner. Only
        /// tokens of a useful length count, so a three-letter account name cannot match half the
        /// domains on the internet.
        /// </summary>
        static string PersonalTokenIn(string host)
        {
            string lower = host.ToLowerInvariant();
            List<string> candidates = new List<string>();
            try { candidates.Add(Environment.UserName); }
            catch { }
            try { candidates.Add(Environment.MachineName); }
            catch { }
            // Only this machine's own names. Matching anything compiled in would be checking
            // a stranger's identity on every other install.

            foreach (string c in candidates)
            {
                if (string.IsNullOrEmpty(c) || c.Length < 4) continue;
                if (lower.IndexOf(c.ToLowerInvariant(), StringComparison.Ordinal) >= 0) return c;
            }
            return null;
        }

        static CheckResult ResolverCheck(ExitInfo exit, ResolverInfo resolver)
        {
            string title = Lang.T("نشت DNS", "DNS leak");
            if (resolver == null)
                return new CheckResult(CheckStatus.Info, title,
                    Lang.T("سرویس بررسی در دسترس نبود", "the checking service was unreachable"));

            string detail = resolver.Description;
            if (resolver.Ip.Length > 0) detail += "  (" + resolver.Ip + ")";

            // The comparison that matters is against the country the address claims. A resolver
            // sitting in the country the user is actually in means the queries never entered the
            // tunnel, whatever the adapter's DNS setting says.
            if (exit != null && exit.Country.Length > 0 && resolver.Country.Length > 0)
            {
                if (SameCountry(exit, resolver))
                    return new CheckResult(CheckStatus.Ok, title, detail,
                        Lang.T("پرس‌وجوها از همان کشوری بیرون می‌روند که آدرس خروجی در آن است — یعنی از تونل رد می‌شوند.",
                               "The queries leave from the same country as the exit address — they are going through the tunnel."));

                return new CheckResult(CheckStatus.Warn, title, detail,
                    Lang.T("سرویس‌دهندهٔ DNS در کشور آدرس خروجی («" + exit.Country + "») نیست. اگر در کشور خودتان است، " +
                           "یعنی نام‌ها بیرون از تونل جست‌وجو می‌شوند و سایت‌ها می‌توانند از همین موقعیت واقعی را حدس بزنند. " +
                           "در تنظیمات، DNS را روی DoH بگذارید.",
                           "The resolver is not in the exit address's country (\"" + exit.Country + "\"). If it is in your own " +
                           "country, names are being looked up outside the tunnel and sites can infer your real location " +
                           "from it. Set the DNS mode to DoH in Settings."));
            }

            return new CheckResult(CheckStatus.Info, title, detail);
        }

        /// <summary>
        /// Whether the resolver and the exit address are in the same country.
        ///
        /// Country codes when both are known, because the two services that supply these names
        /// do not agree on them: "Turkey" against "Türkiye" was enough to report a leak on a
        /// resolver that was in precisely the right place. Names are only a fallback, compared
        /// with the accents stripped so the same pair matches.
        /// </summary>
        static bool SameCountry(ExitInfo exit, ResolverInfo resolver)
        {
            if (exit.CountryCode.Length == 2 && resolver.CountryCode.Length == 2)
                return string.Equals(exit.CountryCode, resolver.CountryCode,
                                     StringComparison.OrdinalIgnoreCase);

            string a = Fold(exit.Country), b = Fold(resolver.Country);
            if (a.Length == 0 || b.Length == 0) return false;
            return a.IndexOf(b, StringComparison.Ordinal) >= 0 ||
                   b.IndexOf(a, StringComparison.Ordinal) >= 0;
        }

        /// <summary>Lower case with the accents removed, so "Türkiye" and "Turkiye" agree.</summary>
        static string Fold(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string decomposed = s.Normalize(NormalizationForm.FormD);
            StringBuilder sb = new StringBuilder(decomposed.Length);
            foreach (char c in decomposed)
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    sb.Append(char.ToLowerInvariant(c));
            return sb.ToString().Trim();
        }

        // ------------------------------------------------------------------ plumbing

        /// <summary>
        /// Fetches JSON, through the SOCKS proxy when `viaProxy` is given and over the ordinary
        /// route when it is null.
        /// </summary>
        static Dictionary<string, object> GetJson(Settings viaProxy, string host, string path,
                                                  int timeoutMs, out string error)
        {
            error = null;
            string body;

            if (viaProxy != null)
            {
                body = Socks5.HttpsGet(viaProxy.ProxyHost, viaProxy.ProxyPort, host, path,
                                       timeoutMs, out error);
                if (body == null) return null;
            }
            else
            {
                try
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create("https://" + host + path);
                    req.Proxy = null;         // no system proxy: ride the tunnel, not a stale setting
                    req.Timeout = timeoutMs;
                    req.ReadWriteTimeout = timeoutMs;
                    req.UserAgent = "VMTun/" + Integration.Version;
                    req.Accept = "application/json";

                    using (WebResponse resp = req.GetResponse())
                    using (StreamReader sr = new StreamReader(resp.GetResponseStream()))
                        body = sr.ReadToEnd();
                }
                catch (Exception ex) { error = ex.Message; return null; }
            }

            try
            {
                JavaScriptSerializer js = new JavaScriptSerializer();
                js.MaxJsonLength = 2 * 1024 * 1024;
                Dictionary<string, object> root = js.DeserializeObject(body) as Dictionary<string, object>;
                if (root == null) error = "unexpected response";
                return root;
            }
            catch (Exception ex) { error = ex.Message; return null; }
        }

        static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null) return v.ToString();
            return "";
        }

        static bool Flag(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v is bool) return (bool)v;
            return false;
        }

        static Dictionary<string, object> Obj(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v)) return v as Dictionary<string, object>;
            return null;
        }
    }
}
