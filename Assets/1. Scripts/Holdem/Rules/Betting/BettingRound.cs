using System;
using System.Collections.Generic;

namespace Poker.Foundation
{
    /// <summary>Immutable street rules for sequential or fixed simultaneous betting. Authority-only state.</summary>
    public sealed class BettingRound
    {
        private struct Player
        {
            public SeatId Seat;
            public long Paid;
            public long LastFaced;
            public bool HasActed;
            public bool Folded;
            public FixedBettingProposal? Proposal;
            public FixedBettingResponse? Response;
        }

        private readonly Player[] players;
        private readonly SequentialBettingRules sequentialRules;
        private int currentIndex = -1;

        private BettingRound(ChipLedger ledger, Player[] ownedPlayers, long minimumBet,
            long currentBet, long lastFullRaise, SequentialBettingRules sequentialRules = null)
        {
            Ledger = ledger;
            players = ownedPlayers;
            MinimumBet = minimumBet;
            CurrentBet = currentBet;
            LastFullRaise = lastFullRaise;
            this.sequentialRules = sequentialRules;
        }

        public ChipLedger Ledger { get; private set; }
        public int SeatCount => players.Length;
        public int ActiveSeatCount
        {
            get { int count = 0; foreach (Player p in players) if (!p.Folded) count++; return count; }
        }
        public bool IsComplete => FixedPhase.HasValue
            ? FixedPhase == FixedBettingPhase.Complete : currentIndex < 0;
        public SeatId? CurrentSeat => currentIndex < 0 ? (SeatId?)null : players[currentIndex].Seat;
        /// <summary>Nominal opening bring-in while pending; actual highest street payment after completion.</summary>
        public long CurrentBet { get; private set; }
        public long MinimumBet { get; }
        public long LastFullRaise { get; private set; }
        public SeatId? RefundedSeat { get; private set; }
        public long RefundedAmount { get; private set; }
        public FixedBettingPhase? FixedPhase { get; private set; }
        public long FixedIncrement { get; private set; }
        public long PostedAnte { get; private set; }
        public long ContributionLimit { get; private set; }

        /// <summary>
        /// Prototype common-cap opening: equal ante, no blinds and no preflop betting.
        /// Contributions are hand money, not street payments. The hand owns the next deal window.
        /// </summary>
        public static BettingRound BeginSharedAnte(ChipLedger ledger, IReadOnlyList<SeatId> seats,
            long ante, long minimumBet)
        {
            if (ante <= 0) throw new ArgumentOutOfRangeException(nameof(ante));
            if (minimumBet <= 0) throw new ArgumentOutOfRangeException(nameof(minimumBet));
            Player[] players = CopyPlayers(ledger, seats);
            if (players.Length < 2 || players.Length > 4 || players.Length != ledger.SeatCount
                || ledger.TotalCommitted != 0)
                throw new ArgumentException("Shared ante requires two to four starting seats and no prior contributions.");
            long limit = long.MaxValue;
            foreach (Player player in players)
            {
                long stack = ledger.GetChips(player.Seat).Stack;
                if (stack == 0) throw new ArgumentException("Starting seats must have chips.");
                limit = Math.Min(limit, stack);
            }
            long paid = Math.Min(ante, limit);
            ChipLedger candidate = ledger;
            foreach (Player player in players) candidate = candidate.Contribute(player.Seat, paid);
            return new BettingRound(candidate, players, minimumBet, 0, minimumBet)
            { PostedAnte = paid, ContributionLimit = limit };
        }

