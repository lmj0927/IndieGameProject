using System;

namespace Poker.Foundation
{
    // Explicit development scope. Expanding to cards in the remaining scheduled
    // runout requires a policy for attributing the resulting later board changes.
    public enum HoldemCardSourceScope { UndealtOutsideCurrentHandRunout = 1 }
    /// <summary>
    /// Host-only selected effect, not an LLM interpretation or player command. Selection,
    /// probability and flop-slot policy belong to the caller. A request is not proof of success.
    /// </summary>
    public sealed class HoldemCardChange
    {
        public HoldemCardChange(Guid utteranceWindowId, Guid utteranceId, SeatId speaker,
            int boardIndex, Card card, HoldemCardSourceScope sourceScope)
        {
            if (utteranceWindowId == Guid.Empty || utteranceId == Guid.Empty)
                throw new ArgumentException("The source utterance and its window are required.");
            if (!speaker.IsValid) throw new ArgumentException("A valid speaker is required.", nameof(speaker));
            if (boardIndex < 0 || boardIndex > 4) throw new ArgumentOutOfRangeException(nameof(boardIndex));
            if (!card.IsValid) throw new ArgumentException("A valid replacement card is required.", nameof(card));
            if (sourceScope != HoldemCardSourceScope.UndealtOutsideCurrentHandRunout)
                throw new ArgumentOutOfRangeException(nameof(sourceScope));
            UtteranceWindowId = utteranceWindowId; UtteranceId = utteranceId;
            Speaker = speaker; BoardIndex = boardIndex; Card = card; SourceScope = sourceScope;
        }
        public Guid UtteranceWindowId { get; }
        public Guid UtteranceId { get; }
        public SeatId Speaker { get; }
        public int BoardIndex { get; }
        public Card Card { get; }
        public HoldemCardSourceScope SourceScope { get; }
        internal bool SamePayload(HoldemCardChange other) => other != null
            && UtteranceWindowId == other.UtteranceWindowId && UtteranceId == other.UtteranceId
            && Speaker == other.Speaker && BoardIndex == other.BoardIndex && Card == other.Card && SourceScope == other.SourceScope;
    }

    /// <summary>Immutable causal evidence created only by a committed card-changing deal. Host only.</summary>
    public sealed class HoldemCardMutation
    {
        internal HoldemCardMutation(HoldemCardChange change, Card before, int sourceDeckIndex)
        {
            UtteranceWindowId = change.UtteranceWindowId; UtteranceId = change.UtteranceId;
            Speaker = change.Speaker; BoardIndex = change.BoardIndex; Before = before; After = change.Card;
            SourceDeckIndex = sourceDeckIndex; SourceScope = change.SourceScope;
        }
        public Guid UtteranceWindowId { get; }
        public Guid UtteranceId { get; }
        public SeatId Speaker { get; }
        public int BoardIndex { get; }
        public Card Before { get; }
        public Card After { get; }
        public int SourceDeckIndex { get; }
        public HoldemCardSourceScope SourceScope { get; }
    }

    /// <summary>
    /// Complete host record for one committed reveal, including unchanged deals. A missing
    /// record is unknown, not a negative verdict. Never serialize this object to a player.
    /// </summary>
    public sealed class HoldemDealRecord
    {
        internal HoldemDealRecord(HoldemDealCommand command, Guid? accusationWindowId, long version,
            HoldemCardMutation mutation)
        {
            SessionId = command.SessionId; HandId = command.HandId; DealWindowId = command.WindowId;
            CommandId = command.CommandId; Street = command.Street; AccusationWindowId = accusationWindowId;
            Version = version; Mutation = mutation;
        }
        public Guid SessionId { get; }
        public Guid HandId { get; }
        public Guid DealWindowId { get; }
        public Guid CommandId { get; }
        public Guid? AccusationWindowId { get; }
        public HoldemStreet Street { get; }
        public long Version { get; }
        public HoldemCardMutation Mutation { get; }
    }

    /// <summary>Minimal owner-only feedback. No original card, other seat or AI interpretation.</summary>
    public sealed class HoldemOwnCardChange
    {
        internal HoldemOwnCardChange(HoldemDealRecord record)
        {
            HandId = record.HandId; Street = record.Street; DealWindowId = record.DealWindowId;
            BoardIndex = record.Mutation.BoardIndex; Card = record.Mutation.After;
        }
        public Guid HandId { get; }
        public Guid DealWindowId { get; }
        public HoldemStreet Street { get; }
        public int BoardIndex { get; }
        public Card Card { get; }
    }
}
