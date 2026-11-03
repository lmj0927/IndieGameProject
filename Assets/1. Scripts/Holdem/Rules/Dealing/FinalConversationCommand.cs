using System;

namespace Poker.Foundation
{
    /// <summary>In-process host intent to finish this hand's River conversation, not a player action.</summary>
    public sealed class FinalConversationCommand
    {
        public FinalConversationCommand(Guid sessionId, Guid handId, Guid commandId, long expectedVersion)
        {
            if (sessionId == Guid.Empty || handId == Guid.Empty || commandId == Guid.Empty)
                throw new ArgumentException("Session, hand and command IDs are required.");
            if (expectedVersion < 0) throw new ArgumentOutOfRangeException(nameof(expectedVersion));
            SessionId = sessionId; HandId = handId; CommandId = commandId; ExpectedVersion = expectedVersion;
        }
        public Guid SessionId { get; }
        public Guid HandId { get; }
        public Guid CommandId { get; }
        public long ExpectedVersion { get; }
        internal bool HasSamePayload(FinalConversationCommand other) => other != null
            && SessionId == other.SessionId && HandId == other.HandId && CommandId == other.CommandId
            && ExpectedVersion == other.ExpectedVersion;
    }
}
