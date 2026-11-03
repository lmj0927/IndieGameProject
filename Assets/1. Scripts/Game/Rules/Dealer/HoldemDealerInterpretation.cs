using System;
using System.Collections.Generic;
using Poker.Foundation;

namespace Poker.Application
{
    // Internal adapter vocabulary, not the model's agreed wire format or a success decision.
    public enum HoldemDealerIntent { NoRequest, CardPreference }

    public sealed class HoldemDealerInterpretation
    {
        public HoldemDealerInterpretation(Guid utteranceId, HoldemDealerIntent intent, Rank? rank, Suit? suit,
            double explicitness, double confidence, HoldemDealerColor? color = null, int? level = null)
        {
            if (utteranceId == Guid.Empty) throw new ArgumentException("An utterance ID is required.");
            if (!Enum.IsDefined(typeof(HoldemDealerIntent), intent)) throw new ArgumentOutOfRangeException(nameof(intent));
            if (rank.HasValue && (rank < Foundation.Rank.Two || rank > Foundation.Rank.Ace))
                throw new ArgumentOutOfRangeException(nameof(rank));
            if (suit.HasValue && (suit < Foundation.Suit.Clubs || suit > Foundation.Suit.Spades))
                throw new ArgumentOutOfRangeException(nameof(suit));
            if (color.HasValue && !Enum.IsDefined(typeof(HoldemDealerColor), color.Value))
                throw new ArgumentOutOfRangeException(nameof(color));
            if (color.HasValue && (rank.HasValue || suit.HasValue))
                throw new ArgumentException("Color is a separate target range.");
            if ((intent == HoldemDealerIntent.NoRequest) != (!rank.HasValue && !suit.HasValue && !color.HasValue))
                throw new ArgumentException("A card preference needs a rank, suit or color; no-request has no target.");
            CheckUnit(explicitness, nameof(explicitness)); CheckUnit(confidence, nameof(confidence));
            if (level.HasValue && (level < 0 || level > 4 || explicitness != level.Value / 4.0
                || (level == 0) != (intent == HoldemDealerIntent.NoRequest)))
                throw new ArgumentException("Level must match the normalized intent and explicitness.");
            UtteranceId = utteranceId; Intent = intent; Rank = rank; Suit = suit;
            Explicitness = explicitness; Confidence = confidence; Color = color; Level = level;
        }
        public Guid UtteranceId { get; }
        public HoldemDealerIntent Intent { get; }
        public Rank? Rank { get; }
        public Suit? Suit { get; }
        public HoldemDealerColor? Color { get; }
        /// <summary>Code-calculated 0..4; null only for legacy score fixtures.</summary>
        public int? Level { get; }
        /// <summary>Adapter-normalized 0..1. Neither this value nor Confidence is an application probability.</summary>
        public double Explicitness { get; }
        public double Confidence { get; }
        public bool MatchesCard(Card card) => Intent == HoldemDealerIntent.CardPreference && card.IsValid
            && (!Rank.HasValue || Rank.Value == card.Rank) && (!Suit.HasValue || Suit.Value == card.Suit)
            && (!Color.HasValue || (card.Suit == Foundation.Suit.Hearts || card.Suit == Foundation.Suit.Diamonds)
                == (Color.Value == HoldemDealerColor.Red));
        private static void CheckUnit(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0 || value > 1)
                throw new ArgumentOutOfRangeException(name);
        }
        internal bool Matches(HoldemDealerInterpretation other) => other != null && UtteranceId == other.UtteranceId
            && Intent == other.Intent && Rank == other.Rank && Suit == other.Suit
            && Explicitness == other.Explicitness && Confidence == other.Confidence
            && Color == other.Color && Level == other.Level;
    }

    /// <summary>Complete, immutable host-only interpretation of exactly one closed input batch.</summary>
    public sealed class HoldemDealerInterpretationBatch
    {
        private readonly HoldemDealerInterpretation[] entries;
        public HoldemDealerInterpretationBatch(Guid sessionId, Guid handId, Guid dealWindowId, long expectedVersion,
            HoldemStreet street, Guid utteranceWindowId, IReadOnlyList<HoldemDealerInterpretation> entries)
        {
            if (sessionId == Guid.Empty || handId == Guid.Empty || dealWindowId == Guid.Empty || utteranceWindowId == Guid.Empty)
                throw new ArgumentException("Full response correlation is required.");
            if (expectedVersion < 0) throw new ArgumentOutOfRangeException(nameof(expectedVersion));
            if (street < HoldemStreet.Flop || street > HoldemStreet.River) throw new ArgumentOutOfRangeException(nameof(street));
            if (entries == null) throw new ArgumentNullException(nameof(entries));
            this.entries = new HoldemDealerInterpretation[entries.Count];
            var seen = new HashSet<Guid>();
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry == null || !seen.Add(entry.UtteranceId)) throw new ArgumentException("Null or duplicate interpretation.");
                this.entries[i] = entry;
            }
            SessionId = sessionId; HandId = handId; DealWindowId = dealWindowId; ExpectedVersion = expectedVersion;
            Street = street; UtteranceWindowId = utteranceWindowId;
        }
        public Guid SessionId { get; }
        public Guid HandId { get; }
        public Guid DealWindowId { get; }
        public long ExpectedVersion { get; }
        public HoldemStreet Street { get; }
        public Guid UtteranceWindowId { get; }
        public int Count => entries.Length;
        public HoldemDealerInterpretation GetEntry(int index) => entries[index];
        internal HoldemDealerInterpretation Find(Guid id)
        { foreach (var entry in entries) if (entry.UtteranceId == id) return entry; return null; }
        internal bool MatchesWork(HoldemDealerWork work)
        {
            if (work == null || work.InputKind != HoldemDealerInputKind.ClosedBettingWindow || work.SourceUtterances == null)
                return false;
            var deal = work.TargetDeal; var source = work.SourceUtterances;
            if (SessionId != deal.SessionId || HandId != deal.HandId || DealWindowId != deal.WindowId
                || ExpectedVersion != work.ExpectedVersion || Street != deal.Street
                || UtteranceWindowId != source.WindowId || entries.Length != source.Count) return false;
            for (int i = 0; i < source.Count; i++) if (Find(source.GetEntry(i).CommandId) == null) return false;
            return true;
        }
        internal bool Matches(HoldemDealerInterpretationBatch other)
        {
            if (other == null || SessionId != other.SessionId || HandId != other.HandId || DealWindowId != other.DealWindowId
                || ExpectedVersion != other.ExpectedVersion || Street != other.Street
                || UtteranceWindowId != other.UtteranceWindowId || Count != other.Count) return false;
            foreach (var entry in entries) if (!entry.Matches(other.Find(entry.UtteranceId))) return false;
            return true;
        }
    }

    public enum HoldemDealerSelectionKind { Unchanged, Change }

    /// <summary>Explicit terminal policy decision. An absent decision is an error, never implicit no-cheating evidence.</summary>
    public sealed class HoldemDealerSelection
    {
        private HoldemDealerSelection(HoldemDealerSelectionKind kind, Guid utteranceId, int boardIndex,
            Card card, HoldemCardSourceScope sourceScope)
        { Kind = kind; UtteranceId = utteranceId; BoardIndex = boardIndex; Card = card; SourceScope = sourceScope; }
        public static HoldemDealerSelection Unchanged() => new HoldemDealerSelection(HoldemDealerSelectionKind.Unchanged,
            Guid.Empty, 0, default, default);
        public static HoldemDealerSelection Change(Guid utteranceId, int boardIndex, Card card, HoldemCardSourceScope sourceScope)
        {
            if (utteranceId == Guid.Empty || boardIndex < 0 || boardIndex > 4 || !card.IsValid
                || sourceScope != HoldemCardSourceScope.UndealtOutsideCurrentHandRunout)
                throw new ArgumentException("An explicit source, slot, card and supported source scope are required.");
            return new HoldemDealerSelection(HoldemDealerSelectionKind.Change, utteranceId, boardIndex, card, sourceScope);
        }
        public HoldemDealerSelectionKind Kind { get; }
        public Guid UtteranceId { get; }
        public int BoardIndex { get; }
        public Card Card { get; }
        public HoldemCardSourceScope SourceScope { get; }
    }

    /// <summary>Host-owner thread only. The coordinator receives its selection policy explicitly.</summary>
    public interface IHoldemDealerSelectionPolicy
    {
        HoldemDealerSelection Select(HoldemDealerWork work, HoldemDealerInterpretationBatch interpretations);
    }

    /// <summary>The same host authority must provide both the pending turn and actual application.</summary>
    public interface IHoldemDealerDealApplicationPort : IHoldemDealerTurnPort
    {
        HoldemRoomReceipt ApplyDealerDeal(HoldemDealCommand command);
    }

    /// <summary>In-process host capability. Never serialized or accepted as a player command.</summary>
    public interface IHoldemHostDealerPort : IHoldemDealerDealApplicationPort, IHoldemAccusationResolutionPort
    {
        IReadOnlyList<HoldemUtteranceEntry> ReadDesignatedUtterances();
        IReadOnlyList<HoldemUtteranceBatch> ReadPendingUtterances();
        bool AcknowledgeUtteranceBatch(Guid windowId);
    }

    /// <summary>In-process host capability: text accepted in the still-open window. Never serialized.</summary>
    public interface IHoldemOpenUtterancePort
    {
        IReadOnlyList<HoldemUtteranceEntry> ReadOpenUtterances();
    }

    /// <summary>Optional in-process clock publication. No player wire command exposes this capability.</summary>
    public interface IHoldemProgressClockHostPort
    {
        bool PublishProgressClock(HoldemProgressClock sample);
    }

    /// <summary>Host capability only. A final conversation is distinct from an odd-chip settlement decision.</summary>
    public interface ISequentialConversationHostPort
    {
        HoldemRoomReceipt CloseFinalConversation(FinalConversationCommand command);
    }

    /// <summary>Sequential deadlines/reveal capability. Never exposed as player commands.</summary>
    public interface ISequentialProgressHostPort : IHoldemDealerTurnPort, IHoldemDealerInputDeadlinePort,
        ISequentialConversationHostPort
    {
        bool TryReadSequentialProgress(out HoldemSnapshot game, out bool paused);
        HoldemRoomReceipt ResumeAfterReveal(HoldemRevealCommand command);
        /// <summary>In-process host only. Rechecks the exact turn and applies legal Check, otherwise Fold.</summary>
        bool TryExpireTurn(Guid handId, long handVersion, SeatId actor);
    }

    /// <summary>Local host progression only. Read returns the host's own viewer projection, not other private hands.</summary>
    public interface IHoldemSocialHostPort : IHoldemHostDealerPort, IHoldemDealerInputDeadlinePort
    {
        bool TryReadProgress(out HoldemRoomView view);
        HoldemRoomReceipt SealFixedBetting(FixedBettingHostCommand command);
        HoldemRoomReceipt ResumeAfterReveal(HoldemRevealCommand command);
        HoldemRoomReceipt ResolveSettlement(Guid handId, Guid commandId, long expectedVersion);
    }
}