        /// <summary>Individual ante contributions followed by real preflop turns; no common-stack cap.</summary>
        public static BettingRound BeginSequentialAnte(ChipLedger ledger, IReadOnlyList<SeatId> order,
            long ante, SequentialBettingRules rules)
        {
            if (ante <= 0) throw new ArgumentOutOfRangeException(nameof(ante));
            if (rules == null) throw new ArgumentNullException(nameof(rules));
            Player[] players = CopyPlayers(ledger, order);
            if (players.Length < 2 || players.Length > 4 || players.Length != ledger.SeatCount || ledger.TotalCommitted != 0)
                throw new ArgumentException("Sequential ante requires all starting seats and no previous contributions.");
            ChipLedger candidate = ledger;
            long highestAnte = 0, secondHighestAnte = 0;
            SeatId? unmatchedSeat = null;
            foreach (Player player in players)
            {
                long available = ledger.GetChips(player.Seat).Stack;
                if (available == 0) throw new ArgumentException("Starting seats must have chips.");
                if (available < ante && rules.AnteShortfall == ChipShortfallPolicy.RequireFullAmount)
                    throw new InvalidOperationException("The configured ante shortfall rule requires the full ante.");
                long payment = Math.Min(ante, available);
                candidate = candidate.Contribute(player.Seat, payment);
                if (payment > highestAnte)
                { secondHighestAnte = highestAnte; highestAnte = payment; unmatchedSeat = player.Seat; }
                else if (payment > secondHighestAnte) secondHighestAnte = payment;
            }
            // Ante is hand money, not a street bet. Return only a singleton top layer;
            // lowering every ante to the smallest stack would erase legitimate side pots.
            long unmatchedAnte = checked(highestAnte - secondHighestAnte);
            if (unmatchedAnte > 0) candidate = candidate.RefundContribution(unmatchedSeat.Value, unmatchedAnte);
            var round = new BettingRound(candidate, players, rules.MinimumBet, 0, rules.MinimumBet, rules)
            {
                PostedAnte = ante,
                RefundedSeat = unmatchedAnte > 0 ? unmatchedSeat : null,
                RefundedAmount = unmatchedAnte
            };
            round.Advance(0);
            return round;
        }

        /// <summary>
        /// Opens a private, two-stage fixed wager. One shared increment is applied at most once.
        /// The hand-start contribution limit is supplied by the hand, never by a player command.
        /// Do not send this authority object or its unsealed choices to other participants.
        /// </summary>
        public static BettingRound BeginFixed(ChipLedger ledger, IReadOnlyList<SeatId> activeSeats,
            long increment, long contributionLimit)
        {
            if (increment <= 0) throw new ArgumentOutOfRangeException(nameof(increment));
            if (contributionLimit <= 0) throw new ArgumentOutOfRangeException(nameof(contributionLimit));
            Player[] players = CopyPlayers(ledger, activeSeats);
            if (players.Length < 2 || players.Length > 4)
                throw new ArgumentException("Fixed betting requires two to four live seats.", nameof(activeSeats));
            long committed = ledger.GetChips(players[0].Seat).Committed;
            if (committed > contributionLimit)
                throw new ArgumentException("The contribution limit cannot be below existing payments.", nameof(contributionLimit));
            long payable = Math.Min(increment, contributionLimit - committed);
            foreach (Player player in players)
            {
                SeatChips chips = ledger.GetChips(player.Seat);
                if (chips.Committed != committed)
                    throw new ArgumentException("Common-cap fixed betting requires equal live contributions.", nameof(activeSeats));
                payable = Math.Min(payable, chips.Stack);
            }
            return new BettingRound(ledger, players, increment, 0, increment)
            {
                FixedIncrement = payable,
                ContributionLimit = contributionLimit,
                FixedPhase = payable == 0 ? FixedBettingPhase.Complete : FixedBettingPhase.Proposal
            };
        }

        /// <summary>Authority stores the seat's latest private choice; it does not reveal or charge it yet.</summary>
        public BettingRound SubmitFixedProposal(SeatId seat, FixedBettingProposal proposal)
        {
            RequireFixedPhase(FixedBettingPhase.Proposal);
            if (proposal != FixedBettingProposal.Keep && proposal != FixedBettingProposal.Increase)
                throw new ArgumentOutOfRangeException(nameof(proposal));
            var next = CopyFixed();
            next.players[FindSeat(seat)].Proposal = proposal;
            return next;
        }

