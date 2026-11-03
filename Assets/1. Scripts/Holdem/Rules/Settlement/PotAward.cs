using System;

namespace Poker.Foundation
{
    public readonly struct SeatPayout
    {
        internal SeatPayout(SeatId seat, long amount, bool hasOddChip)
        { Seat = seat; Amount = amount; HasOddChip = hasOddChip; }

        public SeatId Seat { get; }
        /// <summary>Gross chips received, not profit.</summary>
        public long Amount { get; }
        public bool HasOddChip { get; }
    }

    /// <summary>One matched contribution layer and its awards; contains no cards or player names.</summary>
    public sealed class PotAward
    {
        private readonly SeatId[] eligible;
        private readonly SeatPayout[] payouts;

        internal PotAward(long lowerBound, long contributionCap, long amount,
            SeatId[] ownedEligible, SeatPayout[] ownedPayouts)
        {
            LowerBound = lowerBound;
            ContributionCap = contributionCap;
            Amount = amount;
            eligible = ownedEligible;
            payouts = ownedPayouts;
        }

        public long LowerBound { get; }
        public long ContributionCap { get; }
        public long Amount { get; }
        public int EligibleSeatCount => eligible.Length;
        public int PayoutCount => payouts.Length;
        public SeatId GetEligibleSeat(int index)
        {
            if (index < 0 || index >= eligible.Length) throw new ArgumentOutOfRangeException(nameof(index));
            return eligible[index];
        }
        public SeatPayout GetPayout(int index)
        {
            if (index < 0 || index >= payouts.Length) throw new ArgumentOutOfRangeException(nameof(index));
            return payouts[index];
        }
    }
}
