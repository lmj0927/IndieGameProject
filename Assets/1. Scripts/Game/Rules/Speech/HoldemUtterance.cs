using System;
using Poker.Foundation;

namespace Poker.Application
{
    [Flags]
    public enum HoldemUtteranceSeats { None = 0, Active = 1, AllIn = 2, Folded = 4, Busted = 8 }

    public enum HoldemUtteranceVisibility { OwnOnly = 0, PublicRaw = 1 }
    public enum HoldemUtteranceBatchRetention { UntilHostAcknowledges = 0, None = 1 }
    public enum HoldemSpeechAudience { Table = 0, Eliminated = 1 }

    /// <summary>Explicit intake limits, not team gameplay defaults. Length is measured in UTF-16 code units.</summary>
    public sealed class HoldemUtterancePolicy
    {
        public HoldemUtterancePolicy(int maximumTextLength, int maximumPerSeatPerStreet, HoldemUtteranceSeats allowedSeats,
            HoldemUtteranceVisibility visibility = HoldemUtteranceVisibility.PublicRaw,
            HoldemUtteranceBatchRetention batchRetention = HoldemUtteranceBatchRetention.UntilHostAcknowledges)
        {
            if (maximumTextLength < 1 || maximumTextLength > 4096)
                throw new ArgumentOutOfRangeException(nameof(maximumTextLength));
            if (maximumPerSeatPerStreet < 1 || maximumPerSeatPerStreet > 32)
                throw new ArgumentOutOfRangeException(nameof(maximumPerSeatPerStreet));
            if ((allowedSeats & ~(HoldemUtteranceSeats.Active | HoldemUtteranceSeats.AllIn | HoldemUtteranceSeats.Folded | HoldemUtteranceSeats.Busted)) != 0)
                throw new ArgumentOutOfRangeException(nameof(allowedSeats));
            if (!Enum.IsDefined(typeof(HoldemUtteranceVisibility), visibility))
                throw new ArgumentOutOfRangeException(nameof(visibility));
            if (!Enum.IsDefined(typeof(HoldemUtteranceBatchRetention), batchRetention))
                throw new ArgumentOutOfRangeException(nameof(batchRetention));
            MaximumTextLength = maximumTextLength; MaximumPerSeatPerStreet = maximumPerSeatPerStreet;
            AllowedSeats = allowedSeats; Visibility = visibility;
            BatchRetention = batchRetention;
        }
        public int MaximumTextLength { get; }
        // Legacy betting shares one street allowance. SharedAnteFixed reserves this allowance
        // separately for request-eligible messages and ordinary reactions on that street.
        public int MaximumPerSeatPerStreet { get; }
        public HoldemUtteranceSeats AllowedSeats { get; }
        public HoldemUtteranceVisibility Visibility { get; }
        // Host delivery is independent of public display. None does not claim AI consumption.
        public HoldemUtteranceBatchRetention BatchRetention { get; }
        internal static void RequireSequentialInput(HoldemConfig config, HoldemUtterancePolicy policy)
        {
            if (config.BettingPolicy != HoldemBettingPolicy.SharedAnteSequential) return;
            var liveSeats = HoldemUtteranceSeats.Active | HoldemUtteranceSeats.AllIn;
            if (policy == null || policy.Visibility != HoldemUtteranceVisibility.PublicRaw
                || policy.BatchRetention != HoldemUtteranceBatchRetention.UntilHostAcknowledges
                || (policy.AllowedSeats & liveSeats) != liveSeats)
                throw new ArgumentException("Sequential play requires public speech, retained designation and both active and all-in input.", nameof(policy));
        }
        internal bool Allows(HoldemSeatView seat)
        {
            if (!seat.WasDealtIn && seat.Status != HoldemSeatStatus.Busted) return false;
            var flag = seat.Status == HoldemSeatStatus.Active ? HoldemUtteranceSeats.Active
                : seat.Status == HoldemSeatStatus.AllIn ? HoldemUtteranceSeats.AllIn
                : seat.Status == HoldemSeatStatus.Folded ? HoldemUtteranceSeats.Folded
                : seat.Status == HoldemSeatStatus.Busted ? HoldemUtteranceSeats.Busted : HoldemUtteranceSeats.None;
            return flag != HoldemUtteranceSeats.None && (AllowedSeats & flag) != 0;
        }
    }

