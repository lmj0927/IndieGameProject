using System;

namespace Poker.Foundation
{
    public sealed partial class HoldemHand
    {
        // Build the changed deck and revealed hand before publishing either. The
        // original hand/deck remain unchanged if any validation or settlement fails.
        internal bool TryDealWithCardChange(HoldemStreet expectedStreet, HoldemCardChange change,
            out HoldemHand candidate, out Card before, out int sourceDeckIndex)
        {
            candidate = null; before = default; sourceDeckIndex = -1;
            if (!IsDealPending || expectedStreet != PendingDealStreet || change == null
                || Config.RevealPolicy != HoldemRevealPolicy.PauseAfterCommunityReveal
                || !WasDealtIn(change.Speaker)
                || (Config.BettingPolicy == HoldemBettingPolicy.SharedAnteFixed && IsFolded(change.Speaker))) return false;
            int count = PendingRevealCount;
            int remainingRunout = RemainingRunoutCount;
            int slot = change.BoardIndex - board.Length;
            var changedDeck = deck.Copy();
            if (!changedDeck.TrySwapCommunityCard(count, remainingRunout, slot, change.Card, out before, out sourceDeckIndex)) return false;
            var changed = NewState(Version, foldedSeats, board, changedDeck, Street,
                CurrentBetting, null, null, null, isDealPending: true);
            candidate = changed.DealUnchanged(expectedStreet);
            return true;
        }
    }
}
