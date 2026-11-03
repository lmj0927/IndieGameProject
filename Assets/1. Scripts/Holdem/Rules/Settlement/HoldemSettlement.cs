using System;
using System.Collections.Generic;

namespace Poker.Foundation
{
    /// <summary>Community-card result backed by the shared multi-pot settlement engine.</summary>
    public sealed class HoldemSettlement
    {
        private readonly PotSettlement payment;
        private readonly SeatId[] showdownSeats;
        private readonly HoldemEvaluatedHand[] showdownHands;
        private readonly SeatId compatibilityFirstSeat;
        private readonly SeatId compatibilitySecondSeat;

        private HoldemSettlement(PotSettlement payment, HoldemResultKind kind,
            SeatId? lastFoldedSeat, SeatId[] ownedShowdownSeats,
            HoldemEvaluatedHand[] ownedShowdownHands, SeatId firstSeat, SeatId secondSeat,
            SeatId? accusedSeat = null)
        {
            this.payment = payment ?? throw new ArgumentNullException(nameof(payment));
            Kind = kind;
            FoldedSeat = lastFoldedSeat;
            AccusedSeat = accusedSeat;
            showdownSeats = ownedShowdownSeats ?? Array.Empty<SeatId>();
            showdownHands = ownedShowdownHands ?? Array.Empty<HoldemEvaluatedHand>();
            compatibilityFirstSeat = firstSeat;
            compatibilitySecondSeat = secondSeat;
            WinnerSeat = FindSoleRecipient(payment);
        }

        public ChipLedger Ledger => payment.Ledger;
        public HoldemResultKind Kind { get; }
        public long PotAmount => payment.TotalAwarded;
        public int PotCount => payment.PotCount;
        /// <summary>Set only when exactly one seat receives chips from the result.</summary>
        public SeatId? WinnerSeat { get; }
        /// <summary>Compatibility detail for heads-up folds; multi-seat hands may have earlier folds.</summary>
        public SeatId? FoldedSeat { get; }
        /// <summary>Set only for <see cref="HoldemResultKind.Accusation"/>: the seat whose card change was caught.</summary>
        public SeatId? AccusedSeat { get; }
        public SeatId FirstSeat => CompatibilitySeat(compatibilityFirstSeat);
        public SeatId SecondSeat => CompatibilitySeat(compatibilitySecondSeat);

        public PotAward GetPot(int index) => payment.GetPot(index);
        public long GetAwardedTo(SeatId seat) => payment.GetAwardedTo(seat);

        public bool HasHandValue(SeatId seat)
        {
            for (int i = 0; i < showdownSeats.Length; i++) if (showdownSeats[i] == seat) return true;
            Ledger.GetChips(seat);
            return false;
        }

        public HandValue GetHandValue(SeatId seat) => GetEvaluatedHand(seat).Value;
        public Card GetBestCard(SeatId seat, int index) => GetEvaluatedHand(seat).GetBestCard(index);

        internal static HoldemSettlement Showdown(ChipLedger ledger,
            IReadOnlyList<SeatId> liveSeats, IReadOnlyList<HoldemEvaluatedHand> liveHands,
            IReadOnlyList<SeatId> oddChipPriority, SeatId compatibilityFirstSeat,
            SeatId compatibilitySecondSeat)
        {
            if (ledger == null) throw new ArgumentNullException(nameof(ledger));
            if (liveSeats == null) throw new ArgumentNullException(nameof(liveSeats));
            if (liveHands == null) throw new ArgumentNullException(nameof(liveHands));
            if (liveSeats.Count < 2 || liveSeats.Count != liveHands.Count)
                throw new ArgumentException("Showdown requires matching live seats and evaluated hands.");
            var seats = new SeatId[liveSeats.Count];
            var hands = new HoldemEvaluatedHand[liveHands.Count];
            var ranked = new SeatHandValue[liveSeats.Count];
            var seen = new HashSet<SeatId>();
            for (int i = 0; i < seats.Length; i++)
            {
                SeatId seat = liveSeats[i];
                HoldemEvaluatedHand hand = liveHands[i];
                if (!seat.IsValid || !seen.Add(seat) || hand == null)
                    throw new ArgumentException("Showdown seats must be valid, unique and evaluated.");
                ledger.GetChips(seat);
                seats[i] = seat;
                hands[i] = hand;
                ranked[i] = new SeatHandValue(seat, hand.Value);
            }
            PotSettlement paid = PotSettlement.ShowdownByValue(ledger, ranked, oddChipPriority);
            return new HoldemSettlement(paid, HoldemResultKind.Showdown, null, seats, hands,
                compatibilityFirstSeat, compatibilitySecondSeat);
        }

        internal static HoldemSettlement AwardUncontested(ChipLedger ledger,
            SeatId soleWinner, SeatId? lastFoldedSeat, SeatId compatibilityFirstSeat,
            SeatId compatibilitySecondSeat)
        {
            PotSettlement paid = PotSettlement.AwardUncontested(ledger, soleWinner);
            return new HoldemSettlement(paid, HoldemResultKind.Fold,
                lastFoldedSeat, null, null, compatibilityFirstSeat, compatibilitySecondSeat);
        }

        internal static HoldemSettlement AwardAccusation(ChipLedger ledger, SeatId accuser, SeatId accused,
            SeatId compatibilityFirstSeat, SeatId compatibilitySecondSeat)
        {
            PotSettlement paid = PotSettlement.AwardEntirePot(ledger, accuser);
            return new HoldemSettlement(paid, HoldemResultKind.Accusation, null, null, null,
                compatibilityFirstSeat, compatibilitySecondSeat, accused);
        }

        internal static HoldemSettlement AwardAccusation(ChipLedger ledger, SeatId accuser,
            SeatId compatibilityFirstSeat, SeatId compatibilitySecondSeat)
        {
            PotSettlement paid = PotSettlement.AwardUncontested(ledger, accuser);
            return new HoldemSettlement(paid, HoldemResultKind.Accusation, null, null, null,
                compatibilityFirstSeat, compatibilitySecondSeat);
        }

        internal static HoldemSettlement AwardTurnAccusation(ChipLedger ledger, SeatId accuser,
            SeatId compatibilityFirstSeat, SeatId compatibilitySecondSeat)
            => new HoldemSettlement(PotSettlement.AwardCurrentPot(ledger, accuser), HoldemResultKind.Accusation,
                null, null, null, compatibilityFirstSeat, compatibilitySecondSeat);

        private HoldemEvaluatedHand GetEvaluatedHand(SeatId seat)
        {
            if (Kind != HoldemResultKind.Showdown)
                throw new InvalidOperationException("Folded hands do not reveal private-card evaluations.");
            for (int i = 0; i < showdownSeats.Length; i++)
                if (showdownSeats[i] == seat) return showdownHands[i];
            Ledger.GetChips(seat);
            throw new InvalidOperationException("This seat did not reach showdown.");
        }

        private SeatId CompatibilitySeat(SeatId seat)
        {
            if (Ledger.SeatCount != 2)
                throw new InvalidOperationException("The heads-up seat alias is unavailable at a multi-seat table.");
            Ledger.GetChips(seat);
            return seat;
        }

        private static SeatId? FindSoleRecipient(PotSettlement settlement)
        {
            SeatId? recipient = null;
            for (int i = 0; i < settlement.Ledger.SeatCount; i++)
            {
                SeatId seat = settlement.Ledger.GetSeatAt(i);
                if (settlement.GetAwardedTo(seat) == 0) continue;
                if (recipient.HasValue) return null;
                recipient = seat;
            }
            return recipient;
        }
    }
}
