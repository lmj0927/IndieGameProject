using System;
using System.Collections.Generic;
using Poker.Foundation;

namespace Poker.Application
{
    /// <summary>Committed selection feedback; never an uncommitted AI candidate or hidden card.</summary>
    public sealed class HoldemDealerSelectionFeedback
    {
        internal HoldemDealerSelectionFeedback(Guid handId, Guid dealWindowId, HoldemStreet street,
            SeatId speaker, string text, bool cardChanged, int? boardIndex = null, Card? appliedCard = null)
        { HandId = handId; DealWindowId = dealWindowId; Street = street; Speaker = speaker;
            Text = text; CardChanged = cardChanged; BoardIndex = boardIndex; AppliedCard = appliedCard; }
        public Guid HandId { get; }
        public Guid DealWindowId { get; }
        public HoldemStreet Street { get; }
        public SeatId Speaker { get; }
        public string Text { get; }
        // Selection can be accepted even when the requested card was already scheduled.
        public bool CardChanged { get; }
        // Only the committed, already-public card. Never the original card or AI candidate.
        public int? BoardIndex { get; }
        public Card? AppliedCard { get; }
    }

    public sealed partial class HoldemRoom
    {
        private readonly List<HoldemDealerSelectionFeedback> dealerSelections = new List<HoldemDealerSelectionFeedback>(3);

        private HoldemDealerSelectionFeedback[] ReadDealerSelections(SeatId viewer, HoldemSnapshot game)
        {
            if (game == null) return Array.Empty<HoldemDealerSelectionFeedback>();
            var visible = new List<HoldemDealerSelectionFeedback>(3);
            foreach (var selection in dealerSelections)
                if (selection.HandId == game.HandId && (selection.Speaker == viewer || game.Result != null))
                    visible.Add(selection);
            return visible.ToArray();
        }
    }
}
