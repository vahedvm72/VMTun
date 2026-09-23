using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace VMTun
{
    /// <summary>One STUN server's answer.</summary>
    class StunReply
    {
        public string Server = "";
        public string Address = "";     // empty when it did not answer
        public string Error = "";
    }

    /// <summary>
    /// Asks STUN servers which public address this machine's UDP appears to come from.
    ///
    /// This is the question a browser asks before it offers a WebRTC connection, and the answer
    /// becomes the "srflx" candidate it publishes to any page that wants one.
    ///
    /// Several servers, and every one of them, because they do not have to agree. A leak test
    /// run against a real machine showed Google's servers reporting the tunnel's address while
    /// three others reported the subscriber's own — traffic to some destinations was leaving
    /// outside the tunnel and traffic to others was not. Stopping at the first reply, which is
    /// what this used to do, therefore produced a confident all-clear over a live leak. A
    /// browser queries a list too, so the honest answer is the worst one in it.
    /// </summary>
    static class Stun
    {
        // Deliberately spread across operators and regions. A list that is all one company's
        // servers tests one network path and calls it the whole picture.
        static readonly string[,] Servers =
        {
            { "stun.l.google.com", "19302" },
            { "stun.cloudflare.com", "3478" },
            { "stun.nextcloud.com", "3478" },
            { "stun.chat.bilibili.com", "3478" },
            { "stun.miwifi.com", "3478" },
            { "stun.qq.com", "3478" },
        };

        const int XorMappedAddress = 0x0020;
        const int MappedAddress = 0x0001;

        /// <summary>
        /// Queries every server at once and returns what each one said. Parallel because six
        /// timeouts in a row would make the privacy scan feel broken.
        /// </summary>
        public static List<StunReply> QueryAll(int timeoutMs)
        {
            int count = Servers.GetLength(0);
            StunReply[] results = new StunReply[count];
            ManualResetEvent[] done = new ManualResetEvent[count];

            for (int i = 0; i < count; i++)
            {
                int index = i;
                done[i] = new ManualResetEvent(false);
                ThreadPool.QueueUserWorkItem(delegate
                {
                    StunReply r = new StunReply();
                    r.Server = Servers[index, 0];
                    try
                    {
                        string error;
                        string address = Ask(Servers[index, 0], int.Parse(Servers[index, 1]),
                                             timeoutMs, out error);
                        r.Address = address ?? "";
                        r.Error = error ?? "";
                    }
                    catch (Exception ex) { r.Error = ex.Message; }
                    results[index] = r;
                    done[index].Set();
                });
            }

            foreach (ManualResetEvent h in done)
            {
                try { h.WaitOne(timeoutMs + 2000); }
                catch { }
            }

            List<StunReply> list = new List<StunReply>();
            for (int i = 0; i < count; i++)
            {
                if (results[i] != null) list.Add(results[i]);
                try { done[i].Close(); }
                catch { }
            }
            return list;
        }

        /// <summary>
        /// The distinct addresses seen, in the order they were first observed. Empty when no
        /// server answered, which is the safe outcome: a browser has nothing to publish either.
        /// </summary>
        public static List<string> DistinctAddresses(List<StunReply> replies)
        {
            List<string> seen = new List<string>();
            if (replies == null) return seen;
            foreach (StunReply r in replies)
                if (r.Address.Length > 0 && !seen.Contains(r.Address)) seen.Add(r.Address);
            return seen;
        }

        /// <summary>Which servers reported a given address, for naming names in the report.</summary>
        public static List<string> ServersReporting(List<StunReply> replies, string address)
        {
            List<string> names = new List<string>();
            if (replies == null) return names;
            foreach (StunReply r in replies)
                if (r.Address == address) names.Add(ShortName(r.Server));
            return names;
        }

        static string ShortName(string host)
        {
            // "stun.chat.bilibili.com" reads better as "bilibili" in a one-line finding.
            string[] parts = host.Split('.');
            if (parts.Length >= 2) return parts[parts.Length - 2];
            return host;
        }

        // ------------------------------------------------------------------ the protocol

        static string Ask(string host, int port, int timeoutMs, out string error)
        {
            error = null;
            UdpClient udp = null;
            try
            {
                udp = new UdpClient();
                udp.Client.ReceiveTimeout = timeoutMs;
                udp.Client.SendTimeout = timeoutMs;

                byte[] request = new byte[20];
                request[0] = 0x00; request[1] = 0x01;          // Binding Request
                request[2] = 0x00; request[3] = 0x00;          // no attributes
                request[4] = 0x21; request[5] = 0x12; request[6] = 0xA4; request[7] = 0x42;

                byte[] transaction = new byte[12];
                new Random(Environment.TickCount + host.GetHashCode()).NextBytes(transaction);
                Buffer.BlockCopy(transaction, 0, request, 8, 12);

                udp.Connect(host, port);
                udp.Send(request, request.Length);

                IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                byte[] reply = udp.Receive(ref from);
                return Parse(reply, request);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
            finally
            {
                try { if (udp != null) udp.Close(); }
                catch { }
            }
        }

        /// <summary>Walks the reply's attributes for the address, XORed or plain.</summary>
        static string Parse(byte[] reply, byte[] request)
        {
            if (reply == null || reply.Length < 20) return null;

            int i = 20;
            while (i + 4 <= reply.Length)
            {
                int type = (reply[i] << 8) | reply[i + 1];
                int length = (reply[i + 2] << 8) | reply[i + 3];
                int value = i + 4;
                if (value + length > reply.Length) break;

                // Both forms carry: one reserved byte, a family byte, the port, then the address.
                if ((type == XorMappedAddress || type == MappedAddress) &&
                    length >= 8 && reply[value + 1] == 0x01)
                {
                    byte[] octets = new byte[4];
                    for (int k = 0; k < 4; k++)
                    {
                        octets[k] = reply[value + 4 + k];
                        // The XOR form hides the address behind the magic cookie, which is the
                        // same four bytes we sent; the plain form is used as it stands.
                        if (type == XorMappedAddress) octets[k] ^= request[4 + k];
                    }
                    return new IPAddress(octets).ToString();
                }

                i = value + length + ((4 - (length % 4)) % 4);   // attributes are 4-byte aligned
            }
            return null;
        }
    }
}
