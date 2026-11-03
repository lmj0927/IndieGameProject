using System;
using Poker.Foundation;

namespace Poker.Application
{
    /// <summary>
    /// Trusted dealer integration. Return null until the actual manipulation record is available.
    /// An intended action or an LLM prediction is not evidence that a card was changed.
    /// </summary>
    public interface IHoldemDealerEvidenceSource
    {
        HoldemDealerEvidence FindEvidence(HoldemAccusationClaim claim);
    }

    /// <summary>Correlated host input, not a player message or cryptographic proof.</summary>
    public sealed class HoldemDealerEvidence
    {
        public HoldemDealerEvidence(Guid sessionId, Guid handId, Guid windowId, HoldemStreet street,
            Guid claimId, SeatId accuser, SeatId target, bool wasManipulated)
        {
            if (sessionId == Guid.Empty || handId == Guid.Empty || windowId == Guid.Empty || claimId == Guid.Empty)
                throw new ArgumentException("Evidence must identify its session, hand, window and claim.");
            if (!accuser.IsValid || !target.IsValid || accuser == target)
                throw new ArgumentException("Evidence must identify two distinct seats.");
            if (street != HoldemStreet.Flop && street != HoldemStreet.Turn && street != HoldemStreet.River)
                throw new ArgumentOutOfRangeException(nameof(street));
            SessionId = sessionId; HandId = handId; WindowId = windowId; Street = street;
            ClaimId = claimId; Accuser = accuser; Target = target; WasManipulated = wasManipulated;
        }
        public Guid SessionId { get; }
        public Guid HandId { get; }
        public Guid WindowId { get; }
        public HoldemStreet Street { get; }
        public Guid ClaimId { get; }
        public SeatId Accuser { get; }
        public SeatId Target { get; }
        public bool WasManipulated { get; }

        public static HoldemDealerEvidence ForClaim(HoldemAccusationClaim claim, bool wasManipulated)
        {
            if (claim == null) throw new ArgumentNullException(nameof(claim));
            return new HoldemDealerEvidence(claim.SessionId, claim.HandId, claim.WindowId, claim.Street,
                claim.ClaimId, claim.Accuser, claim.Target, wasManipulated);
        }

        internal bool Matches(HoldemAccusationClaim claim) => SessionId == claim.SessionId && HandId == claim.HandId
            && WindowId == claim.WindowId && Street == claim.Street && ClaimId == claim.ClaimId
            && Accuser == claim.Accuser && Target == claim.Target;
    }

    public enum HoldemEvidenceResolution
    {
        NoPendingClaim = 0,
        EvidencePending = 1,
        EvidenceUnavailable = 2,
        EvidenceMismatch = 3,
        StateChanged = 4,
        Recorded = 5
    }

    /// <summary>
    /// Run on the serialized, authenticated host path only. The provider owns evidence correctness;
    /// this bridge checks correlation and freshness. It cannot apply a fine, fold or payout.
    /// </summary>
    public sealed class HoldemAccusationResolver
    {
        private readonly HoldemSession session;
        private readonly IHoldemDealerEvidenceSource evidence;

        public HoldemAccusationResolver(HoldemSession session, IHoldemDealerEvidenceSource evidence)
        {
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            this.evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        }

        public HoldemEvidenceResolution Resolve(Guid claimId)
        {
            if (claimId == Guid.Empty) throw new ArgumentException("A claim ID is required.", nameof(claimId));
            HoldemAccusationClaim pending = null;
            foreach (var claim in session.GetPendingAccusations())
                if (claim.ClaimId == claimId) { pending = claim; break; }
            if (pending == null) return HoldemEvidenceResolution.NoPendingClaim;
            long version = session.Version;
            HoldemDealerEvidence record;
            try { record = evidence.FindEvidence(pending); }
            catch (Exception) { return HoldemEvidenceResolution.EvidenceUnavailable; }
            if (record == null) return HoldemEvidenceResolution.EvidencePending;
            if (!record.Matches(pending)) return HoldemEvidenceResolution.EvidenceMismatch;
            var command = HoldemAccusationHostCommand.Verdict(pending.SessionId, pending.HandId, pending.WindowId,
                Guid.NewGuid(), version, pending.Street, pending.ClaimId, record.WasManipulated);
            var receipt = session.ProcessAccusationHostCommand(command);
            return receipt.Accepted ? HoldemEvidenceResolution.Recorded : HoldemEvidenceResolution.StateChanged;
        }
    }
}
