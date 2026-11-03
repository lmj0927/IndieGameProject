using System;

namespace Poker.Foundation
{
    // Zero is reserved so default(Card) cannot silently become a playable card.
    public enum Suit
    {
        Clubs = 1,
        Diamonds = 2,
        Hearts = 3,
        Spades = 4
    }

    public enum Rank
    {
        Two = 2,
        Three = 3,
        Four = 4,
        Five = 5,
        Six = 6,
        Seven = 7,
        Eight = 8,
        Nine = 9,
        Ten = 10,
        Jack = 11,
        Queen = 12,
        King = 13,
        Ace = 14
    }

    /// <summary>A value from the standard 52-card deck; contains no display or player state.</summary>
    public readonly struct Card : IEquatable<Card>
    {
        public const int DeckSize = 52;

        public Rank Rank { get; }
        public Suit Suit { get; }
        public bool IsValid => Rank >= Rank.Two && Rank <= Rank.Ace
            && Suit >= Suit.Clubs && Suit <= Suit.Spades;

        /// <summary>Stable identity, 0..51, ordered by rank then suit. NOT poker strength.</summary>
        public int Id
        {
            get
            {
                if (!IsValid)
                    throw new InvalidOperationException("An uninitialized card has no deck identity.");
                return ((int)Rank - (int)Rank.Two) * 4 + (int)Suit - (int)Suit.Clubs;
            }
        }

        public Card(Rank rank, Suit suit)
        {
            if (rank < Rank.Two || rank > Rank.Ace)
                throw new ArgumentOutOfRangeException(nameof(rank), rank, "Rank must be Two through Ace.");
            if (suit < Suit.Clubs || suit > Suit.Spades)
                throw new ArgumentOutOfRangeException(nameof(suit), suit, "Suit must be Clubs through Spades.");
            Rank = rank;
            Suit = suit;
        }

        public static Card FromId(int id)
        {
            if (id < 0 || id >= DeckSize)
                throw new ArgumentOutOfRangeException(nameof(id), id, "Card identity must be 0 through 51.");
            return new Card((Rank)(id / 4 + (int)Rank.Two), (Suit)(id % 4 + (int)Suit.Clubs));
        }

        public bool Equals(Card other) => Rank == other.Rank && Suit == other.Suit;
        public override bool Equals(object obj) => obj is Card other && Equals(other);
        public override int GetHashCode() => ((int)Rank * 397) ^ (int)Suit;
        public static bool operator ==(Card left, Card right) => left.Equals(right);
        public static bool operator !=(Card left, Card right) => !left.Equals(right);

        // Diagnostic notation only. User-facing text belongs to the presentation layer.
        public override string ToString()
        {
            if (!IsValid) return "[Invalid Card]";
            const string ranks = "23456789TJQKA";
            const string suits = "cdhs";
            return ranks[(int)Rank - 2].ToString() + suits[(int)Suit - 1];
        }
    }
}
