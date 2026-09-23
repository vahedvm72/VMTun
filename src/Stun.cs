using System;
using System.Net;
using System.Net.Sockets;

namespace VMTun
{
    /// <summary>
    /// Asks a STUN server which public address this machine's UDP appears to come from.
    ///
    /// This is the exact question a browser asks before it offers a WebRTC connection, and the
    /// answer is the "srflx" candidate it then publishes in its SDP to any page that asks for
    /// one. It is worth asking separately from the HTTP lookup because the two can disagree:
    /// a tunnel that carries TCP but cannot relay UDP leaves the UDP to find its own way out,
    /// and what it finds is the real network. When that happens a site sees one country over
    /// HTTPS and the subscriber's own address over WebRTC, which is worse than no tunnel at all
    /// — it is a tunnel plus a contradiction that points straight at the person using it.
    ///
    /// Plain UDP with no dependencies: a binding request is 20 bytes and the reply carries the
    /// address, XORed against the magic cookie.
    /// </summary>
    static class Stun
    {
        // Several, because any one of them may be blocked or unreachable, and "no answer" has
        // to mean "UDP does not get out" rather than "that host was down".
        static readonly string[,] Servers =
        {
            { "stun.l.google.com", "19302" },
            { "stun.cloudflare.com", "3478" },
            { "stun1.l.google.com", "19302" },
        };

        const int MagicCookie = 0x2112A442;
        const int BindingRequest = 0x0001;
        const int XorMappedAddress = 0x0020;
        const int MappedAddress = 0x0001;

        /// <summary>
        /// The public address UDP leaves from, or null when no server answered — which is the
        /// safe outcome: nothing escapes, so a browser can publish no address either.
        /// </summary>
        public static string PublicAddress(int timeoutMs, out string error)
        {
            error = null;
            string lastError = null;

            for (int i = 0; i < Servers.GetLength(0); i++)
            {
                string address = Ask(Servers[i, 0], int.Parse(Servers[i, 1]), timeoutMs, out lastError);
                if (address != null) return address;
            }

            error = lastError;
            return null;
        }

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
                new Random().NextBytes(transaction);
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
