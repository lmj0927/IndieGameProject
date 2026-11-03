using Poker.Foundation;

namespace Poker.Application
{
    public sealed class HoldemActionNotice
    {
        internal HoldemActionNotice(SeatId seat, BettingAction action, long paid, HoldemStreet street)
        { Seat = seat; Kind = action.Kind; Paid = paid; Target = action.Target; Street = street; }
        public SeatId Seat { get; }
        public BettingActionKind Kind { get; }
        public long Paid { get; }
        public long Target { get; }
        public HoldemStreet Street { get; }
    }
}
