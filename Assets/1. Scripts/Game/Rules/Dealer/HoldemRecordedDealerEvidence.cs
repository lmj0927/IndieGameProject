using System;
using Poker.Foundation;

namespace Poker.Application
{
    public enum HoldemAccusationEvidenceScope { CurrentRevealOnly = 1 }
    /// <summary>Verdicts derived from the same committed reveal records as owner feedback.</summary>
    public sealed class HoldemRecordedDealerEvidence : IHoldemDealerEvidenceSource
    {
        private readonly HoldemSession session;
        public HoldemRecordedDealerEvidence(HoldemSession session, HoldemAccusationEvidenceScope scope)
        {
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            if (scope != HoldemAccusationEvidenceScope.CurrentRevealOnly) throw new ArgumentOutOfRangeException(nameof(scope));
        }

        public HoldemDealerEvidence FindEvidence(HoldemAccusationClaim claim)
        {
            if (claim == null) throw new ArgumentNullException(nameof(claim));
            if (claim.SessionId != session.SessionId) return null;
            var record = session.FindDealRecord(claim.HandId, claim.WindowId, claim.Street);
            return record == null ? null : HoldemDealerEvidence.ForClaim(claim,
                record.Mutation != null && record.Mutation.Speaker == claim.Target);
        }
    }
}
