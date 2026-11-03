using System;

namespace Poker.Foundation
{
    /// <summary>An opaque seat identity, not an account ID or a dealing order.</summary>
    public readonly struct SeatId : IEquatable<SeatId>
    {
        public SeatId(int value)
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), value, "Seat IDs must be positive.");
            Value = value;
        }
        public int Value { get; }
        public bool IsValid => Value > 0;
        public bool Equals(SeatId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is SeatId other && Equals(other);
        public override int GetHashCode() => Value;
        public static bool operator ==(SeatId left, SeatId right) => left.Equals(right);
        public static bool operator !=(SeatId left, SeatId right) => !left.Equals(right);
    }
}
