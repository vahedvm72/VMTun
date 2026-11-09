using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace VMTun
{
    /// <summary>
    /// Where the app registers itself with Windows: the Start Menu and desktop shortcuts and
    /// the Apps-and-Features entry. Shared by the installer, which creates them, and by the
    /// application's own --uninstall, which takes them away again.
    /// </summary>
    static class Integration
    {
        public const string AppName = "VMTun";

        /// <summary>
        /// Bumped for every release. The tag pushed to GitHub must match it (1.4 -> v1.4), because
        /// the updater compares this against the latest tag to decide whether there is anything
        /// new; the release workflow refuses to build when the two disagree.
        /// </summary>
        public const string Version = "3.5";

        /// <summary>Where the updater looks for new releases.</summary>
        public const string RepoOwner = "vahedvm72";
        public const string RepoName = "VMTun";
        const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\VMTun";

        public static string DefaultTarget
        {
            get
            {
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                if (string.IsNullOrEmpty(pf)) pf = @"C:\Program Files";
                return Path.Combine(pf, AppName);
            }
        }

        /// <summary>
        /// Where the previous install put itself, from the Apps-and-Features entry, or null.
        /// This is the authority on where an update belongs: it was written by an installer
        /// that succeeded, rather than passed in on a command line that may be malformed.
        /// </summary>
        public static string InstalledLocation()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k =
                       Microsoft.Win32.Registry.LocalMachine.OpenSubKey(UninstallKey))
                {
                    if (k == null) return null;
                    object v = k.GetValue("InstallLocation");
                    string path = v == null ? null : v.ToString().Trim().Trim('"');
                    return string.IsNullOrEmpty(path) ? null : path;
                }
            }
            catch { return null; }
        }

        public static string StartMenuLink()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                "Programs" + Path.DirectorySeparatorChar + AppName + ".lnk");
        }

        public static string DesktopLink()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                AppName + ".lnk");
        }

        /// <summary>The icon that travels beside the exe and is the one source of the mark.</summary>
        public static string IconFile(string dir)
        {
            return Path.Combine(dir, "VMTun.ico");
        }

        /// <summary>
        /// What a shortcut should point its icon at: the .ico file itself, not the exe.
        ///
        /// The shell caches an icon per path, and a shortcut that says "the icon of VMTun.exe"
        /// asks for a *file* icon that Explorer keeps after the exe is replaced — which is how a
        /// taskbar pin, a Start Menu entry and a notification kept last version's logo after the
        /// logo changed. Naming the .ico directly gives the shell a different icon source to
        /// look up, so the new mark is read the moment the file is replaced.
        /// </summary>
        public static string IconLocation(string exe, string workingDir)
        {
            string ico = IconFile(workingDir);
            try { if (File.Exists(ico)) return ico + ",0"; }
            catch { }
            return exe + ",0";
        }

        /// <summary>
        /// Writes a .lnk through the Windows Script Host object, bound late so nothing needs a
        /// COM reference at build time.
        /// </summary>
        public static void CreateShortcut(string linkPath, string exe, string workingDir)
        {
            try
            {
                string dir = Path.GetDirectoryName(linkPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return;
                object shell = Activator.CreateInstance(shellType);
                object link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod,
                    null, shell, new object[] { linkPath });
                Type linkType = link.GetType();
                linkType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { exe });
                linkType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { workingDir });
                linkType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link,
                    new object[] { IconLocation(exe, workingDir) });
                linkType.InvokeMember("Description", BindingFlags.SetProperty, null, link,
                    new object[] { "VMTun \u2014 system-wide tunnel for a local V2Ray proxy" });
                linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            }
            catch (Exception ex) { Log.Error("Shortcut creation failed", ex); }
        }

        /// <summary>Reads a .lnk's target, or null when it cannot be read.</summary>
        static string ReadTarget(string linkPath)
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return null;
                object shell = Activator.CreateInstance(shellType);
                object link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod,
                    null, shell, new object[] { linkPath });
                object target = link.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, link, null);
                return target == null ? null : target.ToString();
            }
            catch { return null; }
        }

        /// <summary>
        /// Every shortcut on the machine that launches this installation: the two the installer
        /// writes, plus any taskbar pin. A pin is a copy of the shortcut kept under its own
        /// folder and is never touched by an install, which is why the pinned button went on
        /// showing the previous logo.
        /// </summary>
        public static List<string> LinksTo(string exe)
        {
            List<string> links = new List<string>();
            string full;
            try { full = Path.GetFullPath(exe); } catch { return links; }

            foreach (string link in new string[] { StartMenuLink(), DesktopLink() })
            {
                string target = ReadTarget(link);
                if (target != null && SamePath(target, full)) links.Add(link);
            }

            foreach (string pin in PinnedFolders())
            {
                try
                {
                    foreach (string link in Directory.GetFiles(pin, "*.lnk"))
                    {
                        string target = ReadTarget(link);
                        if (target != null && SamePath(target, full)) links.Add(link);
                    }
                }
                catch { }
            }
            return links;
        }

        static bool SamePath(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a), b, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        /// <summary>
        /// The pinned-taskbar folder of this profile and of every other profile on the machine.
        /// The installer runs elevated, so it can reach the other profiles too.
        /// </summary>
        static List<string> PinnedFolders()
        {
            List<string> folders = new List<string>();
            List<string> roaming = new List<string>();

            try
            {
                string own = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (!string.IsNullOrEmpty(own)) roaming.Add(own);
            }
            catch { }

            try
            {
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string users = string.IsNullOrEmpty(profile) ? null : Path.GetDirectoryName(profile);
                if (!string.IsNullOrEmpty(users) && Directory.Exists(users))
                {
                    foreach (string dir in Directory.GetDirectories(users))
                    {
                        try { roaming.Add(Path.Combine(dir, "AppData", "Roaming")); }
                        catch { }
                    }
                }
            }
            catch { }

            foreach (string root in roaming)
            {
                try
                {
                    string pin = Path.Combine(root,
                        @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
                    if (Directory.Exists(pin) && !folders.Contains(pin)) folders.Add(pin);
                }
                catch { }
            }
            return folders;
        }

        /// <summary>
        /// Rewrites every shortcut that points at this exe so it carries the current mark. Run by
        /// the installer after it has unpacked the files.
        /// </summary>
        public static void RepairLinks(string exe, string workingDir)
        {
            foreach (string link in LinksTo(exe))
            {
                try { CreateShortcut(link, exe, workingDir); }
                catch (Exception ex) { Log.Error("Repairing " + link + " failed", ex); }
            }
        }

        [DllImport("shell32.dll")]
        static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

        /// <summary>
        /// Tells the shell to forget the icons and thumbnails it has cached. Without this the
        /// notification banner and the taskbar can keep drawing the previous logo from memory
        /// long after the file on disk has changed.
        /// </summary>
        public static void RefreshShell()
        {
            try { SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero); }
            catch { }

            // Rebuilds the icon cache in place, without restarting Explorer. -show is the one
            // Windows 10 and 11 honour; -ClearIconCache is the older spelling.
            foreach (string arg in new string[] { "-show", "-ClearIconCache" })
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo("ie4uinit.exe", arg);
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    psi.WindowStyle = ProcessWindowStyle.Hidden;
                    using (Process p = Process.Start(psi))
                    {
                        if (p != null) p.WaitForExit(4000);
                    }
                }
                catch { }
            }
        }

        public static void RegisterUninstall(string targetDir, string exe)
        {
            try
            {
                long size = 0;
                foreach (string f in Directory.GetFiles(targetDir, "*", SearchOption.AllDirectories))
                {
                    try { size += new FileInfo(f).Length; }
                    catch { }
                }

                using (Microsoft.Win32.RegistryKey k =
                       Microsoft.Win32.Registry.LocalMachine.CreateSubKey(UninstallKey))
                {
                    if (k == null) return;
                    k.SetValue("DisplayName", AppName);
                    k.SetValue("DisplayVersion", Version);
                    k.SetValue("Publisher", AppName);
                    k.SetValue("InstallLocation", targetDir);
                    k.SetValue("DisplayIcon", IconLocation(exe, targetDir));
                    k.SetValue("UninstallString", "\"" + exe + "\" --uninstall");
                    k.SetValue("QuietUninstallString", "\"" + exe + "\" --uninstall --silent");
                    k.SetValue("EstimatedSize", (int)(size / 1024), Microsoft.Win32.RegistryValueKind.DWord);
                    k.SetValue("NoModify", 1, Microsoft.Win32.RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, Microsoft.Win32.RegistryValueKind.DWord);
                }
            }
            catch (Exception ex) { Log.Error("Registering the uninstall entry failed", ex); }
        }

        /// <summary>Removes the shortcuts and the Apps-and-Features entry.</summary>
        public static void Remove()
        {
            foreach (string link in new string[] { StartMenuLink(), DesktopLink() })
            {
                try { if (File.Exists(link)) File.Delete(link); }
                catch { }
            }
            try { Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false); }
            catch { }
            RefreshShell();
        }
    }
}
