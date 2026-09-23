using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
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
        public static ExitInfo LookupExit(out string error)
        {
            error = null;
            string firstError = null;

            ExitInfo info = FromIpWhoIs(out error);
            if (info != null) return info;
            firstError = error;

            info = FromIpInfo(out error);
            if (info != null) return info;

            error = firstError + "; " + error;
            return null;
        }

        static ExitInfo FromIpWhoIs(out string error)
        {
            error = null;
            try
            {
                Dictionary<string, object> root = GetJson("https://ipwho.is/", 15000);
                if (root == null) { error = "unexpected response"; return null; }
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
                Dictionary<string, object> root = GetJson("https://ipinfo.io/json", 15000);
                if (root == null) { error = "unexpected response"; return null; }

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
        public static ResolverInfo LookupResolver(out string error)
        {
            error = null;
            try
            {
                Dictionary<string, object> root = GetJson("https://edns.ip-api.com/json", 15000);
                Dictionary<string, object> dns = root == null ? null : Obj(root, "dns");
                if (dns == null) { error = "unexpected response"; return null; }

                ResolverInfo r = new ResolverInfo();
                r.Ip = Str(dns, "ip");
                r.Description = Str(dns, "geo");
                int dash = r.Description.IndexOf(" - ", StringComparison.Ordinal);
                r.Country = dash > 0 ? r.Description.Substring(0, dash).Trim() : r.Description;
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

            // ---- the part this app cannot reach ------------------------------------------
            list.Add(new CheckResult(CheckStatus.Info,
                Lang.T("اثرانگشت مرورگر", "Browser fingerprint"),
                Lang.T("خارج از دسترس این برنامه", "outside this application's reach"),
                Lang.T("WebRTC، canvas، فهرست فونت‌ها، زبان مرورگر و وضوح صفحه از داخل خود مرورگر خوانده می‌شوند و " +
                       "هیچ تونلی تغییرشان نمی‌دهد. برای آن‌ها باید تنظیمات مرورگر را عوض کنید یا از Tor Browser " +
                       "استفاده کنید. نتیجه را در browserleaks.com ببینید.",
                       "WebRTC, canvas, the font list, the browser's language and the screen size are read inside the " +
                       "browser itself and no tunnel changes them. Those need browser settings or Tor Browser. " +
                       "browserleaks.com shows what they give away.")));

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
                       "اختلافی که با کشور IP بخواند عملاً کشور واقعی را لو می‌دهد. کلید «تطبیق منطقه زمانی» " +
                       "پایین همین صفحه هنگام اتصال آن را یکی می‌کند.",
                       "This is the strongest signal left: the browser hands the system clock's zone to any site that " +
                       "asks, and an offset that disagrees with the address effectively names the real country. " +
                       "The switch below matches them while the tunnel is up."));
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
                if (resolver.Country.IndexOf(exit.Country, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    exit.Country.IndexOf(resolver.Country, StringComparison.OrdinalIgnoreCase) >= 0)
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

        // ------------------------------------------------------------------ plumbing

        static Dictionary<string, object> GetJson(string url, int timeoutMs)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Proxy = null;                 // no system proxy: ride the tunnel, not a stale setting
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.UserAgent = "VMTun/" + Integration.Version;
            req.Accept = "application/json";

            using (WebResponse resp = req.GetResponse())
            using (StreamReader sr = new StreamReader(resp.GetResponseStream()))
            {
                JavaScriptSerializer js = new JavaScriptSerializer();
                js.MaxJsonLength = 2 * 1024 * 1024;
                return js.DeserializeObject(sr.ReadToEnd()) as Dictionary<string, object>;
            }
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
