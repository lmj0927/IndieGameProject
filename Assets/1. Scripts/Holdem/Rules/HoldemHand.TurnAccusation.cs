using System;

namespace Poker.Foundation
{
    public sealed partial class HoldemHand
    {
        /// <summary>
        /// The current seat on its first action of a post-reveal street. A raise that brings the action
        /// back to a seat that already acted does not reopen the accusation.
        /// </summary>
        private bool CanAccuseLegacyOnTurn(SeatId seat)
            => Config.TurnAccusations && CurrentSeat == seat
                && (Street == HoldemStreet.Flop || Street == HoldemStreet.Turn || Street == HoldemStreet.River)
                && !CurrentBetting.HasActed(seat);

        public bool IsTurnAccusationTarget(SeatId accuser, SeatId target)
            => target.IsValid && target != accuser && WasDealtIn(target) && !IsFolded(target);

        /// <summary>The caller supplies the verdict from the host's committed deal record.</summary>
        internal HoldemHand ResolveTurnAccusation(SeatId accuser, SeatId target, bool caught)
        {
            if (!CanAccuseLegacyOnTurn(accuser)) throw new InvalidOperationException("This seat cannot accuse now.");
            if (!IsTurnAccusationTarget(accuser, target)) throw new ArgumentException("Invalid accusation target.", nameof(target));
            if (!caught) return Apply(accuser, BettingAction.Fold());
            HoldemSettlement settlement = HoldemSettlement.AwardAccusation(CurrentBetting.Ledger, accuser, target,
                ButtonSeat, BigBlindSeat);
            return WithComplete(checked(Version + 1), board, deck, CurrentBetting, settlement);
        }
    }
}
