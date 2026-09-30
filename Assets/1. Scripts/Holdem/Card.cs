using System;

public readonly struct Card : IEquatable<Card>
{
    public const int MinRank = 1;
    public const int MaxRank = 13;

    // 1 = A, 11 = J, 12 = Q, 13 = K
    public readonly int Rank;
    public readonly Suit Suit;

    public Card(int rank, Suit suit)
    {
        if (rank < MinRank || rank > MaxRank)
        {
            throw new ArgumentOutOfRangeException(nameof(rank), rank, $"Rank must be {MinRank}~{MaxRank}.");
        }

        if (!Enum.IsDefined(typeof(Suit), suit))
        {
            throw new ArgumentOutOfRangeException(nameof(suit), suit, "Undefined suit.");
        }

        Rank = rank;
        Suit = suit;
    }

    public bool Equals(Card other) => Rank == other.Rank && Suit == other.Suit;

    public override bool Equals(object obj) => obj is Card other && Equals(other);

    public override int GetHashCode() => (int)Suit * 16 + Rank;

    public static bool operator ==(Card left, Card right) => left.Equals(right);

    public static bool operator !=(Card left, Card right) => !left.Equals(right);

    public override string ToString() => $"{Suit} {Rank}";
}