        /// <summary>Host deadline seals proposals. Missing input keeps the pot unchanged.</summary>
        public BettingRound SealFixedProposals()
        {
            RequireFixedPhase(FixedBettingPhase.Proposal);
            var next = CopyFixed();
            bool increased = false;
            foreach (Player player in next.players)
                if (player.Proposal == FixedBettingProposal.Increase) increased = true;
            next.FixedPhase = increased ? FixedBettingPhase.Response : FixedBettingPhase.Complete;
            return next;
        }

        /// <summary>Only non-raisers choose whether to match the fixed payment or fold.</summary>
        public BettingRound SubmitFixedResponse(SeatId seat, FixedBettingResponse response)
        {
            RequireFixedPhase(FixedBettingPhase.Response);
            if (response != FixedBettingResponse.Match && response != FixedBettingResponse.Fold)
                throw new ArgumentOutOfRangeException(nameof(response));
            int index = FindSeat(seat);
            if (players[index].Proposal == FixedBettingProposal.Increase)
                throw new InvalidOperationException("A sealed raiser has already committed to matching.");
            var next = CopyFixed();
            next.players[index].Response = response;
            return next;
        }

        /// <summary>
        /// Host seals responses atomically. Missing input folds; one survivor pays no unmatched increment.
        /// Existing antes and earlier wagers remain in the pot even when their owners fold.
        /// </summary>
        public BettingRound SealFixedResponses()
        {
            RequireFixedPhase(FixedBettingPhase.Response);
            var next = CopyFixed();
            int live = 0;
            for (int i = 0; i < next.players.Length; i++)
            {
                Player player = next.players[i];
                player.Folded = player.Proposal != FixedBettingProposal.Increase
                    && player.Response != FixedBettingResponse.Match;
                if (!player.Folded) live++;
                next.players[i] = player;
            }
            if (live > 1)
            {
                for (int i = 0; i < next.players.Length; i++)
                {
                    if (next.players[i].Folded) continue;
                    next.Ledger = next.Ledger.Contribute(next.players[i].Seat, FixedIncrement);
                    next.players[i].Paid = FixedIncrement;
                }
                next.CurrentBet = FixedIncrement;
            }
            next.FixedPhase = FixedBettingPhase.Complete;
            next.ValidateState();
            return next;
        }

        /// <summary>Caller must authenticate this seat before using the private read in a viewer projection.</summary>
        public FixedBettingProposal? GetOwnFixedProposal(SeatId seat) => players[FindSeat(seat)].Proposal;
        public FixedBettingResponse? GetOwnFixedResponse(SeatId seat) => players[FindSeat(seat)].Response;

        public bool IsSealedRaiser(SeatId seat)
        {
            if (!FixedPhase.HasValue || FixedPhase == FixedBettingPhase.Proposal)
                throw new InvalidOperationException("Proposals are not public before the host seals them.");
            return players[FindSeat(seat)].Proposal == FixedBettingProposal.Increase;
        }

        private void RequireFixedPhase(FixedBettingPhase phase)
        {
            if (FixedPhase != phase) throw new InvalidOperationException("This fixed-betting window is not accepting that operation.");
        }

        private BettingRound CopyFixed() => new BettingRound(Ledger, (Player[])players.Clone(),
            MinimumBet, CurrentBet, LastFullRaise)
        { FixedPhase = FixedPhase, FixedIncrement = FixedIncrement, ContributionLimit = ContributionLimit };

