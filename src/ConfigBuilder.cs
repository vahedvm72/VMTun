using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VMTun
{
    /// <summary>
    /// Produces the sing-box configuration that turns the local V2Ray/Xray proxy into a
    /// system-wide tunnel. Written by hand rather than with a serializer so the output stays
    /// readable and matches the sing-box 1.12+ schema exactly.
    /// </summary>
    static class ConfigBuilder
    {
        /// <summary>Name of the virtual adapter as Windows will show it.</summary>
        public const string InterfaceName = "VMTun";

        public const string Ipv4Address = "172.19.0.1/30";
        public const string Ipv6Address = "fdfe:dcba:9876::1/126";

        const string GeoipIrUrl = "https://raw.githubusercontent.com/Chocolate4U/Iran-sing-box-rules/rule-set/geoip-ir.srs";
        const string GeositeIrUrl = "https://raw.githubusercontent.com/Chocolate4U/Iran-sing-box-rules/rule-set/geosite-ir.srs";

        static string Esc(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        static string StrArray(IList<string> items)
        {
            StringBuilder sb = new StringBuilder("[");
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append('"').Append(Esc(items[i])).Append('"');
            }
            return sb.Append(']').ToString();
        }

        static string N(int v) { return v.ToString(CultureInfo.InvariantCulture); }

        /// <summary>The upstream resolver, reached through the proxy over the chosen transport.</summary>
        static string RemoteDnsServer(Settings s)
        {
            string type;
            switch (s.DnsMode)
            {
                case "dot": type = "tls"; break;    // DNS over TLS, TCP 853
                case "tcp": type = "tcp"; break;    // plain DNS over TCP 53
                case "udp": type = "udp"; break;    // needs UDP relay on the server
                default: type = "https"; break;     // DNS over HTTPS, TCP 443
            }
            return "{ \"type\": \"" + type + "\", \"tag\": \"dns-remote\", \"server\": \"" +
                   Esc(s.RemoteDns) + "\", \"detour\": \"proxy\" }";
        }

        /// <summary>A human-readable description of the DNS transport, for the UI.</summary>
        public static string DescribeDns(Settings s)
        {
            switch (s.DnsMode)
            {
                case "dot": return "DoT / TCP 853";
                case "tcp": return "TCP 53";
                case "udp": return "UDP 53";
                default: return "DoH / TCP 443";
            }
        }

        public static string Build(Settings s)
        {
            bool iran = s.Routing == RoutingMode.IranDirect;
            bool v6 = s.EnableIpv6;
            List<string> directProcs = ProxyDetector.DirectProcessNames(s);

            StringBuilder b = new StringBuilder();
            b.AppendLine("{");

            // ---- log ------------------------------------------------------------------
            // "info" prints a line for every connection the core handles. That is useful when
            // diagnosing and ruinous otherwise: it floods the log file and the UI alike.
            b.AppendLine("  \"log\": { \"level\": \"" + (s.VerboseCoreLog ? "info" : "warn") +
                         "\", \"timestamp\": true },");

            // ---- dns ------------------------------------------------------------------
            // Queries from the system are hijacked off the TUN and answered through the proxy,
            // so nothing resolves over the physical adapter and there is no DNS leak.
            //
            // The transport matters: plain UDP requires the upstream server to relay UDP, and a
            // great many VLESS/VMess-over-WebSocket servers do not. When that is the case every
            // lookup silently times out and the tunnel looks dead even though TCP flows fine, so
            // a TCP-based transport (DoH by default) is used instead.
            b.AppendLine("  \"dns\": {");
            b.AppendLine("    \"servers\": [");
            b.AppendLine("      " + RemoteDnsServer(s) + ",");
            b.AppendLine("      { \"type\": \"local\", \"tag\": \"dns-local\" }");
            b.AppendLine("    ],");
            if (iran)
            {
                // Iranian names are resolved by the ISP resolver so they get local CDN answers.
                b.AppendLine("    \"rules\": [");
                b.AppendLine("      { \"rule_set\": [ \"geosite-ir\" ], \"server\": \"dns-local\" },");
                b.AppendLine("      { \"domain_suffix\": [ \".ir\" ], \"server\": \"dns-local\" }");
                b.AppendLine("    ],");
            }
            b.AppendLine("    \"final\": \"dns-remote\",");
            b.AppendLine("    \"strategy\": \"" + (v6 ? "prefer_ipv4" : "ipv4_only") + "\"");
            b.AppendLine("  },");

            // ---- inbound: the TUN adapter ---------------------------------------------
            // The adapter claims an IPv6 address only when IPv6 is switched on. Claiming it and
            // then rejecting IPv6 inside the core looks tidy but behaves terribly: Windows then
            // believes IPv6 works, applications with their own resolver (Chrome's DoH ignores
            // our ipv4_only strategy) keep opening IPv6 connections, each is refused instantly,
            // and they retry in a tight loop that floods the log and stalls the machine.
            // Without the address there is simply no IPv6 route into the tunnel, and the OS
            // fails those attempts immediately so applications fall back to IPv4 at once.
            List<string> addrs = new List<string>();
            addrs.Add(Ipv4Address);
            if (v6) addrs.Add(Ipv6Address);

            b.AppendLine("  \"inbounds\": [");
            b.AppendLine("    {");
            b.AppendLine("      \"type\": \"tun\",");
            b.AppendLine("      \"tag\": \"tun-in\",");
            b.AppendLine("      \"interface_name\": \"" + Esc(InterfaceName) + "\",");
            b.AppendLine("      \"address\": " + StrArray(addrs) + ",");
            b.AppendLine("      \"mtu\": " + N(s.Mtu) + ",");
            // auto_route installs the default routes; strict_route adds the WFP filters that stop
            // an application from reaching the network around the tunnel (this is what fixes UWP).
            b.AppendLine("      \"auto_route\": true,");
            b.AppendLine("      \"strict_route\": true,");
            b.AppendLine("      \"stack\": \"" + Esc(s.Stack) + "\"");
            b.AppendLine("    }");
            b.AppendLine("  ],");

            // ---- outbounds -------------------------------------------------------------
            b.AppendLine("  \"outbounds\": [");
            if (s.ProxyType == "http")
            {
                b.AppendLine("    { \"type\": \"http\", \"tag\": \"proxy\", \"server\": \"" + Esc(s.ProxyHost) + "\", \"server_port\": " + N(s.ProxyPort) + " },");
            }
            else
            {
                b.AppendLine("    { \"type\": \"socks\", \"tag\": \"proxy\", \"server\": \"" + Esc(s.ProxyHost) + "\", \"server_port\": " + N(s.ProxyPort) + ", \"version\": \"5\" },");
            }
            b.AppendLine("    { \"type\": \"direct\", \"tag\": \"direct\" }");
            b.AppendLine("  ],");

            // ---- route -----------------------------------------------------------------
            b.AppendLine("  \"route\": {");
            // Binds the direct outbound to the real adapter, so it is not swallowed by our own
            // default route. Without this the proxy core's own traffic would loop back into TUN.
            b.AppendLine("    \"auto_detect_interface\": true,");
            b.AppendLine("    \"default_domain_resolver\": { \"server\": \"dns-local\" },");
            // Built as a list so adding or removing a rule can never break the JSON commas.
            List<string> rules = new List<string>();
            rules.Add("{ \"action\": \"sniff\" }");
            rules.Add("{ \"protocol\": \"dns\", \"action\": \"hijack-dns\" }");
            // The proxy core itself must never enter the tunnel it feeds.
            rules.Add("{ \"process_name\": " + StrArray(directProcs) + ", \"outbound\": \"direct\" }");
            rules.Add("{ \"ip_is_private\": true, \"outbound\": \"direct\" }");
            // No IPv6 reject rule: with no IPv6 address on the adapter nothing arrives over IPv6,
            // and rejecting what does arrive only produces a retry storm (see the inbound above).
            if (s.BlockQuic)
            {
                // QUIC is UDP. Where the server cannot relay UDP it would stall for seconds before
                // a browser gives up; rejecting it makes the fallback to TCP immediate.
                rules.Add("{ \"network\": \"udp\", \"port\": 443, \"action\": \"reject\" }");
            }
            if (iran)
            {
                rules.Add("{ \"domain_suffix\": [ \".ir\" ], \"outbound\": \"direct\" }");
                rules.Add("{ \"rule_set\": [ \"geoip-ir\", \"geosite-ir\" ], \"outbound\": \"direct\" }");
            }

            b.AppendLine("    \"rules\": [");
            for (int i = 0; i < rules.Count; i++)
                b.AppendLine("      " + rules[i] + (i < rules.Count - 1 ? "," : ""));
            b.AppendLine("    ],");
            if (iran)
            {
                b.AppendLine("    \"rule_set\": [");
                b.AppendLine("      { \"type\": \"remote\", \"tag\": \"geoip-ir\", \"format\": \"binary\", \"url\": \"" + Esc(GeoipIrUrl) + "\", \"download_detour\": \"proxy\", \"update_interval\": \"72h\" },");
                b.AppendLine("      { \"type\": \"remote\", \"tag\": \"geosite-ir\", \"format\": \"binary\", \"url\": \"" + Esc(GeositeIrUrl) + "\", \"download_detour\": \"proxy\", \"update_interval\": \"72h\" }");
                b.AppendLine("    ],");
            }
            b.AppendLine("    \"final\": \"proxy\"");
            b.AppendLine("  },");

            // ---- experimental: rule-set and DNS cache ----------------------------------
            b.AppendLine("  \"experimental\": {");
            b.AppendLine("    \"cache_file\": { \"enabled\": true, \"path\": \"cache.db\" }");
            b.AppendLine("  }");

            b.Append("}");
            return b.ToString();
        }
    }
}
