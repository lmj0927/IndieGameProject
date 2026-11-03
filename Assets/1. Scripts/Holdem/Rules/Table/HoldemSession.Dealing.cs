using System;
using System.Collections.Generic;

namespace Poker.Foundation
{
    public sealed partial class HoldemSession
    {
        private sealed class AcceptedDeal
        {
            public AcceptedDeal(HoldemDealCommand command, HoldemReceipt receipt)
            { Command = command; Receipt = receipt; }
            public HoldemDealCommand Command { get; }
            public HoldemReceipt Receipt { get; }
        }

        private readonly Dictionary<Guid, AcceptedDeal> acceptedDeals = new Dictionary<Guid, AcceptedDeal>();
        private HoldemDealWindow pendingDeal;
        private HoldemDealRecord[] dealRecords = Array.Empty<HoldemDealRecord>();

        public HoldemDealWindow PendingDeal => pendingDeal;

        /// <summary>
        /// Serialized host path only. A missing/failed interpretation may take this neutral path;
        /// no manipulation evidence or accusation verdict is fabricated here.
        /// </summary>
        public HoldemReceipt DealUnchanged(HoldemDealCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            if (command.CardChange != null) return Reject(command, HoldemCommandError.InvalidCardChange);
            return ApplyDealerDeal(command);
        }

        /// <summary>Serialized host-only card application and causal record transaction.</summary>
        public HoldemReceipt ApplyDealerDeal(HoldemDealCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            if (command.SessionId != SessionId) return Reject(command, HoldemCommandError.WrongSession);
            if (currentHand == null) return Reject(command, HoldemCommandError.NotStarted);
            if (command.HandId != currentHand.HandId) return Reject(command, HoldemCommandError.WrongHand);
            if (processing) return Reject(command, HoldemCommandError.Busy);
            if (acceptedDeals.TryGetValue(command.CommandId, out AcceptedDeal previous))
                return previous.Command.HasSamePayload(command) ? previous.Receipt
                    : Reject(command, HoldemCommandError.CommandConflict);
            if (usedCommandIds.Contains(command.CommandId)) return Reject(command, HoldemCommandError.CommandConflict);
            if (command.ExpectedVersion != Version) return Reject(command, HoldemCommandError.VersionMismatch);
            if (pendingDeal == null || !currentHand.IsDealPending)
                return Reject(command, HoldemCommandError.DealNotPending);
            if (command.WindowId != pendingDeal.WindowId || command.Street != pendingDeal.Street)
                return Reject(command, HoldemCommandError.WrongDealWindow);

            processing = true;
            try
            {
                long nextVersion = checked(Version + 1);
                HoldemHand candidate;
                HoldemCardMutation mutation = null;
                if (command.CardChange == null) candidate = currentHand.DealUnchanged(command.Street);
                else
                {
                    if (!currentHand.TryDealWithCardChange(command.Street, command.CardChange, out candidate,
                        out Card before, out int sourceDeckIndex))
                        return Reject(command, HoldemCommandError.InvalidCardChange);
                    if (before != command.CardChange.Card) mutation = new HoldemCardMutation(command.CardChange, before, sourceDeckIndex);
                }
                ChipLedger candidateLedger = candidate.IsComplete ? MergeSettled(candidate) : settledLedger;
                HoldemDealWindow nextDeal = DealWindowFor(candidate);
                HoldemAccusationWindow nextAccusations = AccusationsFor(candidate);
                var receipt = new HoldemReceipt(SessionId, command.HandId, command.CommandId,
                    null, nextVersion, HoldemCommandError.None);
                var nextRecords = new HoldemDealRecord[dealRecords.Length + 1];
                Array.Copy(dealRecords, nextRecords, dealRecords.Length);
                nextRecords[dealRecords.Length] = new HoldemDealRecord(command, nextAccusations?.Id, nextVersion, mutation);
                currentHand = candidate; pendingDeal = nextDeal; accusations = nextAccusations;
                settledLedger = candidateLedger; Version = nextVersion; dealRecords = nextRecords;
                acceptedDeals.Add(command.CommandId, new AcceptedDeal(command, receipt));
                usedCommandIds.Add(command.CommandId);
                return receipt;
            }
            finally { processing = false; }
        }

        /// <summary>Host-only lookup with exact reveal attribution. No record means evidence is unavailable.</summary>
        public HoldemDealRecord FindDealRecord(Guid handId, Guid accusationWindowId, HoldemStreet street)
        {
            if (handId != currentHand?.HandId || accusationWindowId == Guid.Empty) return null;
            foreach (var record in dealRecords)
                if (record.HandId == handId && record.AccusationWindowId == accusationWindowId
                    && record.Street == street) return record;
            return null;
        }

        /// <summary>
        /// Seat-bound feedback for the current reveal only. Longer retention is a separate display policy.
        /// Authentication belongs to the adapter, as with GetSnapshot. No pre-reveal success is exposed.
        /// </summary>
        public HoldemOwnCardChange ReadCurrentRevealCardChange(SeatId viewer)
        {
            HoldemSeatOrder.IndexOf(roster, viewer);
            if (currentHand == null || !currentHand.IsRevealPending) return null;
            foreach (var record in dealRecords)
                if (record.HandId == currentHand.HandId && record.Street == currentHand.Street
                    && record.Mutation != null && record.Mutation.Speaker == viewer)
                    return new HoldemOwnCardChange(record);
            return null;
        }

        /// <summary>Shared-game owner feedback retained for this hand only, after cards are revealed.</summary>
        public IReadOnlyList<HoldemOwnCardChange> ReadOwnHandCardChanges(SeatId viewer)
        {
            HoldemSeatOrder.IndexOf(roster, viewer);
            if (currentHand == null || currentHand.Config.BettingPolicy == HoldemBettingPolicy.NoLimit)
                return Array.Empty<HoldemOwnCardChange>();
            var result = new List<HoldemOwnCardChange>(3);
            foreach (var record in dealRecords)
                if (record.HandId == currentHand.HandId && record.Mutation != null && record.Mutation.Speaker == viewer
                    && record.Mutation.BoardIndex < currentHand.BoardCount
                    && currentHand.GetBoardCard(record.Mutation.BoardIndex) == record.Mutation.After)
                    result.Add(new HoldemOwnCardChange(record));
            return result.AsReadOnly();
        }

        private HoldemDealWindow DealWindowFor(HoldemHand candidate)
        {
            if (!candidate.IsDealPending) return null;
            return pendingDeal != null && pendingDeal.HandId == candidate.HandId && pendingDeal.Street == candidate.PendingDealStreet
                ? pendingDeal : new HoldemDealWindow(SessionId, candidate);
        }

        private HoldemReceipt Reject(HoldemDealCommand command, HoldemCommandError error)
            => new HoldemReceipt(command.SessionId, command.HandId, command.CommandId, null, null, error);
    }
}
