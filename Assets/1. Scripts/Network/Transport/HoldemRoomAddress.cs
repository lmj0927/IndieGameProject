using System.Globalization;
using System.Net;

namespace Poker.Transport
{
    /// <summary>Plain IPv4:port sharing only. Parsing does not resolve, connect, or grant LAN trust.</summary>
    public static class HoldemRoomAddress
    {
        public static bool TryParse(string value, out string address, out int port)
        {
            address = null; port = 0;
            if (string.IsNullOrEmpty(value) || value.Length > 64) return false;
            foreach (char c in value) if (char.IsControl(c)) return false;
            value = value.Trim(' ');
            int colon = value.IndexOf(':');
            if (colon <= 0 || colon != value.LastIndexOf(':')) return false;
            string[] octets = value.Substring(0, colon).Split('.');
            if (octets.Length != 4) return false;
            var bytes = new byte[4];
            for (int i = 0; i < bytes.Length; i++)
            {
                if (!AsciiDigits(octets[i], 3) || !byte.TryParse(octets[i], NumberStyles.None,
                    CultureInfo.InvariantCulture, out bytes[i])) return false;
                // Avoid platform-specific interpretations of octal/abbreviated address strings.
                if (octets[i].Length > 1 && octets[i][0] == '0') return false;
            }
            string portText = value.Substring(colon + 1);
            if (!AsciiDigits(portText, 5) || !int.TryParse(portText, NumberStyles.None,
                CultureInfo.InvariantCulture, out int parsedPort) || parsedPort < 1 || parsedPort > 65535) return false;
            address = new IPAddress(bytes).ToString(); port = parsedPort; return true;
        }

        private static bool AsciiDigits(string value, int limit)
        {
            if (value.Length == 0 || value.Length > limit) return false;
            foreach (char c in value) if (c < '0' || c > '9') return false;
            return true;
        }
    }
}
