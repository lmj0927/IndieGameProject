using System;
using System.Collections.Generic;
using System.Diagnostics;
using Poker.Foundation;

namespace Poker.Application
{
    /// <summary>
    /// Serialized host intake, independent of betting revisions. Retains at most the current hand's four
    /// windows. Sequential play has no speaking quota; technical rate/storage guards are separate.
    /// This is not a network authenticator, broadcaster, AI interpreter or card-manipulation request queue.
    /// </summary>
    public sealed class HoldemUtteranceInbox
    {
        private readonly Func<HoldemSnapshot> readState;
        private readonly HoldemUtterancePolicy policy;
        private readonly HoldemSpeechAudience audience;
        private readonly Guid sessionId;
        private Guid handId;
        private readonly List<Window> windows = new List<Window>();
        private readonly Dictionary<Guid, Accepted> accepted = new Dictionary<Guid, Accepted>();
        private Window current;
        private long ordinal;
        // Operational safety, not a gameplay allowance. Accepted text is never evicted to fit these bounds.
        public const int MaximumRetainedEntries = 65536;
        public const long MaximumRetainedBytes = 16 * 1024 * 1024;
        private const double SpeechBurst = 32, SpeechPerSecond = 8;
        private readonly Func<double> seconds;
        private readonly double[] tokens = { SpeechBurst, SpeechBurst, SpeechBurst, SpeechBurst, SpeechBurst };
        private readonly double[] rateTimes = new double[5];
        private long retainedBytes;
        private bool inputClosed;
        public bool StorageFaulted { get; private set; }
        // Local view invalidation only; never a betting revision or a participant packet field.
        private long sharedProjectionRevision;
        private readonly long[] ownProjectionRevisions = new long[5];
        internal long ReadProjectionRevision(SeatId viewer)
        {
            Synchronize();
            return sharedProjectionRevision + ownProjectionRevisions[viewer.Value];
        }

