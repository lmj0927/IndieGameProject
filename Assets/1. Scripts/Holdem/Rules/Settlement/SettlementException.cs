using System;

namespace Poker.Foundation
{
    public enum SettlementFailure
    {
        Invalid = 0,
        MissingOddChipOrder,
        UnmatchedContribution,
        NoEligibleWinner,
        NothingToAward
    }

    /// <summary>Stable reason for an unresolved/invalid settlement; never render Exception.Message in the UI.</summary>
    public sealed class SettlementException : InvalidOperationException
    {
        internal SettlementException(SettlementFailure reason) : base("Settlement rejected: " + reason)
        { Reason = reason; }
        public SettlementFailure Reason { get; }
    }
}
