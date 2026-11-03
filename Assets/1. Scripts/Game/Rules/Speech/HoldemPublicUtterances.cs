using System;
using Poker.Foundation;

namespace Poker.Application
{
    /// <summary>Deliberately separate from host intake entries: no request, command or delivery metadata.</summary>
    public sealed class HoldemPublicUtterance
    {
        internal HoldemPublicUtterance(SeatId speaker, HoldemStreet street, string text, long? historyPosition = null)
        { Speaker = speaker; Street = street; Text = text; HistoryPosition = historyPosition; }
        public SeatId Speaker { get; }
        public HoldemStreet Street { get; }
        public string Text { get; }
        // Count of public chip movements when the host accepted this remark, never a private intake ordinal.
        public long? HistoryPosition { get; }
    }

    /// <summary>Current-hand raw remarks, optionally anchored to public betting history by the host.</summary>
    public sealed class HoldemPublicUtterances
    {
        private readonly HoldemPublicUtterance[] entries;
        internal HoldemPublicUtterances(Guid sessionId, Guid handId, HoldemPublicUtterance[] entries, bool hasHistoryOrder = false)
        { SessionId = sessionId; HandId = handId; this.entries = (HoldemPublicUtterance[])entries.Clone(); HasHistoryOrder = hasHistoryOrder; }
        public Guid SessionId { get; }
        public Guid HandId { get; }
        public int Count => entries.Length;
        public bool HasHistoryOrder { get; }
        public HoldemPublicUtterance GetEntry(int index) => entries[index];
    }

    public interface IHoldemPublicUtteranceSource
    {
        HoldemPublicUtterances ReadPublicUtterances();
    }
}
