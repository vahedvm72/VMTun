using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace VMTun
{
    /// <summary>
    /// The installer. Everything VMTun needs travels inside this one executable as
    /// gzip-compressed resources: the application, the sing-box core, wintun.dll, the icon and
    /// the Persian font. It unpacks them, makes the shortcuts and registers an entry under
    /// Apps and Features; uninstalling is handled by VMTun.exe itself with --uninstall.
    /// </summary>
    static class Setup
    {
        const string ResourcePrefix = "payload/";

        [STAThread]
        static int Main(string[] args)
        {
            bool silent = false;
            string target = null;

            // The target has to be rebuilt from several arguments, not read from one.
            //
            // Every released version up to 1.6.1 launched this installer with an unquoted path:
            //   --silent /D=C:\Program Files\VMTun
            // which the runtime splits into "/D=C:\Program" and "Files\VMTun", so the installer
            // took the target to be C:\Program, unpacked there and left the real installation
            // untouched. Those versions are the ones that have to be able to update away from
            // the bug, so the tolerance belongs here: once /D= is seen, every following argument
            // that is not a switch is part of the path.
            bool collecting = false;
            List<string> pathParts = new List<string>();
            foreach (string a in args)
            {
                string arg = a.Trim();
                bool isSwitch = arg.StartsWith("/") || arg.StartsWith("--");

                if (arg.Equals("/S", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("--silent", StringComparison.OrdinalIgnoreCase))
                {
                    silent = true;
                    collecting = false;
                }
                else if (arg.StartsWith("/D=", StringComparison.OrdinalIgnoreCase))
                {
                    pathParts.Clear();
                    pathParts.Add(arg.Substring(3));
                    collecting = true;
                }
                else if (arg.Equals("--target", StringComparison.OrdinalIgnoreCase))
                {
                    pathParts.Clear();
                    collecting = true;
                }
                else if (collecting && !isSwitch)
                {
                    pathParts.Add(arg);
                }
                else
                {
                    collecting = false;
                }
            }
            if (pathParts.Count > 0) target = string.Join(" ", pathParts.ToArray()).Trim();

            // A silent run only ever comes from the updater, and an update belongs wherever the
            // app already is. The registry entry was written by an installer that finished, so
            // it beats a command line that may be malformed — which is exactly the case this is
            // recovering from. The command line is only consulted for a fresh install.
            string registered = Integration.InstalledLocation();
            if (silent && !string.IsNullOrEmpty(registered) && Directory.Exists(registered))
                target = registered;
            else if (!LooksLikeTarget(target))
                target = !string.IsNullOrEmpty(registered) ? registered : Integration.DefaultTarget;

            // No file log: the installer would otherwise create a data folder wherever it was
            // downloaded to, just to record that a shortcut could not be written yet.
            Log.ToFile = false;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Lang.Fa = !IsEnglishSystem();
            Theme.Use("auto");

            if (silent)
            {
                // A silent install has nowhere to show a failure, which is how a broken target
                // went unnoticed: the app closed, nothing was installed and nothing said why.
                string logPath = Path.Combine(Path.GetTempPath(), "VMTun-Setup.log");
                SilentLog(logPath, "--- " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                                   "  VMTun " + Integration.Version + " silent install");
                SilentLog(logPath, "args:   " + string.Join(" | ", args));
                SilentLog(logPath, "target: " + target +
                                   (registered == null ? "  (no registry entry)" : "  (registered: " + registered + ")"));

                string error;
                bool ok = Install(target, true, false, null, out error);
                SilentLog(logPath, ok ? "result: installed" : "result: FAILED - " + error);
                if (!ok) { Console.Error.WriteLine(error); return 1; }

                // The application has to come back. A silent install is only ever run by the
                // updater, which closed the app to let its files be replaced, and the dialog
                // that started all this says the app will restart when it is done. Without
                // this it simply vanished: the update had in fact been applied, but nothing
                // on screen said so, which is indistinguishable from it having failed.
                SilentLog(logPath, Relaunch(target));
                return 0;
            }

            Application.Run(new SetupForm(target));
            return 0;
        }

        /// <summary>
        /// Starts the freshly installed application. Returns a line for the install log,
        /// because a failure here is otherwise as silent as the one it is fixing.
        /// </summary>
        static string Relaunch(string targetDir)
        {
            string exe = Path.Combine(targetDir, "VMTun.exe");
            if (!File.Exists(exe)) return "relaunch: FAILED - " + exe + " is not there";

            // The file was written moments ago and a scanner may still have it open, so a
            // first refusal is not final.
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(exe);
                    psi.UseShellExecute = true;       // carries the manifest's elevation request
                    psi.WorkingDirectory = targetDir;
                    Process.Start(psi);
                    return "relaunch: started" + (attempt > 1 ? " (attempt " + attempt + ")" : "");
                }
                catch (Exception ex)
                {
                    if (attempt == 3) return "relaunch: FAILED - " + ex.Message;
                    Thread.Sleep(1500);
                }
            }
            return "relaunch: FAILED";
        }

        /// <summary>A path that could plausibly be an installation folder.</summary>
        static bool LooksLikeTarget(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;
                if (!Path.IsPathRooted(path)) return false;
                // A bare drive root is a mis-parse, not a choice. A folder that does not exist
                // yet is fine: a first install creates it.
                string parent = Path.GetDirectoryName(path);
                return !string.IsNullOrEmpty(parent);
            }
            catch { return false; }
        }

        static void SilentLog(string path, string line)
        {
            try { File.AppendAllText(path, line + Environment.NewLine); }
            catch { }
        }

        static bool IsEnglishSystem()
        {
            try { return !System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWith("fa"); }
            catch { return false; }
        }

        // ------------------------------------------------------------------ payload

        public static List<string> PayloadNames()
        {
            List<string> names = new List<string>();
            foreach (string n in Assembly.GetExecutingAssembly().GetManifestResourceNames())
                if (n.StartsWith(ResourcePrefix, StringComparison.Ordinal)) names.Add(n);
            names.Sort();
            return names;
        }

        static void Extract(string resource, string targetDir)
        {
            string relative = resource.Substring(ResourcePrefix.Length).Replace('/', Path.DirectorySeparatorChar);
            string path = Path.Combine(targetDir, relative);
            string dir = Path.GetDirectoryName(path);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            using (Stream src = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            using (GZipStream gz = new GZipStream(src, CompressionMode.Decompress))
            using (FileStream dst = File.Create(path))
            {
                byte[] buffer = new byte[128 * 1024];
                int n;
                while ((n = gz.Read(buffer, 0, buffer.Length)) > 0) dst.Write(buffer, 0, n);
            }
        }

        // ------------------------------------------------------------------ install

        public static bool Install(string targetDir, bool startMenu, bool desktop,
                                   Action<int, string> progress, out string error)
        {
            error = null;
            try
            {
                Report(progress, 2, Lang.T("در حال بستن نسخه در حال اجرا…", "Closing any running copy…"));
                StopRunningCopy(targetDir);

                Report(progress, 8, Lang.T("ساخت پوشه…", "Creating the folder…"));
                if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

                List<string> names = PayloadNames();
                for (int i = 0; i < names.Count; i++)
                {
                    string shown = names[i].Substring(ResourcePrefix.Length);
                    Report(progress, 10 + (int)(75.0 * i / Math.Max(1, names.Count)),
                        Lang.T("در حال باز کردن ", "Unpacking ") + shown);
                    Extract(names[i], targetDir);
                }

                Report(progress, 88, Lang.T("ساخت میان‌برها…", "Creating shortcuts…"));
                string exe = Path.Combine(targetDir, "VMTun.exe");
                if (startMenu) Integration.CreateShortcut(Integration.StartMenuLink(), exe, targetDir);
                if (desktop) Integration.CreateShortcut(Integration.DesktopLink(), exe, targetDir);

                Report(progress, 95, Lang.T("ثبت در «برنامه‌ها و قابلیت‌ها»…", "Registering with Apps and Features…"));
                Integration.RegisterUninstall(targetDir, exe);

                Report(progress, 100, Lang.T("نصب کامل شد.", "Installation complete."));
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        static void Report(Action<int, string> progress, int percent, string text)
        {
            if (progress != null) progress(percent, text);
        }

        /// <summary>
        /// A previous copy may be running and holding its own exe open. Ask it to undo any
        /// firewall changes first, then stop it and its core.
        /// </summary>
        static void StopRunningCopy(string targetDir)
        {
            string exe = Path.Combine(targetDir, "VMTun.exe");
            try
            {
                if (File.Exists(exe))
                {
                    string o, e;
                    ProcUtil.Run(exe, "--repair", 30000, out o, out e);
                }
            }
            catch { }

            foreach (string name in new string[] { "VMTun", "sing-box" })
            {
                foreach (Process p in ProcUtil.FindByName(name))
                {
                    try { p.Kill(); p.WaitForExit(5000); }
                    catch { }
                }
            }
            Thread.Sleep(400);
        }
    }

    /// <summary>The installer window, styled like the application itself.</summary>
    class SetupForm : Form
    {
        TextBox _path;
        CheckBox _desktop, _launch;
        Button _install, _browse, _close;
        Label _status, _headline;
        Theme.ProgressStrip _bar;
        bool _done;

        public SetupForm(string target)
        {
            Text = Integration.AppName + " " + Integration.Version;
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.F(Theme.FBody);
            RightToLeft = Theme.TextDirection;
            RightToLeftLayout = false;
            ClientSize = Ui.Sz(560, 390);
            HandleCreated += delegate { Theme.ApplyTitleBar(this); };
            try { Icon = new Icon(new MemoryStream(IconBytes())); }
            catch { }

            Theme.CardPanel head = new Theme.CardPanel();
            head.Location = Ui.Pt(20, 18);
            head.Size = Ui.Sz(520, 92);
            Controls.Add(head);

            Label logo = Theme.Label("VMTun", Theme.FH1, Theme.Text, true);
            logo.Font = Theme.FLatinB(Theme.FH1);
            logo.Location = Ui.Pt(20, 14);
            head.Controls.Add(logo);

            _headline = Theme.Label(
                Lang.T("تونل سراسری ویندوز برای پروکسی محلی v2rayN",
                       "System-wide Windows tunnel for a local v2rayN proxy"),
                Theme.FSmall, Theme.Muted, false);
            _headline.Location = new Point(Ui.Px(21), logo.Bottom + Ui.Px(2));
            head.Controls.Add(_headline);

            Label where = Theme.Label(Lang.T("محل نصب", "Install to"), Theme.FSmall, Theme.Text, false);
            where.Location = Ui.Pt(22, 128);
            Controls.Add(where);

            _path = new TextBox();
            _path.Text = target;
            _path.Location = Ui.Pt(22, 152);
            _path.Width = Ui.Px(400);
            Theme.StyleInput(_path);
            Controls.Add(_path);

            _browse = Theme.Button(Lang.T("انتخاب…", "Browse\u2026"), Theme.CardHi, 108, 32);
            _browse.Location = new Point(Ui.Px(432), Ui.Px(152));
            _browse.Click += delegate { Browse(); };
            Controls.Add(_browse);

            _desktop = MakeCheck(Lang.T("ساخت میان‌بر روی دسکتاپ", "Create a desktop shortcut"), 22, 196);
            _desktop.Checked = true;
            _launch = MakeCheck(Lang.T("اجرای برنامه پس از نصب", "Run VMTun when finished"), 22, 226);
            _launch.Checked = true;

            _bar = new Theme.ProgressStrip();
            _bar.Location = Ui.Pt(22, 274);
            _bar.Size = Ui.Sz(516, 12);
            Controls.Add(_bar);

            _status = Theme.Label(
                Lang.T("برای شروع نصب، دکمه را بزنید.", "Press Install to begin."),
                Theme.FTiny, Theme.Muted, false);
            _status.AutoSize = false;
            _status.Location = Ui.Pt(22, 296);
            _status.Size = Ui.Sz(516, 40);
            Controls.Add(_status);

            _install = Theme.Button(Lang.T("نصب", "Install"), Theme.Accent, 150, 38);
            _install.Font = Theme.FB(Theme.FBody);
            _install.Location = Ui.Pt(388, 338);
            _install.Click += delegate { Begin(); };
            Controls.Add(_install);
            // Focus the action, not the path box: an auto-selected path looks like an error.
            ActiveControl = _install;

            _close = Theme.Button(Lang.T("انصراف", "Cancel"), Theme.CardHi, 120, 38);
            _close.Location = Ui.Pt(258, 338);
            _close.Click += delegate { Close(); };
            Controls.Add(_close);
        }

        static byte[] IconBytes()
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload/VMTun.ico"))
            using (GZipStream gz = new GZipStream(s, CompressionMode.Decompress))
            using (MemoryStream ms = new MemoryStream())
            {
                byte[] buffer = new byte[64 * 1024];
                int n;
                while ((n = gz.Read(buffer, 0, buffer.Length)) > 0) ms.Write(buffer, 0, n);
                return ms.ToArray();
            }
        }

        CheckBox MakeCheck(string text, int x, int y)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.AutoSize = true;
            c.ForeColor = Theme.Text;
            c.BackColor = Color.Transparent;
            c.Font = Theme.F(Theme.FSmall);
            c.Cursor = Cursors.Hand;
            c.Location = Ui.Pt(x, y);
            Controls.Add(c);
            return c;
        }

        void Browse()
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = Lang.T("پوشه نصب را انتخاب کنید", "Choose the installation folder");
                dlg.SelectedPath = _path.Text;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    _path.Text = Path.Combine(dlg.SelectedPath, Integration.AppName);
            }
        }

        void Begin()
        {
            if (_done) { Close(); return; }

            string target = _path.Text.Trim();
            if (target.Length < 3)
            {
                Theme.Tell(this, Lang.T("مسیر نصب معتبر نیست.", "That installation path is not valid."));
                return;
            }

            _install.Enabled = false;
            _browse.Enabled = false;
            _path.Enabled = false;
            _close.Enabled = false;

            bool desktop = _desktop.Checked;
            bool launch = _launch.Checked;

            ThreadPool.QueueUserWorkItem(delegate
            {
                string error;
                bool ok = Setup.Install(target, true, desktop,
                    delegate(int percent, string text)
                    {
                        Invoke(new Action(delegate
                        {
                            _bar.Value = Math.Max(0, Math.Min(100, percent));
                            _status.Text = text;
                        }));
                    },
                    out error);

                Invoke(new Action(delegate
                {
                    _close.Enabled = true;
                    if (!ok)
                    {
                        _status.ForeColor = Theme.Red;
                        _status.Text = Lang.T("نصب ناموفق بود: ", "Installation failed: ") + error;
                        _install.Enabled = true;
                        _browse.Enabled = true;
                        _path.Enabled = true;
                        return;
                    }

                    _done = true;
                    _status.ForeColor = Theme.Green;
                    _status.Text = Lang.T("نصب شد در ", "Installed to ") + target;
                    _install.Text = Lang.T("پایان", "Finish");
                    _install.Enabled = true;
                    _close.Visible = false;

                    if (launch)
                    {
                        try
                        {
                            ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(target, "VMTun.exe"));
                            psi.UseShellExecute = true;
                            psi.WorkingDirectory = target;
                            Process.Start(psi);
                        }
                        catch (Exception ex) { Log.Error("Launch after install failed", ex); }
                    }
                }));
            });
        }
    }
}
