using System;

namespace Poker.Foundation
{
    /// <summary>Public timing/correlation only. Contains no future cards or dealer interpretation.</summary>
    public sealed class HoldemDealWindow
    {
        internal HoldemDealWindow(Guid sessionId, HoldemHand hand)
        {
            SessionId = sessionId; HandId = hand.HandId; WindowId = Guid.NewGuid();
            Street = hand.PendingDealStreet.Value;
        }
        public Guid SessionId { get; }
        public Guid HandId { get; }
        public Guid WindowId { get; }
        public HoldemStreet Street { get; }
    }

    /// <summary>Trusted host authorizes one deal. Never accept this through a player connection.</summary>
    public sealed class HoldemDealCommand
    {
        public HoldemDealCommand(Guid sessionId, Guid handId, Guid windowId, Guid commandId,
            long expectedVersion, HoldemStreet street, HoldemCardChange cardChange = null)
        {
            if (sessionId == Guid.Empty || handId == Guid.Empty || windowId == Guid.Empty || commandId == Guid.Empty)
                throw new ArgumentException("A deal must identify its session, hand, window and command.");
            if (expectedVersion < 0) throw new ArgumentOutOfRangeException(nameof(expectedVersion));
            if (street != HoldemStreet.Flop && street != HoldemStreet.Turn && street != HoldemStreet.River)
                throw new ArgumentOutOfRangeException(nameof(street));
            SessionId = sessionId; HandId = handId; WindowId = windowId; CommandId = commandId;
            ExpectedVersion = expectedVersion; Street = street; CardChange = cardChange;
        }
        public Guid SessionId { get; }
        public Guid HandId { get; }
        public Guid WindowId { get; }
        public Guid CommandId { get; }
        public long ExpectedVersion { get; }
        public HoldemStreet Street { get; }
        public HoldemCardChange CardChange { get; }

        /// <summary>Adapter rejects an uncorrelated source without advancing the core.</summary>
        public HoldemReceipt RejectCardChange()
            => new HoldemReceipt(SessionId, HandId, CommandId, null, null, HoldemCommandError.InvalidCardChange);

        internal bool HasSamePayload(HoldemDealCommand other) => other != null
            && SessionId == other.SessionId && HandId == other.HandId && WindowId == other.WindowId
            && CommandId == other.CommandId && ExpectedVersion == other.ExpectedVersion && Street == other.Street
            && (CardChange == null ? other.CardChange == null : CardChange.SamePayload(other.CardChange));
    }
}
