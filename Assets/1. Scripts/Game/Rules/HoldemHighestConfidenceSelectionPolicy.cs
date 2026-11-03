using System;
using Poker.Foundation;

namespace Poker.Application
{
    /// <summary>
    /// Selects the highest-confidence card preference from one complete, correlated input batch.
    /// Ties are broken uniformly by the injected host random source. Explicitness is preserved for
    /// suspicion/accusation and never affects this choice. Card choice belongs to the injected resolver.
    /// </summary>
    public sealed class HoldemHighestConfidenceSelectionPolicy : IHoldemDealerSelectionPolicy
    {
        private readonly IRandomSource random;
        private readonly Func<HoldemDealerWork, HoldemDealerInterpretation, HoldemDealerSelection> resolve;
        private Guid chosenWindow;
        private HoldemDealerInterpretation chosen;

        public HoldemHighestConfidenceSelectionPolicy(IRandomSource random,
            Func<HoldemDealerWork, HoldemDealerInterpretation, HoldemDealerSelection> resolveSelectedRequest)
        {
            this.random = random ?? throw new ArgumentNullException(nameof(random));
            resolve = resolveSelectedRequest ?? throw new ArgumentNullException(nameof(resolveSelectedRequest));
        }

        /// <returns>Null when no entry is a card preference.</returns>
        public static HoldemDealerInterpretation Choose(HoldemDealerWork work, HoldemDealerInterpretationBatch batch,
            IRandomSource random)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            if (batch == null) throw new ArgumentNullException(nameof(batch));
            if (random == null) throw new ArgumentNullException(nameof(random));
            if (!batch.MatchesWork(work)) throw new ArgumentException("Interpretations must match the complete closed input.", nameof(batch));
            HoldemDealerInterpretation best = null;
            int tieCount = 0;
            for (int i = 0; i < batch.Count; i++)
            {
                var entry = batch.GetEntry(i);
                if (entry.Intent != HoldemDealerIntent.CardPreference) continue;
                if (best == null || entry.Confidence > best.Confidence)
                { best = entry; tieCount = 1; }
                else if (entry.Confidence == best.Confidence && random.NextInt(++tieCount) == 0) best = entry;
            }
            return best;
        }

        public HoldemDealerSelection Select(HoldemDealerWork work, HoldemDealerInterpretationBatch interpretations)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            if (interpretations == null) throw new ArgumentNullException(nameof(interpretations));
            // A retried deal window keeps its first tie-break: the host draws it only once.
            var window = work.TargetDeal.WindowId;
            var previous = window == chosenWindow && chosen != null && interpretations.MatchesWork(work)
                ? interpretations.Find(chosen.UtteranceId) : null;
            var candidate = previous != null && previous.Matches(chosen) ? previous : Choose(work, interpretations, random);
            chosenWindow = window; chosen = candidate;
            if (candidate == null) return HoldemDealerSelection.Unchanged();

            var decision = resolve(work, candidate)
                ?? throw new InvalidOperationException("The selected request has no explicit application outcome.");
            if (decision.Kind == HoldemDealerSelectionKind.Change
                && (decision.UtteranceId != candidate.UtteranceId
                    || candidate.Rank.HasValue && candidate.Rank.Value != decision.Card.Rank
                    || candidate.Suit.HasValue && candidate.Suit.Value != decision.Card.Suit))
                throw new InvalidOperationException("The application outcome must belong to the selected request and match its preference.");
            return decision;
        }
    }
}
