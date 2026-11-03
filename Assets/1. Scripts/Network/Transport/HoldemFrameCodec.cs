using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Poker.Transport
{
    /// <summary>One UTF-8 JSON document prefixed by its unsigned, big-endian byte length.</summary>
    public static class HoldemFrameCodec
    {
        public const int MaximumBytes = 65536;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        public static byte[] Encode(string json)
        {
            if (string.IsNullOrEmpty(json) || json.Length > MaximumBytes) throw new InvalidDataException("Invalid frame size.");
            int size = Utf8.GetByteCount(json);
            if (size > MaximumBytes) throw new InvalidDataException("Invalid frame size.");
            var frame = new byte[size + 4];
            frame[0] = (byte)(size >> 24); frame[1] = (byte)(size >> 16);
            frame[2] = (byte)(size >> 8); frame[3] = (byte)size;
            Utf8.GetBytes(json, 0, json.Length, frame, 4);
            return frame;
        }

        // EOF is legal only before a new header. A partial header/body is not a complete message.
        public static string Read(Stream stream, int deadlineMilliseconds = 30000)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (deadlineMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(deadlineMilliseconds));
            var elapsed = Stopwatch.StartNew(); var header = new byte[4];
            if (!ReadExactly(stream, header, elapsed, deadlineMilliseconds, true)) return null;
            uint size = ((uint)header[0] << 24) | ((uint)header[1] << 16) | ((uint)header[2] << 8) | header[3];
            if (size == 0 || size > MaximumBytes) throw new InvalidDataException("Invalid frame size.");
            var body = new byte[(int)size];
            ReadExactly(stream, body, elapsed, deadlineMilliseconds, false);
            return Utf8.GetString(body);
        }

        private static bool ReadExactly(Stream stream, byte[] bytes, Stopwatch elapsed, int deadline, bool allowCleanEof)
        {
            int offset = 0;
            while (offset < bytes.Length)
            {
                long remaining = deadline - elapsed.ElapsedMilliseconds;
                if (remaining <= 0) throw new IOException("Frame deadline exceeded.");
                if (stream.CanTimeout) stream.ReadTimeout = (int)remaining;
                int count = stream.Read(bytes, offset, bytes.Length - offset);
                if (count == 0)
                {
                    if (allowCleanEof && offset == 0) return false;
                    throw new EndOfStreamException("Incomplete frame.");
                }
                offset += count;
            }
            return true;
        }
    }
}