        public HoldemUtteranceInbox(Func<HoldemSnapshot> readState, HoldemUtterancePolicy policy, Func<double> seconds = null,
            HoldemSpeechAudience audience = HoldemSpeechAudience.Table)
        {
            this.readState = readState ?? throw new ArgumentNullException(nameof(readState));
            this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
            this.seconds = seconds ?? (() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
            var state = readState() ?? throw new InvalidOperationException("An active table snapshot is required.");
            if (!Enum.IsDefined(typeof(HoldemSpeechAudience), audience)
                || audience == HoldemSpeechAudience.Eliminated && state.BettingPolicy != HoldemBettingPolicy.SharedAnteSequential)
                throw new ArgumentException("Eliminated speech requires sequential play.", nameof(audience));
            this.audience = audience;
            sessionId = state.SessionId;
            Synchronize();
        }

        // The caller is the trusted host. Bind only the authenticated connection's assigned seat.
        public IHoldemUtterancePlayerPort Bind(SeatId authorizedSeat)
        {
            if (inputClosed) throw new InvalidOperationException("This speech input has been closed.");
            var state = Synchronize();
            if (!IsRosterSeat(state, authorizedSeat)) throw new ArgumentException("Unknown table seat.", nameof(authorizedSeat));
            return new SeatPort(this, authorizedSeat);
        }

        // Shutdown is not a normal dealer deadline: retain text without creating a new batch.
        internal void CloseInput()
        {
            if (inputClosed) return;
            inputClosed = true;
            foreach (var window in windows) window.Features.Close();
            sharedProjectionRevision++;
        }

        public HoldemSnapshot Synchronize()
        {
            var state = readState() ?? throw new InvalidOperationException("The table snapshot is unavailable.");
            if (state.SessionId != sessionId) throw new InvalidOperationException("An inbox cannot move between sessions.");
            if (state.HandId != handId)
            {
                CloseCurrent(); windows.Clear(); accepted.Clear(); ordinal = 0;
                handId = state.HandId; current = null;
                retainedBytes = 0; StorageFaulted = false;
                sharedProjectionRevision++;
                for (int i = 1; i < tokens.Length; i++) { tokens[i] = SpeechBurst; rateTimes[i] = this.seconds(); }
            }
            bool open = IsBettingSpeechPhase(state);
            if (current != null && (current.Street != state.Street || !open)) CloseCurrent();
            if (open && current == null)
            {
                // A closed window may never reopen even if a caller supplies a regressed snapshot.
                bool knownStreet = false;
                foreach (var window in windows) if (window.Street == state.Street) knownStreet = true;
                if (!knownStreet)
                {
                    current = new Window(state.Street); windows.Add(current);
                    sharedProjectionRevision++;
                }
            }
            return state;
        }

        public IReadOnlyList<HoldemUtteranceBatch> ReadClosedDesignatedBatches()
        {
            Synchronize();
            var result = new List<HoldemUtteranceBatch>();
            foreach (var window in windows) if (window.Batch != null) result.Add(window.Batch);
            return result.AsReadOnly();
        }

        // Host-only early input. Poll while betting to begin interpreting newly designated messages.
        // CommandId identifies the source; consumers must discard results from a different hand/window.
        public IReadOnlyList<HoldemUtteranceEntry> ReadOpenEntries()
            => current == null ? Array.Empty<HoldemUtteranceEntry>() : current.Entries.ToArray();

        public IReadOnlyList<HoldemUtteranceEntry> ReadDesignatedUtterances()
        {
            Synchronize();
            var result = new List<HoldemUtteranceEntry>();
            foreach (var window in windows)
                foreach (var entry in window.Entries)
                    if (entry.DesignationId != Guid.Empty) result.Add(entry);
            return result.AsReadOnly();
        }

        internal DealerFeatureWindow ReadCurrentFeatureWindow()
        {
            if (inputClosed) return null;
            var state = Synchronize();
            if (state.BettingPolicy != HoldemBettingPolicy.SharedAnteSequential || current == null
                || current.Street >= HoldemStreet.River || state.IsRevealPending) return null;
            var entries = current.Entries.FindAll(entry => entry.DesignationId != Guid.Empty);
            var batch = current.Batch ?? new HoldemUtteranceBatch(sessionId, handId, current.Id,
                current.Street, entries.ToArray(), audience);
            lock (current.Features.Gate)
                return new DealerFeatureWindow(batch, current.Features, current.Features.Closed);
        }

        // Independent of dealer delivery/acknowledgement. Retain the current hand's accepted raw text.
        public HoldemPublicUtterances ReadPublicUtterances()
        {
            if (audience != HoldemSpeechAudience.Table || policy.Visibility != HoldemUtteranceVisibility.PublicRaw) return null;
            return ReadRawMessages();
        }

        internal HoldemPublicUtterances ReadEliminatedMessages()
        {
            if (audience != HoldemSpeechAudience.Eliminated) throw new InvalidOperationException("Not an eliminated channel.");
            return ReadRawMessages();
        }

        private HoldemPublicUtterances ReadRawMessages()
        {
            var state = Synchronize();
            var entries = new List<HoldemPublicUtterance>();
            foreach (var window in windows)
                foreach (var entry in window.Entries)
                    entries.Add(new HoldemPublicUtterance(entry.Speaker, entry.Street, entry.Text));
            return new HoldemPublicUtterances(state.SessionId, state.HandId, entries.ToArray());
        }

        private void CloseCurrent()
        {
            if (current == null) return;
            FreezeCurrent();
            current = null;
            sharedProjectionRevision++;
        }

        // Explicit host deadline. Reading pending dealer work must never close player input.
        public bool FreezeDealerInput(Guid dealWindowId)
        {
            if (inputClosed) return false;
            var state = Synchronize();
            if (state.BettingPolicy == HoldemBettingPolicy.NoLimit || current == null
                || !state.IsDealPending || state.PendingDeal?.WindowId != dealWindowId
                || current.Street >= HoldemStreet.River || current.Street != state.PendingDeal.SourceStreet) return false;
            FreezeCurrent();
            return true;
        }

        private void FreezeCurrent()
        {
            if (current == null || current.Batch != null) return;
            current.Features.Close();
            var designated = policy.Visibility == HoldemUtteranceVisibility.OwnOnly
                ? new List<HoldemUtteranceEntry>(current.Entries)
                : current.Entries.FindAll(entry => entry.DesignationId != Guid.Empty);
            if (current.Street < HoldemStreet.River)
                current.Batch = new HoldemUtteranceBatch(sessionId, handId, current.Id, current.Street, designated.ToArray(), audience);
            sharedProjectionRevision++;
        }

        private HoldemUtteranceView Read(SeatId viewer)
        {
            var state = Synchronize();
            if (!IsRosterSeat(state, viewer)) throw new InvalidOperationException("The bound seat is no longer in the roster.");
            var own = new List<HoldemUtteranceEntry>();
            foreach (var window in windows)
                foreach (var entry in window.Entries) if (entry.Speaker == viewer) own.Add(entry);
            bool? requestBucket = state.BettingPolicy != HoldemBettingPolicy.NoLimit
                ? (bool?)(CanRequest(state, viewer) && current?.Batch == null) : null;
            bool unlimited = state.BettingPolicy == HoldemBettingPolicy.SharedAnteSequential;
            int remaining = current == null || unlimited ? 0 : policy.MaximumPerSeatPerStreet - CountFor(current, viewer, requestBucket);
            bool canSubmit = !inputClosed && current != null && (unlimited || remaining > 0) && !StorageFaulted && Allows(state.GetSeat(viewer));
            bool canDesignate = !inputClosed && current != null && current.Street < HoldemStreet.River && Allows(state.GetSeat(viewer))
                && !StorageFaulted && current.Batch == null && CanRequest(state, viewer)
                && policy.Visibility == HoldemUtteranceVisibility.PublicRaw
                && policy.BatchRetention == HoldemUtteranceBatchRetention.UntilHostAcknowledges
                && !current.Entries.Exists(entry => entry.Speaker == viewer && entry.DesignationId != Guid.Empty);
            return new HoldemUtteranceView(state, viewer, current?.Id ?? Guid.Empty, canSubmit,
                policy.MaximumTextLength, remaining, own.ToArray(), isBacklogged: StorageFaulted,
                canDesignate: canDesignate, unlimitedMessages: unlimited);
        }

        private HoldemUtteranceReceipt Submit(SeatId authorizedSeat, HoldemUtteranceCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            if (inputClosed) return new HoldemUtteranceReceipt(HoldemUtteranceError.WindowClosed, command.CommandId);
            var state = Synchronize();
            if (command.SessionId != sessionId) return Reject(HoldemUtteranceError.WrongSession);
            if (command.HandId != handId) return Reject(HoldemUtteranceError.WrongHand);
            if (command.Speaker != authorizedSeat || !IsRosterSeat(state, authorizedSeat))
                return Reject(HoldemUtteranceError.UnauthorizedSeat);
            if (accepted.TryGetValue(command.CommandId, out var previous))
                return previous.Command.SamePayload(command) ? previous.Receipt : Reject(HoldemUtteranceError.CommandConflict);
            if (StorageFaulted) return Reject(HoldemUtteranceError.StorageFull);
            if (current == null || !IsBettingSpeechPhase(state)) return Reject(HoldemUtteranceError.WindowClosed);
            if (command.WindowId != current.Id || command.Street != current.Street) return Reject(HoldemUtteranceError.WrongWindow);
            if (!Allows(state.GetSeat(authorizedSeat))) return Reject(HoldemUtteranceError.SeatNotAllowed);
            if (command.IsDesignation)
            {
                if (current.Street >= HoldemStreet.River || policy.Visibility != HoldemUtteranceVisibility.PublicRaw
                    || policy.BatchRetention != HoldemUtteranceBatchRetention.UntilHostAcknowledges)
                    return Reject(HoldemUtteranceError.Disabled);
                if (current.Batch != null || !CanRequest(state, authorizedSeat)) return Reject(HoldemUtteranceError.WindowClosed);
                int messageIndex = current.Entries.FindIndex(entry => entry.CommandId == command.DesignatedMessageId);
                if (messageIndex < 0) return Reject(HoldemUtteranceError.UnknownMessage);
                var message = current.Entries[messageIndex];
                if (message.Speaker != authorizedSeat) return Reject(HoldemUtteranceError.NotOwnMessage);
                if (!message.DesignationEligible) return Reject(HoldemUtteranceError.NotRequestMessage);
                if (current.Entries.Exists(entry => entry.Speaker == authorizedSeat && entry.DesignationId != Guid.Empty))
                    return Reject(HoldemUtteranceError.AlreadyDesignated);
                var selection = new HoldemUtteranceReceipt(HoldemUtteranceError.None, command.CommandId, ++ordinal);
                current.Entries[messageIndex] = new HoldemUtteranceEntry(message, command.CommandId);
                accepted.Add(command.CommandId, new Accepted(command, selection));
                ownProjectionRevisions[authorizedSeat.Value]++;
                return selection;
            }
            if (string.IsNullOrWhiteSpace(command.Text)) return Reject(HoldemUtteranceError.EmptyText);
            if (command.Text.Length > policy.MaximumTextLength) return Reject(HoldemUtteranceError.TextTooLong);
            if (!ValidText(command.Text)) return Reject(HoldemUtteranceError.InvalidText);
            bool requestEligible = CanRequest(state, authorizedSeat) && current.Batch == null;
            // Shared play has a reaction period before the next request period on the same street.
            // Reaction chatter must not consume the following card's request allowance.
            bool? quotaBucket = state.BettingPolicy != HoldemBettingPolicy.NoLimit ? (bool?)requestEligible : null;
            bool unlimited = state.BettingPolicy == HoldemBettingPolicy.SharedAnteSequential;
            if (!unlimited && CountFor(current, authorizedSeat, quotaBucket) >= policy.MaximumPerSeatPerStreet)
                return Reject(HoldemUtteranceError.LimitReached);
            if (unlimited)
            {
                long bytes = 2L * command.Text.Length + 512;
                if (retainedBytes + bytes > MaximumRetainedBytes || ordinal >= MaximumRetainedEntries)
                { StorageFaulted = true; sharedProjectionRevision++; return Reject(HoldemUtteranceError.StorageFull); }
                if (!ConsumeSpeechToken(authorizedSeat)) return Reject(HoldemUtteranceError.RateLimited);
                retainedBytes += bytes;
            }
            var receipt = new HoldemUtteranceReceipt(HoldemUtteranceError.None, command.CommandId, ++ordinal);
            current.Entries.Add(new HoldemUtteranceEntry(command, ordinal, requestEligible));
            accepted.Add(command.CommandId, new Accepted(command, receipt));
            if (policy.Visibility == HoldemUtteranceVisibility.PublicRaw) sharedProjectionRevision++;
            else ownProjectionRevisions[authorizedSeat.Value]++;
            return receipt;

            HoldemUtteranceReceipt Reject(HoldemUtteranceError error) => new HoldemUtteranceReceipt(error, command.CommandId);
        }

        internal static bool ValidText(string text) => HoldemPlayerText.IsValidSingleLine(text);

        private bool ConsumeSpeechToken(SeatId seat)
        {
            double now = seconds();
            tokens[seat.Value] = Math.Min(SpeechBurst, tokens[seat.Value] + Math.Max(0, now - rateTimes[seat.Value]) * SpeechPerSecond);
            rateTimes[seat.Value] = Math.Max(now, rateTimes[seat.Value]);
            if (tokens[seat.Value] < 1) return false;
            tokens[seat.Value]--; return true;
        }

        private static bool IsRosterSeat(HoldemSnapshot state, SeatId seat)
        {
            for (int i = 0; i < state.SeatCount; i++) if (state.GetSeatAt(i).Seat == seat) return true;
            return false;
        }
        private static bool IsBettingSpeechPhase(HoldemSnapshot state) => state.Result == null
            && !state.IsOver && !state.IsSettlementPending
            && (state.Street >= HoldemStreet.Preflop && state.Street <= HoldemStreet.River)
            && (state.BettingPolicy != HoldemBettingPolicy.NoLimit
                || !state.IsDealPending && !state.IsRevealPending && state.Accusations == null && state.CurrentSeat.HasValue);
        private static bool IsRequestPhase(HoldemSnapshot state) => state.Street < HoldemStreet.River
            && (state.BettingPolicy == HoldemBettingPolicy.SharedAnteFixed ? state.IsDealPending
                : state.BettingPolicy == HoldemBettingPolicy.SharedAnteSequential
                    ? IsBettingSpeechPhase(state) && !state.IsRevealPending : IsBettingSpeechPhase(state));
        private bool Allows(HoldemSeatView seat) => audience == HoldemSpeechAudience.Eliminated
            ? seat.Status == HoldemSeatStatus.Busted && !seat.WasDealtIn : policy.Allows(seat);
        private bool CanRequest(HoldemSnapshot state, SeatId viewer) => IsRequestPhase(state)
            && Allows(state.GetSeat(viewer))
            && (state.BettingPolicy == HoldemBettingPolicy.NoLimit
                || audience == HoldemSpeechAudience.Eliminated
                || state.GetSeat(viewer).Status == HoldemSeatStatus.Active || state.GetSeat(viewer).Status == HoldemSeatStatus.AllIn);
        private static int CountFor(Window window, SeatId seat, bool? requestBucket = null)
        {
            int result = 0;
            foreach (var entry in window.Entries)
                if (entry.Speaker == seat && (!requestBucket.HasValue || entry.DesignationEligible == requestBucket.Value)) result++;
            return result;
        }
        private sealed class Window
        {
            public Window(HoldemStreet street) { Street = street; Id = Guid.NewGuid(); }
            public readonly Guid Id;
            public readonly HoldemStreet Street;
            public readonly List<HoldemUtteranceEntry> Entries = new List<HoldemUtteranceEntry>();
            internal readonly DealerInputLifetime Features = new DealerInputLifetime();
            public HoldemUtteranceBatch Batch;
        }
        private sealed class Accepted
        {
            public Accepted(HoldemUtteranceCommand command, HoldemUtteranceReceipt receipt)
            { Command = command; Receipt = receipt; }
            public readonly HoldemUtteranceCommand Command;
            public readonly HoldemUtteranceReceipt Receipt;
        }
        private sealed class SeatPort : IHoldemUtterancePlayerPort
        {
            private readonly HoldemUtteranceInbox inbox;
            private readonly SeatId seat;
            public SeatPort(HoldemUtteranceInbox inbox, SeatId seat) { this.inbox = inbox; this.seat = seat; }
            public HoldemUtteranceView ReadUtterances() => inbox.Read(seat);
            public HoldemUtteranceReceipt SubmitUtterance(HoldemUtteranceCommand command) => inbox.Submit(seat, command);
        }
    }
}
