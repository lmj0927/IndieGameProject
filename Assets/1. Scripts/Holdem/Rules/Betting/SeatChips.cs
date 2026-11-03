using System;

namespace Poker.Foundation
{
    /// <summary>Immutable stack and outstanding hand contribution for one seat, not its betting status.</summary>
    public sealed class SeatChips
    {
        /// <summary>Starting chips for a ledger; no contribution is outstanding yet.</summary>
        public SeatChips(SeatId owner, long stack) : this(owner, stack, 0) { }

        internal SeatChips(SeatId owner, long stack, long committed)
        {
            if (!owner.IsValid) throw new ArgumentException("A valid owner is required.", nameof(owner));
            if (stack < 0) throw new ArgumentOutOfRangeException(nameof(stack));
            if (committed < 0) throw new ArgumentOutOfRangeException(nameof(committed));
            Owner = owner;
            Stack = stack;
            Committed = committed;
        }

        public SeatId Owner { get; }
        public long Stack { get; }
        /// <summary>Hand contribution remaining after refunds, not a street bet or a prize entitlement.</summary>
        public long Committed { get; }
    }
}