    /// <summary>Publish raw speech or designate one own message. Neither action authorizes a card change.</summary>
    public sealed class HoldemUtteranceCommand
    {
        public HoldemUtteranceCommand(Guid sessionId, Guid handId, Guid windowId, Guid commandId,
            HoldemStreet street, SeatId speaker, string text)
        {
            if (sessionId == Guid.Empty || handId == Guid.Empty || windowId == Guid.Empty || commandId == Guid.Empty)
                throw new ArgumentException("An utterance must identify its session, hand, window and command.");
            if (!speaker.IsValid) throw new ArgumentException("A speaker seat is required.", nameof(speaker));
            if (street < HoldemStreet.Preflop || street > HoldemStreet.River)
                throw new ArgumentOutOfRangeException(nameof(street));
            Text = text ?? throw new ArgumentNullException(nameof(text));
            SessionId = sessionId; HandId = handId; WindowId = windowId; CommandId = commandId;
            Street = street; Speaker = speaker;
        }
        public Guid SessionId { get; }
        public Guid HandId { get; }
        public Guid WindowId { get; }
        public Guid CommandId { get; }
        public HoldemStreet Street { get; }
        public SeatId Speaker { get; }
        public string Text { get; }
        public Guid DesignatedMessageId { get; private set; }
        public bool IsDesignation => DesignatedMessageId != Guid.Empty;

        public bool MatchesConfirmation(HoldemUtteranceEntry entry) => entry != null
            && entry.SessionId == SessionId && entry.HandId == HandId && entry.WindowId == WindowId
            && entry.Speaker == Speaker && entry.Street == Street
            && (IsDesignation ? entry.CommandId == DesignatedMessageId && entry.DesignationId == CommandId
                : entry.CommandId == CommandId && entry.Text == Text);

        public static HoldemUtteranceCommand Designate(Guid sessionId, Guid handId, Guid windowId,
            Guid commandId, HoldemStreet street, SeatId speaker, Guid messageId)
        {
            if (messageId == Guid.Empty || messageId == commandId)
                throw new ArgumentException("A separate existing message ID is required.", nameof(messageId));
            return new HoldemUtteranceCommand(sessionId, handId, windowId, commandId, street, speaker, "")
                { DesignatedMessageId = messageId };
        }
        internal bool SamePayload(HoldemUtteranceCommand other) => other != null
            && SessionId == other.SessionId && HandId == other.HandId && WindowId == other.WindowId
            && CommandId == other.CommandId && Street == other.Street && Speaker == other.Speaker && Text == other.Text
            && DesignatedMessageId == other.DesignatedMessageId;
    }

    public enum HoldemUtteranceError
    {
        None = 0, Disabled, WrongSession, WrongHand, UnauthorizedSeat, WrongWindow, WindowClosed,
        SeatNotAllowed, EmptyText, TextTooLong, InvalidText, LimitReached, CommandConflict, BacklogFull, DeliveryUnavailable,
        UnknownMessage, NotOwnMessage, AlreadyDesignated, NotRequestMessage, RateLimited, StorageFull
    }

    public sealed class HoldemUtteranceReceipt
    {
        internal HoldemUtteranceReceipt(HoldemUtteranceError error, Guid commandId, long ordinal = 0)
        { Error = error; CommandId = commandId; Ordinal = ordinal; }
        public bool Accepted => Error == HoldemUtteranceError.None;
        public HoldemUtteranceError Error { get; }
        public Guid CommandId { get; }
        /// <summary>Host observation order, never a game priority or manipulation entitlement.</summary>
        public long Ordinal { get; }
        /// <summary>Reconstruct a player confirmation without transmitting host observation order.</summary>
        public static HoldemUtteranceReceipt Confirmation(Guid commandId, HoldemUtteranceError error)
        {
            if (commandId == Guid.Empty || !Enum.IsDefined(typeof(HoldemUtteranceError), error))
                throw new ArgumentException("A valid command and confirmation error are required.");
            return new HoldemUtteranceReceipt(error, commandId);
        }
    }

    /// <summary>Immutable source text. Acceptance does not establish a real manipulation request.</summary>
    public sealed class HoldemUtteranceEntry
    {
        internal HoldemUtteranceEntry(HoldemUtteranceEntry source, Guid designationId)
        {
            SessionId = source.SessionId; HandId = source.HandId; WindowId = source.WindowId;
            CommandId = source.CommandId; Street = source.Street; Speaker = source.Speaker;
            Text = source.Text; Ordinal = source.Ordinal; DesignationId = designationId;
            DesignationEligible = source.DesignationEligible;
        }
        internal HoldemUtteranceEntry(HoldemUtteranceCommand command, long ordinal, bool designationEligible = true)
        {
            SessionId = command.SessionId; HandId = command.HandId; WindowId = command.WindowId;
            CommandId = command.CommandId; Street = command.Street; Speaker = command.Speaker;
            Text = command.Text; Ordinal = ordinal;
            DesignationEligible = designationEligible && Street < HoldemStreet.River;
        }
        public Guid SessionId { get; }
        public Guid HandId { get; }
        public Guid WindowId { get; }
        public Guid CommandId { get; }
        public HoldemStreet Street { get; }
        public SeatId Speaker { get; }
        public string Text { get; }
        public long Ordinal { get; }
        // Owner/host only. Not included in the public raw-message projection.
        public Guid DesignationId { get; }
        // Owner/host only: reaction text is not retroactively promoted into a later request.
        public bool DesignationEligible { get; }
    }

