using System;
using System.Collections.Generic;
using Poker.Foundation;

namespace Poker.Application
{
    public interface IHoldemHistoryPort
    {
        HoldemHandHistory ReadHistory();
    }

    public enum HoldemHistoryKind { SmallBlind, BigBlind, Action, UncalledReturn, Ante, FixedWager, FixedFold, AccusationFold }

    /// <summary>Public chip movements only. No cards, accusation choices or dealer evidence.</summary>
    public sealed class HoldemHistoryEntry
    {
        internal HoldemHistoryEntry(HoldemHistoryKind kind, HoldemStreet street, SeatId seat,
            BettingActionKind? action, long amount, long streetTotal, bool allIn)
        { Kind = kind; Street = street; Seat = seat; Action = action; Amount = amount; StreetTotal = streetTotal; IsAllIn = allIn; }
        public HoldemHistoryKind Kind { get; }
        public HoldemStreet Street { get; }
        public SeatId Seat { get; }
        public BettingActionKind? Action { get; }
        public long Amount { get; }
        public long StreetTotal { get; }
        public bool IsAllIn { get; }
    }

    /// <summary>Immutable copy of the current hand's history, safe to retain while play continues.</summary>
    public sealed class HoldemHandHistory
    {
        private readonly HoldemHistoryEntry[] entries;
        internal HoldemHandHistory(Guid sessionId, Guid handId, long handNumber, List<HoldemHistoryEntry> entries, long omittedCount = 0)
        { SessionId = sessionId; HandId = handId; HandNumber = handNumber; this.entries = entries.ToArray(); OmittedCount = omittedCount; }
        public Guid SessionId { get; }
        public Guid HandId { get; }
        public long HandNumber { get; }
        public long OmittedCount { get; }
        public int Count => entries.Length;
        public HoldemHistoryEntry GetEntry(int index) => entries[index];

        // Only publish completed movements. Private proposals/responses never enter public history.
        internal static List<HoldemHistoryEntry> FixedBettingEntries(HoldemSnapshot before, HoldemSnapshot after)
        {
            var result = new List<HoldemHistoryEntry>();
            for (int i = 0; i < before.SeatCount; i++)
            {
                var old = before.GetSeatAt(i); var current = after.GetSeat(old.Seat);
                long paid = checked(old.Stack - current.Stack + current.Awarded - old.Awarded);
                if (paid > 0) result.Add(new HoldemHistoryEntry(HoldemHistoryKind.FixedWager,
                    before.Street, old.Seat, null, paid, checked(old.StreetContribution + paid), paid == old.Stack));
                if (old.Status != HoldemSeatStatus.Folded && current.Status == HoldemSeatStatus.Folded)
                    result.Add(new HoldemHistoryEntry(HoldemHistoryKind.FixedFold,
                        before.Street, old.Seat, null, 0, 0, false));
            }
            return result;
        }

        // Fines are pot-external and are described by the public accusation outcome, never as a bet refund.
        internal static HoldemHistoryEntry AccusationEntry(HoldemSnapshot before, HoldemSnapshot after)
        {
            if (after.AccusationOutcomeCount <= before.AccusationOutcomeCount) return null;
            var outcome = after.GetAccusationOutcome(after.AccusationOutcomeCount - 1);
            return outcome.Hit ? null : new HoldemHistoryEntry(HoldemHistoryKind.AccusationFold,
                outcome.Street, outcome.Accuser, null, 0, 0, false);
        }
    }
}
