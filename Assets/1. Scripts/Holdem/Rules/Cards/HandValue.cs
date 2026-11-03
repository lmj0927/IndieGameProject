using System;

namespace Poker.Foundation
{
    public enum HandCategory
    {
        Invalid = 0,
        HighCard = 1,
        OnePair = 2,
        TwoPair = 3,
        ThreeOfAKind = 4,
        Straight = 5,
        Flush = 6,
        FullHouse = 7,
        FourOfAKind = 8,
        StraightFlush = 9
    }

    /// <summary>
    /// Category followed by tie breakers in priority order. Larger compares stronger.
    /// Only the evaluator creates values; scalar fields prevent collection aliasing.
    /// </summary>
    public readonly struct HandValue : IEquatable<HandValue>, IComparable<HandValue>
    {
        private readonly int rank0;
        private readonly int rank1;
        private readonly int rank2;
        private readonly int rank3;
        private readonly int rank4;

        public HandCategory Category { get; }
        public int TieBreakerCount { get; }
        public bool IsValid => Category != HandCategory.Invalid;

        internal HandValue(HandCategory category, int first, int second = 0,
            int third = 0, int fourth = 0, int fifth = 0)
        {
            Category = category;
            TieBreakerCount = CountFor(category);
            rank0 = first;
            rank1 = second;
            rank2 = third;
            rank3 = fourth;
            rank4 = fifth;
            for (int i = 0; i < 5; i++)
            {
                int rank = GetRank(i);
                if (i < TieBreakerCount ? rank < 2 || rank > 14 : rank != 0)
                    throw new ArgumentException("Invalid tie-breaker shape for the hand category.");
            }
            if ((category == HandCategory.Straight || category == HandCategory.StraightFlush) && first < 5)
                throw new ArgumentException("A straight must be at least five-high.");
        }

        public int GetTieBreaker(int index)
        {
            EnsureValid();
            if (index < 0 || index >= TieBreakerCount)
                throw new ArgumentOutOfRangeException(nameof(index));
            return GetRank(index);
        }

        public int CompareTo(HandValue other)
        {
            EnsureValid();
            other.EnsureValid();
            int comparison = Category.CompareTo(other.Category);
            if (comparison != 0) return comparison;
            for (int i = 0; i < TieBreakerCount; i++)
            {
                comparison = GetRank(i).CompareTo(other.GetRank(i));
                if (comparison != 0) return comparison;
            }
            return 0;
        }

        public bool Equals(HandValue other) => Category == other.Category
            && rank0 == other.rank0 && rank1 == other.rank1 && rank2 == other.rank2
            && rank3 == other.rank3 && rank4 == other.rank4;
        public override bool Equals(object obj) => obj is HandValue other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = (int)Category;
                for (int i = 0; i < 5; i++) hash = hash * 31 + GetRank(i);
                return hash;
            }
        }

        public static bool operator ==(HandValue left, HandValue right) => left.Equals(right);
        public static bool operator !=(HandValue left, HandValue right) => !left.Equals(right);
        public static bool operator <(HandValue left, HandValue right) => left.CompareTo(right) < 0;
        public static bool operator >(HandValue left, HandValue right) => left.CompareTo(right) > 0;
        public static bool operator <=(HandValue left, HandValue right) => left.CompareTo(right) <= 0;
        public static bool operator >=(HandValue left, HandValue right) => left.CompareTo(right) >= 0;

        private void EnsureValid()
        {
            if (!IsValid) throw new InvalidOperationException("An uninitialized hand value cannot be ranked.");
        }

        private int GetRank(int index)
        {
            switch (index)
            {
                case 0: return rank0;
                case 1: return rank1;
                case 2: return rank2;
                case 3: return rank3;
                case 4: return rank4;
                default: throw new ArgumentOutOfRangeException(nameof(index));
            }
        }

        private static int CountFor(HandCategory category)
        {
            switch (category)
            {
                case HandCategory.HighCard:
                case HandCategory.Flush: return 5;
                case HandCategory.OnePair: return 4;
                case HandCategory.TwoPair:
                case HandCategory.ThreeOfAKind: return 3;
                case HandCategory.FullHouse:
                case HandCategory.FourOfAKind: return 2;
                case HandCategory.Straight:
                case HandCategory.StraightFlush: return 1;
                default: throw new ArgumentOutOfRangeException(nameof(category));
            }
        }
    }
}
