using System;

namespace Poker.Foundation
{
    /// <summary>Current actor's legal actions. Short all-in aggression is represented by a singleton range.</summary>
    public sealed class LegalBettingActions
    {
        internal LegalBettingActions(bool fold, bool check, long callAmount, bool bet,
            long? minimumTarget, long? maximumTarget, long? allInTarget = null)
        {
            CanFold = fold;
            CanCheck = check;
            CallAmount = callAmount;
            CanBet = bet && (minimumTarget.HasValue || allInTarget.HasValue);
            CanRaise = !bet && (minimumTarget.HasValue || allInTarget.HasValue);
            MinimumAggressiveTarget = minimumTarget;
            MaximumAggressiveTarget = maximumTarget;
            AllInTarget = allInTarget;
        }
        public bool CanFold { get; }
        public bool CanCheck { get; }
        public bool CanCall => CallAmount > 0;
        public long CallAmount { get; }
        public bool CanBet { get; }
        public bool CanRaise { get; }
        public long? MinimumAggressiveTarget { get; }
        public long? MaximumAggressiveTarget { get; }
        /// <summary>Optional singleton exception, not a license to make any intermediate over-cap raise.</summary>
        public long? AllInTarget { get; }

        /// <summary>Shared legality gate for state application and command rejection; no independent UI rules.</summary>
        public bool Allows(BettingAction action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            switch (action.Kind)
            {
                case BettingActionKind.Fold: return CanFold;
                case BettingActionKind.Check: return CanCheck;
                case BettingActionKind.Call: return CanCall;
                case BettingActionKind.BetTo: return CanBet && InRange(action.Target);
                case BettingActionKind.RaiseTo: return CanRaise && InRange(action.Target);
                default: return false;
            }
        }

        private bool InRange(long target) => AllInTarget == target || (MinimumAggressiveTarget.HasValue &&
            target >= MinimumAggressiveTarget.Value && target <= MaximumAggressiveTarget.Value);
    }
}