        /// <summary>
        /// Posts blinds on a candidate. Order starts at first action, with SB then BB last (also heads-up).
        /// Caller resolves button, eligibility and hand entry. No antes, reset or duplicate-StartHand protection.
        /// </summary>
        public static BettingRound BeginOpening(ChipLedger ledger, IReadOnlyList<SeatId> actionOrder,
            SeatId smallBlindSeat, SeatId bigBlindSeat, long smallBlind, long bigBlind)
        {
            if (bigBlind <= 0) throw new ArgumentOutOfRangeException(nameof(bigBlind));
            if (smallBlind <= 0 || smallBlind > bigBlind) throw new ArgumentOutOfRangeException(nameof(smallBlind));
            Player[] players = CopyPlayers(ledger, actionOrder);
            if (players.Length < 2 || players.Length != ledger.SeatCount || ledger.TotalCommitted != 0)
                throw new ArgumentException("An opening round requires all funded starting seats and no prior contributions.");
            int sb = players.Length - 2, bb = players.Length - 1;
            if (players[sb].Seat != smallBlindSeat || players[bb].Seat != bigBlindSeat)
                throw new ArgumentException("The final two positions must be the small and big blind seats.");
            foreach (Player p in players)
                if (ledger.GetChips(p.Seat).Stack == 0) throw new ArgumentException("Starting seats must have chips.");
            players[sb].Paid = Math.Min(smallBlind, ledger.GetChips(smallBlindSeat).Stack);
            players[bb].Paid = Math.Min(bigBlind, ledger.GetChips(bigBlindSeat).Stack);
            ChipLedger candidate = ledger.Contribute(smallBlindSeat, players[sb].Paid)
                .Contribute(bigBlindSeat, players[bb].Paid);
            var round = new BettingRound(candidate, players, bigBlind, bigBlind, bigBlind);
            round.Advance(0);
            return round;
        }

        /// <summary>
        /// Starts a zero-payment street using the caller's nonfolded order, including all-in seats.
        /// Preserves prior contributions; the caller controls when to advance to the next round.
        /// </summary>
        public static BettingRound BeginUnopened(ChipLedger ledger, IReadOnlyList<SeatId> activeOrder,
            long minimumBet, SequentialBettingRules sequentialRules = null)
        {
            if (minimumBet <= 0) throw new ArgumentOutOfRangeException(nameof(minimumBet));
            if (sequentialRules != null && sequentialRules.MinimumBet != minimumBet)
                throw new ArgumentException("The minimum bet must agree with the sequential options.", nameof(minimumBet));
            var round = new BettingRound(ledger, CopyPlayers(ledger, activeOrder), minimumBet, 0, minimumBet, sequentialRules);
            round.Advance(0);
            return round;
        }

        public SeatId GetSeatAt(int index)
        {
            if (index < 0 || index >= players.Length) throw new ArgumentOutOfRangeException(nameof(index));
            return players[index].Seat;
        }
        public long GetStreetContribution(SeatId seat) => players[FindSeat(seat)].Paid;
        public bool IsFolded(SeatId seat) => players[FindSeat(seat)].Folded;
        public bool HasActed(SeatId seat) => players[FindSeat(seat)].HasActed;
        public bool IsAllIn(SeatId seat)
        {
            int index = FindSeat(seat);
            return !players[index].Folded && Ledger.GetChips(seat).Stack == 0;
        }

