using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace VMTun
{
    /// <summary>Everything the app reads or writes on disk, resolved once at startup.</summary>
    static class AppPaths
    {
        public static readonly string ExeDir =
            Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);

        /// <summary>sing-box.exe + wintun.dll live here, next to the exe, so the app stays portable.</summary>
        public static readonly string ToolsDir = Path.Combine(ExeDir, "tools");

        /// <summary>
        /// Generated config, logs and the rule-set cache. Kept beside the executable so the whole
        /// thing stays portable and the log is trivial to find; falls back to LocalAppData when the
        /// app lives somewhere unwritable such as Program Files.
        /// </summary>
        /// Resolved lazily, not as a static field: touching any member of this class runs the
        /// type initialiser, and the probe below creates a folder. The installer reads ExeDir
        /// and must not leave a data folder wherever it was downloaded to.
        static string _dataDir;

        public static string DataDir
        {
            get
            {
                if (_dataDir == null) _dataDir = ResolveDataDir();
                return _dataDir;
            }
        }

        static string ResolveDataDir()
        {
            string beside = Path.Combine(ExeDir, "data");
            try
            {
                if (!Directory.Exists(beside)) Directory.CreateDirectory(beside);
                string probe = Path.Combine(beside, ".writable");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return beside;
            }
            catch { }
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VMTun");
        }

        public static string SingBox { get { return Path.Combine(ToolsDir, "sing-box.exe"); } }
        public static string Wintun { get { return Path.Combine(ToolsDir, "wintun.dll"); } }
        public static string ConfigFile { get { return Path.Combine(DataDir, "config.json"); } }
        public static string SettingsFile { get { return Path.Combine(DataDir, "settings.ini"); } }
        public static string LogFile { get { return Path.Combine(DataDir, "vmtun.log"); } }

        /// <summary>Written while the firewall kill switch is active so a crash can still be undone.</summary>
        public static string GuardStateFile { get { return Path.Combine(DataDir, "guard.state"); } }

        public static void EnsureDirs()
        {
            if (!Directory.Exists(DataDir)) Directory.CreateDirectory(DataDir);
            if (!Directory.Exists(ToolsDir)) Directory.CreateDirectory(ToolsDir);
        }
    }

    public enum CheckStatus { Ok, Warn, Fail, Info, Running }

    /// <summary>One line in the diagnostics list.</summary>
    class CheckResult
    {
        public CheckStatus Status = CheckStatus.Info;
        public string Title = "";
        public string Detail = "";
        public string Hint = "";

        public CheckResult() { }
        public CheckResult(CheckStatus s, string title, string detail)
        {
            Status = s; Title = title; Detail = detail;
        }
        public CheckResult(CheckStatus s, string title, string detail, string hint)
        {
            Status = s; Title = title; Detail = detail; Hint = hint;
        }
    }

    public enum LogLevel { Info, Warn, Error, Core }

    /// <summary>Append-only log file plus a live feed for the UI.</summary>
    static class Log
    {
        static readonly object Gate = new object();
        public static event Action<LogLevel, string> Line;

        /// <summary>
        /// The installer shares this code but must not create a data folder wherever it happens
        /// to have been downloaded to, so it turns the file log off.
        /// </summary>
        public static bool ToFile = true;

        public static void Info(string m) { Write(LogLevel.Info, m); }
        public static void Warn(string m) { Write(LogLevel.Warn, m); }
        public static void Error(string m) { Write(LogLevel.Error, m); }
        public static void Core(string m) { Write(LogLevel.Core, m); }

        public static void Error(string m, Exception ex)
        {
            Write(LogLevel.Error, m + " :: " + (ex == null ? "(null)" : ex.ToString()));
        }

        static void Write(LogLevel level, string message)
        {
            // UTC, because Pro Connect moves the local time zone while it is connected and
            // a log stamped in local time then runs backwards across exactly the entries
            // someone is reading to work out what happened.
            string stamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "Z";
            string text = stamp + " [" + level.ToString().ToUpperInvariant() + "] " + message;
            if (ToFile) lock (Gate)
            {
                try
                {
                    AppPaths.EnsureDirs();
                    // Keep the log from growing without bound across long sessions.
                    FileInfo fi = new FileInfo(AppPaths.LogFile);
                    if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                    {
                        string old = AppPaths.LogFile + ".1";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(AppPaths.LogFile, old);
                    }
                    File.AppendAllText(AppPaths.LogFile, text + Environment.NewLine, Encoding.UTF8);
                }
                catch { /* logging must never take the app down */ }
            }
            Action<LogLevel, string> h = Line;
            if (h != null)
            {
                try { h(level, message); }
                catch { }
            }
        }
    }

    public enum RoutingMode { Full, IranDirect }

    /// <summary>User settings, stored as a flat ini so no JSON parser is needed.</summary>
    class Settings
    {
        public string Lang = "en";        // Persian is a choice, not the default
        public string Theme = "auto";               // dark | light | auto (follows Windows)
        public string ProxyHost = "127.0.0.1";
        public int ProxyPort = 10808;
        public string ProxyType = "socks";          // socks | http
        public RoutingMode Routing = RoutingMode.Full;
        // Off by default: the tunnel alone already carries every process, and a kill switch that
        // outlives a crash is the one failure mode that leaves the machine without internet.
        public bool KillSwitch = false;
        public bool EnableIpv6 = false;
        public string Stack = "gvisor";             // gvisor | system | mixed
        public int Mtu = 9000;
        public string RemoteDns = "1.1.1.1";
        // DNS transport towards the proxy. UDP needs the upstream server to relay UDP, which many
        // VLESS/VMess-over-WebSocket servers do not, so a TCP-based transport is the default.
        public string DnsMode = "doh";              // doh | dot | tcp | udp
        // Rejecting QUIC pushes browsers back to TCP, which works even when the server has no UDP.
        public bool BlockQuic = true;
        // The core logs a line per connection at "info", which on a busy machine is hundreds
        // per second. Only turn it up when something needs diagnosing.
        public bool VerboseCoreLog = false;
        // Checked once per day, shortly after the tunnel proves itself, so the request can
        // ride the connection the app just established.
        public bool AutoUpdate = true;
        public string LastUpdateCheck = "";
        // "owner/name". Empty means the repository compiled into Integration, which is what
        // a normal install uses; a fork can point elsewhere without rebuilding.
        public string UpdateRepo = "";
        // Matching the clock to the exit country closes the loudest remaining giveaway, but it
        // moves every appointment and log timestamp on the machine with it, so it is asked for
        // rather than assumed.
        public bool MatchTimeZone = false;
        // Pro Connect. Consent is asked once and remembered; the rest are what a Pro connect
        // turns on for the duration of the session and puts back afterwards.
        public bool ProConsent = false;
        public string ProDns = "";
        public bool MatchRegion = false;
        public bool DisableAdapterIpv6 = false;
        public bool AutoConnect = false;
        public bool StartWithWindows = false;
        public bool MinimizeToTray = true;
        // Tray balloons for connect and disconnect. Nothing routine ever notifies, but
        // some people want the tray icon completely silent.
        public bool Notifications = true;
        public string ExtraDirectProcesses = "";    // comma separated exe names

        /// <summary>
        /// A field-for-field copy. Pro Connect runs with a derived set of settings and must not
        /// write its own choices over what the user configured.
        /// </summary>
        public Settings Clone()
        {
            return (Settings)MemberwiseClone();
        }

        public static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                if (!File.Exists(AppPaths.SettingsFile)) return s;
                foreach (string raw in File.ReadAllLines(AppPaths.SettingsFile))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    s.Apply(line.Substring(0, eq).Trim(), line.Substring(eq + 1).Trim());
                }
            }
            catch (Exception ex) { Log.Error("Could not read settings", ex); }
            return s;
        }

        void Apply(string key, string val)
        {
            switch (key)
            {
                case "Lang": Lang = (val == "en" ? "en" : "fa"); break;
                case "Theme": Theme = (val == "dark" || val == "light") ? val : "auto"; break;
                case "ProxyHost": if (val.Length > 0) ProxyHost = val; break;
                case "ProxyPort": ProxyPort = ParseInt(val, ProxyPort); break;
                case "ProxyType": ProxyType = (val == "http" ? "http" : "socks"); break;
                case "Routing": Routing = (val == "iran" ? RoutingMode.IranDirect : RoutingMode.Full); break;
                case "KillSwitch": KillSwitch = (val == "1"); break;
                case "EnableIpv6": EnableIpv6 = (val == "1"); break;
                case "Stack": Stack = (val == "system" || val == "mixed") ? val : "gvisor"; break;
                case "Mtu": Mtu = ParseInt(val, Mtu); break;
                case "RemoteDns": if (val.Length > 0) RemoteDns = val; break;
                case "DnsMode":
                    DnsMode = (val == "dot" || val == "tcp" || val == "udp") ? val : "doh";
                    break;
                case "BlockQuic": BlockQuic = (val == "1"); break;
                case "VerboseCoreLog": VerboseCoreLog = (val == "1"); break;
                case "AutoUpdate": AutoUpdate = (val == "1"); break;
                case "LastUpdateCheck": LastUpdateCheck = val; break;
                case "UpdateRepo": UpdateRepo = val; break;
                case "MatchTimeZone": MatchTimeZone = (val == "1"); break;
                case "ProConsent": ProConsent = (val == "1"); break;
                case "ProDns": ProDns = val; break;
                case "MatchRegion": MatchRegion = (val == "1"); break;
                case "DisableAdapterIpv6": DisableAdapterIpv6 = (val == "1"); break;
                case "AutoConnect": AutoConnect = (val == "1"); break;
                case "StartWithWindows": StartWithWindows = (val == "1"); break;
                case "MinimizeToTray": MinimizeToTray = (val == "1"); break;
                case "Notifications": Notifications = (val == "1"); break;
                case "ExtraDirectProcesses": ExtraDirectProcesses = val; break;
            }
        }

        static int ParseInt(string v, int fallback)
        {
            int n;
            if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
            return fallback;
        }

        public void Save()
        {
            try
            {
                AppPaths.EnsureDirs();
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# VMTun settings");
                sb.AppendLine("Lang=" + Lang);
                sb.AppendLine("Theme=" + Theme);
                sb.AppendLine("ProxyHost=" + ProxyHost);
                sb.AppendLine("ProxyPort=" + ProxyPort.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("ProxyType=" + ProxyType);
                sb.AppendLine("Routing=" + (Routing == RoutingMode.IranDirect ? "iran" : "full"));
                sb.AppendLine("KillSwitch=" + (KillSwitch ? "1" : "0"));
                sb.AppendLine("EnableIpv6=" + (EnableIpv6 ? "1" : "0"));
                sb.AppendLine("Stack=" + Stack);
                sb.AppendLine("Mtu=" + Mtu.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("RemoteDns=" + RemoteDns);
                sb.AppendLine("DnsMode=" + DnsMode);
                sb.AppendLine("BlockQuic=" + (BlockQuic ? "1" : "0"));
                sb.AppendLine("VerboseCoreLog=" + (VerboseCoreLog ? "1" : "0"));
                sb.AppendLine("AutoUpdate=" + (AutoUpdate ? "1" : "0"));
                sb.AppendLine("LastUpdateCheck=" + LastUpdateCheck);
                sb.AppendLine("UpdateRepo=" + UpdateRepo);
                sb.AppendLine("MatchTimeZone=" + (MatchTimeZone ? "1" : "0"));
                sb.AppendLine("ProConsent=" + (ProConsent ? "1" : "0"));
                sb.AppendLine("ProDns=" + ProDns);
                sb.AppendLine("MatchRegion=" + (MatchRegion ? "1" : "0"));
                sb.AppendLine("DisableAdapterIpv6=" + (DisableAdapterIpv6 ? "1" : "0"));
                sb.AppendLine("AutoConnect=" + (AutoConnect ? "1" : "0"));
                sb.AppendLine("StartWithWindows=" + (StartWithWindows ? "1" : "0"));
                sb.AppendLine("MinimizeToTray=" + (MinimizeToTray ? "1" : "0"));
                sb.AppendLine("Notifications=" + (Notifications ? "1" : "0"));
                sb.AppendLine("ExtraDirectProcesses=" + ExtraDirectProcesses);
                File.WriteAllText(AppPaths.SettingsFile, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex) { Log.Error("Could not save settings", ex); }
        }
    }

    /// <summary>Two-language UI text. T() picks by the current language.</summary>
    static class Lang
    {
        // False by default, to agree with Settings.Lang. Anything that runs before the
        // settings file is read — the startup notices in Program — uses this, and a
        // Persian default there put a Persian button under an English sentence.
        public static bool Fa = false;
        public static string T(string fa, string en) { return Fa ? fa : en; }
    }

    static class ProcUtil
    {
        /// <summary>Runs a console program with no window and returns its exit code (-1 on timeout).</summary>
        public static int Run(string exe, string args, int timeoutMs, out string stdout, out string stderr)
        {
            stdout = ""; stderr = "";
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            using (Process p = Process.Start(psi))
            {
                StringBuilder o = new StringBuilder();
                StringBuilder e = new StringBuilder();
                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs ev) { if (ev.Data != null) o.AppendLine(ev.Data); };
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs ev) { if (ev.Data != null) e.AppendLine(ev.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); }
                    catch { }
                    try { p.WaitForExit(2000); }
                    catch { }
                    stdout = o.ToString(); stderr = e.ToString();
                    return -1;
                }

                // Bounded, never the parameterless overload. That one waits for the redirected
                // pipes to reach EOF as well, and a grandchild that inherited the handles keeps
                // them open forever — the process is long gone and the call never returns.
                try { p.WaitForExit(2000); }
                catch { }
                stdout = o.ToString(); stderr = e.ToString();
                return p.ExitCode;
            }
        }

        /// <summary>Runs a PowerShell snippet. Needed for the NetSecurity cmdlets, which netsh cannot replace.</summary>
        public static int PowerShell(string script, int timeoutMs, out string stdout, out string stderr)
        {
            // -EncodedCommand sidesteps every layer of quoting between C#, cmd and PowerShell.
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            return Run("powershell.exe",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded,
                timeoutMs, out stdout, out stderr);
        }

        /// <summary>
        /// PowerShell serialises errors as CLIXML on stderr when it is not attached to a console.
        /// This pulls the human-readable message back out so the log and the UI stay readable.
        /// </summary>
        public static string CleanPsError(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (text.IndexOf("CLIXML", StringComparison.Ordinal) < 0) return text.Trim();

            StringBuilder sb = new StringBuilder();
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(text, "<S S=\"Error\">(.*?)</S>"))
            {
                string line = m.Groups[1].Value
                    .Replace("_x000D_", "").Replace("_x000A_", " ")
                    .Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&")
                    .Trim();
                if (line.Length == 0 || line.StartsWith("+") || line.StartsWith("At line:")) continue;
                if (line.StartsWith("+ CategoryInfo") || line.StartsWith("+ FullyQualifiedErrorId")) continue;
                sb.Append(line).Append(' ');
            }
            string result = sb.ToString().Trim();
            return result.Length > 0 ? result : "(no details)";
        }

        public static List<Process> FindByName(params string[] names)
        {
            List<Process> found = new List<Process>();
            foreach (string n in names)
            {
                try { found.AddRange(Process.GetProcessesByName(n)); }
                catch { }
            }
            return found;
        }
    }
}
