using System;
using System.Collections.Generic;
using Poker.Foundation;

namespace Poker.Application
{
    /// <summary>Seat-free transport input. Only the room can assign the authenticated speaker.</summary>
    public sealed class HoldemRoomUtterance
    {
        public HoldemRoomUtterance(Guid sessionId, Guid handId, Guid windowId, Guid commandId,
            HoldemStreet street, string text, Guid designatedMessageId = default)
        {
            if (sessionId == Guid.Empty || handId == Guid.Empty || windowId == Guid.Empty || commandId == Guid.Empty)
                throw new ArgumentException("Session, hand, window and command IDs are required.");
            if (street < HoldemStreet.Preflop || street > HoldemStreet.River)
                throw new ArgumentOutOfRangeException(nameof(street));
            Text = text ?? throw new ArgumentNullException(nameof(text));
            SessionId = sessionId; HandId = handId; WindowId = windowId; CommandId = commandId; Street = street;
            DesignatedMessageId = designatedMessageId;
        }
        public Guid SessionId { get; }
        public Guid HandId { get; }
        public Guid WindowId { get; }
        public Guid CommandId { get; }
        public HoldemStreet Street { get; }
        public string Text { get; }
        public Guid DesignatedMessageId { get; }
        internal HoldemUtteranceCommand Bind(SeatId seat) => DesignatedMessageId == Guid.Empty
            ? new HoldemUtteranceCommand(SessionId, HandId, WindowId, CommandId, Street, seat, Text)
            : HoldemUtteranceCommand.Designate(SessionId, HandId, WindowId, CommandId, Street, seat, DesignatedMessageId);
    }

    public sealed class HoldemRoomUtteranceReceipt
    {
        internal HoldemRoomUtteranceReceipt(HoldemRoomError error, HoldemUtteranceReceipt utterance = null)
        { Error = error; Utterance = utterance; }
        public HoldemRoomError Error { get; }
        public HoldemUtteranceReceipt Utterance { get; }
        public bool Accepted => Error == HoldemRoomError.None && Utterance != null && Utterance.Accepted;
    }

    public sealed partial class HoldemRoom
    {
        private readonly HoldemUtterancePolicy utterancePolicy;
        private HoldemUtteranceInbox utterances;
        private Guid utteranceHandId;
        private long lastUtteranceOrdinal;
        private readonly List<HoldemPublicUtterance> publicRemarks = new List<HoldemPublicUtterance>();
        private readonly int[] designationRevisions = new int[HoldemRoom.Capacity + 1];
        // Technical dealer backpressure only. Sequential public chat remains independent.
        public const int MaximumPendingUtteranceBatches = 16;
        private readonly List<HoldemUtteranceBatch> pendingUtteranceBatches = new List<HoldemUtteranceBatch>();
        private readonly HashSet<Guid> capturedUtteranceWindows = new HashSet<Guid>();
        private bool RetainsDealerInput => utterancePolicy != null
            && utterancePolicy.BatchRetention == HoldemUtteranceBatchRetention.UntilHostAcknowledges;

        /// <summary>Optional own-text confirmation. Null before the first hand or when disabled.</summary>
        public HoldemUtteranceView ReadUtterances(Guid connection)
        {
            lock (gate)
            {
                if (FindConnected(connection, out var member) != HoldemRoomError.None)
                    throw new InvalidOperationException("The connection has no active room binding.");
                if (utterances == null) return null;
                var view = utterances.Bind(member.Seat).ReadUtterances();
                if (!IsPaused() && pendingUtteranceBatches.Count < MaximumPendingUtteranceBatches) return view;
                var entries = new HoldemUtteranceEntry[view.Count];
                for (int i = 0; i < entries.Length; i++) entries[i] = view.GetEntry(i);
                return new HoldemUtteranceView(ReadGameSnapshot(member), member.Seat, view.WindowId,
                    !IsPaused() && view.UnlimitedMessages && view.CanSubmit, view.MaximumTextLength, view.Remaining, entries,
                    pendingUtteranceBatches.Count >= MaximumPendingUtteranceBatches || view.IsBacklogged,
                    unlimitedMessages: view.UnlimitedMessages);
            }
        }

