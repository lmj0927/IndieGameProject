using System;
using System.Collections.Generic;

namespace Poker.Foundation
{
    public enum HoldemAccusationPhase
    {
        Collecting = 1,
        ClosedWithoutClaims = 2,
        AwaitingVerdicts = 3,
        AwaitingConsequences = 4,
        ConsequencesApplied = 5
    }

    /// <summary>Viewer-relative choice and own claim result. No other seat's choice or dealer record is exposed.</summary>
    public sealed class HoldemAccusationView
    {
        private readonly SeatId[] targets;
        internal HoldemAccusationView(Guid windowId, HoldemAccusationPhase phase, int eligibleCount,
            int responseCount, bool canRespond, bool hasResponded, SeatId? ownTarget, SeatId[] targets,
            bool? ownVerdict = null, long ownChoiceRevision = 0, bool ownAdmitted = false)
        {
            WindowId = windowId; Phase = phase; EligibleCount = eligibleCount; ResponseCount = responseCount;
            CanRespond = canRespond; HasResponded = hasResponded; OwnTarget = ownTarget;
            this.targets = targets; OwnVerdict = ownVerdict;
            OwnChoiceRevision = ownChoiceRevision; OwnAdmitted = ownAdmitted;
        }
        public Guid WindowId { get; }
        public HoldemAccusationPhase Phase { get; }
        public int EligibleCount { get; }
        public int ResponseCount { get; }
        public bool CanRespond { get; }
        public bool HasResponded { get; }
        public SeatId? OwnTarget { get; }
        /// <summary>Result of this viewer's frozen claim only. Null means no recorded result, never a failed claim.</summary>
        public bool? OwnVerdict { get; }
        public long OwnChoiceRevision { get; }
        public bool OwnAdmitted { get; }
        public int TargetCount => targets.Length;
        public SeatId GetTargetAt(int index)
        {
            if (index < 0 || index >= targets.Length) throw new ArgumentOutOfRangeException(nameof(index));
            return targets[index];
        }
    }

    /// <summary>Host-only pending claim, keyed to a frozen choice and a specific reveal.</summary>
    public sealed class HoldemAccusationClaim
    {
        internal HoldemAccusationClaim(Guid windowId, HoldemAccusationChoiceCommand choice)
        {
            WindowId = windowId; SessionId = choice.SessionId; HandId = choice.HandId;
            Street = choice.Street; ClaimId = choice.CommandId; Accuser = choice.Seat; Target = choice.Target.Value;
        }
        internal HoldemAccusationClaim(Guid dealWindowId, TurnAccusationCommand command)
        {
            WindowId = dealWindowId; SessionId = command.SessionId; HandId = command.HandId;
            Street = command.Street; ClaimId = command.CommandId; Accuser = command.Seat; Target = command.Target;
        }
        public Guid SessionId { get; }
        public Guid HandId { get; }
        public Guid WindowId { get; }
        public HoldemStreet Street { get; }
        public Guid ClaimId { get; }
        public SeatId Accuser { get; }
        public SeatId Target { get; }
    }

    /// <summary>Host-only recorded decision for later consequence handling. Never part of a player snapshot.</summary>
    public sealed class HoldemAccusationDecision
    {
        internal HoldemAccusationDecision(HoldemAccusationClaim claim, bool wasManipulated)
        { Claim = claim; WasManipulated = wasManipulated; }
        public HoldemAccusationClaim Claim { get; }
        public bool WasManipulated { get; }
    }

    /// <summary>Public consequence, without revealing another player's request or the unaccused mutation source.</summary>
    public sealed class HoldemAccusationOutcome
    {
        internal HoldemAccusationOutcome(HoldemAccusationClaim claim, bool hit, long fine, long potAward)
        {
            WindowId = claim.WindowId; Street = claim.Street; Accuser = claim.Accuser; Target = claim.Target;
            Hit = hit; Fine = fine; PotAward = potAward;
        }
        public Guid WindowId { get; }
        public HoldemStreet Street { get; }
        public SeatId Accuser { get; }
        public SeatId Target { get; }
        public bool Hit { get; }
        public long Fine { get; }
        public long PotAward { get; }
    }