    /// <summary>Seat-bound intake view. Contains only this viewer's source text, not other players' speech.</summary>
    public sealed class HoldemUtteranceView
    {
        private readonly HoldemUtteranceEntry[] ownEntries;
        internal HoldemUtteranceView(HoldemSnapshot state, SeatId viewer, Guid windowId, bool canSubmit,
            int maximumTextLength, int remaining, HoldemUtteranceEntry[] entries, bool isBacklogged = false, bool canDesignate = false, bool unlimitedMessages = false)
            : this(state.SessionId, state.HandId, state.Street, viewer, windowId, canSubmit, maximumTextLength, remaining, entries, isBacklogged, canDesignate, unlimitedMessages) { }
        internal HoldemUtteranceView(Guid sessionId, Guid handId, HoldemStreet street, SeatId viewer, Guid windowId,
            bool canSubmit, int maximumTextLength, int remaining, HoldemUtteranceEntry[] entries, bool isBacklogged = false, bool canDesignate = false, bool unlimitedMessages = false)
        {
            SessionId = sessionId; HandId = handId; Street = street; ViewerSeat = viewer;
            WindowId = windowId; CanSubmit = canSubmit; MaximumTextLength = maximumTextLength;
            Remaining = remaining; ownEntries = (HoldemUtteranceEntry[])entries.Clone();
            IsBacklogged = isBacklogged;
            CanDesignate = canDesignate;
            UnlimitedMessages = unlimitedMessages;
        }
        public Guid SessionId { get; }
        public Guid HandId { get; }
        public Guid WindowId { get; }
        public HoldemStreet Street { get; }
        public SeatId ViewerSeat { get; }
        public bool CanSubmit { get; }
        public bool CanDesignate { get; }
        public bool IsBacklogged { get; }
        public int MaximumTextLength { get; }
        public int Remaining { get; }
        /// <summary>No per-street speaking quota. Remaining is zero rather than a fabricated large allowance.</summary>
        public bool UnlimitedMessages { get; }
        public int Count => ownEntries.Length;
        public HoldemUtteranceEntry GetEntry(int index) => ownEntries[index];
    }

    /// <summary>Frozen host-only designated messages. No game outcome or permission to manipulate cards is implied.</summary>
    public sealed class HoldemUtteranceBatch
    {
        private readonly HoldemUtteranceEntry[] entries;
        internal HoldemUtteranceBatch(Guid sessionId, Guid handId, Guid windowId, HoldemStreet street,
            HoldemUtteranceEntry[] entries, HoldemSpeechAudience audience = HoldemSpeechAudience.Table)
        {
            SessionId = sessionId; HandId = handId; WindowId = windowId; Street = street;
            this.entries = (HoldemUtteranceEntry[])entries.Clone();
            Audience = audience;
        }
        public Guid SessionId { get; }
        public Guid HandId { get; }
        public Guid WindowId { get; }
        public HoldemStreet Street { get; }
        public int Count => entries.Length;
        public HoldemSpeechAudience Audience { get; }
        public HoldemUtteranceEntry GetEntry(int index) => entries[index];
    }

    /// <summary>Eliminated viewers only. Group raw text and the caller's private designation are separate.</summary>
    public sealed class HoldemEliminatedSpeechView
    {
        internal HoldemEliminatedSpeechView(HoldemUtteranceView own, HoldemPublicUtterances messages, long revision)
        {
            Own = own ?? throw new ArgumentNullException(nameof(own));
            Messages = messages ?? throw new ArgumentNullException(nameof(messages));
            if (own.SessionId != messages.SessionId || own.HandId != messages.HandId || revision < 0)
                throw new ArgumentException("Eliminated speech must belong to the same hand.");
            Revision = revision;
        }
        public HoldemUtteranceView Own { get; }
        public HoldemPublicUtterances Messages { get; }
        public long Revision { get; }
    }

    /// <summary>Trusted host feed only. No weight or authorization to change a live player's cards.</summary>
    public interface IHoldemEliminatedSpeechHostPort
    {
        System.Collections.Generic.IReadOnlyList<HoldemUtteranceEntry> ReadEliminatedDesignations();
        System.Collections.Generic.IReadOnlyList<HoldemUtteranceBatch> ReadClosedEliminatedBatches();
        bool AcknowledgeEliminatedBatch(Guid windowId);
    }
    public interface IHoldemEliminatedSpeechSource
    {
        HoldemEliminatedSpeechView ReadEliminatedSpeech();
        IHoldemUtterancePlayerPort EliminatedUtterances { get; }
    }

    public interface IHoldemUtterancePlayerPort
    {
        HoldemUtteranceView ReadUtterances();
        HoldemUtteranceReceipt SubmitUtterance(HoldemUtteranceCommand command);
    }
}
