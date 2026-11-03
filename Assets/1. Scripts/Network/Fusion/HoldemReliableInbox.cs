using System;
using System.Collections.Generic;

namespace Poker.Online
{
    // Reliable streams may complete in a different order. Each connection starts at sequence one.
    public sealed class HoldemReliableInbox<T>
    {
        private readonly SortedDictionary<int, Entry> pending = new SortedDictionary<int, Entry>();
        private int next = 1, bytes;
        private sealed class Entry { public T Value; public int Bytes; }
        public int PendingCount => pending.Count;
        public bool Accept(int sequence, T value, int length)
        {
            if (sequence < next || sequence - (long)next >= 32 || length < 1 || length > 65536
                || pending.ContainsKey(sequence) || bytes + length > 262144) return false;
            pending.Add(sequence, new Entry { Value = value, Bytes = length }); bytes += length;
            return true;
        }
        public bool TryRead(out T value)
        {
            if (!pending.TryGetValue(next, out var entry)) { value = default; return false; }
            pending.Remove(next++); bytes -= entry.Bytes; value = entry.Value; return true;
        }
    }
}
