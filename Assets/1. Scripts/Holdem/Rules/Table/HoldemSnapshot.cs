using System;

namespace Poker.Foundation
{
    /// <summary>One roster seat projected for a specific viewer without hidden-card authority.</summary>
    public sealed class HoldemSeatView
    {
        private readonly Card[] visibleHoleCards;
        private readonly Card[] revealedBestCards;

        internal HoldemSeatView(SeatId seat, int tableIndex, bool isViewer,
            bool wasDealtIn, bool isButton, bool isSmallBlind, bool isBigBlind,
            bool isCurrentActor, HoldemSeatStatus status, SeatChips chips,
            long streetContribution, long awarded, Card[] ownedVisibleHoleCards,
            HandValue? revealedHandValue, Card[] ownedRevealedBestCards)
        {
            Seat = seat;
            TableIndex = tableIndex;
            IsViewer = isViewer;
            WasDealtIn = wasDealtIn;
            IsButton = isButton;
            IsSmallBlind = isSmallBlind;
            IsBigBlind = isBigBlind;
            IsCurrentActor = isCurrentActor;
            Status = status;
            Stack = chips.Stack;
            Committed = chips.Committed;
            StreetContribution = streetContribution;
            Awarded = awarded;
            visibleHoleCards = ownedVisibleHoleCards ?? Array.Empty<Card>();
            RevealedHandValue = revealedHandValue;
            revealedBestCards = ownedRevealedBestCards ?? Array.Empty<Card>();
        }

        public SeatId Seat { get; }
        public int TableIndex { get; }
        public bool IsViewer { get; }
        public bool WasDealtIn { get; }
        public bool IsButton { get; }
        public bool IsSmallBlind { get; }
        public bool IsBigBlind { get; }
        public bool IsCurrentActor { get; }
        public HoldemSeatStatus Status { get; }
        public long Stack { get; }
        public long Committed { get; }
        public long StreetContribution { get; }
        public long Awarded { get; }
        public HandValue? RevealedHandValue { get; }
        public int VisibleHoleCardCount => visibleHoleCards.Length;
        public int RevealedBestCardCount => revealedBestCards.Length;
        public Card GetVisibleHoleCard(int index) => GetCard(visibleHoleCards, index);
        public Card GetRevealedBestCard(int index) => GetCard(revealedBestCards, index);

        private static Card GetCard(Card[] cards, int index)
        {
            if (index < 0 || index >= cards.Length) throw new ArgumentOutOfRangeException(nameof(index));
            return cards[index];
        }
    }

    /// <summary>Seat-relative completed result with public pot layers and no folded holes.</summary>
    public sealed class HoldemPublicResult
    {
        private readonly HoldemSettlement settlement;
        private readonly HoldemHand hand;
        private readonly SeatId[] roster;
        private readonly SeatId viewer;

        internal HoldemPublicResult(HoldemSettlement settlement, HoldemHand hand,
            SeatId[] rosterSeats, SeatId viewer)
        {
            this.settlement = settlement ?? throw new ArgumentNullException(nameof(settlement));
            this.hand = hand ?? throw new ArgumentNullException(nameof(hand));
            if (rosterSeats == null) throw new ArgumentNullException(nameof(rosterSeats));
            roster = (SeatId[])rosterSeats.Clone();
            this.viewer = viewer;
        }

        public HoldemResultKind Kind => settlement.Kind;
        public long PotAmount => settlement.PotAmount;
        public int PotCount => settlement.PotCount;
        public SeatId? WinnerSeat => settlement.WinnerSeat;
        public SeatId? FoldedSeat => settlement.FoldedSeat;
        public SeatId? AccusedSeat => settlement.AccusedSeat;
        public long OwnPayout => GetAwardedTo(viewer);
        public long OpponentPayout => GetAwardedTo(CompatibilityOpponent());
        public HandValue? OwnHandValue => RevealedValue(viewer);
        public HandValue? OpponentHandValue => RevealedValue(CompatibilityOpponent());
        public int OwnBestCardCount => hand.HasRevealedHand(viewer) ? HandEvaluator.HandSize : 0;
        public int OpponentBestCardCount => hand.HasRevealedHand(CompatibilityOpponent()) ? HandEvaluator.HandSize : 0;

