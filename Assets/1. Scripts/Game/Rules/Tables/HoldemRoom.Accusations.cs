using System;
using Poker.Foundation;

namespace Poker.Application
{
    /// <summary>Host-process capability. Never exposed as a player command endpoint.</summary>
    public interface IHoldemAccusationResolutionPort
    {
        HoldemRoomReceipt CloseAccusations(HoldemRoomAccusationClose input);
        HoldemRoomAccusationResolution ResolveAccusations();
    }

    /// <summary>No speaker identity or verdict is accepted from a client.</summary>
    public sealed class HoldemRoomAccusation : HoldemWindowCommand
    {
        public HoldemRoomAccusation(Guid sessionId, Guid handId, Guid windowId, Guid commandId,
            long expectedVersion, HoldemStreet street, SeatId? target, long choiceRevision = 1)
            : base(sessionId, handId, windowId, commandId, expectedVersion, street)
        {
            if (target.HasValue && !target.Value.IsValid) throw new ArgumentException("Invalid target.", nameof(target));
            if (choiceRevision <= 0) throw new ArgumentOutOfRangeException(nameof(choiceRevision));
            Target = target; ChoiceRevision = choiceRevision;
        }
        public SeatId? Target { get; }
        public long ChoiceRevision { get; }
    }

    /// <summary>The authenticated connection supplies only a turn basis and target, never a seat or verdict.</summary>
    public sealed class HoldemRoomTurnAccusation
    {
        public HoldemRoomTurnAccusation(Guid sessionId, Guid handId, Guid commandId,
            long expectedVersion, HoldemStreet street, SeatId target)
        {
            if (sessionId == Guid.Empty || handId == Guid.Empty || commandId == Guid.Empty)
                throw new ArgumentException("Session, hand and command IDs are required.");
            if (expectedVersion < 0) throw new ArgumentOutOfRangeException(nameof(expectedVersion));
            if (street < HoldemStreet.Flop || street > HoldemStreet.River)
                throw new ArgumentOutOfRangeException(nameof(street));
            if (!target.IsValid) throw new ArgumentException("Invalid target.", nameof(target));
            SessionId = sessionId; HandId = handId; CommandId = commandId;
            ExpectedVersion = expectedVersion; Street = street; Target = target;
        }
        public Guid SessionId { get; }
        public Guid HandId { get; }
        public Guid CommandId { get; }
        public long ExpectedVersion { get; }
        public HoldemStreet Street { get; }
        public SeatId Target { get; }
    }

    /// <summary>Host-process close request. No caller-supplied verdict.</summary>
    public sealed class HoldemRoomAccusationClose : HoldemWindowCommand
    {
        public HoldemRoomAccusationClose(Guid sessionId, Guid handId, Guid windowId, Guid commandId,
            long expectedVersion, HoldemStreet street, Guid selectedClaimId = default)
            : base(sessionId, handId, windowId, commandId, expectedVersion, street) { SelectedClaimId = selectedClaimId; }
        // Host-only admission decision, never sent by a player accusation command.
        public Guid SelectedClaimId { get; }
    }

    public sealed class HoldemRoomAccusationResolution
    {
        internal HoldemRoomAccusationResolution(HoldemRoomError error, int recorded = 0, int pending = 0)
        { Error = error; RecordedCount = recorded; PendingCount = pending; }
        public HoldemRoomError Error { get; }
        public int RecordedCount { get; }
        public int PendingCount { get; }
    }

    public sealed partial class HoldemRoom
    {
        private readonly HoldemAccusationResolver accusationResolver;
        private readonly System.Collections.Generic.Dictionary<Guid, Guid> accusationAdmissions = new System.Collections.Generic.Dictionary<Guid, Guid>();

        public HoldemRoomReceipt SubmitTurnAccusation(Guid connection, HoldemRoomTurnAccusation input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            lock (gate)
            {
                var error = Guard(connection, false, out var member);
                if (error != HoldemRoomError.None) return Reject(error);
                var before = session.CurrentHandId.HasValue ? session.GetSnapshot(member.Seat) : null;
                var command = new TurnAccusationCommand(input.SessionId, input.HandId, input.CommandId,
                    input.ExpectedVersion, input.Street, member.Seat, input.Target);
                var receipt = session.SubmitTurnAccusation(member.Seat, command);
                if (receipt.Accepted && before != null && receipt.Version > before.SessionVersion)
                {
                    var after = session.GetSnapshot(member.Seat);
                    RecordAccusationHistory(before, after);
                    var outcome = after.GetAccusationOutcome(after.AccusationOutcomeCount - 1);
                    // A fine is a transfer between stacks, not a wager or an uncalled return.
                    RecordHistoryReturns(before, after, member.Seat, outcome.Fine, input.Target, outcome.Fine);
                    SynchronizeUtterances(); revision++;
                }
                return Wrap(receipt);
            }
        }

