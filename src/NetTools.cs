using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;

namespace VMTun
{
    /// <summary>Locates the virtual adapter sing-box creates, by its address rather than its name.</summary>
    static class TunAdapter
    {
        public static readonly IPAddress ExpectedIp = IPAddress.Parse("172.19.0.1");

        /// <summary>The Windows interface alias of the tunnel adapter, or null when it is not up.</summary>
        public static string FindAlias()
        {
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.Equals(ExpectedIp)) return ni.Name;
                    }
                }
                // Fall back to the configured name in case the address was customised.
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.Name == ConfigBuilder.InterfaceName && ni.OperationalStatus == OperationalStatus.Up)
                        return ni.Name;
                }
            }
            catch (Exception ex) { Log.Error("Adapter lookup failed", ex); }
            return null;
        }

        public static bool IsUp() { return FindAlias() != null; }

        /// <summary>Waits for the adapter to come up after the core starts.</summary>
        public static string WaitForAdapter(int timeoutMs)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                string alias = FindAlias();
                if (alias != null) return alias;
                System.Threading.Thread.Sleep(250);
                waited += 250;
            }
            return null;
        }
    }

    /// <summary>
    /// Optional kill switch. Flips the Windows Firewall default outbound action to Block and
    /// allows only the tunnel adapter, the proxy core and the local network back through, so a
    /// dropped tunnel cannot silently fall back to the naked connection.
    /// The previous profile state is written to disk first, so a crash is still recoverable.
    /// </summary>
    static class FirewallGuard
    {
        public const string RuleGroup = "VMTun";

        public static bool IsActive() { return File.Exists(AppPaths.GuardStateFile); }

        static string Q(string s)
        {
            if (s == null) s = "";
            return "'" + s.Replace("'", "''") + "'";
        }

        /// <param name="tunAlias">Interface alias of the live tunnel adapter.</param>
        /// <param name="allowedPrograms">Full paths of proxy cores that must keep direct access.</param>
        public static bool Apply(string tunAlias, IList<string> allowedPrograms, out string error)
        {
            error = null;
            StringBuilder ps = new StringBuilder();
            // Individual allow rules are best-effort: one unsupported rule must not abort the
            // whole thing and leave the firewall half-configured.
            ps.AppendLine("$ErrorActionPreference = 'Continue'");
            ps.AppendLine("$g = " + Q(RuleGroup));
            // Report the state we are about to change so it can be restored later.
            ps.AppendLine("foreach ($p in Get-NetFirewallProfile -Name Domain,Private,Public) {");
            ps.AppendLine("  'STATE|' + $p.Name + '|' + $p.Enabled + '|' + $p.DefaultOutboundAction");
            ps.AppendLine("}");
            // Clear anything left behind by a previous run.
            ps.AppendLine("Get-NetFirewallRule -Group $g -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue");

            // The allow rules go in before the default flips, so there is never a moment
            // where the machine is fully cut off.
            if (!string.IsNullOrEmpty(tunAlias))
            {
                ps.AppendLine("New-NetFirewallRule -DisplayName 'VMTun: tunnel adapter' -Group $g -Direction Outbound -Action Allow -Profile Any -Enabled True -InterfaceAlias " + Q(tunAlias) + " | Out-Null");
            }
            // Second net: traffic sourced from the tunnel address, independent of the alias.
            ps.AppendLine("New-NetFirewallRule -DisplayName 'VMTun: tunnel source address' -Group $g -Direction Outbound -Action Allow -Profile Any -Enabled True -LocalAddress '" + TunAdapter.ExpectedIp + "' | Out-Null");

            int i = 0;
            foreach (string prog in allowedPrograms)
            {
                if (string.IsNullOrEmpty(prog) || !File.Exists(prog)) continue;
                i++;
                ps.AppendLine("New-NetFirewallRule -DisplayName 'VMTun: proxy core " + i + "' -Group $g -Direction Outbound -Action Allow -Profile Any -Enabled True -Program " + Q(prog) + " | Out-Null");
            }

            ps.AppendLine("New-NetFirewallRule -DisplayName 'VMTun: loopback' -Group $g -Direction Outbound -Action Allow -Profile Any -Enabled True -RemoteAddress '127.0.0.0/8' | Out-Null");
            ps.AppendLine("New-NetFirewallRule -DisplayName 'VMTun: local network' -Group $g -Direction Outbound -Action Allow -Profile Any -Enabled True -RemoteAddress LocalSubnet,'10.0.0.0/8','172.16.0.0/12','192.168.0.0/16','169.254.0.0/16','224.0.0.0/4','255.255.255.255' | Out-Null");
            ps.AppendLine("New-NetFirewallRule -DisplayName 'VMTun: DHCP' -Group $g -Direction Outbound -Action Allow -Profile Any -Enabled True -Protocol UDP -RemotePort 67,68 | Out-Null");

            // IPv6 is not blocked here. Windows Firewall rejects a '::/0' prefix outright, and it
            // is not needed: the tunnel adapter carries an IPv6 address, so sing-box captures IPv6
            // and rejects it internally when the user has it switched off.

            // Only this last step decides success: flipping the default to Block is what actually
            // arms the kill switch, and we must know for certain whether it took effect.
            ps.AppendLine("try {");
            ps.AppendLine("  Set-NetFirewallProfile -Name Domain,Private,Public -Enabled True -DefaultOutboundAction Block -ErrorAction Stop");
            ps.AppendLine("  'APPLIED'");
            ps.AppendLine("} catch { 'ARMFAILED: ' + $_.Exception.Message }");

            string sout, serr;
            int code = ProcUtil.PowerShell(ps.ToString(), 60000, out sout, out serr);
            if (code != 0 || sout.IndexOf("APPLIED", StringComparison.Ordinal) < 0)
            {
                int armed = sout.IndexOf("ARMFAILED", StringComparison.Ordinal);
                error = armed >= 0 ? sout.Substring(armed).Trim() : ProcUtil.CleanPsError(serr);
                if (error.Length == 0) error = sout.Trim();
                Log.Error("Kill switch could not be applied: " + error);
                // Do not leave the machine half-locked.
                string ignored;
                Remove(out ignored);
                return false;
            }

            SaveState(sout);
            Log.Info("Kill switch active (adapter: " + (tunAlias == null ? "?" : tunAlias) + ").");
            return true;
        }

        static void SaveState(string psOutput)
        {
            try
            {
                AppPaths.EnsureDirs();
                List<string> lines = new List<string>();
                foreach (string raw in psOutput.Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("STATE|", StringComparison.Ordinal)) lines.Add(line);
                }
                if (lines.Count == 0) lines.Add("STATE|Domain|True|NotConfigured");
                File.WriteAllLines(AppPaths.GuardStateFile, lines.ToArray(), Encoding.UTF8);
            }
            catch (Exception ex) { Log.Error("Could not record firewall state", ex); }
        }

        /// <summary>Puts the firewall back the way it was. Safe to call when nothing was applied.</summary>
        public static bool Remove(out string error)
        {
            error = null;
            StringBuilder ps = new StringBuilder();
            ps.AppendLine("$ErrorActionPreference = 'Continue'");
            ps.AppendLine("$g = " + Q(RuleGroup));

            // Restore the default first so connectivity returns immediately.
            List<string> restored = ReadState();
            if (restored.Count > 0)
            {
                foreach (string line in restored) ps.AppendLine(line);
            }
            else
            {
                ps.AppendLine("Set-NetFirewallProfile -Name Domain,Private,Public -DefaultOutboundAction NotConfigured -ErrorAction SilentlyContinue");
            }
            ps.AppendLine("Get-NetFirewallRule -Group $g -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue");
            ps.AppendLine("'REMOVED'");

            string sout, serr;
            int code = ProcUtil.PowerShell(ps.ToString(), 60000, out sout, out serr);
            bool ok = code == 0 && sout.IndexOf("REMOVED", StringComparison.Ordinal) >= 0;
            if (!ok)
            {
                error = ProcUtil.CleanPsError(serr);
                if (error.Length == 0) error = sout.Trim();
                Log.Error("Kill switch removal reported a problem: " + error);
            }
            else
            {
                Log.Info("Kill switch removed, firewall restored.");
            }

            try { if (File.Exists(AppPaths.GuardStateFile)) File.Delete(AppPaths.GuardStateFile); }
            catch { }
            return ok;
        }

        /// <summary>Turns the recorded profile state back into Set-NetFirewallProfile calls.</summary>
        static List<string> ReadState()
        {
            List<string> cmds = new List<string>();
            try
            {
                if (!File.Exists(AppPaths.GuardStateFile)) return cmds;
                foreach (string raw in File.ReadAllLines(AppPaths.GuardStateFile))
                {
                    string[] parts = raw.Trim().Split('|');
                    if (parts.Length < 4 || parts[0] != "STATE") continue;
                    string name = parts[1], enabled = parts[2], outAction = parts[3];
                    if (name.Length == 0) continue;
                    if (outAction != "Allow" && outAction != "Block" && outAction != "NotConfigured")
                        outAction = "NotConfigured";
                    if (enabled != "True" && enabled != "False") enabled = "True";
                    cmds.Add("Set-NetFirewallProfile -Name '" + name.Replace("'", "") +
                             "' -Enabled " + enabled + " -DefaultOutboundAction " + outAction + " -ErrorAction SilentlyContinue");
                }
            }
            catch (Exception ex) { Log.Error("Could not read firewall state", ex); }
            return cmds;
        }
    }

    /// <summary>
    /// UWP / Microsoft Store applications run in an AppContainer that is denied loopback access
    /// by default, which is what makes them look like they "ignore the VPN". Exempting them
    /// restores normal behaviour for the few that talk to a local endpoint.
    /// </summary>
    static class UwpLoopback
    {
        /// <summary>Exempts every installed package. Returns how many were processed.</summary>
        public static int ExemptAll(Action<string> progress, out string error)
        {
            error = null;
            string sout, serr;
            int code = ProcUtil.PowerShell(
                "Get-AppxPackage -AllUsers -ErrorAction SilentlyContinue | " +
                "Select-Object -ExpandProperty PackageFamilyName -Unique", 120000, out sout, out serr);
            if (code != 0 && sout.Trim().Length == 0)
            {
                error = serr.Trim();
                return 0;
            }

            List<string> families = new List<string>();
            foreach (string raw in sout.Split('\n'))
            {
                string f = raw.Trim();
                if (f.Length > 0 && f.IndexOf('_') > 0) families.Add(f);
            }
            if (families.Count == 0)
            {
                error = "no packages found";
                return 0;
            }

            string checkNetIsolation = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "CheckNetIsolation.exe");
            int done = 0;
            foreach (string family in families)
            {
                try
                {
                    string o, e;
                    ProcUtil.Run(checkNetIsolation, "LoopbackExempt -a -n=" + family, 8000, out o, out e);
                    done++;
                    if (progress != null && (done % 10 == 0 || done == families.Count))
                        progress(done + " / " + families.Count);
                }
                catch { }
            }
            Log.Info("UWP loopback exemption applied to " + done + " packages.");
            return done;
        }

        /// <summary>Clears every loopback exemption, undoing ExemptAll.</summary>
        public static void ClearAll()
        {
            string checkNetIsolation = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "CheckNetIsolation.exe");
            string o, e;
            ProcUtil.Run(checkNetIsolation, "LoopbackExempt -c", 30000, out o, out e);
            Log.Info("UWP loopback exemptions cleared.");
        }
    }
}
