using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace VMTun
{
    class ProxyCandidate
    {
        public string Host = "127.0.0.1";
        public int Port;
        public string ProcessName = "";
        public string ProcessPath = "";
        public string Kind = "";      // socks | http | unknown

        public override string ToString()
        {
            return Host + ":" + Port + "  (" + ProcessName + (Kind.Length > 0 ? ", " + Kind : "") + ")";
        }
    }

    /// <summary>
    /// Finds the local proxy that v2rayN (or any compatible client) exposes, by walking the
    /// TCP listener table and keeping the loopback listeners owned by a known proxy core.
    /// </summary>
    static class ProxyDetector
    {
        static readonly string[] KnownCores = { "xray", "v2rayN", "v2ray", "sing-box", "mihomo", "clash", "clash-verge", "hysteria", "nekoray", "Hiddify" };

        #region iphlpapi

        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen, bool sort,
            int ipVersion, int tableClass, int reserved);

        const int AF_INET = 2;
        const int TCP_TABLE_OWNER_PID_LISTENER = 3;

        [StructLayout(LayoutKind.Sequential)]
        struct MIB_TCPROW_OWNER_PID
        {
            public uint state;
            public uint localAddr;
            public uint localPort;   // port is in the low two bytes, network byte order
            public uint remoteAddr;
            public uint remotePort;
            public uint owningPid;
        }

        /// <summary>Returns every IPv4 TCP listener as (pid, endpoint).</summary>
        static List<KeyValuePair<int, IPEndPoint>> GetListeners()
        {
            List<KeyValuePair<int, IPEndPoint>> list = new List<KeyValuePair<int, IPEndPoint>>();
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
            if (size <= 0) return list;

            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buffer, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0) != 0)
                    return list;

                int count = Marshal.ReadInt32(buffer);
                IntPtr row = (IntPtr)((long)buffer + 4);
                int rowSize = Marshal.SizeOf(typeof(MIB_TCPROW_OWNER_PID));
                for (int i = 0; i < count; i++)
                {
                    MIB_TCPROW_OWNER_PID r = (MIB_TCPROW_OWNER_PID)Marshal.PtrToStructure(row, typeof(MIB_TCPROW_OWNER_PID));
                    int port = ((int)(r.localPort & 0xFF) << 8) | (int)((r.localPort >> 8) & 0xFF);
                    list.Add(new KeyValuePair<int, IPEndPoint>((int)r.owningPid, new IPEndPoint(new IPAddress(r.localAddr), port)));
                    row = (IntPtr)((long)row + rowSize);
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
            return list;
        }

        #endregion

        /// <summary>Loopback listeners owned by a recognised proxy core, probed for their protocol.</summary>
        public static List<ProxyCandidate> Discover()
        {
            List<ProxyCandidate> result = new List<ProxyCandidate>();
            Dictionary<int, Process> byPid = new Dictionary<int, Process>();
            foreach (Process p in ProcUtil.FindByName(KnownCores))
            {
                if (!byPid.ContainsKey(p.Id)) byPid[p.Id] = p;
            }
            if (byPid.Count == 0) return result;

            HashSet<int> seenPorts = new HashSet<int>();
            foreach (KeyValuePair<int, IPEndPoint> l in GetListeners())
            {
                Process owner;
                if (!byPid.TryGetValue(l.Key, out owner)) continue;
                if (!IPAddress.IsLoopback(l.Value.Address) && !l.Value.Address.Equals(IPAddress.Any)) continue;
                if (seenPorts.Contains(l.Value.Port)) continue;
                seenPorts.Add(l.Value.Port);

                ProxyCandidate c = new ProxyCandidate();
                c.Port = l.Value.Port;
                c.ProcessName = owner.ProcessName;
                try { c.ProcessPath = owner.MainModule.FileName; }
                catch { c.ProcessPath = ""; }
                c.Kind = Probe("127.0.0.1", c.Port);
                if (c.Kind != "unknown") result.Add(c);
            }

            result.Sort(delegate(ProxyCandidate a, ProxyCandidate b)
            {
                // A SOCKS listener is the better tunnel target, so surface it first.
                if (a.Kind != b.Kind) return a.Kind == "socks" ? -1 : 1;
                return a.Port.CompareTo(b.Port);
            });
            return result;
        }

        /// <summary>Last resort when no core is running: read the port v2rayN has configured.</summary>
        public static int ReadV2rayNConfiguredPort()
        {
            string[] candidates =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"v2rayN\guiConfigs\guiNConfig.json"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"v2rayN\guiConfigs\guiNConfig.json"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"v2rayN\guiConfigs\guiNConfig.json"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"v2rayN\guiConfigs\guiNConfig.json")
            };
            foreach (string f in candidates)
            {
                try
                {
                    if (!File.Exists(f)) continue;
                    Match m = Regex.Match(File.ReadAllText(f), "\"LocalPort\"\\s*:\\s*(\\d+)");
                    if (m.Success)
                    {
                        int port;
                        if (int.TryParse(m.Groups[1].Value, out port) && port > 0 && port < 65536)
                        {
                            Log.Info("Read proxy port " + port + " from " + f);
                            return port;
                        }
                    }
                }
                catch { }
            }
            return 0;
        }

        /// <summary>Speaks just enough of each protocol to tell a SOCKS5 port from an HTTP one.</summary>
        public static string Probe(string host, int port)
        {
            try
            {
                using (TcpClient tcp = new TcpClient())
                {
                    IAsyncResult ar = tcp.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(700)) return "unknown";
                    tcp.EndConnect(ar);
                    tcp.ReceiveTimeout = 700;
                    tcp.SendTimeout = 700;

                    NetworkStream s = tcp.GetStream();
                    // SOCKS5 greeting: version 5, one method, "no authentication".
                    s.Write(new byte[] { 0x05, 0x01, 0x00 }, 0, 3);
                    byte[] reply = new byte[2];
                    int read = s.Read(reply, 0, 2);
                    if (read == 2 && reply[0] == 0x05) return "socks";
                    if (read > 0 && reply[0] == (byte)'H') return "http";
                }
            }
            catch { }

            // A strict HTTP proxy closes or errors on the SOCKS bytes, so ask it a real question.
            try
            {
                using (TcpClient tcp = new TcpClient())
                {
                    IAsyncResult ar = tcp.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(700)) return "unknown";
                    tcp.EndConnect(ar);
                    tcp.ReceiveTimeout = 700;
                    tcp.SendTimeout = 700;
                    NetworkStream s = tcp.GetStream();
                    byte[] req = System.Text.Encoding.ASCII.GetBytes("GET http://example.com/ HTTP/1.1\r\nHost: example.com\r\nConnection: close\r\n\r\n");
                    s.Write(req, 0, req.Length);
                    byte[] reply = new byte[4];
                    int read = s.Read(reply, 0, 4);
                    if (read >= 4 && reply[0] == (byte)'H' && reply[1] == (byte)'T') return "http";
                }
            }
            catch { }

            return "unknown";
        }

        /// <summary>True when something is accepting connections on the endpoint.</summary>
        public static bool IsReachable(string host, int port, int timeoutMs)
        {
            try
            {
                using (TcpClient tcp = new TcpClient())
                {
                    IAsyncResult ar = tcp.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return false;
                    tcp.EndConnect(ar);
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>Full paths of proxy cores that must keep a direct route, or the tunnel would loop.</summary>
        public static List<string> RunningCorePaths()
        {
            List<string> paths = new List<string>();
            foreach (Process p in ProcUtil.FindByName(KnownCores))
            {
                try
                {
                    string path = p.MainModule.FileName;
                    if (!string.IsNullOrEmpty(path) && !paths.Contains(path)) paths.Add(path);
                }
                catch { /* a 32-bit or protected process may refuse MainModule */ }
            }
            return paths;
        }

        /// <summary>Executable names routed direct in the generated config.</summary>
        public static List<string> DirectProcessNames(Settings s)
        {
            List<string> names = new List<string>();
            foreach (string n in KnownCores)
            {
                string exe = n + ".exe";
                if (!names.Contains(exe)) names.Add(exe);
            }
            if (s != null && !string.IsNullOrEmpty(s.ExtraDirectProcesses))
            {
                foreach (string raw in s.ExtraDirectProcesses.Split(','))
                {
                    string n = raw.Trim();
                    if (n.Length == 0) continue;
                    if (!n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n += ".exe";
                    if (!names.Contains(n)) names.Add(n);
                }
            }
            return names;
        }
    }
}
