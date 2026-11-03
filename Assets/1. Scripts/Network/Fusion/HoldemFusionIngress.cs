using System;
using System.Text;

namespace Poker.Online
{
    /// <summary>Limits apply before decoding a completed stream, including stale generations.</summary>
    public sealed class HoldemFusionIngress
    {
        private double windowStart;
        private int count;
        public bool AcceptMessage(double now)
        {
            if (double.IsNaN(now) || double.IsInfinity(now) || now < windowStart) return false;
            if (now - windowStart >= 1) { windowStart = now; count = 0; }
            if (count >= 60) return false;
            count++; return true;
        }

        // This is a connection-generation nonce, not authentication or encryption.
        public static bool TryReadToken(ReadOnlySpan<byte> bytes, out string token)
        {
            token = null;
            if (bytes.Length != 32) return false;
            bool nonzero = false;
            foreach (byte value in bytes)
            {
                if (!(value >= '0' && value <= '9' || value >= 'a' && value <= 'f' || value >= 'A' && value <= 'F'))
                    return false;
                nonzero |= value != '0';
            }
            if (!nonzero) return false;
            token = Encoding.ASCII.GetString(bytes.ToArray()); return true;
        }
    }
}