        public LegalBettingActions GetLegalActions()
        {
            if (currentIndex < 0) return new LegalBettingActions(false, false, 0, false, null, null);
            Player actor = players[currentIndex];
            long stack = Ledger.GetChips(actor.Seat).Stack;
            long needed = Math.Max(0, CallTarget(currentIndex) - actor.Paid);
            long ownMaximum = checked(actor.Paid + stack);
            long? minimum = null, maximum = null, allInTarget = null;
            if (HasSolventOpponent(currentIndex) && ownMaximum > CurrentBet &&
                (!actor.HasActed || CurrentBet - actor.LastFaced >= LastFullRaise))
            {
                // If a full raise would overflow, only a finite whole-stack short all-in can fit.
                minimum = LastFullRaise > long.MaxValue - CurrentBet
                    ? ownMaximum : Math.Min(checked(CurrentBet + LastFullRaise), ownMaximum);
                maximum = ownMaximum;
                if (sequentialRules != null)
                {
                    long cap = CurrentBet == 0 ? sequentialRules.MaximumOpeningBet
                        : CurrentBet > long.MaxValue / 2 ? long.MaxValue : CurrentBet * 2;
                    maximum = Math.Min(ownMaximum, cap);
                    if (minimum > maximum) minimum = maximum = null;
                    if (ownMaximum > cap && sequentialRules.AllInCap == SequentialAllInCap.WholeStackException)
                        allInTarget = ownMaximum;
                }
            }
            return new LegalBettingActions(true, needed == 0 && (sequentialRules == null || sequentialRules.AllowCheck),
                Math.Min(needed, stack), CurrentBet == 0, minimum, maximum, allInTarget);
        }

        /// <summary>
        /// Validates using the same options exposed to callers, then returns a new whole street/chip candidate.
        /// Invalid gameplay is InvalidOperationException; malformed/null/unknown inputs remain argument errors.
        /// The caller must authenticate and deduplicate before accepting this candidate as current state.
        /// </summary>
        public BettingRound Apply(SeatId seat, BettingAction action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            int index = FindSeat(seat);
            if (IsComplete || index != currentIndex) throw new InvalidOperationException("This seat cannot act now.");
            LegalBettingActions legal = GetLegalActions();
            if (!legal.Allows(action)) throw new InvalidOperationException("The action is not legal for this street state.");

            var next = new BettingRound(Ledger, (Player[])players.Clone(), MinimumBet, CurrentBet, LastFullRaise, sequentialRules);
            Player actor = next.players[index];
            if (action.Kind == BettingActionKind.Fold) actor.Folded = true;
            else if (action.Kind == BettingActionKind.Call)
            {
                next.Ledger = Ledger.Contribute(seat, legal.CallAmount);
                actor.Paid = checked(actor.Paid + legal.CallAmount);
            }
            else if (action.Kind == BettingActionKind.BetTo || action.Kind == BettingActionKind.RaiseTo)
            {
                next.Ledger = Ledger.Contribute(seat, checked(action.Target - actor.Paid));
                actor.Paid = action.Target;
                long increase = checked(action.Target - CurrentBet);
                if (increase >= LastFullRaise) next.LastFullRaise = increase;
                next.CurrentBet = action.Target;
            }
            actor.HasActed = true;
            actor.LastFaced = next.CurrentBet;
            next.players[index] = actor;
            next.Advance((index + 1) % players.Length);
            return next;
        }

        /// <summary>False accusation consumes this turn while preserving every prior bet and acted flag.</summary>
        internal BettingRound FoldAndTransfer(SeatId seat, SeatId recipient, long fine)
        {
            if (seat == recipient) throw new ArgumentException("A fine cannot be paid to the accuser.", nameof(recipient));
            FindSeat(recipient);
            if (fine < 0) throw new ArgumentOutOfRangeException(nameof(fine));
            if (CurrentSeat != seat || IsComplete) throw new InvalidOperationException("This seat cannot act now.");
            var candidate = new BettingRound(fine == 0 ? Ledger : Ledger.TransferStack(seat, recipient, fine),
                (Player[])players.Clone(), MinimumBet, CurrentBet, LastFullRaise, sequentialRules)
            { currentIndex = currentIndex, PostedAnte = PostedAnte, ContributionLimit = ContributionLimit };
            return candidate.Apply(seat, BettingAction.Fold());
        }

