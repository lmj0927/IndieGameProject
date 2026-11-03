using System;
using System.Collections.Generic;
using Poker.Foundation;

namespace Poker.Application
{
    /// <summary>Separate audience, retention and publication stamp. Never adds to the public table feed.</summary>
    public sealed partial class HoldemRoom
    {
        private HoldemUtteranceInbox eliminatedSpeech;
        private Guid eliminatedHandId;
        private long eliminatedOrdinal, eliminatedGroupRevision;
        private readonly int[] eliminatedDesignationRevisions = new int[Capacity + 1];
        private readonly List<HoldemUtteranceBatch> pendingEliminatedBatches = new List<HoldemUtteranceBatch>();
        private readonly HashSet<Guid> capturedEliminatedWindows = new HashSet<Guid>();

        public HoldemEliminatedSpeechView ReadEliminatedSpeech(Guid connection)
        {
            lock (gate)
            {
                if (FindConnected(connection, out var member) != HoldemRoomError.None)
                    throw new InvalidOperationException("The connection has no active room binding.");
                var state = ReadGameSnapshot(member);
                if (eliminatedSpeech == null || state?.GetSeat(member.Seat).Status != HoldemSeatStatus.Busted) return null;
                var own = eliminatedSpeech.Bind(member.Seat).ReadUtterances();
                if (IsPaused() || pendingEliminatedBatches.Count >= MaximumPendingUtteranceBatches)
                {
                    var entries = new HoldemUtteranceEntry[own.Count];
                    for (int i = 0; i < entries.Length; i++) entries[i] = own.GetEntry(i);
                    own = new HoldemUtteranceView(state, member.Seat, own.WindowId,
                        !IsPaused() && own.CanSubmit, own.MaximumTextLength, 0, entries,
                        pendingEliminatedBatches.Count >= MaximumPendingUtteranceBatches || own.IsBacklogged,
                        unlimitedMessages: true);
                }
                return new HoldemEliminatedSpeechView(own, eliminatedSpeech.ReadEliminatedMessages(), ReadEliminatedRevision(member));
            }
        }

        public HoldemRoomUtteranceReceipt SubmitEliminatedSpeech(Guid connection, HoldemRoomUtterance input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            lock (gate)
            {
                var error = Guard(connection, false, out var member);
                if (error != HoldemRoomError.None) return new HoldemRoomUtteranceReceipt(error);
                var state = ReadGameSnapshot(member);
                if (eliminatedSpeech == null || state?.GetSeat(member.Seat).Status != HoldemSeatStatus.Busted)
                    return new HoldemRoomUtteranceReceipt(HoldemRoomError.UtteranceRejected,
                        new HoldemUtteranceReceipt(HoldemUtteranceError.SeatNotAllowed, input.CommandId));
                var command = input.Bind(member.Seat);
                var port = eliminatedSpeech.Bind(member.Seat);
                if (command.IsDesignation && pendingEliminatedBatches.Count >= MaximumPendingUtteranceBatches)
                {
                    bool retry = false; var own = port.ReadUtterances();
                    for (int i = 0; i < own.Count; i++)
                        if (own.GetEntry(i).DesignationId == command.CommandId) { retry = true; break; }
                    if (!retry) return new HoldemRoomUtteranceReceipt(HoldemRoomError.UtteranceRejected,
                        new HoldemUtteranceReceipt(HoldemUtteranceError.BacklogFull, input.CommandId));
                }
                bool faulted = eliminatedSpeech.StorageFaulted;
                var receipt = port.SubmitUtterance(command);
                if (!faulted && eliminatedSpeech.StorageFaulted) eliminatedGroupRevision++;
                if (receipt.Accepted && receipt.Ordinal > eliminatedOrdinal)
                {
                    eliminatedOrdinal = receipt.Ordinal;
                    if (command.IsDesignation) eliminatedDesignationRevisions[member.Seat.Value]++;
                    else eliminatedGroupRevision++;
                }
                return new HoldemRoomUtteranceReceipt(receipt.Accepted ? HoldemRoomError.None
                    : HoldemRoomError.UtteranceRejected, receipt);
            }
        }

        public IReadOnlyList<HoldemUtteranceEntry> ReadEliminatedDesignations(Guid hostConnection)
        {
            lock (gate)
            {
                RequireEliminatedHost(hostConnection);
                return eliminatedSpeech?.ReadDesignatedUtterances() ?? Array.Empty<HoldemUtteranceEntry>();
            }
        }
        public IReadOnlyList<HoldemUtteranceBatch> ReadClosedEliminatedBatches(Guid hostConnection)
        {
            lock (gate)
            {
                RequireEliminatedHost(hostConnection);
                return Array.AsReadOnly(pendingEliminatedBatches.ToArray());
            }
        }
        public bool AcknowledgeEliminatedBatch(Guid hostConnection, Guid windowId)
        {
            lock (gate)
            {
                RequireEliminatedHost(hostConnection);
                int index = pendingEliminatedBatches.FindIndex(batch => batch.WindowId == windowId);
                if (index < 0) return false;
                bool full = pendingEliminatedBatches.Count >= MaximumPendingUtteranceBatches;
                pendingEliminatedBatches.RemoveAt(index);
                if (full) eliminatedGroupRevision++;
                return true;
            }
        }
        private void RequireEliminatedHost(Guid connection)
        {
            if (FindConnected(connection, out var member) != HoldemRoomError.None || member != members[0])
                throw new InvalidOperationException("Only the connected host can read or acknowledge eliminated input.");
        }
        private long ReadEliminatedRevision(Member member)
        {
            var state = ReadGameSnapshot(member);
            return eliminatedSpeech != null && state?.GetSeat(member.Seat).Status == HoldemSeatStatus.Busted
                ? checked(eliminatedGroupRevision * 4 + eliminatedDesignationRevisions[member.Seat.Value]) : 0;
        }
        private void SynchronizeEliminatedSpeech()
        {
            if (rules.BettingPolicy != HoldemBettingPolicy.SharedAnteSequential) return;
            if (eliminatedHandId != session.CurrentHandId.Value)
            {
                eliminatedHandId = session.CurrentHandId.Value;
                eliminatedOrdinal = eliminatedGroupRevision = 0;
                Array.Clear(eliminatedDesignationRevisions, 0, eliminatedDesignationRevisions.Length);
                capturedEliminatedWindows.Clear();
            }
            if (eliminatedSpeech == null)
                eliminatedSpeech = new HoldemUtteranceInbox(() => ReadGameSnapshot(members[0]), utterancePolicy,
                    audience: HoldemSpeechAudience.Eliminated);
            else eliminatedSpeech.Synchronize();
            foreach (var batch in eliminatedSpeech.ReadClosedDesignatedBatches())
                if (capturedEliminatedWindows.Add(batch.WindowId) && batch.Count > 0) pendingEliminatedBatches.Add(batch);
        }
    }
}
