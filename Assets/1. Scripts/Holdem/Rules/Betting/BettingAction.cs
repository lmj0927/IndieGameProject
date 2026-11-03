using System;

namespace Poker.Foundation
{
    public enum BettingActionKind { Fold, Check, Call, BetTo, RaiseTo }

    /// <summary>A local domain action, not an authenticated or deduplicated network command.</summary>
    public sealed class BettingAction
    {
        private BettingAction(BettingActionKind kind, long target = 0)
        {
            Kind = kind;
            Target = target;
        }
        public BettingActionKind Kind { get; }
        /// <summary>Total contribution on this street, not additional payment. Zero for Fold/Check/Call.</summary>
        public long Target { get; }
        public static BettingAction Fold() => new BettingAction(BettingActionKind.Fold);
        public static BettingAction Check() => new BettingAction(BettingActionKind.Check);
        public static BettingAction Call() => new BettingAction(BettingActionKind.Call);
        /// <summary>Non-paying choice shared by the context button and host timeout. Never calls or accuses.</summary>
        public static BettingAction CheckOrFold(bool canCheck, bool canFold)
            => canCheck ? Check() : canFold ? Fold() : null;
        public static BettingAction BetTo(long target) => Sized(BettingActionKind.BetTo, target);
        public static BettingAction RaiseTo(long target) => Sized(BettingActionKind.RaiseTo, target);
        private static BettingAction Sized(BettingActionKind kind, long target)
        {
            if (target <= 0) throw new ArgumentOutOfRangeException(nameof(target));
            return new BettingAction(kind, target);
        }
    }
}
