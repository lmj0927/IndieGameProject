using System;
using System.Collections.Generic;

namespace Poker.Foundation
{
    /// <summary>Seat input only. The verdict is never accepted from a client.</summary>
    public sealed class HoldemTurnAccusationCommand
    {
        public HoldemTurnAccusationCommand(Guid sessionId, Guid handId, Guid commandId, SeatId seat,
            long expectedVersion, SeatId target)
        {
            if (sessionId == Guid.Empty) throw new ArgumentException("A session ID is required.", nameof(sessionId));
            if (handId == Guid.Empty) throw new ArgumentException("A hand ID is required.", nameof(handId));
            if (commandId == Guid.Empty) throw new ArgumentException("A command ID is required.", nameof(commandId));
            if (!seat.IsValid) throw new ArgumentException("A valid seat is required.", nameof(seat));
            if (!target.IsValid) throw new ArgumentException("A valid target is required.", nameof(target));
            if (expectedVersion < 0) throw new ArgumentOutOfRangeException(nameof(expectedVersion));
            SessionId = sessionId; HandId = handId; CommandId = commandId;
            Seat = seat; ExpectedVersion = expectedVersion; Target = target;
        }

        public Guid SessionId { get; }
        public Guid HandId { get; }
        public Guid CommandId { get; }
        public SeatId Seat { get; }
        public long ExpectedVersion { get; }
        public SeatId Target { get; }

        internal bool HasSamePayload(HoldemTurnAccusationCommand other) => other != null
            && SessionId == other.SessionId && HandId == other.HandId && CommandId == other.CommandId
            && Seat == other.Seat && ExpectedVersion == other.ExpectedVersion && Target == other.Target;
    }

    public sealed partial class HoldemSession
    {
        private sealed class AcceptedLegacyTurnAccusation
        {
            public AcceptedLegacyTurnAccusation(HoldemTurnAccusationCommand command, HoldemReceipt receipt)
            { Command = command; Receipt = receipt; }
            public HoldemTurnAccusationCommand Command { get; }
            public HoldemReceipt Receipt { get; }
        }

        private readonly Dictionary<Guid, AcceptedLegacyTurnAccusation> acceptedLegacyTurnAccusations
            = new Dictionary<Guid, AcceptedLegacyTurnAccusation>();

        /// <summary>
        /// The adapter supplies authorizedSeat from access control. The verdict comes only from this
        /// street's committed deal record: a hit ends the hand for the accuser, a miss folds the accuser.
        /// </summary>
        public HoldemReceipt SubmitTurnAccusation(SeatId authorizedSeat, HoldemTurnAccusationCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            if (!authorizedSeat.IsValid || authorizedSeat != command.Seat)
                return Reject(command, HoldemCommandError.UnauthorizedSeat);
            if (command.SessionId != SessionId) return Reject(command, HoldemCommandError.WrongSession);
            if (currentHand == null) return Reject(command, HoldemCommandError.NotStarted);
            if (command.HandId != currentHand.HandId) return Reject(command, HoldemCommandError.WrongHand);
            if (processing) return Reject(command, HoldemCommandError.Busy);
            if (acceptedLegacyTurnAccusations.TryGetValue(command.CommandId, out AcceptedLegacyTurnAccusation previous))
                return previous.Command.HasSamePayload(command) ? previous.Receipt
                    : Reject(command, HoldemCommandError.CommandConflict);
            if (usedCommandIds.Contains(command.CommandId)) return Reject(command, HoldemCommandError.CommandConflict);
            if (command.ExpectedVersion != Version) return Reject(command, HoldemCommandError.VersionMismatch);
            if (!config.TurnAccusations) return Reject(command, HoldemCommandError.AccusationNotEnabled);
            if (currentHand.IsSettlementPending) return Reject(command, HoldemCommandError.SettlementRuleRequired);
            if (currentHand.IsDealPending) return Reject(command, HoldemCommandError.DealPending);
            if (currentHand.IsRevealPending) return Reject(command, HoldemCommandError.RevealPending);
            if (currentHand.IsComplete) return Reject(command, HoldemCommandError.HandComplete);
            if (currentHand.CurrentSeat != command.Seat) return Reject(command, HoldemCommandError.WrongTurn);
            if (!currentHand.CanAccuseOnTurn(command.Seat)) return Reject(command, HoldemCommandError.AccusationNotAllowed);
            if (!currentHand.IsTurnAccusationTarget(command.Seat, command.Target))
                return Reject(command, HoldemCommandError.InvalidAccusationTarget);

            processing = true;
            try
            {
                long nextVersion = checked(Version + 1);
                HoldemHand candidate = currentHand.ResolveTurnAccusation(command.Seat, command.Target,
                    CurrentStreetChangedBy(command.Target));
                HoldemDealWindow nextDeal = DealWindowFor(candidate);
                var receipt = new HoldemReceipt(SessionId, command.HandId, command.CommandId,
                    command.Seat, nextVersion, HoldemCommandError.None);
                ChipLedger nextLedger = candidate.IsComplete ? MergeSettled(candidate) : settledLedger;
                currentHand = candidate;
                pendingDeal = nextDeal;
                settledLedger = nextLedger;
                Version = nextVersion;
                acceptedLegacyTurnAccusations.Add(command.CommandId, new AcceptedLegacyTurnAccusation(command, receipt));
                usedCommandIds.Add(command.CommandId);
                return receipt;
            }
            finally { processing = false; }
        }

        private bool CurrentStreetChangedBy(SeatId target)
        {
            foreach (var record in dealRecords)
                if (record.HandId == currentHand.HandId && record.Street == currentHand.Street
                    && record.Mutation != null && record.Mutation.Speaker == target) return true;
            return false;
        }

        private HoldemReceipt Reject(HoldemTurnAccusationCommand command, HoldemCommandError error)
            => new HoldemReceipt(command.SessionId, command.HandId, command.CommandId, command.Seat, null, error);
    }
}
