using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Web.Script.Serialization;

namespace VMTun
{
    /// <summary>One published release, as much of it as the updater cares about.</summary>
    class ReleaseInfo
    {
        public string Tag = "";
        public string Version = "";
        public string Notes = "";
        public string DownloadUrl = "";
        public long Size;
    }

    /// <summary>
    /// Checks GitHub for a newer release and installs it.
    ///
    /// Everything goes over an ordinary unproxied request, which means it travels through the
    /// tunnel whenever the tunnel is up. That is deliberate: where this app is useful, a direct
    /// request to GitHub is often the thing that does not work, and routing the check through
    /// the connection the app itself provides needs no extra plumbing and no HTTP proxy setting.
    /// When the tunnel is down and the direct attempt fails, the error says so plainly.
    /// </summary>
    static class Updater
    {
        public const string AssetName = "VMTun-Setup.exe";

        /// <summary>"owner/name", from the settings when overridden, otherwise compiled in.</summary>
        public static string RepoOf(Settings s)
        {
            if (s != null && !string.IsNullOrEmpty(s.UpdateRepo) && s.UpdateRepo.IndexOf('/') > 0)
                return s.UpdateRepo.Trim();
            return Integration.RepoOwner + "/" + Integration.RepoName;
        }

        static string ApiUrl(Settings s)
        {
            return "https://api.github.com/repos/" + RepoOf(s) + "/releases/latest";
        }

        public static string ReleasesPage(Settings s)
        {
            return "https://github.com/" + RepoOf(s) + "/releases";
        }

        /// <summary>False while the repository placeholder has not been filled in.</summary>
        public static bool Configured(Settings s)
        {
            string repo = RepoOf(s);
            return repo.IndexOf('/') > 0 && repo.IndexOf("__") < 0;
        }

        // ------------------------------------------------------------------ version compare

        /// <summary>
        /// Compares dotted numeric versions, ignoring a leading "v". Anything unparsable sorts
        /// as zero, so a malformed tag can never masquerade as an upgrade.
        /// </summary>
        public static bool IsNewer(string candidate, string current)
        {
            int[] a = Parse(candidate), b = Parse(current);
            for (int i = 0; i < 4; i++)
            {
                if (a[i] > b[i]) return true;
                if (a[i] < b[i]) return false;
            }
            return false;
        }