        public PotAward GetPot(int index) => settlement.GetPot(index);

        public long GetAwardedTo(SeatId seat)
        {
            RequireRosterSeat(seat);
            return hand.WasDealtIn(seat) ? settlement.GetAwardedTo(seat) : 0;
        }

        public HandValue? GetRevealedHandValue(SeatId seat)
        {
            RequireRosterSeat(seat);
            return RevealedValue(seat);
        }

        public Card GetOwnBestCard(int index) => GetRevealedBestCard(viewer, index);
        public Card GetOpponentBestCard(int index) => GetRevealedBestCard(CompatibilityOpponent(), index);

        public Card GetRevealedBestCard(SeatId seat, int index)
        {
            RequireRosterSeat(seat);
            if (!hand.HasRevealedHand(seat))
                throw new InvalidOperationException("This seat has no revealed showdown hand.");
            return settlement.GetBestCard(seat, index);
        }

        private HandValue? RevealedValue(SeatId seat)
            => hand.HasRevealedHand(seat) ? settlement.GetHandValue(seat) : (HandValue?)null;

        private SeatId CompatibilityOpponent()
        {
            if (roster.Length != 2)
                throw new InvalidOperationException("The singular opponent alias is only available heads-up.");
            return roster[0] == viewer ? roster[1] : roster[0];
        }

        private void RequireRosterSeat(SeatId seat)
        {
            for (int i = 0; i < roster.Length; i++) if (roster[i] == seat) return;
            throw new ArgumentException("The seat is not part of this table.", nameof(seat));
        }
    }

    /// <summary>Player-facing state; private cards are copied only when visible to this viewer.</summary>
    public sealed class HoldemSnapshot
    {
        private readonly Card[] board;
        private readonly HoldemSeatView[] seats;
        private readonly HoldemAccusationOutcome[] accusationOutcomes;

        internal HoldemSnapshot(Guid sessionId, long sessionVersion, long handNumber,
            HoldemButtonPolicy buttonPolicy, bool canContinue, bool isOver,
            SeatId? sessionWinnerSeat, SeatId? compatibilityBustedSeat,
            SeatId[] roster, ChipLedger baseRosterLedger, HoldemHand hand, SeatId viewer,
            HoldemAccusationView accusations = null, HoldemDealWindow pendingDeal = null,
            FixedBettingView fixedBetting = null, HoldemAccusationOutcome[] accusationOutcomes = null, long roundInMatch = 0)
        {
            if (hand == null) throw new ArgumentNullException(nameof(hand));
            if (roster == null) throw new ArgumentNullException(nameof(roster));
            if (baseRosterLedger == null) throw new ArgumentNullException(nameof(baseRosterLedger));
            int viewerIndex = IndexOf(roster, viewer);
            SessionId = sessionId;
            SessionVersion = sessionVersion;
            HandNumber = handNumber;
            RoundInMatch = roundInMatch == 0 ? handNumber : roundInMatch;
            ButtonPolicy = buttonPolicy;
            CanContinue = canContinue;
            IsOver = isOver;
            SessionWinnerSeat = sessionWinnerSeat;
            compatibilityBusted = compatibilityBustedSeat;
            HandId = hand.HandId;
            HandVersion = hand.Version;
            ViewerSeat = viewer;
            Street = hand.Street;
            SettlementState = hand.SettlementState;
            IsRevealPending = hand.IsRevealPending;
            IsDealPending = hand.IsDealPending;
            IsFinalConversationPending = hand.IsFinalConversationPending;
            PendingDeal = pendingDeal;
            Accusations = accusations;
            this.accusationOutcomes = accusationOutcomes == null ? Array.Empty<HoldemAccusationOutcome>()
                : (HoldemAccusationOutcome[])accusationOutcomes.Clone();
            FixedBetting = fixedBetting;
            BettingPolicy = hand.Config.BettingPolicy;
            PostedAnte = BettingPolicy == HoldemBettingPolicy.SharedAnteFixed
                ? Math.Min(hand.Config.Ante, hand.ContributionLimit)
                : BettingPolicy == HoldemBettingPolicy.SharedAnteSequential ? hand.Config.Ante : 0;
            CanAccuseOnTurn = hand.CanAccuseOnTurn(viewer);
            ButtonSeat = hand.ButtonSeat;
            SmallBlindSeat = hand.SmallBlindSeat;
            BigBlindSeat = hand.BigBlindSeat;
            CurrentSeat = hand.CurrentSeat;
            board = CopyBoard(hand);
            seats = new HoldemSeatView[roster.Length];
            for (int i = 0; i < roster.Length; i++)
                seats[i] = CreateSeatView(roster[i], i, viewer, baseRosterLedger, hand);
            LegalActions = hand.CurrentSeat == viewer ? hand.CurrentBetting.GetLegalActions() : null;
            Result = hand.IsComplete
                ? new HoldemPublicResult(hand.Result, hand, roster, viewer)
                : null;
            PotAmount = hand.IsComplete ? hand.Result.PotAmount : hand.Ledger.TotalCommitted;
            OwnSeatIndex = viewerIndex;
        }

