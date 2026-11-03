using System;

namespace Poker.Foundation
{
    /// <summary>Host intent bound to one community-card reveal, not a player betting action.</summary>
    public sealed class HoldemRevealCommand
    {
        public HoldemRevealCommand(Guid sessionId, Guid handId, Guid commandId,
            long expectedVersion, HoldemStreet street)
        {
            if (sessionId == Guid.Empty) throw new ArgumentException("A session ID is required.", nameof(sessionId));
            if (handId == Guid.Empty) throw new ArgumentException("A hand ID is required.", nameof(handId));
            if (commandId == Guid.Empty) throw new ArgumentException("A command ID is required.", nameof(commandId));
            if (expectedVersion < 0) throw new ArgumentOutOfRangeException(nameof(expectedVersion));
            if (street != HoldemStreet.Flop && street != HoldemStreet.Turn && street != HoldemStreet.River)
                throw new ArgumentOutOfRangeException(nameof(street));
            SessionId = sessionId;
            HandId = handId;
            CommandId = commandId;
            ExpectedVersion = expectedVersion;
            Street = street;
        }

        public Guid SessionId { get; }
        public Guid HandId { get; }
        public Guid CommandId { get; }
        /// <summary>The authority session version, not the per-hand version.</summary>
        public long ExpectedVersion { get; }
        public HoldemStreet Street { get; }

        internal bool HasSamePayload(HoldemRevealCommand other) => other != null
            && SessionId == other.SessionId && HandId == other.HandId && CommandId == other.CommandId
            && ExpectedVersion == other.ExpectedVersion && Street == other.Street;
    }
}
