using System;
using System.Collections.Generic;

namespace Poker.Foundation
{
    /// <summary>A seat and its already-validated poker value; contains no physical-card ownership claim.</summary>
    public sealed class SeatHandValue
    {
        public SeatHandValue(SeatId owner, HandValue value)
        {
            if (!owner.IsValid) throw new ArgumentException("A valid owner is required.", nameof(owner));
            if (!value.IsValid) throw new ArgumentException("A valid hand value is required.", nameof(value));
            Owner = owner;
            Value = value;
        }

        public SeatId Owner { get; }
        public HandValue Value { get; }
    }

    /// <summary>Immutable payout candidate from matched hand contributions. No authentication, phase or command history.</summary>
    public sealed class PotSettlement
    {
        private readonly PotAward[] pots;
        private readonly long[] awarded;

        private PotSettlement(ChipLedger ledger, PotAward[] ownedPots, long[] ownedAwards, long totalAwarded)
        { Ledger = ledger; pots = ownedPots; awarded = ownedAwards; TotalAwarded = totalAwarded; }

        public ChipLedger Ledger { get; }
        public long TotalAwarded { get; }
        public int PotCount => pots.Length;
        public PotAward GetPot(int index)
        {
            if (index < 0 || index >= pots.Length) throw new ArgumentOutOfRangeException(nameof(index));
            return pots[index];
        }
        public long GetAwardedTo(SeatId seat)
        {
            Ledger.GetChips(seat);
            for (int i = 0; i < Ledger.SeatCount; i++) if (Ledger.GetSeatAt(i) == seat) return awarded[i];
            throw new InvalidOperationException("Ledger seat lookup failed.");
        }

        /// <summary>
        /// Settles matched contribution layers from caller-validated values after betting completes.
        /// The caller validates hole cards and the shared board. Missing values are treated as folded.
        /// Priority must contain exactly all live seats, or be null while the odd-chip policy remains unresolved.
        /// Null priority succeeds only when no tied pot has a remainder; never uses input order as a fallback.
        /// </summary>
        public static PotSettlement ShowdownByValue(ChipLedger ledger,
            IReadOnlyList<SeatHandValue> liveValues, IReadOnlyList<SeatId> oddChipPriority = null)
        {
            if (ledger == null) throw new ArgumentNullException(nameof(ledger));
            if (liveValues == null) throw new ArgumentNullException(nameof(liveValues));
            long highest = RequireMatchedPot(ledger);
            int count = liveValues.Count;
            if (count < 2 || count > ledger.SeatCount)
                throw new ArgumentException("At least two live values are required.", nameof(liveValues));
            var values = new Dictionary<SeatId, HandValue>();
            var caps = new SortedSet<long>();
            for (int i = 0; i < count; i++)
            {
                SeatHandValue ranked = liveValues[i];
                if (ranked == null || values.ContainsKey(ranked.Owner))
                    throw new ArgumentException("Live values must be non-null and have unique owners.", nameof(liveValues));
                long paid = ledger.GetChips(ranked.Owner).Committed;
                if (paid == 0) throw new ArgumentException("A live value must have contributed to this pot.", nameof(liveValues));
                values.Add(ranked.Owner, ranked.Value);
                caps.Add(paid);
            }
            SeatId[] priority = CopyPriority(oddChipPriority, values);
            if (caps.Max < highest) throw new SettlementException(SettlementFailure.NoEligibleWinner);

            var awards = new long[ledger.SeatCount];
            var pots = new List<PotAward>();
            long lower = 0;
            foreach (long cap in caps)
            {
                long amount = 0;
                var eligible = new List<SeatId>();
                var winners = new List<SeatId>();
                HandValue best = default;
                for (int i = 0; i < ledger.SeatCount; i++)
                {
                    SeatId seat = ledger.GetSeatAt(i);
                    long paid = ledger.GetChips(seat).Committed;
                    amount = checked(amount + Math.Min(Math.Max(0, paid - lower), cap - lower));
                    if (paid < cap || !values.TryGetValue(seat, out HandValue value)) continue;
                    eligible.Add(seat);
                    if (winners.Count == 0 || value > best)
                    { best = value; winners.Clear(); winners.Add(seat); }
                    else if (value == best) winners.Add(seat);
                }
                if (winners.Count == 0) throw new SettlementException(SettlementFailure.NoEligibleWinner);
                long share = amount / winners.Count;
                long remainder = amount % winners.Count;
                var oddRecipients = new HashSet<SeatId>();
                if (remainder > 0)
                {
                    if (priority == null) throw new SettlementException(SettlementFailure.MissingOddChipOrder);
                    foreach (SeatId seat in priority)
                        if (winners.Contains(seat) && remainder > 0) { oddRecipients.Add(seat); remainder--; }
                }
                var payouts = new SeatPayout[winners.Count];
                for (int i = 0; i < winners.Count; i++)
                {
                    SeatId seat = winners[i];
                    bool odd = oddRecipients.Contains(seat);
                    long received = checked(share + (odd ? 1 : 0));
                    payouts[i] = new SeatPayout(seat, received, odd);
                    int index = IndexOf(ledger, seat);
                    awards[index] = checked(awards[index] + received);
                }
                pots.Add(new PotAward(lower, cap, amount, eligible.ToArray(), payouts));
                lower = cap;
            }
            return new PotSettlement(ledger.Distribute(awards), pots.ToArray(), awards, ledger.TotalCommitted);
        }