        private readonly SeatId? compatibilityBusted;
        private int OwnSeatIndex { get; }
        public Guid SessionId { get; }
        public Guid HandId { get; }
        public long SessionVersion { get; }
        /// <summary>Informational per-hand revision only. Commands must use SessionVersion.</summary>
        public long HandVersion { get; }
        public long HandNumber { get; }
        public long RoundInMatch { get; }
        public HoldemButtonPolicy ButtonPolicy { get; }
        public bool CanContinue { get; }
        public bool IsOver { get; }
        public SeatId? SessionWinnerSeat { get; }
        public HoldemStreet Street { get; }
        public HoldemSettlementState SettlementState { get; }
        public bool IsSettlementPending => SettlementState == HoldemSettlementState.AwaitingOddChipPriority;
        public bool IsRevealPending { get; }
        public bool IsDealPending { get; }
        public bool IsFinalConversationPending { get; }
        public HoldemDealWindow PendingDeal { get; }
        public HoldemAccusationView Accusations { get; }
        public FixedBettingView FixedBetting { get; }
        public HoldemBettingPolicy BettingPolicy { get; }
        public long PostedAnte { get; }
        public bool CanAccuseOnTurn { get; }
        public SeatId ButtonSeat { get; }
        public SeatId SmallBlindSeat { get; }
        public SeatId BigBlindSeat { get; }
        public SeatId ViewerSeat { get; }
        public SeatId? CurrentSeat { get; }
        public long PotAmount { get; }
        public LegalBettingActions LegalActions { get; }
        public HoldemPublicResult Result { get; }
        public int BoardCount => board.Length;
        public int SeatCount => seats.Length;
        public int AccusationOutcomeCount => accusationOutcomes.Length;
        public HoldemAccusationOutcome GetAccusationOutcome(int index)
        {
            if (index < 0 || index >= accusationOutcomes.Length) throw new ArgumentOutOfRangeException(nameof(index));
            return accusationOutcomes[index];
        }

        public HoldemSeatView GetSeatAt(int tableIndex)
        {
            if (tableIndex < 0 || tableIndex >= seats.Length) throw new ArgumentOutOfRangeException(nameof(tableIndex));
            return seats[tableIndex];
        }

        public HoldemSeatView GetSeat(SeatId seat)
        {
            for (int i = 0; i < seats.Length; i++) if (seats[i].Seat == seat) return seats[i];
            throw new ArgumentException("The seat is not part of this table.", nameof(seat));
        }

        public Card GetBoardCard(int index)
        {
            if (index < 0 || index >= board.Length) throw new ArgumentOutOfRangeException(nameof(index));
            return board[index];
        }