        private void Advance(int firstIndex)
        {
            int solventCount = 0, sole = -1;
            for (int i = 0; i < players.Length; i++)
                if (!players[i].Folded && Ledger.GetChips(players[i].Seat).Stack > 0) { solventCount++; sole = i; }
            if (ActiveSeatCount <= 1 || solventCount == 0) Finish();
            else if (solventCount == 1)
            {
                if (players[sole].Paid < CallTarget(sole)) currentIndex = sole;
                else Finish();
            }
            else
            {
                currentIndex = -1;
                for (int offset = 0; offset < players.Length; offset++)
                {
                    int i = (firstIndex + offset) % players.Length;
                    Player p = players[i];
                    if (!p.Folded && Ledger.GetChips(p.Seat).Stack > 0 && (!p.HasActed || p.Paid < CurrentBet))
                    { currentIndex = i; break; }
                }
                if (currentIndex < 0) Finish();
            }
            ValidateState();
        }

        private long CallTarget(int actorIndex)
        {
            if (HasSolventOpponent(actorIndex)) return CurrentBet;
            long actual = 0;
            for (int i = 0; i < players.Length; i++)
                if (i != actorIndex && !players[i].Folded) actual = Math.Max(actual, players[i].Paid);
            return actual;
        }

        private bool HasSolventOpponent(int actorIndex)
        {
            for (int i = 0; i < players.Length; i++)
                if (i != actorIndex && !players[i].Folded && Ledger.GetChips(players[i].Seat).Stack > 0) return true;
            return false;
        }

        private void Finish()
        {
            long highest = 0, second = 0;
            int owner = -1;
            for (int i = 0; i < players.Length; i++)
            {
                long paid = players[i].Paid; // Folded money still matches a wager; never omit it here.
                if (paid > highest) { second = highest; highest = paid; owner = i; }
                else if (paid > second) second = paid;
            }
            if (highest > second)
            {
                if (players[owner].Folded) throw new InvalidOperationException("A folded seat cannot own unmatched excess.");
                long refund = checked(highest - second);
                Ledger = Ledger.RefundContribution(players[owner].Seat, refund);
                players[owner].Paid = second;
                RefundedSeat = players[owner].Seat;
                RefundedAmount = refund;
            }
            CurrentBet = Math.Min(highest, second);
            currentIndex = -1;
        }

        private void ValidateState()
        {
            if (MinimumBet <= 0 || LastFullRaise < MinimumBet || CurrentBet < 0 || ActiveSeatCount == 0)
                throw new InvalidOperationException("Invalid betting state.");
            foreach (Player p in players)
                if (p.Paid < 0 || p.Paid > Ledger.GetChips(p.Seat).Committed || p.Paid > CurrentBet)
                    throw new InvalidOperationException("Street payments must agree with the chip ledger and target.");
        }

        private int FindSeat(SeatId seat)
        {
            if (!seat.IsValid) throw new ArgumentException("A valid seat is required.", nameof(seat));
            for (int i = 0; i < players.Length; i++) if (players[i].Seat == seat) return i;
            throw new KeyNotFoundException("The seat is not part of this betting round.");
        }

        private static Player[] CopyPlayers(ChipLedger ledger, IReadOnlyList<SeatId> order)
        {
            if (ledger == null) throw new ArgumentNullException(nameof(ledger));
            if (order == null) throw new ArgumentNullException(nameof(order));
            int count = order.Count;
            if (count < 1 || count > ledger.SeatCount) throw new ArgumentException("Invalid number of round seats.", nameof(order));
            var result = new Player[count];
            var seen = new HashSet<SeatId>();
            for (int i = 0; i < count; i++)
            {
                SeatId seat = order[i];
                if (!seat.IsValid || !seen.Add(seat)) throw new ArgumentException("Seats must be valid and unique.", nameof(order));
                ledger.GetChips(seat);
                result[i] = new Player { Seat = seat };
            }
            return result;
        }

    }

    public enum FixedBettingPhase { Proposal = 1, Response = 2, Complete = 3 }
    public enum FixedBettingProposal { Keep = 1, Increase = 2 }
    public enum FixedBettingResponse { Match = 1, Fold = 2 }
}