        public HoldemRoomReceipt SubmitAccusation(Guid connection, HoldemRoomAccusation input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            lock (gate)
            {
                var error = Guard(connection, false, out var member);
                if (error != HoldemRoomError.None) return Reject(error);
                long version = session.Version;
                var command = new HoldemAccusationChoiceCommand(input.SessionId, input.HandId, input.WindowId,
                    input.CommandId, input.ExpectedVersion, input.Street, member.Seat, input.Target, input.ChoiceRevision);
                var receipt = session.SubmitAccusationChoice(member.Seat, command);
                if (receipt.Accepted && session.Version > version) revision++;
                return Wrap(receipt);
            }
        }

        public HoldemRoomReceipt CloseAccusations(Guid hostConnection, HoldemRoomAccusationClose input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            return HostOperation(hostConnection, () =>
            {
                if (rules.AccusationMode != HoldemAccusationMode.SingleClaimSelectedAtClose)
                    return session.ProcessAccusationHostCommand(HoldemAccusationHostCommand.Close(input.SessionId,
                        input.HandId, input.WindowId, input.CommandId, input.ExpectedVersion, input.Street));
                Guid selected = input.SelectedClaimId;
                if (selected == Guid.Empty && !accusationAdmissions.TryGetValue(input.CommandId, out selected))
                {
                    var claims = session.GetSubmittedAccusations();
                    // Prototype default only: sample among applications at the common deadline, not click arrival.
                    if (claims.Count > 0)
                        using (var random = new CryptographicRandomSource()) selected = claims[random.NextInt(claims.Count)].ClaimId;
                }
                var receipt = session.ProcessAccusationHostCommand(HoldemAccusationHostCommand.CloseSingle(input.SessionId,
                    input.HandId, input.WindowId, input.CommandId, input.ExpectedVersion, input.Street, selected));
                if (receipt.Accepted) accusationAdmissions[input.CommandId] = selected;
                return receipt;
            });
        }

        /// <summary>
        /// Host-process only. Uses committed deal records, never client verdicts or AI intentions.
        /// Missing evidence remains pending. In single-claim mode, the verified verdict is followed by
        /// a separate atomic consequence command; legacy preview keeps its consequence wait unchanged.
        /// </summary>
        public HoldemRoomAccusationResolution ResolveAccusations(Guid hostConnection)
        {
            lock (gate)
            {
                var error = Guard(hostConnection, true, out _);
                if (error != HoldemRoomError.None) return new HoldemRoomAccusationResolution(error);
                if (accusationResolver == null) return new HoldemRoomAccusationResolution(HoldemRoomError.AccusationsDisabled);
                int recorded = 0;
                foreach (var claim in session.GetPendingAccusations())
                    if (accusationResolver.Resolve(claim.ClaimId) == HoldemEvidenceResolution.Recorded) recorded++;
                bool applied = false;
                if (rules.AccusationMode == HoldemAccusationMode.SingleClaimSelectedAtClose)
                    foreach (var decision in session.GetAccusationDecisions())
                    {
                        var claim = decision.Claim; var view = session.GetSnapshot(members[0].Seat);
                        if (view.Accusations?.Phase != HoldemAccusationPhase.AwaitingConsequences) continue;
                        var receipt = session.ProcessAccusationHostCommand(HoldemAccusationHostCommand.Apply(claim.SessionId,
                            claim.HandId, claim.WindowId, Guid.NewGuid(), view.SessionVersion, claim.Street, claim.ClaimId));
                        if (receipt.Accepted) { RecordAccusationHistory(view, session.GetSnapshot(members[0].Seat)); applied = true; }
                    }
                if (recorded > 0 || applied) { SynchronizeUtterances(); revision++; }
                return new HoldemRoomAccusationResolution(HoldemRoomError.None, recorded, session.GetPendingAccusations().Count);
            }
        }
    }
}