        // Viewer aliases work at every table size; singular opponent aliases require two seats.
        public SeatId OpponentSeat => CompatibilityOpponent().Seat;
        public SeatId? BustedSeat
        {
            get
            {
                RequireHeadsUp();
                return compatibilityBusted;
            }
        }
        public long OwnStack => seats[OwnSeatIndex].Stack;
        public long OwnCommitted => seats[OwnSeatIndex].Committed;
        public long OwnStreetContribution => seats[OwnSeatIndex].StreetContribution;
        public int OwnCardCount => seats[OwnSeatIndex].VisibleHoleCardCount;
        public Card GetOwnCard(int index) => seats[OwnSeatIndex].GetVisibleHoleCard(index);
        public long OpponentStack => CompatibilityOpponent().Stack;
        public long OpponentCommitted => CompatibilityOpponent().Committed;
        public long OpponentStreetContribution => CompatibilityOpponent().StreetContribution;
        public int OpponentCardCount => CompatibilityOpponent().VisibleHoleCardCount;
        public Card GetOpponentCard(int index) => CompatibilityOpponent().GetVisibleHoleCard(index);

        private HoldemSeatView CompatibilityOpponent()
        {
            RequireHeadsUp();
            return seats[OwnSeatIndex == 0 ? 1 : 0];
        }

        private void RequireHeadsUp()
        {
            if (seats.Length != 2)
                throw new InvalidOperationException("The singular opponent alias is only available heads-up.");
        }

        private static HoldemSeatView CreateSeatView(SeatId seat, int tableIndex,
            SeatId viewer, ChipLedger baseRosterLedger, HoldemHand hand)
        {
            bool dealt = hand.WasDealtIn(seat);
            bool folded = dealt && hand.IsFolded(seat);
            SeatChips chips = dealt ? hand.Ledger.GetChips(seat) : baseRosterLedger.GetChips(seat);
            HoldemSeatStatus status = !dealt ? HoldemSeatStatus.Busted
                : folded ? HoldemSeatStatus.Folded
                : hand.IsComplete && chips.Stack == 0 ? HoldemSeatStatus.Busted
                : hand.IsAllIn(seat) && !hand.IsComplete ? HoldemSeatStatus.AllIn
                : HoldemSeatStatus.Active;
            bool revealHoles = dealt && (seat == viewer || hand.HasRevealedHand(seat));
            Card[] holes = revealHoles ? CopyHoles(hand, seat) : Array.Empty<Card>();
            HandValue? value = null;
            Card[] best = Array.Empty<Card>();
            if (dealt && hand.HasRevealedHand(seat))
            {
                HoldemEvaluatedHand evaluated = hand.GetRevealedHand(seat);
                value = evaluated.Value;
                best = new Card[evaluated.BestCardCount];
                for (int i = 0; i < best.Length; i++) best[i] = evaluated.GetBestCard(i);
            }
            long awarded = hand.Result != null && dealt ? hand.Result.GetAwardedTo(seat) : 0;
            bool hasBlinds = hand.Config.BettingPolicy == HoldemBettingPolicy.NoLimit;
            return new HoldemSeatView(seat, tableIndex, seat == viewer, dealt,
                seat == hand.ButtonSeat, hasBlinds && seat == hand.SmallBlindSeat, hasBlinds && seat == hand.BigBlindSeat,
                hand.CurrentSeat == seat, status, chips,
                dealt ? hand.GetStreetContribution(seat) : 0, awarded, holes, value, best);
        }

        private static Card[] CopyBoard(HoldemHand hand)
        {
            var result = new Card[hand.BoardCount];
            for (int i = 0; i < result.Length; i++) result[i] = hand.GetBoardCard(i);
            return result;
        }

        private static Card[] CopyHoles(HoldemHand hand, SeatId seat)
        {
            var result = new Card[hand.GetHoleCardCount(seat)];
            for (int i = 0; i < result.Length; i++) result[i] = hand.GetHoleCard(seat, i);
            return result;
        }

        private static int IndexOf(SeatId[] roster, SeatId seat)
        {
            for (int i = 0; i < roster.Length; i++) if (roster[i] == seat) return i;
            throw new ArgumentException("The viewer is not part of this table.", nameof(seat));
        }
    }

}
