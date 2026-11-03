using System;
using System.Collections.Generic;

namespace Poker.Foundation
{
    /// <summary>A five-card value plus the exact five physical cards that produced it.</summary>
    public sealed class HoldemEvaluatedHand
    {
        private readonly Card[] bestCards;

        internal HoldemEvaluatedHand(HandValue value, Card[] ownedBestCards)
        {
            Value = value;
            bestCards = ownedBestCards;
        }

        public HandValue Value { get; }
        public int BestCardCount => bestCards.Length;

        public Card GetBestCard(int index)
        {
            if (index < 0 || index >= bestCards.Length) throw new ArgumentOutOfRangeException(nameof(index));
            return bestCards[index];
        }
    }

    public static class HoldemBestHand
    {
        /// <summary>
        /// Chooses the strongest five cards from a three-to-five-card board and exactly two
        /// private cards. The selected best five may use zero, one, or both private cards.
        /// </summary>
        public static HoldemEvaluatedHand Evaluate(IReadOnlyList<Card> board, IReadOnlyList<Card> holeCards)
        {
            if (board == null) throw new ArgumentNullException(nameof(board));
            if (holeCards == null) throw new ArgumentNullException(nameof(holeCards));
            if (board.Count < 3 || board.Count > 5)
                throw new ArgumentException("The visible board must contain three through five cards.", nameof(board));
            if (holeCards.Count != 2)
                throw new ArgumentException("A Hold'em player must supply exactly two private cards.", nameof(holeCards));
            int count = board.Count + holeCards.Count;
            if (count < HandEvaluator.HandSize || count > 7)
                throw new ArgumentException("The combined cards must contain five through seven cards.");

            var all = new Card[count];
            ulong seen = 0;
            for (int i = 0; i < board.Count; i++) CopyChecked(board[i], all, i, ref seen);
            for (int i = 0; i < holeCards.Count; i++) CopyChecked(holeCards[i], all, board.Count + i, ref seen);

            HoldemEvaluatedHand best = null;
            for (int a = 0; a < count - 4; a++)
            for (int b = a + 1; b < count - 3; b++)
            for (int c = b + 1; c < count - 2; c++)
            for (int d = c + 1; d < count - 1; d++)
            for (int e = d + 1; e < count; e++)
            {
                var five = new[] { all[a], all[b], all[c], all[d], all[e] };
                HandValue value = HandEvaluator.Evaluate(five);
                if (best == null || value > best.Value) best = new HoldemEvaluatedHand(value, five);
            }
            return best;
        }

        private static void CopyChecked(Card card, Card[] destination, int index, ref ulong seen)
        {
            if (!card.IsValid) throw new ArgumentException("The cards contain an uninitialized card.");
            ulong bit = 1UL << card.Id;
            if ((seen & bit) != 0) throw new ArgumentException("The board and private cards contain a duplicate card.");
            seen |= bit;
            destination[index] = card;
        }
    }
}
