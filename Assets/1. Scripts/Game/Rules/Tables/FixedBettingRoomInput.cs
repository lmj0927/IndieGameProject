using System;
using Poker.Foundation;

namespace Poker.Application
{
    /// <summary>Seat-free simultaneous input. The room supplies the authenticated connection's seat.</summary>
    public sealed class FixedBettingRoomInput
    {
        public FixedBettingRoomInput(Guid sessionId, Guid handId, Guid windowId, Guid commandId,
            long expectedVersion, HoldemStreet street, long choiceRevision, FixedBettingPhase phase, int choice)
        {
            // Reuse the core command's validation without trusting or carrying a claimed player seat.
            Bind(new SeatId(1), sessionId, handId, windowId, commandId, expectedVersion, street, choiceRevision, phase, choice);
            SessionId = sessionId; HandId = handId; WindowId = windowId; CommandId = commandId;
            ExpectedVersion = expectedVersion; Street = street; ChoiceRevision = choiceRevision; Phase = phase; Choice = choice;
        }
        public Guid SessionId { get; }
        public Guid HandId { get; }
        public Guid WindowId { get; }
        public Guid CommandId { get; }
        public long ExpectedVersion { get; }
        public HoldemStreet Street { get; }
        public long ChoiceRevision { get; }
        public FixedBettingPhase Phase { get; }
        public int Choice { get; }
        internal FixedBettingChoiceCommand Bind(SeatId seat) => Bind(seat, SessionId, HandId, WindowId,
            CommandId, ExpectedVersion, Street, ChoiceRevision, Phase, Choice);

        private static FixedBettingChoiceCommand Bind(SeatId seat, Guid sessionId, Guid handId, Guid windowId,
            Guid commandId, long expectedVersion, HoldemStreet street, long choiceRevision, FixedBettingPhase phase, int choice)
        {
            if (phase == FixedBettingPhase.Proposal) return FixedBettingChoiceCommand.Propose(sessionId, handId,
                windowId, commandId, expectedVersion, street, seat, choiceRevision, (FixedBettingProposal)choice);
            if (phase == FixedBettingPhase.Response) return FixedBettingChoiceCommand.Respond(sessionId, handId,
                windowId, commandId, expectedVersion, street, seat, choiceRevision, (FixedBettingResponse)choice);
            throw new ArgumentOutOfRangeException(nameof(phase));
        }
    }

    public sealed partial class HoldemRoom
    {
        public HoldemRoomReceipt SubmitFixedBettingChoice(Guid connection, FixedBettingRoomInput input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            lock (gate)
            {
                var error = Guard(connection, false, out var member);
                if (error != HoldemRoomError.None) return Reject(error);
                long version = session.Version;
                var receipt = session.SubmitFixedBettingChoice(member.Seat, input.Bind(member.Seat));
                if (receipt.Accepted && session.Version > version) revision++;
                return Wrap(receipt);
            }
        }

        public HoldemRoomReceipt SealFixedBetting(Guid connection, FixedBettingHostCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            lock (gate)
            {
                var error = Guard(connection, true, out _);
                if (error != HoldemRoomError.None) return Reject(error);
                var before = session.CurrentHandId.HasValue ? session.GetSnapshot(members[0].Seat) : null;
                var receipt = session.SealFixedBetting(command);
                if (receipt.Accepted && before != null && session.Version > before.SessionVersion)
                {
                    foreach (var entry in HoldemHandHistory.FixedBettingEntries(before, session.GetSnapshot(members[0].Seat)))
                        AppendHistory(entry);
                    SynchronizeUtterances(); revision++;
                }
                return Wrap(receipt);
            }
        }
    }
}