        static int[] Parse(string version)
        {
            int[] parts = new int[4];
            if (string.IsNullOrEmpty(version)) return parts;
            string v = version.Trim();
            if (v.StartsWith("v", StringComparison.OrdinalIgnoreCase)) v = v.Substring(1);
            int dash = v.IndexOfAny(new char[] { '-', '+' });
            if (dash > 0) v = v.Substring(0, dash);

            string[] bits = v.Split('.');
            for (int i = 0; i < bits.Length && i < 4; i++)
            {
                int n;
                if (int.TryParse(bits[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                    parts[i] = n;
            }
            return parts;
        }

        // ------------------------------------------------------------------ check

        public static ReleaseInfo CheckLatest(Settings s, out string error)
        {
            error = null;
            if (!Configured(s))
            {
                error = Lang.T("مخزن به‌روزرسانی تنظیم نشده است.",
                               "No update repository is configured.");
                return null;
            }

            try
            {
                string json = GetString(ApiUrl(s), 20000);
                JavaScriptSerializer js = new JavaScriptSerializer();
                js.MaxJsonLength = 8 * 1024 * 1024;
                Dictionary<string, object> root = js.DeserializeObject(json) as Dictionary<string, object>;
                if (root == null) { error = "unexpected response"; return null; }

                ReleaseInfo info = new ReleaseInfo();
                info.Tag = Str(root, "tag_name");
                info.Version = info.Tag.TrimStart('v', 'V');
                info.Notes = Str(root, "body");

                object assetsObj;
                if (root.TryGetValue("assets", out assetsObj) && assetsObj is object[])
                {
                    foreach (object o in (object[])assetsObj)
                    {
                        Dictionary<string, object> a = o as Dictionary<string, object>;
                        if (a == null) continue;
                        if (!string.Equals(Str(a, "name"), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
                        info.DownloadUrl = Str(a, "browser_download_url");
                        object size;
                        if (a.TryGetValue("size", out size)) info.Size = Convert.ToInt64(size);
                        break;
                    }
                }

                if (string.IsNullOrEmpty(info.Tag)) { error = "the release has no tag"; return null; }
                Log.Info("Latest release on GitHub: " + info.Tag +
                         (string.IsNullOrEmpty(info.DownloadUrl) ? " (no installer asset)" : ""));
                return info;
            }
            catch (Exception ex)
            {
                error = Describe(ex);
                Log.Warn("Update check failed: " + ex.Message);
                return null;
            }
        }

        static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            if (d.TryGetValue(key, out v) && v != null) return v.ToString();
            return "";
        }

        static string Describe(Exception ex)
        {
            return Lang.T(
                "به گیت‌هاب نمی‌رسد (" + ex.Message + "). اگر تونل وصل نیست، اول وصل شوید.",
                "Could not reach GitHub (" + ex.Message + "). If the tunnel is down, connect first.");
        }

        // ------------------------------------------------------------------ transfer

        static HttpWebRequest Request(string url, int timeoutMs)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Proxy = null;                 // no system proxy: ride the tunnel, not a stale setting
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.UserAgent = "VMTun/" + Integration.Version;
            req.Accept = "application/vnd.github+json";
            return req;
        }

        static string GetString(string url, int timeoutMs)
        {
            using (WebResponse resp = Request(url, timeoutMs).GetResponse())
            using (StreamReader sr = new StreamReader(resp.GetResponseStream()))
                return sr.ReadToEnd();
        }

        /// <summary>
        /// Downloads the installer to a temp file. Progress is reported as a percentage, or -1
        /// while the total size is unknown.
        /// </summary>
        public static string Download(ReleaseInfo release, Action<int> progress, out string error)
        {
            error = null;
            if (release == null || string.IsNullOrEmpty(release.DownloadUrl))
            {
                error = Lang.T("این نسخه فایل نصب ندارد.", "That release has no installer attached.");
                return null;
            }

            string path = Path.Combine(Path.GetTempPath(), "VMTun-Setup-" + release.Version + ".exe");
            try
            {
                HttpWebRequest req = Request(release.DownloadUrl, 60000);
                using (WebResponse resp = req.GetResponse())
                using (Stream src = resp.GetResponseStream())
                using (FileStream dst = File.Create(path))
                {
                    long total = release.Size > 0 ? release.Size : resp.ContentLength;
                    long got = 0;
                    int lastPercent = -1;
                    byte[] buffer = new byte[128 * 1024];
                    int n;
                    while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        dst.Write(buffer, 0, n);
                        got += n;
                        if (progress == null || total <= 0) continue;
                        int percent = (int)(100 * got / total);
                        if (percent == lastPercent) continue;
                        lastPercent = percent;
                        progress(percent);
                    }

                    if (total > 0 && got != total)
                    {
                        error = Lang.T("دانلود ناقص ماند.", "The download was cut short.");
                        return null;
                    }
                }

                // A truncated or error-page download would otherwise be handed to the shell.
                FileInfo fi = new FileInfo(path);
                if (fi.Length < 1024 * 1024)
                {
                    error = Lang.T("فایل دانلودشده معتبر نیست.", "The downloaded file is not a valid installer.");
                    return null;
                }

                Log.Info("Downloaded " + release.Tag + " to " + path + " (" + fi.Length + " bytes)");
                return path;
            }
            catch (Exception ex)
            {
                error = Describe(ex);
                try { if (File.Exists(path)) File.Delete(path); }
                catch { }
                return null;
            }
        }

        /// <summary>
        /// Hands the machine over to the downloaded installer. It stops this copy itself, so
        /// there is nothing to do afterwards but exit.
        /// </summary>
        public static bool Launch(string installerPath, out string error)
        {
            error = null;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(installerPath);
                psi.Arguments = "--silent /D=" + AppPaths.ExeDir;
                psi.UseShellExecute = true;
                psi.Verb = "runas";           // already elevated, but be explicit
                Process.Start(psi);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
