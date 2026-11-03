using System;
using System.Collections.Generic;

namespace Poker.Foundation
{
    /// <summary>One seat's private choice. Revision orders that seat's edits within this exact window.</summary>
    public sealed class FixedBettingChoiceCommand : HoldemWindowCommand
    {
        private FixedBettingChoiceCommand(Guid sessionId, Guid handId, Guid windowId, Guid commandId,
            long expectedVersion, HoldemStreet street, SeatId seat, long choiceRevision,
            FixedBettingProposal? proposal, FixedBettingResponse? response)
            : base(sessionId, handId, windowId, commandId, expectedVersion, street)
        {
            if (!seat.IsValid) throw new ArgumentException("A valid seat is required.", nameof(seat));
            if (choiceRevision <= 0) throw new ArgumentOutOfRangeException(nameof(choiceRevision));
            Seat = seat; ChoiceRevision = choiceRevision; Proposal = proposal; Response = response;
            Phase = proposal.HasValue ? FixedBettingPhase.Proposal : FixedBettingPhase.Response;
        }

        public SeatId Seat { get; }
        public long ChoiceRevision { get; }
        public FixedBettingPhase Phase { get; }
        public FixedBettingProposal? Proposal { get; }
        public FixedBettingResponse? Response { get; }

        public static FixedBettingChoiceCommand Propose(Guid sessionId, Guid handId, Guid windowId, Guid commandId,
            long expectedVersion, HoldemStreet street, SeatId seat, long revision, FixedBettingProposal proposal)
        {
            if (proposal != FixedBettingProposal.Keep && proposal != FixedBettingProposal.Increase)
                throw new ArgumentOutOfRangeException(nameof(proposal));
            return new FixedBettingChoiceCommand(sessionId, handId, windowId, commandId,
                expectedVersion, street, seat, revision, proposal, null);
        }

        public static FixedBettingChoiceCommand Respond(Guid sessionId, Guid handId, Guid windowId, Guid commandId,
            long expectedVersion, HoldemStreet street, SeatId seat, long revision, FixedBettingResponse response)
        {
            if (response != FixedBettingResponse.Match && response != FixedBettingResponse.Fold)
                throw new ArgumentOutOfRangeException(nameof(response));
            return new FixedBettingChoiceCommand(sessionId, handId, windowId, commandId,
                expectedVersion, street, seat, revision, null, response);
        }

        internal bool HasSamePayload(FixedBettingChoiceCommand other) => SameEnvelope(other)
            && Seat == other.Seat && ChoiceRevision == other.ChoiceRevision && Phase == other.Phase
            && Proposal == other.Proposal && Response == other.Response;
    }

    /// <summary>Trusted host closes one selection stage. Never route through a non-host connection.</summary>
    public sealed class FixedBettingHostCommand : HoldemWindowCommand
    {
        public FixedBettingHostCommand(Guid sessionId, Guid handId, Guid windowId, Guid commandId,
            long expectedVersion, HoldemStreet street, FixedBettingPhase phase)
            : base(sessionId, handId, windowId, commandId, expectedVersion, street)
        {
            if (phase != FixedBettingPhase.Proposal && phase != FixedBettingPhase.Response)
                throw new ArgumentOutOfRangeException(nameof(phase));
            Phase = phase;
        }
        public FixedBettingPhase Phase { get; }
        internal bool HasSamePayload(FixedBettingHostCommand other) => SameEnvelope(other) && Phase == other.Phase;
    }

    /// <summary>Viewer-safe input options. No other seat's pending choice or submission status is included.</summary>
    public sealed class FixedBettingView
    {
        private readonly SeatId[] raisers;

        internal FixedBettingView(Guid windowId, HoldemStreet street, FixedBettingPhase phase,
            long increment, bool eligible, FixedBettingProposal? ownProposal,
            FixedBettingResponse? ownResponse, long ownChoiceRevision, IReadOnlyList<SeatId> publicRaisers,
            SeatId viewer)
        {
            WindowId = windowId; Street = street; Phase = phase; Increment = increment;
            OwnProposal = ownProposal; OwnResponse = ownResponse; OwnChoiceRevision = ownChoiceRevision;
            raisers = new SeatId[publicRaisers.Count];
            bool isRaiser = false;
            for (int i = 0; i < raisers.Length; i++)
            { raisers[i] = publicRaisers[i]; if (raisers[i] == viewer) isRaiser = true; }
            CanPropose = eligible && phase == FixedBettingPhase.Proposal;
            CanRespond = eligible && phase == FixedBettingPhase.Response && !isRaiser;
        }

        public Guid WindowId { get; }
        public HoldemStreet Street { get; }
        public FixedBettingPhase Phase { get; }
        public long Increment { get; }
        public bool CanPropose { get; }
        public bool CanRespond { get; }
        public FixedBettingProposal? OwnProposal { get; }
        public FixedBettingResponse? OwnResponse { get; }
        public long OwnChoiceRevision { get; }
        public int RaiserCount => raisers.Length;
        public SeatId GetRaiser(int index)
        {
            if (index < 0 || index >= raisers.Length) throw new ArgumentOutOfRangeException(nameof(index));
            return raisers[index];
        }
    }
}
