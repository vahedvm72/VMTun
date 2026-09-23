using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace VMTun
{
    public enum TunnelState { Disconnected, Connecting, Connected, Disconnecting, Faulted }

    /// <summary>
    /// Owns the lifetime of the tunnel: prepares the tools, writes the config, runs the
    /// sing-box core, waits for the adapter, arms the kill switch, and unwinds all of it again.
    /// </summary>
    class TunnelService
    {
        readonly object _gate = new object();
        Process _core;
        Timer _health;
        Settings _settings;
        string _tunAlias;
        bool _killSwitchArmed;
        bool _stopRequested;

        public TunnelState State { get; private set; }

        /// <summary>True only once a real request has been observed leaving through the tunnel.</summary>
        public bool Verified { get; private set; }

        /// <summary>Exit address seen during verification, when there is one.</summary>
        public string ExitIp { get; private set; }

        /// <summary>Whether the upstream proxy was found to relay UDP.</summary>
        public bool UpstreamUdpWorks { get; private set; }

        /// <summary>Raised on every state change; message is already localised.</summary>
        public event Action<TunnelState, string> StateChanged;

        /// <summary>Raised whenever a diagnostics pass produces results. Phase is a UI heading.</summary>
        public event Action<string, List<CheckResult>> ChecksUpdated;

        void PublishChecks(string phase, List<CheckResult> checks)
        {
            Action<string, List<CheckResult>> h = ChecksUpdated;
            if (h != null)
            {
                try { h(phase, checks); }
                catch (Exception ex) { Log.Error("Checks handler failed", ex); }
            }
        }

        public TunnelService()
        {
            State = TunnelState.Disconnected;
        }

        void SetState(TunnelState s, string message)
        {
            State = s;
            Log.Info("State -> " + s + (message == null ? "" : ": " + message));
            Action<TunnelState, string> h = StateChanged;
            if (h != null)
            {
                try { h(s, message); }
                catch (Exception ex) { Log.Error("State handler failed", ex); }
            }
        }

        // ------------------------------------------------------------------ tools

        static readonly string[] SingBoxSearch =
        {
            @"v2rayN\bin\sing_box\sing-box.exe",
            @"v2rayN\bin\sing-box\sing-box.exe"
        };

        static readonly string[] WintunSearch =
        {
            @"v2rayN\bin\xray\wintun.dll",
            @"v2rayN\bin\sing_box\wintun.dll",
            @"v2rayN\bin\mihomo\wintun.dll"
        };

        static IEnumerable<string> ProgramRoots()
        {
            yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        }

        /// <summary>Copies sing-box.exe and wintun.dll next to the app if they are not there yet.</summary>
        public bool EnsureTools(out string error)
        {
            error = null;
            AppPaths.EnsureDirs();
            try
            {
                if (!File.Exists(AppPaths.SingBox))
                {
                    string src = Locate(SingBoxSearch);
                    // Also accept a core that is running right now, wherever it lives.
                    if (src == null)
                    {
                        foreach (Process p in ProcUtil.FindByName("sing-box"))
                        {
                            try
                            {
                                string path = p.MainModule.FileName;
                                if (File.Exists(path)) { src = path; break; }
                            }
                            catch { }
                        }
                    }
                    if (src == null)
                    {
                        error = Lang.T(
                            "فایل sing-box.exe پیدا نشد. آن را در پوشه tools کنار برنامه قرار دهید.",
                            "sing-box.exe was not found. Place it in the tools folder next to the app.");
                        return false;
                    }
                    File.Copy(src, AppPaths.SingBox, true);
                    Log.Info("Copied core from " + src);
                }

                if (!File.Exists(AppPaths.Wintun))
                {
                    string src = Locate(WintunSearch);
                    if (src == null)
                    {
                        error = Lang.T(
                            "فایل wintun.dll پیدا نشد. آن را در پوشه tools کنار برنامه قرار دهید.",
                            "wintun.dll was not found. Place it in the tools folder next to the app.");
                        return false;
                    }
                    File.Copy(src, AppPaths.Wintun, true);
                    Log.Info("Copied wintun.dll from " + src);
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Log.Error("Preparing tools failed", ex);
                return false;
            }
            return true;
        }

        static string Locate(string[] relatives)
        {
            foreach (string root in ProgramRoots())
            {
                if (string.IsNullOrEmpty(root)) continue;
                foreach (string rel in relatives)
                {
                    try
                    {
                        string full = Path.Combine(root, rel);
                        if (File.Exists(full)) return full;
                    }
                    catch { }
                }
            }
            return null;
        }

        /// <summary>Reads the core version; the config schema needs sing-box 1.12 or newer.</summary>
        public bool CheckCoreVersion(out string version, out string error)
        {
            version = null; error = null;
            try
            {
                string sout, serr;
                ProcUtil.Run(AppPaths.SingBox, "version", 10000, out sout, out serr);
                Match m = Regex.Match(sout + serr, @"version\s+v?(\d+)\.(\d+)\.(\d+)");
                if (!m.Success)
                {
                    error = Lang.T("نسخه هسته خوانده نشد.", "Could not read the core version.");
                    return false;
                }
                version = m.Groups[1].Value + "." + m.Groups[2].Value + "." + m.Groups[3].Value;
                int major = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                int minor = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                if (major < 1 || (major == 1 && minor < 12))
                {
                    error = Lang.T(
                        "نسخه sing-box باید ۱.۱۲ یا بالاتر باشد (نسخه فعلی: " + version + ").",
                        "sing-box 1.12 or newer is required (found " + version + ").");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // ------------------------------------------------------------------ start

        public bool Start(Settings settings, out string error)
        {
            error = null;
            lock (_gate)
            {
                if (State == TunnelState.Connected || State == TunnelState.Connecting)
                {
                    error = Lang.T("تونل از قبل فعال است.", "The tunnel is already running.");
                    return false;
                }
                _settings = settings;
                _stopRequested = false;
                Verified = false;
                ExitIp = null;
            }

            SetState(TunnelState.Connecting, Lang.T("در حال آماده‌سازی…", "Preparing…"));

            if (!EnsureTools(out error)) { Fault(error); return false; }

            string version;
            if (!CheckCoreVersion(out version, out error)) { Fault(error); return false; }
            Log.Info("Core version " + version);

            // Before the core, not after. The window between the two is exactly when a browser
            // would open an IPv6 connection that never enters the tunnel.
            if (_settings.DisableAdapterIpv6) UnbindIpv6();

            // ---- the proxy has to be there before we redirect the whole machine at it ----
            if (!ProxyDetector.IsReachable(_settings.ProxyHost, _settings.ProxyPort, 1500))
            {
                List<ProxyCandidate> found = ProxyDetector.Discover();
                if (found.Count > 0)
                {
                    Log.Warn("Configured proxy " + _settings.ProxyHost + ":" + _settings.ProxyPort +
                             " is not answering; using detected " + found[0]);
                    _settings.ProxyHost = found[0].Host;
                    _settings.ProxyPort = found[0].Port;
                    _settings.ProxyType = (found[0].Kind == "http" ? "http" : "socks");
                    _settings.Save();
                }
            }

            // ---- prove the upstream works before pointing the machine at it --------------
            SetState(TunnelState.Connecting, Lang.T("در حال بررسی پروکسی…", "Checking the proxy…"));
            bool fatal, udpWorks;
            List<CheckResult> checks = Preflight.Run(_settings, out fatal, out udpWorks);
            UpstreamUdpWorks = udpWorks;
            PublishChecks(Lang.T("بررسی پیش از اتصال", "Before connecting"), checks);

            if (fatal)
            {
                string reason = Lang.T("بررسی پیش از اتصال ناموفق بود.", "The pre-flight check failed.");
                foreach (CheckResult c in checks)
                {
                    if (c.Status == CheckStatus.Fail) { reason = c.Title + ": " + c.Detail; break; }
                }
                error = reason;
                Fault(error);
                return false;
            }

            // A server that cannot relay UDP would make every UDP DNS lookup time out, which
            // looks exactly like a dead tunnel. Move to a TCP transport rather than let that happen.
            if (!udpWorks && _settings.DnsMode == "udp")
            {
                _settings.DnsMode = "doh";
                _settings.Save();
                Log.Warn("The server does not relay UDP; DNS switched to DoH.");
            }

            // ---- config -----------------------------------------------------------------
            string config = ConfigBuilder.Build(_settings);
            try
            {
                AppPaths.EnsureDirs();
                File.WriteAllText(AppPaths.ConfigFile, config, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Fault(error);
                return false;
            }

            string vout, verr;
            int vcode = ProcUtil.Run(AppPaths.SingBox,
                "check -c \"" + AppPaths.ConfigFile + "\" -D \"" + AppPaths.DataDir + "\"",
                20000, out vout, out verr);
            if (vcode != 0)
            {
                error = Lang.T("کانفیگ معتبر نیست: ", "Invalid configuration: ") + StripAnsi(verr + vout).Trim();
                Fault(error);
                return false;
            }

            // ---- run the core ------------------------------------------------------------
            SetState(TunnelState.Connecting, Lang.T("در حال ساخت آداپتور مجازی…", "Creating the virtual adapter…"));
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(AppPaths.SingBox,
                    "run -c \"" + AppPaths.ConfigFile + "\" -D \"" + AppPaths.DataDir + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                // wintun.dll must sit beside the core.
                psi.WorkingDirectory = AppPaths.ToolsDir;

                Process p = new Process();
                p.StartInfo = psi;
                p.EnableRaisingEvents = true;
                p.OutputDataReceived += OnCoreOutput;
                p.ErrorDataReceived += OnCoreOutput;
                p.Exited += OnCoreExited;
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                lock (_gate) { _core = p; }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Log.Error("Core failed to start", ex);
                Fault(error);
                return false;
            }

            // ---- wait for the adapter ------------------------------------------------------
            string alias = TunAdapter.WaitForAdapter(25000);
            if (alias == null)
            {
                error = Lang.T(
                    "آداپتور مجازی بالا نیامد. لاگ را بررسی کنید (احتمالا wintun.dll یا دسترسی مدیر).",
                    "The virtual adapter never came up. Check the log (usually wintun.dll or admin rights).");
                StopCore();
                Fault(error);
                return false;
            }
            _tunAlias = alias;
            Log.Info("Tunnel adapter is up: " + alias);

            // ---- prove that traffic really leaves through the tunnel -----------------------
            // The adapter existing means nothing on its own. Until an ordinary request has been
            // seen going out and coming back, "connected" would be a guess.
            SetState(TunnelState.Connecting, Lang.T("در حال تأیید عبور ترافیک…", "Verifying traffic…"));
            bool healthy;
            string exitIp;
            List<CheckResult> verify = Preflight.Verify(out healthy, out exitIp);
            PublishChecks(Lang.T("پس از اتصال", "After connecting"), verify);
            Verified = healthy;
            ExitIp = exitIp;

            if (!healthy)
            {
                string detail = Lang.T("تونل بالا آمد ولی ترافیک از آن عبور نمی‌کند.",
                                       "The tunnel is up but no traffic is getting through.");
                foreach (CheckResult c in verify)
                {
                    if (c.Status == CheckStatus.Fail) { detail = c.Title + ": " + c.Detail; break; }
                }
                StartHealthTimer();
                SetState(TunnelState.Connected, detail);
                return true;   // still connected, but the caller and the UI know it is not working
            }

            // ---- kill switch ---------------------------------------------------------------
            // Armed only now, with a working tunnel behind it, and rolled back at once if it
            // turns out to break connectivity.
            if (_settings.KillSwitch)
            {
                SetState(TunnelState.Connecting, Lang.T("در حال فعال‌سازی کیل‌سوئیچ…", "Arming the kill switch…"));
                List<string> allow = ProxyDetector.RunningCorePaths();
                if (!allow.Contains(AppPaths.SingBox)) allow.Add(AppPaths.SingBox);
                try
                {
                    string self = Process.GetCurrentProcess().MainModule.FileName;
                    if (!allow.Contains(self)) allow.Add(self);
                }
                catch { }

                string fwError;
                if (FirewallGuard.Apply(_tunAlias, allow, out fwError))
                {
                    _killSwitchArmed = true;
                    bool stillHealthy;
                    string ignoredIp;
                    Preflight.Verify(out stillHealthy, out ignoredIp);
                    if (!stillHealthy)
                    {
                        Log.Warn("The kill switch broke connectivity; rolling it back.");
                        string err;
                        FirewallGuard.Remove(out err);
                        _killSwitchArmed = false;
                        PublishChecks(Lang.T("کیل‌سوئیچ", "Kill switch"), new List<CheckResult> {
                            new CheckResult(CheckStatus.Warn,
                                Lang.T("کیل‌سوئیچ برگردانده شد", "Kill switch rolled back"),
                                Lang.T("با فعال شدن آن ترافیک قطع شد، پس خودکار غیرفعال شد",
                                       "it cut the traffic, so it was disabled again"))
                        });
                    }
                }
                else
                {
                    // The tunnel itself is fine, so stay connected and tell the user.
                    Log.Warn("Continuing without the kill switch: " + fwError);
                    PublishChecks(Lang.T("کیل‌سوئیچ", "Kill switch"), new List<CheckResult> {
                        new CheckResult(CheckStatus.Warn,
                            Lang.T("کیل‌سوئیچ فعال نشد", "Kill switch not armed"), fwError)
                    });
                }
            }

            // ---- clock and country -----------------------------------------------------------
            // Last, because both only make sense once the exit address is known and proven.
            if (_settings.MatchTimeZone || _settings.MatchRegion) MatchIdentityToExit();

            StartHealthTimer();
            // The exit address is shown in the summary card, in a Latin face; repeating it
            // here would render its digits in the Persian UI font.
            SetState(TunnelState.Connected, Lang.T("متصل و تأیید شد.", "Connected and verified."));
            return true;
        }

        /// <summary>
        /// Points the clock and the home country at the exit server's. A failure here is
        /// reported and then ignored: the tunnel works either way, and refusing to connect over
        /// a cosmetic mismatch would be the wrong trade.
        /// </summary>
        void MatchIdentityToExit()
        {
            SetState(TunnelState.Connecting,
                Lang.T("در حال تطبیق ساعت و کشور…",
                       "Matching the clock and country…"));

            List<CheckResult> rows = new List<CheckResult>();
            string lookupError;
            ExitInfo exit = Fingerprint.LookupExit(_settings, out lookupError);

            if (exit == null)
            {
                rows.Add(new CheckResult(CheckStatus.Warn,
                    Lang.T("هویت تطبیق نشد", "Identity not matched"),
                    lookupError == null
                        ? Lang.T("آدرس خروجی خوانده نشد", "the exit address could not be read")
                        : lookupError));
                PublishChecks(Lang.T("هویت", "Identity"), rows);
                return;
            }

            if (_settings.MatchTimeZone) rows.Add(ApplyTimeZone(exit));
            if (_settings.MatchRegion) rows.Add(ApplyRegion(exit));

            PublishChecks(Lang.T("هویت", "Identity"), rows);
        }

        CheckResult ApplyTimeZone(ExitInfo exit)
        {
            string title = Lang.T("منطقه زمانی", "Time zone");
            if (string.IsNullOrEmpty(exit.TimeZoneIana))
                return new CheckResult(CheckStatus.Warn, title,
                    Lang.T("سرور خروجی منطقه زمانی اعلام نکرد",
                           "the exit server reported no time zone"));

            string applied, error;
            if (TimeZoneSync.ApplyForIana(exit.TimeZoneIana, out applied, out error))
                return new CheckResult(CheckStatus.Ok, title, exit.TimeZoneIana + "  (" + applied + ")");
            return new CheckResult(CheckStatus.Warn, title, error);
        }

        CheckResult ApplyRegion(ExitInfo exit)
        {
            string title = Lang.T("کشور ویندوز", "Windows home region");
            if (string.IsNullOrEmpty(exit.CountryCode))
                return new CheckResult(CheckStatus.Warn, title,
                    Lang.T("کشور خروجی مشخص نشد", "the exit country is unknown"));

            string applied, error;
            if (RegionSync.ApplyFor(exit.CountryCode, out applied, out error))
                return new CheckResult(CheckStatus.Ok, title, applied);
            return new CheckResult(CheckStatus.Warn, title, error);
        }

        void UnbindIpv6()
        {
            SetState(TunnelState.Connecting,
                Lang.T("در حال خاموش کردن IPv6…", "Switching IPv6 off…"));

            string error;
            List<string> changed = Ipv6Binding.Apply(out error);
            List<CheckResult> rows = new List<CheckResult>();

            if (!string.IsNullOrEmpty(error))
                rows.Add(new CheckResult(CheckStatus.Warn,
                    Lang.T("IPv6 خاموش نشد", "IPv6 not switched off"), error));
            else if (changed.Count == 0)
                rows.Add(new CheckResult(CheckStatus.Ok,
                    Lang.T("IPv6", "IPv6"),
                    Lang.T("از قبل جایی فعال نبود", "already off everywhere")));
            else
                rows.Add(new CheckResult(CheckStatus.Ok,
                    Lang.T("IPv6 خاموش شد", "IPv6 switched off"),
                    string.Join(", ", changed.ToArray())));

            PublishChecks("IPv6", rows);
        }

        void Fault(string message)
        {
            StopHealthTimer();
            SetState(TunnelState.Faulted, message);
        }

        // ------------------------------------------------------------------ stop

        public void Stop()
        {
            lock (_gate)
            {
                if (State == TunnelState.Disconnecting) return;
                _stopRequested = true;
            }
            SetState(TunnelState.Disconnecting, Lang.T("در حال قطع…", "Disconnecting…"));
            StopHealthTimer();

            // Everything system-wide goes back before the core is killed, so the machine is
            // never left both cut off and without a tunnel. These belong to the user, not to
            // the tunnel, so they are undone from the state files rather than from the current
            // settings: turning an option off mid-session cannot strand what it already did.
            SetState(TunnelState.Disconnecting,
                Lang.T("در حال برگرداندن تغییرات ویندوز…",
                       "Undoing the Windows changes…"));
            string undone = ProMode.RestoreAll();
            _killSwitchArmed = false;
            if (undone.Length > 0) Log.Info(undone.Trim().Replace(Environment.NewLine, "  "));

            StopCore();
            _tunAlias = null;
            Verified = false;
            ExitIp = null;
            _ticks = 0;
            SetState(TunnelState.Disconnected, Lang.T("قطع شد", "Disconnected"));
        }

        void StopCore()
        {
            Process p;
            lock (_gate) { p = _core; _core = null; }
            if (p == null) return;
            try
            {
                p.Exited -= OnCoreExited;
                if (!p.HasExited)
                {
                    p.Kill();
                    p.WaitForExit(8000);
                }
            }
            catch (Exception ex) { Log.Error("Stopping the core failed", ex); }
            finally { try { p.Dispose(); } catch { } }

            // The wintun adapter disappears with the process; give Windows a moment to notice.
            for (int i = 0; i < 20 && TunAdapter.IsUp(); i++) Thread.Sleep(100);
        }

        void OnCoreExited(object sender, EventArgs e)
        {
            bool expected;
            lock (_gate) { expected = _stopRequested; }
            if (expected) return;

            Log.Error("The core exited unexpectedly.");
            StopHealthTimer();
            if (_killSwitchArmed || FirewallGuard.IsActive())
            {
                string err;
                FirewallGuard.Remove(out err);
                _killSwitchArmed = false;
            }
            lock (_gate) { _core = null; }
            Fault(Lang.T("هسته به‌طور ناگهانی بسته شد. لاگ را ببینید.",
                         "The core stopped unexpectedly. See the log."));
        }

        void OnCoreOutput(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;
            string line = StripAnsi(e.Data).Trim();
            if (line.Length > 0) Log.Core(line);
        }

        static readonly Regex AnsiRe = new Regex("\x1b\\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);
        static string StripAnsi(string s) { return s == null ? "" : AnsiRe.Replace(s, ""); }

        // ------------------------------------------------------------------ health

        void StartHealthTimer()
        {
            StopHealthTimer();
            _health = new Timer(HealthTick, null, 10000, 10000);
        }

        void StopHealthTimer()
        {
            Timer t = _health;
            _health = null;
            if (t != null) { try { t.Dispose(); } catch { } }
        }

        int _ticks;

        void HealthTick(object state)
        {
            try
            {
                if (State != TunnelState.Connected) return;
                if (!TunAdapter.IsUp())
                {
                    Log.Warn("Tunnel adapter went away.");
                    Stop();
                    Fault(Lang.T("آداپتور تونل از بین رفت.", "The tunnel adapter disappeared."));
                    return;
                }
                if (!ProxyDetector.IsReachable(_settings.ProxyHost, _settings.ProxyPort, 2500))
                {
                    Log.Warn("Upstream proxy " + _settings.ProxyHost + ":" + _settings.ProxyPort + " is not answering.");
                }

                // Re-prove the tunnel every minute. "Connected" must keep meaning "traffic flows",
                // not "the adapter existed once".
                _ticks++;
                if (_ticks % 6 != 0) return;

                bool healthy;
                string exitIp;
                List<CheckResult> checks = Preflight.Verify(out healthy, out exitIp);
                bool changed = healthy != Verified;
                Verified = healthy;
                if (healthy && !string.IsNullOrEmpty(exitIp)) ExitIp = exitIp;

                if (changed)
                {
                    PublishChecks(Lang.T("بررسی دوره‌ای", "Periodic check"), checks);
                    SetState(TunnelState.Connected, healthy
                        ? Lang.T("متصل و تأیید شد.", "Connected and verified.")
                        : Lang.T("تونل بالاست ولی ترافیک عبور نمی‌کند.",
                                 "The tunnel is up but traffic has stopped flowing."));
                }
            }
            catch (Exception ex) { Log.Error("Health check failed", ex); }
        }

        // ------------------------------------------------------------------ recovery

        /// <summary>
        /// Called at startup. Undoes anything a previous crash left behind: an armed kill switch
        /// with no tunnel, or an orphaned core process from our own tools folder.
        /// </summary>
        public static string CleanupStale()
        {
            StringBuilder notes = new StringBuilder();
            try
            {
                foreach (Process p in ProcUtil.FindByName("sing-box"))
                {
                    try
                    {
                        string path = p.MainModule.FileName;
                        if (string.Equals(path, AppPaths.SingBox, StringComparison.OrdinalIgnoreCase))
                        {
                            p.Kill();
                            p.WaitForExit(5000);
                            notes.AppendLine("Killed an orphaned core process.");
                        }
                    }
                    catch { }
                }
            }
            catch { }

            // Anything a Pro connect changed and did not get to undo.
            string undone = ProMode.RestoreAll();
            if (undone.Length > 0)
            {
                notes.AppendLine("Undid changes left by an unclean shutdown:");
                notes.Append(undone);
            }

            return notes.ToString();
        }
    }
}