        /// <summary>Caller proves only this seat remains live. No cards are needed or revealed.</summary>
        public static PotSettlement AwardUncontested(ChipLedger ledger, SeatId soleWinner)
        {
            if (ledger == null) throw new ArgumentNullException(nameof(ledger));
            SeatChips winner = ledger.GetChips(soleWinner);
            long highest = RequireMatchedPot(ledger);
            if (winner.Committed != highest) throw new SettlementException(SettlementFailure.NoEligibleWinner);
            var awards = new long[ledger.SeatCount];
            awards[IndexOf(ledger, soleWinner)] = ledger.TotalCommitted;
            var pot = new PotAward(0, highest, ledger.TotalCommitted, new[] { soleWinner },
                new[] { new SeatPayout(soleWinner, ledger.TotalCommitted, false) });
            return new PotSettlement(ledger.Distribute(awards), new[] { pot }, awards, ledger.TotalCommitted);
        }

        /// <summary>Mid-street forfeit: every committed chip, matched or not, goes to one seat.</summary>
        internal static PotSettlement AwardEntirePot(ChipLedger ledger, SeatId recipient)
        {
            if (ledger == null) throw new ArgumentNullException(nameof(ledger));
            ledger.GetChips(recipient);
            if (ledger.TotalCommitted == 0) throw new SettlementException(SettlementFailure.NothingToAward);
            long highest = 0;
            for (int i = 0; i < ledger.SeatCount; i++)
                highest = Math.Max(highest, ledger.GetChips(ledger.GetSeatAt(i)).Committed);
            var awards = new long[ledger.SeatCount];
            awards[IndexOf(ledger, recipient)] = ledger.TotalCommitted;
            var pot = new PotAward(0, highest, ledger.TotalCommitted, new[] { recipient },
                new[] { new SeatPayout(recipient, ledger.TotalCommitted, false) });
            return new PotSettlement(ledger.Distribute(awards), new[] { pot }, awards, ledger.TotalCommitted);
        }

        /// <summary>
        /// A host-proven social accusation awards the pot as it stands, including wagers not yet called.
        /// The caller gates unresolved side-pot eligibility before adjudication. Ordinary poker settlement
        /// still requires matched contributions.
        /// </summary>
        internal static PotSettlement AwardCurrentPot(ChipLedger ledger, SeatId winner)
        {
            if (ledger == null) throw new ArgumentNullException(nameof(ledger));
            if (ledger.GetChips(winner).Committed == 0) throw new SettlementException(SettlementFailure.NoEligibleWinner);
            if (ledger.TotalCommitted == 0) throw new SettlementException(SettlementFailure.NothingToAward);
            long highest = 0;
            for (int i = 0; i < ledger.SeatCount; i++) highest = Math.Max(highest, ledger.GetChips(ledger.GetSeatAt(i)).Committed);
            var awards = new long[ledger.SeatCount];
            awards[IndexOf(ledger, winner)] = ledger.TotalCommitted;
            var pot = new PotAward(0, highest, ledger.TotalCommitted, new[] { winner },
                new[] { new SeatPayout(winner, ledger.TotalCommitted, false) });
            return new PotSettlement(ledger.Distribute(awards), new[] { pot }, awards, ledger.TotalCommitted);
        }

        private static long RequireMatchedPot(ChipLedger ledger)
        {
            if (ledger.TotalCommitted == 0) throw new SettlementException(SettlementFailure.NothingToAward);
            long highest = 0, second = 0;
            for (int i = 0; i < ledger.SeatCount; i++)
            {
                long paid = ledger.GetChips(ledger.GetSeatAt(i)).Committed;
                if (paid > highest) { second = highest; highest = paid; }
                else if (paid > second) second = paid;
            }
            if (highest > second) throw new SettlementException(SettlementFailure.UnmatchedContribution);
            return highest;
        }

        private static SeatId[] CopyPriority(IReadOnlyList<SeatId> order, Dictionary<SeatId, HandValue> values)
        {
            if (order == null) return null;
            int count = order.Count;
            if (count != values.Count) throw new ArgumentException("Priority must include every live seat exactly once.", nameof(order));
            var seen = new HashSet<SeatId>();
            var copy = new SeatId[count];
            for (int i = 0; i < count; i++)
            {
                SeatId seat = order[i];
                if (!values.ContainsKey(seat) || !seen.Add(seat))
                    throw new ArgumentException("Priority contains an unknown, folded or duplicate seat.", nameof(order));
                copy[i] = seat;
            }
            return copy;
        }

        private static int IndexOf(ChipLedger ledger, SeatId seat)
        {
            for (int i = 0; i < ledger.SeatCount; i++) if (ledger.GetSeatAt(i) == seat) return i;
            throw new InvalidOperationException("A payout seat is absent from the ledger.");
        }
    }
}
