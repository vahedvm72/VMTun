using System;
using System.Diagnostics;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace VMTun
{
    static class Program
    {
        static Mutex _single;

        [STAThread]
        static int Main(string[] args)
        {
            AppPaths.EnsureDirs();

            // Set up before anything can put a window on screen: the startup notices below are
            // themed forms, not message boxes, and they need visual styles already enabled.
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // The notices below fire before the settings file is read, so they follow Windows
            // rather than a preference nobody has loaded yet. Without this they were always
            // dark, on a machine that may be set to light.
            Theme.Use("auto");

            bool repairOnly = false, startInTray = false, uninstall = false, silent = false;
            foreach (string a in args)
            {
                string arg = a.Trim().ToLowerInvariant();
                if (arg == "--repair" || arg == "/repair") repairOnly = true;
                else if (arg == "--tray" || arg == "/tray") startInTray = true;
                else if (arg == "--uninstall" || arg == "/uninstall") uninstall = true;
                else if (arg == "--silent" || arg == "/s") silent = true;
            }

            if (!IsAdministrator())
            {
                // The manifest asks for elevation, so this only happens if it was stripped.
                // English only: this fires before the language setting has been read.
                Theme.TellEnglish(null, "VMTun must be run as administrator.");
                return 2;
            }

            if (repairOnly) return Repair();
            if (uninstall) return Uninstall(silent);

            _single = new Mutex(true, "Global\\VMTun.SingleInstance");
            bool owned = false;
            try { owned = _single.WaitOne(TimeSpan.Zero, false); }
            catch (AbandonedMutexException) { owned = true; }
            if (!owned)
            {
                Theme.TellEnglish(null, "VMTun is already running. Look for it in the notification area.");
                return 1;
            }

            Settings settings = Settings.Load();
            Lang.Fa = settings.Lang != "en";
            Theme.Use(settings.Theme);

            // Whatever happens, the firewall must not stay locked down without a tunnel.
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Log.Error("Unhandled exception", e.ExceptionObject as Exception);
                SafetyNet();
            };
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
            {
                Log.Error("UI thread exception", e.Exception);
                Theme.Tell(null, e.Exception.Message);
            };
            AppDomain.CurrentDomain.ProcessExit += delegate { SafetyNet(); };
            Microsoft.Win32.SystemEvents.SessionEnding += delegate { SafetyNet(); };

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            Log.Info("VMTun starting. Data: " + AppPaths.DataDir);

            // One tunnel for the whole process. The window is rebuilt whenever the theme
            // or language changes, and the connection must not be torn down with it.
            TunnelService tunnel = new TunnelService();
            try
            {
                // Switching theme or language rebuilds the window rather than re-styling it in
                // place: colours and fonts are chosen when the controls are created.
                bool again = true;
                int page = 0;
                while (again)
                {
                    MainForm form = new MainForm(settings, startInTray, page, tunnel);
                    Application.Run(form);
                    again = form.RestartRequested;
                    if (again)
                    {
                        page = form.RestartPage;
                        settings = Settings.Load();
                        Lang.Fa = settings.Lang != "en";
                        Theme.Use(settings.Theme);
                        startInTray = false;
                    }
                }
            }
            finally
            {
                try { tunnel.Stop(); }
                catch { }
                SafetyNet();
                try { _single.ReleaseMutex(); }
                catch { }
            }
            return 0;
        }

        /// <summary>Undoes anything system-wide that could outlive the tunnel: the firewall
        /// policy, the IPv6 bindings, the clock's zone and the home region.</summary>
        static void SafetyNet()
        {
            try { ProMode.RestoreAll(); }
            catch { }
        }

        /// <summary>
        /// Removes the installation. A process cannot delete the folder it is running from, so
        /// the files are handed to a detached cmd that waits for this process to exit first.
        /// </summary>
        static int Uninstall(bool silent)
        {
            if (!silent && !Theme.Ask(null,
                    Lang.T("VMTun حذف شود؟ تنظیمات و گزارش‌ها هم پاک می‌شوند.",
                           "Remove VMTun? Its settings and logs will be deleted too."),
                    Lang.T("حذف", "Remove"), Lang.T("انصراف", "Cancel")))
                return 1;

            // Put the network back before anything else disappears.
            TunnelService.CleanupStale();
            string err;
            FirewallGuard.Remove(out err);

            try
            {
                string o, e;
                ProcUtil.Run("schtasks.exe", "/Delete /TN \"VMTun\" /F", 15000, out o, out e);
            }
            catch { }

            Integration.Remove();

            string dir = AppPaths.ExeDir;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe",
                    "/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"" + dir + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                Process.Start(psi);
            }
            catch (Exception ex) { Log.Error("Scheduling the folder removal failed", ex); }

            if (!silent)
                Theme.Tell(null, Lang.T("VMTun حذف شد.", "VMTun has been removed."));
            return 0;
        }

        /// <summary>Headless recovery used by Repair-Network.cmd and the --repair switch.</summary>
        static int Repair()
        {
            Console.WriteLine("VMTun: restoring network state...");
            string notes = TunnelService.CleanupStale();
            string err;
            bool ok = FirewallGuard.Remove(out err);
            if (!string.IsNullOrEmpty(notes)) Console.WriteLine(notes.Trim());
            Console.WriteLine(ok
                ? "Done. Firewall rules removed and the outbound policy restored."
                : "Finished with warnings: " + err);
            return ok ? 0 : 1;
        }

        static bool IsAdministrator()
        {
            try
            {
                WindowsIdentity id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }
}
