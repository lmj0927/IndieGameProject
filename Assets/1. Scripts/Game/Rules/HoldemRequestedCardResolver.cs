using System;
using System.Collections.Generic;
using Poker.Foundation;

namespace Poker.Application
{
    /// <summary>
    /// Host-only: turns a selected partial or exact preference into one concrete community change.
    /// The host cannot read deck order, so matching cards are proposed in random order; a card the
    /// core rejects is excluded for that deal window. With no candidates left the deal is unchanged.
    /// The flop slot is drawn at random so the changed position is not fixed.
    /// </summary>
    public sealed class HoldemRequestedCardResolver
    {
        private readonly IRandomSource random;
        private readonly HashSet<Card> rejected = new HashSet<Card>();
        private Guid window, utterance;
        private Card? proposed;

        public HoldemRequestedCardResolver(IRandomSource random)
        { this.random = random ?? throw new ArgumentNullException(nameof(random)); }

        public HoldemDealerSelection Resolve(HoldemDealerWork work, HoldemDealerInterpretation selected)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            if (selected == null) throw new ArgumentNullException(nameof(selected));
            if (selected.Intent != HoldemDealerIntent.CardPreference) return HoldemDealerSelection.Unchanged();
            var deal = work.TargetDeal;
            if (deal.WindowId != window || selected.UtteranceId != utterance)
            { rejected.Clear(); window = deal.WindowId; utterance = selected.UtteranceId; }

            var candidates = new List<Card>(13);
            for (int id = 0; id < Card.DeckSize; id++)
            {
                var card = Card.FromId(id);
                if (selected.Rank.HasValue && card.Rank != selected.Rank.Value) continue;
                if (selected.Suit.HasValue && card.Suit != selected.Suit.Value) continue;
                if (!rejected.Contains(card)) candidates.Add(card);
            }
            if (candidates.Count == 0) { proposed = null; return HoldemDealerSelection.Unchanged(); }

            var chosen = candidates[random.NextInt(candidates.Count)];
            int slot = deal.Street == HoldemStreet.Flop ? random.NextInt(3) : deal.Street == HoldemStreet.Turn ? 3 : 4;
            proposed = chosen;
            return HoldemDealerSelection.Change(selected.UtteranceId, slot, chosen,
                HoldemCardSourceScope.UndealtOutsideCurrentHandRunout);
        }

        /// <summary>Call after the core definitively rejected the last proposed card. True if another proposal may follow.</summary>
        public bool RejectProposal(Guid dealWindowId)
        {
            if (!proposed.HasValue || dealWindowId != window) return false;
            rejected.Add(proposed.Value); proposed = null;
            return true;
        }
    }
}
