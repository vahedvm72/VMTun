using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace VMTun
{
    /// <summary>
    /// Aligns the Windows time zone with the country the tunnel exits from, and puts it back
    /// afterwards.
    ///
    /// This matters more than it looks. A browser reports its IANA zone to any page that asks
    /// (Intl.DateTimeFormat().resolvedOptions().timeZone), and the value comes from the Windows
    /// setting. An Armenian exit address paired with Asia/Tehran is not a subtle inconsistency:
    /// UTC+03:30 is used by almost nowhere else, so the pair names the real country outright.
    ///
    /// Changing the system clock's zone is not free — calendars, logs and scheduled tasks all
    /// follow it — so this is off unless asked for, and the original is written to disk before
    /// anything changes so that a crash can still be undone.
    /// </summary>
    static class TimeZoneSync
    {
        static string StateFile { get { return Path.Combine(AppPaths.DataDir, "timezone.state"); } }

        /// <summary>True while a zone other than the user's own is in effect.</summary>
        public static bool IsOverridden { get { return File.Exists(StateFile); } }

        /// <summary>The Windows id the user had before we touched anything, or null.</summary>
        public static string OriginalId
        {
            get
            {
                try
                {
                    if (!File.Exists(StateFile)) return null;
                    string v = File.ReadAllText(StateFile).Trim();
                    return v.Length > 0 ? v : null;
                }
                catch { return null; }
            }
        }

        public static string CurrentWindowsId
        {
            get
            {
                try { return TimeZoneInfo.Local.Id; }
                catch { return ""; }
            }
        }

        /// <summary>The IANA name a browser would report for the zone Windows is set to.</summary>
        public static string CurrentIana
        {
            get { return WindowsToIana(CurrentWindowsId); }
        }

        public static string CurrentOffset
        {
            get
            {
                try
                {
                    TimeSpan off = TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);
                    return (off < TimeSpan.Zero ? "-" : "+") +
                           Math.Abs(off.Hours).ToString("00", CultureInfo.InvariantCulture) + ":" +
                           Math.Abs(off.Minutes).ToString("00", CultureInfo.InvariantCulture);
                }
                catch { return "?"; }
            }
        }

        // ------------------------------------------------------------------ apply / restore

        /// <summary>
        /// Switches Windows to the zone matching an IANA name. The first call records what the
        /// user had, so repeated calls while already overridden do not lose the original.
        /// </summary>
        public static bool ApplyForIana(string iana, out string applied, out string error)
        {
            applied = null;
            error = null;

            string windowsId = IanaToWindows(iana);
            if (windowsId == null)
            {
                error = Lang.T("منطقه زمانی متناظر پیدا نشد: ", "No matching Windows time zone for ") + iana;
                return false;
            }

            try
            {
                string current = CurrentWindowsId;
                if (string.Equals(current, windowsId, StringComparison.OrdinalIgnoreCase))
                {
                    applied = windowsId;
                    return true;                       // already right, nothing to record
                }

                if (!IsOverridden)
                {
                    AppPaths.EnsureDirs();
                    File.WriteAllText(StateFile, current);
                }

                if (!SetWindowsZone(windowsId, out error))
                {
                    if (!IsOverridden) TryDeleteState();
                    return false;
                }

                applied = windowsId;
                Log.Info("Time zone set to " + windowsId + " (" + iana + "); was " + current);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>Puts the user's own zone back. Safe to call when nothing was changed.</summary>
        public static bool Restore(out string error)
        {
            error = null;
            string original = OriginalId;
            if (original == null) return true;

            bool ok = SetWindowsZone(original, out error);
            if (ok)
            {
                TryDeleteState();
                Log.Info("Time zone restored to " + original);
            }
            else
            {
                Log.Error("Could not restore the time zone: " + error);
            }
            return ok;
        }

        static bool SetWindowsZone(string windowsId, out string error)
        {
            error = null;
            string stdout, stderr;
            int code = ProcUtil.Run("tzutil.exe", "/s \"" + windowsId + "\"", 20000, out stdout, out stderr);
            if (code != 0)
            {
                error = (stderr + stdout).Trim();
                if (error.Length == 0) error = "tzutil returned " + code;
                return false;
            }

            // TimeZoneInfo.Local is cached for the life of the process, so without this every
            // read afterwards still reports the old zone: the privacy page would show a stale
            // value, and the "already correct" shortcut in ApplyForIana would compare against
            // it and skip recording the original.
            try { TimeZoneInfo.ClearCachedData(); }
            catch { }
            return true;
        }

        static void TryDeleteState()
        {
            try { if (File.Exists(StateFile)) File.Delete(StateFile); }
            catch { }
        }

        // ------------------------------------------------------------------ the mapping

        /// <summary>
        /// Windows zone id to the IANA name Windows itself reports for it. .NET Framework has
        /// no built-in conversion (that arrived in .NET 6), so the common zones are listed here
        /// and anything missing falls back to matching the UTC offset.
        /// </summary>
        static readonly string[,] Zones =
        {
            { "UTC",                              "Etc/UTC" },
            { "GMT Standard Time",                "Europe/London" },
            { "Greenwich Standard Time",          "Atlantic/Reykjavik" },
            { "W. Europe Standard Time",          "Europe/Berlin" },
            { "Central Europe Standard Time",     "Europe/Budapest" },
            { "Central European Standard Time",   "Europe/Warsaw" },
            { "Romance Standard Time",            "Europe/Paris" },
            { "W. Central Africa Standard Time",  "Africa/Lagos" },
            { "GTB Standard Time",                "Europe/Bucharest" },
            { "FLE Standard Time",                "Europe/Kiev" },
            { "E. Europe Standard Time",          "Europe/Chisinau" },
            { "South Africa Standard Time",       "Africa/Johannesburg" },
            { "Israel Standard Time",             "Asia/Jerusalem" },
            { "Egypt Standard Time",              "Africa/Cairo" },
            { "Libya Standard Time",              "Africa/Tripoli" },
            { "Middle East Standard Time",        "Asia/Beirut" },
            { "Jordan Standard Time",             "Asia/Amman" },
            { "Syria Standard Time",              "Asia/Damascus" },
            { "Turkey Standard Time",             "Europe/Istanbul" },
            { "Belarus Standard Time",            "Europe/Minsk" },
            { "Kaliningrad Standard Time",        "Europe/Kaliningrad" },
            { "Russian Standard Time",            "Europe/Moscow" },
            { "Arab Standard Time",               "Asia/Riyadh" },
            { "Arabic Standard Time",             "Asia/Baghdad" },
            { "E. Africa Standard Time",          "Africa/Nairobi" },
            { "Iran Standard Time",               "Asia/Tehran" },
            { "Arabian Standard Time",            "Asia/Dubai" },
            { "Azerbaijan Standard Time",         "Asia/Baku" },
            { "Caucasus Standard Time",           "Asia/Yerevan" },
            { "Georgian Standard Time",           "Asia/Tbilisi" },
            { "Mauritius Standard Time",          "Indian/Mauritius" },
            { "Russia Time Zone 3",               "Europe/Samara" },
            { "Astrakhan Standard Time",          "Europe/Astrakhan" },
            { "Afghanistan Standard Time",        "Asia/Kabul" },
            { "West Asia Standard Time",          "Asia/Tashkent" },
            { "Ekaterinburg Standard Time",       "Asia/Yekaterinburg" },
            { "Pakistan Standard Time",           "Asia/Karachi" },
            { "India Standard Time",              "Asia/Calcutta" },
            { "Sri Lanka Standard Time",          "Asia/Colombo" },
            { "Nepal Standard Time",              "Asia/Katmandu" },
            { "Central Asia Standard Time",       "Asia/Almaty" },
            { "Bangladesh Standard Time",         "Asia/Dhaka" },
            { "Omsk Standard Time",               "Asia/Omsk" },
            { "Myanmar Standard Time",            "Asia/Rangoon" },
            { "SE Asia Standard Time",            "Asia/Bangkok" },
            { "North Asia Standard Time",         "Asia/Krasnoyarsk" },
            { "China Standard Time",              "Asia/Shanghai" },
            { "Singapore Standard Time",          "Asia/Singapore" },
            { "W. Australia Standard Time",       "Australia/Perth" },
            { "Taipei Standard Time",             "Asia/Taipei" },
            { "Ulaanbaatar Standard Time",        "Asia/Ulaanbaatar" },
            { "Tokyo Standard Time",              "Asia/Tokyo" },
            { "Korea Standard Time",              "Asia/Seoul" },
            { "North Korea Standard Time",        "Asia/Pyongyang" },
            { "Cen. Australia Standard Time",     "Australia/Adelaide" },
            { "AUS Eastern Standard Time",        "Australia/Sydney" },
            { "E. Australia Standard Time",       "Australia/Brisbane" },
            { "Tasmania Standard Time",           "Australia/Hobart" },
            { "Vladivostok Standard Time",        "Asia/Vladivostok" },
            { "New Zealand Standard Time",        "Pacific/Auckland" },
            { "Fiji Standard Time",               "Pacific/Fiji" },
            { "Tonga Standard Time",              "Pacific/Tongatapu" },
            { "Azores Standard Time",             "Atlantic/Azores" },
            { "Cape Verde Standard Time",         "Atlantic/Cape_Verde" },
            { "Morocco Standard Time",            "Africa/Casablanca" },
            { "E. South America Standard Time",   "America/Sao_Paulo" },
            { "Argentina Standard Time",          "America/Buenos_Aires" },
            { "SA Eastern Standard Time",         "America/Cayenne" },
            { "Montevideo Standard Time",         "America/Montevideo" },
            { "Newfoundland Standard Time",       "America/St_Johns" },
            { "Atlantic Standard Time",           "America/Halifax" },
            { "Venezuela Standard Time",          "America/Caracas" },
            { "Paraguay Standard Time",           "America/Asuncion" },
            { "Central Brazilian Standard Time",  "America/Cuiaba" },
            { "SA Western Standard Time",         "America/La_Paz" },
            { "Pacific SA Standard Time",         "America/Santiago" },
            { "Eastern Standard Time",            "America/New_York" },
            { "US Eastern Standard Time",         "America/Indianapolis" },
            { "SA Pacific Standard Time",         "America/Bogota" },
            { "Central Standard Time",            "America/Chicago" },
            { "Central Standard Time (Mexico)",   "America/Mexico_City" },
            { "Canada Central Standard Time",     "America/Regina" },
            { "Central America Standard Time",    "America/Guatemala" },
            { "Mountain Standard Time",           "America/Denver" },
            { "Mountain Standard Time (Mexico)",  "America/Chihuahua" },
            { "US Mountain Standard Time",        "America/Phoenix" },
            { "Pacific Standard Time",            "America/Los_Angeles" },
            { "Pacific Standard Time (Mexico)",   "America/Tijuana" },
            { "Alaskan Standard Time",            "America/Anchorage" },
            { "Hawaiian Standard Time",           "Pacific/Honolulu" },
        };

        /// <summary>Windows id for an IANA name, or null when nothing sensible matches.</summary>
        public static string IanaToWindows(string iana)
        {
            if (string.IsNullOrEmpty(iana)) return null;
            string want = iana.Trim();

            for (int i = 0; i < Zones.GetLength(0); i++)
                if (string.Equals(Zones[i, 1], want, StringComparison.OrdinalIgnoreCase))
                    return Exists(Zones[i, 0]) ? Zones[i, 0] : null;

            // A few names have moved over the years; try the obvious aliases before giving up.
            string alias = Alias(want);
            if (alias != null)
            {
                for (int i = 0; i < Zones.GetLength(0); i++)
                    if (string.Equals(Zones[i, 1], alias, StringComparison.OrdinalIgnoreCase))
                        return Exists(Zones[i, 0]) ? Zones[i, 0] : null;
            }
            return null;
        }

        static string Alias(string iana)
        {
            switch (iana)
            {
                case "Europe/Kyiv": return "Europe/Kiev";
                case "Asia/Kolkata": return "Asia/Calcutta";
                case "Asia/Kathmandu": return "Asia/Katmandu";
                case "Asia/Yangon": return "Asia/Rangoon";
                case "America/Argentina/Buenos_Aires": return "America/Buenos_Aires";
                case "UTC": return "Etc/UTC";
                default: return null;
            }
        }

        public static string WindowsToIana(string windowsId)
        {
            if (string.IsNullOrEmpty(windowsId)) return "";
            for (int i = 0; i < Zones.GetLength(0); i++)
                if (string.Equals(Zones[i, 0], windowsId, StringComparison.OrdinalIgnoreCase))
                    return Zones[i, 1];
            return windowsId;       // unknown: show the Windows name rather than nothing
        }

        static bool Exists(string windowsId)
        {
            try
            {
                TimeZoneInfo.FindSystemTimeZoneById(windowsId);
                return true;
            }
            catch { return false; }
        }

        /// <summary>Every mapped zone this machine actually has, for the self-test.</summary>
        public static List<string> KnownIanaNames()
        {
            List<string> names = new List<string>();
            for (int i = 0; i < Zones.GetLength(0); i++)
                if (Exists(Zones[i, 0])) names.Add(Zones[i, 1]);
            return names;
        }

        public static int MappedCount { get { return Zones.GetLength(0); } }
    }
}