        public HoldemRoomUtteranceReceipt SubmitUtterance(Guid connection, HoldemRoomUtterance input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            lock (gate)
            {
                var error = Guard(connection, false, out var member);
                if (error != HoldemRoomError.None) return new HoldemRoomUtteranceReceipt(error);
                if (utterances == null)
                    return new HoldemRoomUtteranceReceipt(HoldemRoomError.UtteranceRejected,
                        new HoldemUtteranceReceipt(utterancePolicy == null ? HoldemUtteranceError.Disabled
                            : HoldemUtteranceError.WindowClosed, input.CommandId));
                var command = input.Bind(member.Seat);
                var port = utterances.Bind(member.Seat);
                if (pendingUtteranceBatches.Count >= MaximumPendingUtteranceBatches
                    && (command.IsDesignation || rules.BettingPolicy != HoldemBettingPolicy.SharedAnteSequential))
                {
                    bool retry = false;
                    var view = port.ReadUtterances();
                    for (int i = 0; i < view.Count; i++)
                        if (view.GetEntry(i).CommandId == input.CommandId || view.GetEntry(i).DesignationId == input.CommandId)
                        { retry = true; break; }
                    if (!retry) return new HoldemRoomUtteranceReceipt(HoldemRoomError.UtteranceRejected,
                        new HoldemUtteranceReceipt(HoldemUtteranceError.BacklogFull, input.CommandId));
                }
                bool storageWasFaulted = utterances.StorageFaulted;
                var receipt = port.SubmitUtterance(command);
                if (!storageWasFaulted && utterances.StorageFaulted) revision++;
                if (receipt.Accepted && receipt.Ordinal > lastUtteranceOrdinal)
                {
                    if (!command.IsDesignation && utterancePolicy.Visibility == HoldemUtteranceVisibility.PublicRaw)
                        publicRemarks.Add(new HoldemPublicUtterance(member.Seat, input.Street, input.Text,
                            rules.BettingPolicy != HoldemBettingPolicy.NoLimit ? (long?)null : omittedHistoryCount + history.Count));
                    lastUtteranceOrdinal = receipt.Ordinal;
                    // Private designation must not change another viewer's packet or public revision.
                    if (!command.IsDesignation) revision++;
                    else designationRevisions[member.Seat.Value]++;
                }
                return new HoldemRoomUtteranceReceipt(receipt.Accepted ? HoldemRoomError.None
                    : HoldemRoomError.UtteranceRejected, receipt);
            }
        }

        /// <summary>
        /// Trusted server-side consumer only; never expose this through player packets.
        /// Nonempty batches survive hand transitions until the consumer acknowledges their WindowId.
        /// Repeated reads return the same batches: consumers must deduplicate by SessionId and WindowId.
        /// </summary>
        public IReadOnlyList<HoldemUtteranceBatch> ReadClosedDesignatedBatches(Guid hostConnection)
        {
            lock (gate)
            {
                if (FindConnected(hostConnection, out var member) != HoldemRoomError.None || member != members[0])
                    throw new InvalidOperationException("Only the connected host can read closed input batches.");
                return Array.AsReadOnly(pendingUtteranceBatches.ToArray());
            }
        }

        /// <summary>
        /// Trusted server-side consumer only. Text accepted in the still-open window, so interpretation
        /// can start before betting closes; the closed batch remains the only input a deal may use.
        /// </summary>
        public IReadOnlyList<HoldemUtteranceEntry> ReadOpenUtterances(Guid hostConnection)
        {
            lock (gate)
            {
                if (FindConnected(hostConnection, out var member) != HoldemRoomError.None || member != members[0])
                    throw new InvalidOperationException("Only the connected host can read open input.");
                return utterances == null ? Array.Empty<HoldemUtteranceEntry>() : utterances.ReadOpenEntries();
            }
        }

