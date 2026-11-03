using System;
using System.Collections.Generic;

namespace Poker.Foundation
{
    /// <summary>Pure clockwise ordering for the one supported forward-moving button profile.</summary>
    internal static class HoldemSeatOrder
    {
        public static SeatId[] CopyTable(IReadOnlyList<SeatId> source, int minimum = 2, int maximum = 4)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (source.Count < minimum || source.Count > maximum)
                throw new ArgumentException("Hold'em supports two through four table seats.", nameof(source));
            var result = new SeatId[source.Count];
            var seen = new HashSet<SeatId>();
            for (int i = 0; i < result.Length; i++)
            {
                SeatId seat = source[i];
                if (!seat.IsValid || !seen.Add(seat))
                    throw new ArgumentException("Table seats must be valid and unique.", nameof(source));
                result[i] = seat;
            }
            return result;
        }

        public static SeatId Next(IReadOnlyList<SeatId> clockwise, SeatId after,
            Predicate<SeatId> include)
        {
            if (clockwise == null) throw new ArgumentNullException(nameof(clockwise));
            if (include == null) throw new ArgumentNullException(nameof(include));
            int start = IndexOf(clockwise, after);
            for (int offset = 1; offset <= clockwise.Count; offset++)
            {
                SeatId seat = clockwise[(start + offset) % clockwise.Count];
                if (include(seat)) return seat;
            }
            throw new InvalidOperationException("No clockwise seat satisfies the requested state.");
        }

        public static SeatId[] RotateAfter(IReadOnlyList<SeatId> clockwise, SeatId after,
            Predicate<SeatId> include)
        {
            if (clockwise == null) throw new ArgumentNullException(nameof(clockwise));
            if (include == null) throw new ArgumentNullException(nameof(include));
            int count = 0;
            for (int i = 0; i < clockwise.Count; i++) if (include(clockwise[i])) count++;
            var result = new SeatId[count];
            int start = IndexOf(clockwise, after);
            int written = 0;
            for (int offset = 1; offset <= clockwise.Count; offset++)
            {
                SeatId seat = clockwise[(start + offset) % clockwise.Count];
                if (include(seat)) result[written++] = seat;
            }
            return result;
        }

        public static SeatId[] SequentialOrder(IReadOnlyList<SeatId> clockwise, SeatId button,
            Predicate<SeatId> include, SequentialFirstSeat policy)
        {
            if (policy != SequentialFirstSeat.Button && policy != SequentialFirstSeat.AfterButton)
                throw new ArgumentOutOfRangeException(nameof(policy));
            SeatId[] order = RotateAfter(clockwise, button, include);
            if (policy == SequentialFirstSeat.Button && include(button))
            {
                for (int i = order.Length - 1; i > 0; i--) order[i] = order[i - 1];
                order[0] = button;
            }
            return order;
        }

        public static SeatId[] OpeningOrder(IReadOnlyList<SeatId> activeClockwise,
            SeatId button, out SeatId smallBlind, out SeatId bigBlind)
        {
            if (activeClockwise == null) throw new ArgumentNullException(nameof(activeClockwise));
            if (activeClockwise.Count < 2 || activeClockwise.Count > 4)
                throw new ArgumentException("An opening order requires two through four active seats.", nameof(activeClockwise));
            IndexOf(activeClockwise, button);
            Predicate<SeatId> all = _ => true;
            smallBlind = activeClockwise.Count == 2 ? button : Next(activeClockwise, button, all);
            bigBlind = Next(activeClockwise, smallBlind, all);
            SeatId first = activeClockwise.Count == 2 ? button : Next(activeClockwise, bigBlind, all);
            return RotateIncludingFirst(activeClockwise, first);
        }

        private static SeatId[] RotateIncludingFirst(IReadOnlyList<SeatId> clockwise, SeatId first)
        {
            int start = IndexOf(clockwise, first);
            var result = new SeatId[clockwise.Count];
            for (int i = 0; i < result.Length; i++) result[i] = clockwise[(start + i) % clockwise.Count];
            return result;
        }

        public static int IndexOf(IReadOnlyList<SeatId> seats, SeatId target)
        {
            if (seats == null) throw new ArgumentNullException(nameof(seats));
            for (int i = 0; i < seats.Count; i++) if (seats[i] == target) return i;
            throw new ArgumentException("The seat is not part of this clockwise order.", nameof(target));
        }
    }
}
