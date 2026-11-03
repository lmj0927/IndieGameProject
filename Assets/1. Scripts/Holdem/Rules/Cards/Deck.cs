using System;

namespace Poker.Foundation
{
    /// <summary>
    /// A single-use 52-card deck. Public callers can only advance the cursor;
    /// the authority may swap one future community slot with an unused card.
    /// No reset, reshuffle, or remaining-order access. Not thread-safe; the authority
    /// must serialize calls. Returned cards must be routed only to their authorized seat.
    /// </summary>
    public sealed class Deck
    {
        private readonly Card[] cards;
        private int nextIndex;

        // Only the factory can supply this array; it never escapes to the caller.
        private Deck(Card[] ownedCards) { cards = ownedCards; }

        public int RemainingCount => cards.Length - nextIndex;

        // Authority-owned candidate state only; never a public peek/reset API.
        internal Deck Copy()
        {
            var copy = new Deck((Card[])cards.Clone());
            copy.nextIndex = nextIndex;
            return copy;
        }

        // Does not consume cards. The upcoming burn and other cards in this reveal
        // are excluded: moving a card already in the flop only reorders the same board.
        internal bool TrySwapCommunityCard(int revealCount, int remainingRunoutCount, int slot,
            Card replacement, out Card before, out int sourceDeckIndex)
        {
            before = default; sourceDeckIndex = -1;
            if (!replacement.IsValid || (revealCount != 1 && revealCount != 3)
                || remainingRunoutCount < revealCount + 1 || remainingRunoutCount > RemainingCount
                || slot < 0 || slot >= revealCount) return false;
            int target = nextIndex + 1 + slot;
            before = cards[target];
            if (before == replacement) return true;
            // Explicit reserve-only development scope: no indirect changes to the
            // rest of this hand's board or burn sequence. Not a candidate-selection rule.
            for (int i = nextIndex + remainingRunoutCount; i < cards.Length; i++)
            {
                if (cards[i] != replacement) continue;
                cards[i] = before;
                cards[target] = replacement;
                sourceDeckIndex = i;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Creates and shuffles all 52 standard cards before publishing the deck.
        /// The caller owns the random source; it is used synchronously and not retained.
        /// On failure no deck is returned, but the source's own state is not rolled back.
        /// </summary>
        /// <exception cref="ArgumentNullException">The random source is null.</exception>
        /// <exception cref="InvalidOperationException">The source returned an out-of-range value.</exception>
        /// <remarks>Exceptions thrown by the random source propagate to the caller.</remarks>
        public static Deck CreateShuffled(IRandomSource random)
        {
            if (random == null) throw new ArgumentNullException(nameof(random));

            var shuffled = new Card[Card.DeckSize];
            for (int id = 0; id < shuffled.Length; id++) shuffled[id] = Card.FromId(id);

            // Descending Fisher-Yates. Include i itself: a self-swap is a valid choice.
            for (int i = shuffled.Length - 1; i > 0; i--)
            {
                int selected = random.NextInt(i + 1);
                if (selected < 0 || selected > i)
                    throw new InvalidOperationException("Random source returned a value outside its requested range.");
                Card saved = shuffled[i];
                shuffled[i] = shuffled[selected];
                shuffled[selected] = saved;
            }

            return new Deck(shuffled);
        }

        /// <summary>
        /// Takes the next count cards in order, returning a caller-owned array.
        /// Zero is a no-op, including on an empty deck. Rejected requests consume nothing.
        /// This is not a table deal: seat order, burns, and exchanges belong elsewhere.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Count is negative.</exception>
        /// <exception cref="InvalidOperationException">Not enough cards remain.</exception>
        public Card[] Draw(int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count), count, "Draw count cannot be negative.");
            if (count > RemainingCount) throw new InvalidOperationException("Not enough cards remain in the deck.");
            if (count == 0) return Array.Empty<Card>();

            // Validate and copy before committing the cursor change.
            var drawn = new Card[count];
            Array.Copy(cards, nextIndex, drawn, 0, count);
            nextIndex += count;
            return drawn;
        }
    }
}
