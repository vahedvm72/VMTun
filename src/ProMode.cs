using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace VMTun
{
    /// <summary>
    /// The country Windows was set up as the user's home.
    ///
    /// No site reads this directly, but the Microsoft Store, Windows Update and a number of
    /// system apps do, and a machine claiming one country from an address in another is one
    /// more thing that does not add up. It is a per-user setting and needs no elevation.
    /// </summary>
    static class RegionSync
    {
        const uint GeoClassNation = 16;

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetUserGeoID(int geoId);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern int GetUserGeoID(uint geoClass);

        static string StateFile { get { return Path.Combine(AppPaths.DataDir, "region.state"); } }

        public static bool IsOverridden { get { return File.Exists(StateFile); } }

        /// <summary>The GeoID the user had before we touched anything, or 0.</summary>
        public static int OriginalGeoId
        {
            get
            {
                try
                {
                    if (!File.Exists(StateFile)) return 0;
                    int id;
                    if (int.TryParse(File.ReadAllText(StateFile).Trim(), NumberStyles.Integer,
                                     CultureInfo.InvariantCulture, out id)) return id;
                }
                catch { }
                return 0;
            }
        }

        public static int CurrentGeoId
        {
            get
            {
                try { return GetUserGeoID(GeoClassNation); }
                catch { return 0; }
            }
        }

        /// <summary>The two-letter code Windows reports, e.g. "US".</summary>
        public static string CurrentCode
        {
            get { return CodeOf(CurrentGeoId); }
        }

        public static string CodeOf(int geoId)
        {
            if (geoId <= 0) return "";
            try
            {
                foreach (CultureInfo ci in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
                {
                    try
                    {
                        RegionInfo r = new RegionInfo(ci.Name);
                        if (r.GeoId == geoId) return r.TwoLetterISORegionName;
                    }
                    catch { }
                }
            }
            catch { }
            return "";
        }

        /// <summary>The GeoID for a two-letter country code, or 0 when Windows does not know it.</summary>
        public static int GeoIdOf(string twoLetter)
        {
            if (string.IsNullOrEmpty(twoLetter) || twoLetter.Length != 2) return 0;
            try { return new RegionInfo(twoLetter.ToUpperInvariant()).GeoId; }
            catch { return 0; }
        }

        public static bool ApplyFor(string twoLetter, out string applied, out string error)
        {
            applied = null;
            error = null;

            int target = GeoIdOf(twoLetter);
            if (target == 0)
            {
                error = "Windows has no region for \"" + twoLetter + "\"";
                return false;
            }

            try
            {
                int current = CurrentGeoId;
                if (current == target) { applied = twoLetter.ToUpperInvariant(); return true; }

                if (!IsOverridden)
                {
                    AppPaths.EnsureDirs();
                    File.WriteAllText(StateFile, current.ToString(CultureInfo.InvariantCulture));
                }

                if (!SetUserGeoID(target))
                {
                    error = new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message;
                    if (current != 0) TryDelete();
                    return false;
                }

                applied = twoLetter.ToUpperInvariant();
                Log.Info("Home region set to " + applied + " (geo " + target + "); was " + current);
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        public static bool Restore(out string error)
        {
            error = null;
            int original = OriginalGeoId;
            if (original == 0) { TryDelete(); return true; }

            try
            {
                if (!SetUserGeoID(original))
                {
                    error = new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message;
                    Log.Error("Could not restore the home region: " + error);
                    return false;
                }
                TryDelete();
                Log.Info("Home region restored to geo " + original);
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        static void TryDelete()
        {
            try { if (File.Exists(StateFile)) File.Delete(StateFile); }
            catch { }
        }
    }

    /// <summary>
    /// Turns IPv6 off on the machine's real adapters for as long as the tunnel is up.
    ///
    /// The tunnel carries IPv4. Any adapter that still holds a routable IPv6 address is a way
    /// around it: a site reached over IPv6 sees the real address and never touches the tunnel
    /// at all. The kill switch blocks that traffic, but blocking produces a stalled connection
    /// and a retry storm, whereas unbinding the protocol makes Windows stop trying.
    ///
    /// Which adapters were changed is written to disk before anything is touched, so an
    /// unclean exit cannot leave the machine without IPv6.
    /// </summary>
    static class Ipv6Binding
    {
        static string StateFile { get { return Path.Combine(AppPaths.DataDir, "ipv6.state"); } }

        public static bool IsOverridden { get { return File.Exists(StateFile); } }

        public static List<string> ChangedAdapters()
        {
            List<string> list = new List<string>();
            try
            {
                if (!File.Exists(StateFile)) return list;
                foreach (string line in File.ReadAllLines(StateFile))
                {
                    string a = line.Trim();
                    if (a.Length > 0) list.Add(a);
                }
            }
            catch { }
            return list;
        }

        /// <summary>
        /// Unbinds IPv6 from every adapter that has it enabled, except our own tunnel. Returns
        /// the adapters it changed.
        /// </summary>
        public static List<string> Apply(out string error)
        {
            error = null;
            List<string> changed = new List<string>();
            try
            {
                string tun = TunAdapter.FindAlias();
                string exclude = tun == null ? "" : tun.Replace("'", "''");

                // Listed first and written to disk before anything changes, so the restore list
                // exists even if the machine loses power mid-way.
                string listCmd =
                    "Get-NetAdapterBinding -ComponentID ms_tcpip6 -ErrorAction SilentlyContinue | " +
                    "Where-Object { $_.Enabled -and $_.Name -ne '" + exclude + "' } | " +
                    "Select-Object -ExpandProperty Name";

                string stdout, stderr;
                ProcUtil.Run("powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -Command \"" + listCmd.Replace("\"", "\\\"") + "\"",
                    30000, out stdout, out stderr);

                foreach (string raw in stdout.Split('\r', '\n'))
                {
                    string name = raw.Trim();
                    if (name.Length > 0) changed.Add(name);
                }

                if (changed.Count == 0) return changed;

                AppPaths.EnsureDirs();
                File.WriteAllText(StateFile, string.Join(Environment.NewLine, changed.ToArray()),
                                  Encoding.UTF8);

                StringBuilder sb = new StringBuilder();
                foreach (string name in changed)
                {
                    sb.Append("Disable-NetAdapterBinding -Name '").Append(name.Replace("'", "''"))
                      .Append("' -ComponentID ms_tcpip6 -ErrorAction SilentlyContinue; ");
                }

                int code = ProcUtil.Run("powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -Command \"" + sb.ToString().Replace("\"", "\\\"") + "\"",
                    60000, out stdout, out stderr);
                if (code != 0) error = stderr.Trim();

                Log.Info("IPv6 unbound from: " + string.Join(", ", changed.ToArray()));
                return changed;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return changed;
            }
        }

        public static bool Restore(out string error)
        {
            error = null;
            List<string> adapters = ChangedAdapters();
            if (adapters.Count == 0) { TryDelete(); return true; }

            try
            {
                StringBuilder sb = new StringBuilder();
                foreach (string name in adapters)
                {
                    sb.Append("Enable-NetAdapterBinding -Name '").Append(name.Replace("'", "''"))
                      .Append("' -ComponentID ms_tcpip6 -ErrorAction SilentlyContinue; ");
                }

                string stdout, stderr;
                int code = ProcUtil.Run("powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -Command \"" + sb.ToString().Replace("\"", "\\\"") + "\"",
                    60000, out stdout, out stderr);

                if (code != 0)
                {
                    error = stderr.Trim();
                    Log.Error("Could not re-enable IPv6: " + error);
                    return false;                     // keep the state file so a later run retries
                }

                TryDelete();
                Log.Info("IPv6 restored on: " + string.Join(", ", adapters.ToArray()));
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        static void TryDelete()
        {
            try { if (File.Exists(StateFile)) File.Delete(StateFile); }
            catch { }
        }
    }

    /// <summary>
    /// Pro Connect: the settings an ordinary connect leaves to the user, turned on together.
    ///
    /// Every one of these is something the Privacy page would otherwise report as a finding and
    /// leave for the user to act on. They are grouped here because acting on them one at a time
    /// is how a machine ends up half-hardened, and because each has an undo that has to run even
    /// if the app never gets a clean shutdown.
    /// </summary>
    static class ProMode
    {
        /// <summary>The settings a Pro connect runs with, derived from the user's own.</summary>
        public static Settings Derive(Settings user)
        {
            Settings s = user.Clone();
            s.KillSwitch = true;          // nothing escapes if the core dies
            s.BlockQuic = true;           // browsers fall back to TCP, which the tunnel carries
            s.EnableIpv6 = false;         // the tunnel stays IPv4; the adapters lose IPv6 below
            s.DnsMode = "doh";            // lookups travel inside TLS, not as plain UDP
            s.MatchTimeZone = true;
            s.MatchRegion = true;
            s.DisableAdapterIpv6 = true;

            string dns = (user.ProDns == null ? "" : user.ProDns.Trim());
            if (dns.Length > 0) s.RemoteDns = dns;
            return s;
        }

        /// <summary>Everything Pro Connect changes, in the order the consent dialog lists it.</summary>
        public static string[] ChangeSummary(Settings user)
        {
            string dns = (user.ProDns == null ? "" : user.ProDns.Trim());
            return new string[]
            {
                Lang.T("کیل‌سوئیچ فایروال ویندوز", "Windows Firewall kill switch") + "\n" +
                Lang.T("سیاست خروجی ویندوز روی «مسدود» می‌رود و فقط تونل اجازهٔ عبور می‌گیرد. " +
                       "اگر هسته بمیرد، هیچ ترافیکی بیرون نمی‌رود.",
                       "The Windows outbound policy is set to block and only the tunnel is allowed " +
                       "through, so nothing escapes if the core dies."),

                Lang.T("خاموش کردن IPv6 روی کارت‌های شبکه", "IPv6 switched off on your network adapters") + "\n" +
                Lang.T("تونل IPv4 است. هر کارت شبکه‌ای که IPv6 عمومی داشته باشد راه دور زدن تونل است.",
                       "The tunnel carries IPv4. Any adapter holding a routable IPv6 address is a way around it."),

                Lang.T("تطبیق منطقه زمانی ویندوز", "Windows time zone matched to the exit country") + "\n" +
                Lang.T("مرورگر ساعت سیستم را به هر سایتی می‌دهد. ساعت تقویم و جلسات هم تا قطع اتصال با همین منطقه نوشته می‌شود.",
                       "The browser hands the system clock's zone to any site. Calendar entries and meetings " +
                       "follow this zone until you disconnect."),

                Lang.T("تطبیق کشور ویندوز", "Windows home region matched to the exit country") + "\n" +
                Lang.T("فروشگاه ویندوز و بعضی برنامه‌های سیستمی این را می‌خوانند.",
                       "The Microsoft Store and some system apps read this."),

                Lang.T("مسدود کردن QUIC", "QUIC blocked") + "\n" +
                Lang.T("مرورگرها به TCP برمی‌گردند، که تونل مطمئن آن را می‌برد.",
                       "Browsers fall back to TCP, which the tunnel carries reliably."),

                Lang.T("DNS روی DoH", "DNS over HTTPS") + "\n" +
                (dns.Length > 0
                    ? Lang.T("سرویس‌دهندهٔ شما: ", "Your resolver: ") + dns
                    : Lang.T("سرویس‌دهندهٔ پیش‌فرض: ", "Default resolver: ") + user.RemoteDns),
            };
        }

        /// <summary>True while any system-wide change from a Pro connect is still in effect.</summary>
        public static bool AnythingApplied
        {
            get
            {
                try
                {
                    return TimeZoneSync.IsOverridden || RegionSync.IsOverridden ||
                           Ipv6Binding.IsOverridden || FirewallGuard.IsActive();
                }
                catch { return false; }
            }
        }

        /// <summary>
        /// Puts every system-wide change back. Safe to call when nothing was applied, and safe
        /// to call twice. Each step is independent so one failure cannot strand the rest.
        /// </summary>
        public static string RestoreAll()
        {
            StringBuilder notes = new StringBuilder();
            string error;

            try
            {
                if (FirewallGuard.IsActive())
                {
                    FirewallGuard.Remove(out error);
                    notes.AppendLine("Firewall policy restored.");
                }
            }
            catch (Exception ex) { Log.Error("Firewall restore failed", ex); }

            try
            {
                if (Ipv6Binding.IsOverridden)
                {
                    List<string> a = Ipv6Binding.ChangedAdapters();
                    if (Ipv6Binding.Restore(out error))
                        notes.AppendLine("IPv6 re-enabled on " + string.Join(", ", a.ToArray()) + ".");
                    else
                        notes.AppendLine("Could not re-enable IPv6: " + error);
                }
            }
            catch (Exception ex) { Log.Error("IPv6 restore failed", ex); }

            try
            {
                if (TimeZoneSync.IsOverridden)
                {
                    string original = TimeZoneSync.OriginalId;
                    if (TimeZoneSync.Restore(out error))
                        notes.AppendLine("Time zone restored to " + original + ".");
                    else
                        notes.AppendLine("Could not restore the time zone: " + error);
                }
            }
            catch (Exception ex) { Log.Error("Time zone restore failed", ex); }

            try
            {
                if (RegionSync.IsOverridden)
                {
                    int original = RegionSync.OriginalGeoId;
                    if (RegionSync.Restore(out error))
                        notes.AppendLine("Home region restored to " + RegionSync.CodeOf(original) + ".");
                    else
                        notes.AppendLine("Could not restore the home region: " + error);
                }
            }
            catch (Exception ex) { Log.Error("Region restore failed", ex); }

            return notes.ToString();
        }
    }
}
