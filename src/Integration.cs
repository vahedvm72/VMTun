using System;
using System.IO;
using System.Reflection;

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
        public const string Version = "1.9";

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
                linkType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link, new object[] { exe + ",0" });
                linkType.InvokeMember("Description", BindingFlags.SetProperty, null, link,
                    new object[] { "VMTun \u2014 system-wide tunnel for a local V2Ray proxy" });
                linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            }
            catch (Exception ex) { Log.Error("Shortcut creation failed", ex); }
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
                    k.SetValue("DisplayIcon", exe + ",0");
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
        }
    }
}