        public IReadOnlyList<HoldemUtteranceEntry> ReadDesignatedUtterances(Guid hostConnection)
        {
            lock (gate)
            {
                if (FindConnected(hostConnection, out var member) != HoldemRoomError.None || member != members[0])
                    throw new InvalidOperationException("Only the connected host can read dealer input.");
                return utterances?.ReadDesignatedUtterances() ?? Array.Empty<HoldemUtteranceEntry>();
            }
        }

        public DealerFeatureInput ReadFeatureInput(Guid hostConnection)
        {
            lock (gate)
            {
                var error = Guard(hostConnection, true, out _);
                if (error != HoldemRoomError.None) return new DealerFeatureInput(error);
                if (!session.CurrentHandId.HasValue) return new DealerFeatureInput(HoldemRoomError.None);
                SynchronizeUtterances();
                return new DealerFeatureInput(HoldemRoomError.None, utterances?.ReadCurrentFeatureWindow(),
                    eliminatedSpeech?.ReadCurrentFeatureWindow());
            }
        }

        /// <summary>Acknowledge retained raw input, not AI interpretation or manipulation success.</summary>
        public bool AcknowledgeUtteranceBatch(Guid hostConnection, Guid windowId)
        {
            lock (gate)
            {
                if (FindConnected(hostConnection, out var member) != HoldemRoomError.None || member != members[0])
                    throw new InvalidOperationException("Only the connected host can acknowledge input batches.");
                int index = pendingUtteranceBatches.FindIndex(batch => batch.WindowId == windowId);
                if (index < 0) return false;
                bool wasFull = pendingUtteranceBatches.Count >= MaximumPendingUtteranceBatches;
                pendingUtteranceBatches.RemoveAt(index);
                // Routine private consumption has no public event; only restored input availability changes the view.
                if (wasFull) revision++;
                return true;
            }
        }

        // Called under the room gate, like betting and initial remark acceptance.
        private HoldemPublicUtterances ReadPublicRemarks(HoldemSnapshot game)
        {
            if (game == null || utterancePolicy == null || utterancePolicy.Visibility != HoldemUtteranceVisibility.PublicRaw)
                return null;
            return new HoldemPublicUtterances(game.SessionId, game.HandId, publicRemarks.ToArray(),
                rules.BettingPolicy == HoldemBettingPolicy.NoLimit);
        }

        private void SynchronizeUtterances()
        {
            if (utterancePolicy == null || !session.CurrentHandId.HasValue) return;
            if (utteranceHandId != session.CurrentHandId.Value)
            {
                utteranceHandId = session.CurrentHandId.Value; lastUtteranceOrdinal = 0;
                publicRemarks.Clear();
                Array.Clear(designationRevisions, 0, designationRevisions.Length);
                capturedUtteranceWindows.Clear();
            }
            if (utterances == null)
                utterances = new HoldemUtteranceInbox(() => ReadGameSnapshot(members[0]), utterancePolicy);
            else utterances.Synchronize();
            SynchronizeEliminatedSpeech();
            if (!RetainsDealerInput) return;
            foreach (var batch in utterances.ReadClosedDesignatedBatches())
                if (capturedUtteranceWindows.Add(batch.WindowId) && batch.Count > 0)
                    pendingUtteranceBatches.Add(batch);
        }

        /// <summary>Cheap caller-bound publication stamp; avoids rebuilding speech arrays on every network tick.</summary>
        public void ReadProjectionStamp(Guid connection, out long publicRevision, out int ownDesignationRevision)
            => ReadProjectionStamp(connection, out publicRevision, out ownDesignationRevision, out _);

        public void ReadProjectionStamp(Guid connection, out long publicRevision, out int ownDesignationRevision,
            out long eliminatedSpeechRevision)
        {
            lock (gate)
            {
                if (FindConnected(connection, out var member) != HoldemRoomError.None)
                    throw new InvalidOperationException("The connection has no active room binding.");
                publicRevision = revision; ownDesignationRevision = designationRevisions[member.Seat.Value];
                eliminatedSpeechRevision = ReadEliminatedRevision(member);
            }
        }
    }
}
