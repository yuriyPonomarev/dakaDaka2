using System;
using System.Globalization;

namespace Emberdelve.Net.Rules
{
    /// <summary>
    /// U3A5: parses the lobby's address field ("IP", "IP:port", ":port", "localhost:port") into the IPv4 literal and port the transport
    /// needs. Pure (no engine dependency). Only dotted-quad IPv4 and the word "localhost" are accepted: the transport is given a
    /// literal, it does not resolve names, and IPv6 would need a different syntax. An empty field means the default (127.0.0.1:7777).
    /// </summary>
    public static class NetEndpoint
    {
        public const int MaxLength = 64;

        public static bool TryParse(string text, out string address, out int port, out string error)
        {
            address = NetTuning.DefaultAddress;
            port = NetTuning.DefaultPort;
            error = "";

            string t = text == null ? "" : text.Trim();
            if (t.Length == 0) return true;
            if (t.Length > MaxLength)
            {
                error = "address too long";
                return false;
            }

            string host = t;
            string portText = "";
            int colon = t.IndexOf(':');
            if (colon >= 0)
            {
                if (t.IndexOf(':', colon + 1) >= 0)
                {
                    error = "use IPv4 as a.b.c.d:port";
                    return false;
                }

                host = t.Substring(0, colon).Trim();
                portText = t.Substring(colon + 1).Trim();
            }

            if (colon >= 0)
            {
                if (!TryPort(portText, out port))
                {
                    port = NetTuning.DefaultPort;
                    error = "port must be 1 to 65535";
                    return false;
                }
            }

            if (host.Length == 0) return true; // ":7777" keeps the default address

            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                address = NetTuning.DefaultAddress;
                return true;
            }

            if (!TryIPv4(host, out string normalized))
            {
                port = NetTuning.DefaultPort;
                error = "not an IPv4 address (a.b.c.d)";
                return false;
            }

            address = normalized;
            return true;
        }

        private static bool TryPort(string text, out int port)
        {
            port = 0;
            if (text.Length == 0 || text.Length > 5) return false;
            for (int i = 0; i < text.Length; i++) if (text[i] < '0' || text[i] > '9') return false;
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port)) return false;
            return port >= 1 && port <= 65535;
        }

        private static bool TryIPv4(string text, out string normalized)
        {
            normalized = "";
            string[] parts = text.Split('.');
            if (parts.Length != 4) return false;

            int[] octets = new int[4];
            for (int i = 0; i < 4; i++)
            {
                string p = parts[i];
                if (p.Length == 0 || p.Length > 3) return false;
                for (int c = 0; c < p.Length; c++) if (p[c] < '0' || p[c] > '9') return false;
                if (!int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out octets[i])) return false;
                if (octets[i] > 255) return false;
            }

            normalized = octets[0].ToString(CultureInfo.InvariantCulture) + "." + octets[1].ToString(CultureInfo.InvariantCulture) + "." +
                         octets[2].ToString(CultureInfo.InvariantCulture) + "." + octets[3].ToString(CultureInfo.InvariantCulture);
            return true;
        }
    }
}