    /// <summary>Immutable intake only. No fold, fine, elimination or pot settlement is performed here.</summary>
    internal sealed class HoldemAccusationWindow
    {
        private readonly SeatId[] eligible;
        private readonly HoldemAccusationChoiceCommand[] choices;
        private readonly bool?[] verdicts;
        private readonly bool single;
        private readonly Guid admittedClaimId;
        public long OpenedVersion { get; }
        public Guid Id { get; }
        public Guid HandId { get; }
        public HoldemStreet Street { get; }
        public HoldemAccusationPhase Phase { get; }

        public HoldemAccusationWindow(HoldemHand hand, long openedVersion)
        {
            if (!hand.IsRevealPending) throw new ArgumentException("A reveal must be pending.", nameof(hand));
            Id = Guid.NewGuid(); HandId = hand.HandId; Street = hand.Street;
            OpenedVersion = openedVersion;
            single = hand.Config.AccusationMode == HoldemAccusationMode.SingleClaimSelectedAtClose;
            var seats = new List<SeatId>();
            for (int i = 0; i < hand.DealtInSeatCount; i++)
            {
                SeatId seat = hand.GetDealtInSeatAt(i);
                if (!hand.IsFolded(seat)) seats.Add(seat);
            }
            eligible = seats.ToArray(); choices = new HoldemAccusationChoiceCommand[eligible.Length];
            verdicts = new bool?[eligible.Length]; Phase = HoldemAccusationPhase.Collecting;
        }
        private HoldemAccusationWindow(HoldemAccusationWindow source, HoldemAccusationChoiceCommand[] choices,
            bool?[] verdicts, HoldemAccusationPhase phase, Guid? admittedClaimId = null)
        {
            Id = source.Id; HandId = source.HandId; Street = source.Street; eligible = source.eligible;
            this.choices = choices; this.verdicts = verdicts; Phase = phase;
            single = source.single; OpenedVersion = source.OpenedVersion;
            this.admittedClaimId = admittedClaimId ?? source.admittedClaimId;
        }
        public bool IsEligible(SeatId seat) => IndexOf(seat) >= 0;
        public long ChoiceRevision(SeatId seat) => IndexOf(seat) >= 0 ? choices[IndexOf(seat)]?.ChoiceRevision ?? 0 : 0;
        public HoldemAccusationWindow Choose(HoldemAccusationChoiceCommand command)
        {
            var next = (HoldemAccusationChoiceCommand[])choices.Clone();
            next[IndexOf(command.Seat)] = command;
            return new HoldemAccusationWindow(this, next, verdicts, Phase);
        }
        public HoldemCommandError CanClose(Guid selectedClaimId = default)
        {
            if (Phase != HoldemAccusationPhase.Collecting) return HoldemCommandError.AccusationWindowClosed;
            if (single)
            {
                int claims = 0;
                foreach (var choice in choices) if (choice != null && choice.Target.HasValue) claims++;
                if (claims == 0) return selectedClaimId == Guid.Empty ? HoldemCommandError.None : HoldemCommandError.UnknownAccusation;
                return selectedClaimId != Guid.Empty && ClaimIndex(selectedClaimId) >= 0
                    ? HoldemCommandError.None : HoldemCommandError.UnknownAccusation;
            }
            if (selectedClaimId != Guid.Empty) return HoldemCommandError.UnknownAccusation;
            for (int i = 0; i < choices.Length; i++)
                if (choices[i] == null) return HoldemCommandError.AccusationResponsesPending;
            return HoldemCommandError.None;
        }
        public HoldemAccusationWindow Close(Guid selectedClaimId = default)
        {
            bool hasClaims = false;
            foreach (var choice in choices) if (choice != null && choice.Target.HasValue) hasClaims = true;
            return new HoldemAccusationWindow(this, choices, verdicts, hasClaims
                ? HoldemAccusationPhase.AwaitingVerdicts : HoldemAccusationPhase.ClosedWithoutClaims, selectedClaimId);
        }
        public HoldemCommandError CanRecord(Guid claimId)
        {
            if (Phase == HoldemAccusationPhase.Collecting) return HoldemCommandError.AccusationWindowNotClosed;
            int index = ClaimIndex(claimId);
            if (index < 0 || !IsAdmitted(index)) return HoldemCommandError.UnknownAccusation;
            return verdicts[index].HasValue ? HoldemCommandError.AccusationVerdictRecorded : HoldemCommandError.None;
        }
        public HoldemAccusationWindow Record(Guid claimId, bool wasManipulated)
        {
            var next = (bool?[])verdicts.Clone(); next[ClaimIndex(claimId)] = wasManipulated;
            bool pending = false;
            for (int i = 0; i < choices.Length; i++)
                if (IsAdmitted(i) && !next[i].HasValue) pending = true;
            return new HoldemAccusationWindow(this, choices, next, pending
                ? HoldemAccusationPhase.AwaitingVerdicts : HoldemAccusationPhase.AwaitingConsequences);
        }
        public IReadOnlyList<HoldemAccusationClaim> PendingClaims()
        {
            var claims = new List<HoldemAccusationClaim>();
            if (Phase == HoldemAccusationPhase.AwaitingVerdicts)
                for (int i = 0; i < choices.Length; i++)
                    if (IsAdmitted(i) && !verdicts[i].HasValue)
                        claims.Add(new HoldemAccusationClaim(Id, choices[i]));
            return claims.AsReadOnly();
        }
        public IReadOnlyList<HoldemAccusationDecision> RecordedDecisions()
        {
            var decisions = new List<HoldemAccusationDecision>();
            for (int i = 0; i < choices.Length; i++)
                if (verdicts[i].HasValue)
                    decisions.Add(new HoldemAccusationDecision(new HoldemAccusationClaim(Id, choices[i]), verdicts[i].Value));
            return decisions.AsReadOnly();
        }
        public IReadOnlyList<HoldemAccusationClaim> SubmittedClaims()
        {
            var claims = new List<HoldemAccusationClaim>();
            if (Phase == HoldemAccusationPhase.Collecting)
                foreach (var choice in choices)
                    if (choice != null && choice.Target.HasValue) claims.Add(new HoldemAccusationClaim(Id, choice));
            return claims.AsReadOnly();
        }
        public HoldemCommandError CanApply(Guid claimId)
        {
            if (!single || Phase != HoldemAccusationPhase.AwaitingConsequences)
                return HoldemCommandError.AccusationConsequencesPending;
            return claimId == admittedClaimId ? HoldemCommandError.None : HoldemCommandError.UnknownAccusation;
        }
        public HoldemAccusationWindow Applied()
            => new HoldemAccusationWindow(this, choices, verdicts, HoldemAccusationPhase.ConsequencesApplied);
        public HoldemAccusationView ForViewer(SeatId viewer)
        {
            int own = IndexOf(viewer), responded = 0;
            foreach (var choice in choices) if (choice != null) responded++;
            var targets = new List<SeatId>();
            if (own >= 0) foreach (var seat in eligible) if (seat != viewer) targets.Add(seat);
            return new HoldemAccusationView(Id, Phase, eligible.Length, responded,
                own >= 0 && Phase == HoldemAccusationPhase.Collecting, own >= 0 && choices[own] != null,
                own >= 0 ? choices[own]?.Target : null, targets.ToArray(), own >= 0 ? verdicts[own] : null,
                own >= 0 ? choices[own]?.ChoiceRevision ?? 0 : 0,
                own >= 0 && Phase != HoldemAccusationPhase.Collecting && IsAdmitted(own));
        }
        private bool IsAdmitted(int index) => choices[index] != null && choices[index].Target.HasValue
            && (!single || choices[index].CommandId == admittedClaimId);
        private int IndexOf(SeatId seat) => Array.IndexOf(eligible, seat);
        private int ClaimIndex(Guid claimId)
        {
            for (int i = 0; i < choices.Length; i++)
                if (choices[i] != null && choices[i].Target.HasValue && choices[i].CommandId == claimId) return i;
            return -1;
        }
    }
}
