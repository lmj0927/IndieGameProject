using System;

namespace Poker.Foundation
{
    /// <summary>
    /// Explicit options for the v3 sequential flow. Supplying a test profile does not make it a team rule.
    /// There are deliberately no defaults for the decisions still open in the design document.
    /// </summary>
    public sealed class SequentialBettingRules
    {
        public SequentialBettingRules(long minimumBet, long maximumOpeningBet, bool allowCheck,
            SequentialFirstSeat firstSeat, SequentialAllInCap allInCap,
            ChipShortfallPolicy anteShortfall, ChipShortfallPolicy fineShortfall,
            TurnAccusationScope accusationScope, BaitFineLimit baitFineLimit)
        {
            if (minimumBet <= 0) throw new ArgumentOutOfRangeException(nameof(minimumBet));
            if (maximumOpeningBet < minimumBet) throw new ArgumentOutOfRangeException(nameof(maximumOpeningBet));
            if (firstSeat != SequentialFirstSeat.Button && firstSeat != SequentialFirstSeat.AfterButton)
                throw new ArgumentOutOfRangeException(nameof(firstSeat));
            if (allInCap != SequentialAllInCap.DoubleMaximum && allInCap != SequentialAllInCap.WholeStackException)
                throw new ArgumentOutOfRangeException(nameof(allInCap));
            ValidateShortfall(anteShortfall, nameof(anteShortfall));
            ValidateShortfall(fineShortfall, nameof(fineShortfall));
            if (accusationScope != TurnAccusationScope.CurrentReveal && accusationScope != TurnAccusationScope.WholeHand)
                throw new ArgumentOutOfRangeException(nameof(accusationScope));
            if (baitFineLimit != BaitFineLimit.EachMiss && baitFineLimit != BaitFineLimit.OncePerTargetPerHand)
                throw new ArgumentOutOfRangeException(nameof(baitFineLimit));
            MinimumBet = minimumBet;
            MaximumOpeningBet = maximumOpeningBet;
            AllowCheck = allowCheck;
            FirstSeat = firstSeat;
            AllInCap = allInCap;
            AnteShortfall = anteShortfall;
            FineShortfall = fineShortfall;
            AccusationScope = accusationScope;
            BaitFineLimit = baitFineLimit;
        }

        public long MinimumBet { get; }
        public long MaximumOpeningBet { get; }
        public bool AllowCheck { get; }
        public SequentialFirstSeat FirstSeat { get; }
        public SequentialAllInCap AllInCap { get; }
        public ChipShortfallPolicy AnteShortfall { get; }
        public ChipShortfallPolicy FineShortfall { get; }
        public TurnAccusationScope AccusationScope { get; }
        public BaitFineLimit BaitFineLimit { get; }

        private static void ValidateShortfall(ChipShortfallPolicy policy, string name)
        {
            if (policy != ChipShortfallPolicy.RequireFullAmount && policy != ChipShortfallPolicy.PayAvailable)
                throw new ArgumentOutOfRangeException(name);
        }
    }

    public enum SequentialFirstSeat { Button = 1, AfterButton = 2 }
    public enum SequentialAllInCap { DoubleMaximum = 1, WholeStackException = 2 }
    public enum ChipShortfallPolicy { RequireFullAmount = 1, PayAvailable = 2 }
    public enum TurnAccusationScope { CurrentReveal = 1, WholeHand = 2 }
    public enum BaitFineLimit { EachMiss = 1, OncePerTargetPerHand = 2 }
}
