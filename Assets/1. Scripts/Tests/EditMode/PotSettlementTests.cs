using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;

namespace Poker.Foundation.Tests
{
    public class PotSettlementTests
    {
        private static readonly SeatId A = new SeatId(42), B = new SeatId(7), C = new SeatId(999),
            D = new SeatId(3), E = new SeatId(55);
        private static readonly SeatId[] Seats = { A, B, C, D, E };

        [Test]
        public void StrongestHandWinsMatchedPotWithoutChangingSource()
        {
            ChipLedger ledger = Paid(20, 20);
            PotSettlement result = PotSettlement.ShowdownByValue(ledger, new[] { Strong(A), Medium(B) });
            Assert.That(result.PotCount, Is.EqualTo(1));
            Assert.That(result.GetAwardedTo(A), Is.EqualTo(40));
            Assert.That(result.GetAwardedTo(B), Is.Zero);
            Assert.That(result.GetPot(0).EligibleSeatCount, Is.EqualTo(2));
            AssertSettled(result, 40);
            Assert.That(ledger.TotalCommitted, Is.EqualTo(40));
        }

        [Test]
        public void SharedBoardCanGiveBothSeatsTheSameBestFive()
        {
            Card[] board = Run(Suit.Spades, 14);
            var a = new[] { new Card(Rank.Two, Suit.Hearts), new Card(Rank.Three, Suit.Hearts) };
            var b = new[] { new Card(Rank.Four, Suit.Clubs), new Card(Rank.Five, Suit.Clubs) };
            var values = new[] {
                new SeatHandValue(A, HoldemBestHand.Evaluate(board, a).Value),
                new SeatHandValue(B, HoldemBestHand.Evaluate(board, b).Value)
            };
            PotSettlement result = PotSettlement.ShowdownByValue(Paid(5, 5, 1), values, new[] { B, A });
            Assert.That(result.GetAwardedTo(A), Is.EqualTo(5));
            Assert.That(result.GetAwardedTo(B), Is.EqualTo(6));
            AssertSettled(result, 11);
        }

        [Test]
        public void EachSidePotHasOnlyItsOwnEligibleWinnersAndFoldedMoneyStays()
        {
            PotSettlement result = PotSettlement.ShowdownByValue(Paid(30, 70, 100, 100),
                new[] { Strong(A), Medium(B), Weak(C) });
            Assert.That(result.PotCount, Is.EqualTo(3));
            Assert.That(result.GetPot(0).Amount, Is.EqualTo(120));
            Assert.That(result.GetPot(1).Amount, Is.EqualTo(120));
            Assert.That(result.GetPot(2).Amount, Is.EqualTo(60));
            Assert.That(result.GetAwardedTo(A), Is.EqualTo(120));
            Assert.That(result.GetAwardedTo(B), Is.EqualTo(120));
            Assert.That(result.GetAwardedTo(C), Is.EqualTo(60));
            Assert.That(result.GetAwardedTo(D), Is.Zero);
            Assert.That(result.GetPot(2).PayoutCount, Is.EqualTo(1));
            Assert.That(result.GetPot(2).GetPayout(0).HasOddChip, Is.False);
            AssertSettled(result, 300);
        }

        [Test]
        public void FoldedIntermediateAmountsDoNotCreateArtificialPotsOrRoundTwice()
        {
            PotSettlement result = PotSettlement.ShowdownByValue(Paid(5, 5, 1, 2, 3),
                new[] { Strong(A), Strong(B, Suit.Hearts) });
            Assert.That(result.PotCount, Is.EqualTo(1));
            Assert.That(result.GetAwardedTo(A), Is.EqualTo(8));
            Assert.That(result.GetAwardedTo(B), Is.EqualTo(8));
            AssertSettled(result, 16);
        }

        [Test]
        public void FoldedPartialContributionIsIncludedInTheCorrectLayer()
        {
            PotSettlement result = PotSettlement.ShowdownByValue(Paid(5, 10, 10, 7),
                new[] { Strong(A), Medium(B), Weak(C) });
            Assert.That(result.PotCount, Is.EqualTo(2));
            Assert.That(result.GetPot(0).Amount, Is.EqualTo(20));
            Assert.That(result.GetPot(1).Amount, Is.EqualTo(12));
            Assert.That(result.GetAwardedTo(B), Is.EqualTo(12));
            AssertSettled(result, 32);
        }

        [Test]
        public void ExactTieNeedsNoOddChipPolicyAndIgnoresSuitStrength()
        {
            PotSettlement result = PotSettlement.ShowdownByValue(Paid(10, 10),
                new[] { Strong(A), Strong(B, Suit.Hearts) });
            Assert.That(result.GetAwardedTo(A), Is.EqualTo(10));
            Assert.That(result.GetAwardedTo(B), Is.EqualTo(10));
            Assert.That(result.GetPot(0).GetPayout(0).HasOddChip, Is.False);
        }

        [Test]
        public void RemainderWithoutExplicitOrderRejectsWholeSettlement()
        {
            ChipLedger ledger = Paid(5, 5, 1);
            PotSettlement result = null;
            AssertFailure(SettlementFailure.MissingOddChipOrder, () => result = PotSettlement.ShowdownByValue(ledger,
                new[] { Strong(A), Strong(B, Suit.Hearts) }));
            Assert.That(result, Is.Null);
            Assert.That(ledger.TotalCommitted, Is.EqualTo(11));
        }

        [Test]
        public void ExplicitPriorityNotNumericSeatOrHandOrderReceivesOddChip()
        {
            var hands = new[] { Strong(A), Strong(B, Suit.Hearts) };
            PotSettlement first = PotSettlement.ShowdownByValue(Paid(5, 5, 1), hands, new[] { B, A });
            PotSettlement second = PotSettlement.ShowdownByValue(Paid(5, 5, 1), hands, new[] { A, B });
            Assert.That(first.GetAwardedTo(B), Is.EqualTo(6));
            Assert.That(first.GetAwardedTo(A), Is.EqualTo(5));
            Assert.That(second.GetAwardedTo(A), Is.EqualTo(6));
            Assert.That(first.GetPot(0).GetPayout(1).HasOddChip, Is.True);
            Array.Reverse(hands);
            PotSettlement reversed = PotSettlement.ShowdownByValue(Paid(5, 5, 1), hands, new[] { B, A });
            Assert.That(reversed.GetAwardedTo(B), Is.EqualTo(6));
        }

        [Test]
        public void MultipleRemainderChipsGoOneEachToWinningSeatsOnly()
        {
            PotSettlement result = PotSettlement.ShowdownByValue(Paid(5, 5, 5, 2, 2),
                new[] { Strong(A), Strong(B, Suit.Hearts), Strong(C, Suit.Diamonds), Weak(D, Suit.Clubs) },
                new[] { D, C, A, B });
            Assert.That(result.PotCount, Is.EqualTo(2));
            Assert.That(result.GetPot(0).Amount, Is.EqualTo(10));
            Assert.That(result.GetPot(1).Amount, Is.EqualTo(9));
            Assert.That(result.GetAwardedTo(D), Is.Zero);
            Assert.That(result.GetAwardedTo(C), Is.EqualTo(7));
            Assert.That(result.GetAwardedTo(A), Is.EqualTo(6));
            Assert.That(result.GetAwardedTo(B), Is.EqualTo(6));
            AssertSettled(result, 19);
        }

        [Test]
        public void TwoOddChipsAmongThreeTiedWinnersUseDifferentRecipients()
        {
            PotSettlement result = PotSettlement.ShowdownByValue(Paid(5, 5, 5, 2),
                new[] { Strong(A), Strong(B, Suit.Hearts), Strong(C, Suit.Diamonds) }, new[] { C, A, B });
            Assert.That(result.GetAwardedTo(C), Is.EqualTo(6));
            Assert.That(result.GetAwardedTo(A), Is.EqualTo(6));
            Assert.That(result.GetAwardedTo(B), Is.EqualTo(5));
            Assert.That(result.PotCount, Is.EqualTo(1));
        }

        [Test]
        public void DifferentEligibleGroupsStaySeparateEvenWhenTheirWinnersMatch()
        {
            PotSettlement result = PotSettlement.ShowdownByValue(Paid(1, 3, 3, 2, 1),
                new[] { Weak(A), Strong(B), Strong(C, Suit.Hearts) }, new[] { B, C, A });
            Assert.That(result.PotCount, Is.EqualTo(2));
            Assert.That(result.GetPot(0).Amount, Is.EqualTo(5));
            Assert.That(result.GetPot(1).Amount, Is.EqualTo(5));
            Assert.That(result.GetPot(0).EligibleSeatCount, Is.EqualTo(3));
            Assert.That(result.GetPot(1).EligibleSeatCount, Is.EqualTo(2));
            Assert.That(result.GetAwardedTo(B), Is.EqualTo(6));
            Assert.That(result.GetAwardedTo(C), Is.EqualTo(4));
            AssertSettled(result, 10);
        }

        [Test]
        public void MissingPriorityInLaterPotCannotPublishEarlierPotPayment()
        {
            ChipLedger ledger = Paid(2, 5, 5, 3);
            PotSettlement result = null;
            AssertFailure(SettlementFailure.MissingOddChipOrder, () => result = PotSettlement.ShowdownByValue(ledger,
                new[] { Strong(A), Medium(B), new SeatHandValue(C, HandEvaluator.Evaluate(Run(Suit.Diamonds, 13))) }));
            Assert.That(result, Is.Null);
            Assert.That(ledger.TotalCommitted, Is.EqualTo(15));
            Assert.That(ledger.GetChips(A).Stack, Is.Zero);
        }

        [Test]
        public void ThrowingPriorityCannotPublishPartialPayment()
        {
            ChipLedger ledger = Paid(5, 5, 1);
            var failure = new ApplicationException("Priority read failed.");
            Assert.That(Assert.Throws<ApplicationException>(() => PotSettlement.ShowdownByValue(ledger,
                new[] { Strong(A), Strong(B, Suit.Hearts) }, new ThrowingPriority(failure))), Is.SameAs(failure));
            Assert.That(ledger.TotalCommitted, Is.EqualTo(11));
        }

        [Test]
        public void SeededTwoStreetBettingTracesSettleMatchedPotsAndConserveChips()
        {
            var random = new Random(20260912);
            for (int run = 0; run < 200; run++)
            {
                int count = random.Next(2, 5);
                var chips = new long[count];
                var order = new SeatId[count];
                for (int i = 0; i < count; i++) { chips[i] = random.Next(1, 101); order[i] = Seats[i]; }
                BettingRound round = BettingRound.BeginOpening(Starting(chips), order, order[count - 2], order[count - 1], 1, 2);
                round = FinishRandomly(round, random);
                var live = LiveSeats(round);
                if (live.Count > 1)
                    round = FinishRandomly(BettingRound.BeginUnopened(round.Ledger, live, 2), random);
                live = LiveSeats(round);
                ChipLedger source = round.Ledger;
                long outstanding = source.TotalCommitted;
                PotSettlement result;
                if (live.Count == 1) result = PotSettlement.AwardUncontested(source, live[0]);
                else
                {
                    var hands = new List<SeatHandValue>();
                    for (int i = 0; i < live.Count; i++) hands.Add(new SeatHandValue(live[i], HandEvaluator.Evaluate(Run((Suit)(i + 1), random.Next(10, 15)))));
                    live.Reverse();
                    result = PotSettlement.ShowdownByValue(source, hands, live);
                    for (int i = 0; i < result.PotCount; i++)
                    {
                        PotAward pot = result.GetPot(i);
                        for (int j = 0; j < pot.PayoutCount; j++)
                        {
                            SeatPayout payout = pot.GetPayout(j);
                            Assert.That(live, Does.Contain(payout.Seat));
                            Assert.That(source.GetChips(payout.Seat).Committed, Is.GreaterThanOrEqualTo(pot.ContributionCap));
                        }
                    }
                }
                AssertSettled(result, outstanding);
                Assert.That(source.TotalCommitted, Is.EqualTo(outstanding));
            }
        }

        [Test]
        public void UncalledExcessMustBeRefundedByBettingBeforeSettlement()
        {
            ChipLedger ledger = Paid(40, 100);
            AssertFailure(SettlementFailure.UnmatchedContribution,
                () => PotSettlement.ShowdownByValue(ledger, new[] { Strong(A), Medium(B) }));
            AssertFailure(SettlementFailure.UnmatchedContribution, () => PotSettlement.AwardUncontested(ledger, B));
            Assert.That(ledger.TotalCommitted, Is.EqualTo(140));
        }

        [Test]
        public void RealBettingRefundIsNotAppliedAgainAtShowdown()
        {
            BettingRound round = BettingRound.BeginUnopened(Starting(100, 40), new[] { A, B }, 2);
            round = round.Apply(A, BettingAction.BetTo(100)).Apply(B, BettingAction.Call());
            Assert.That(round.RefundedAmount, Is.EqualTo(60));
            PotSettlement result = PotSettlement.ShowdownByValue(round.Ledger, new[] { Medium(A), Strong(B) });
            Assert.That(result.Ledger.GetChips(A).Stack, Is.EqualTo(60));
            Assert.That(result.Ledger.GetChips(B).Stack, Is.EqualTo(80));
            AssertSettled(result, 80);
        }

        [Test]
        public void FoldsToBigBlindAwardWithoutRequiringAnyCards()
        {
            BettingRound round = BettingRound.BeginOpening(Starting(100, 100, 100), new[] { A, B, C }, B, C, 1, 2);
            round = round.Apply(A, BettingAction.Fold()).Apply(B, BettingAction.Fold());
            PotSettlement result = PotSettlement.AwardUncontested(round.Ledger, C);
            Assert.That(result.Ledger.GetChips(A).Stack, Is.EqualTo(100));
            Assert.That(result.Ledger.GetChips(B).Stack, Is.EqualTo(99));
            Assert.That(result.Ledger.GetChips(C).Stack, Is.EqualTo(101));
            AssertSettled(result, 2);
        }

        [Test]
        public void UncontestedWinnerCollectsAllMatchedDeadMoneyAsOneAward()
        {
            PotSettlement result = PotSettlement.AwardUncontested(Paid(30, 70, 100, 100), C);
            Assert.That(result.PotCount, Is.EqualTo(1));
            Assert.That(result.GetAwardedTo(C), Is.EqualTo(300));
            AssertSettled(result, 300);
        }

        [Test]
        public void LayerWithoutEligibleLiveHandRejectsWithoutPartialPayout()
        {
            ChipLedger ledger = Paid(30, 30, 100, 100);
            AssertFailure(SettlementFailure.NoEligibleWinner,
                () => PotSettlement.ShowdownByValue(ledger, new[] { Strong(A), Medium(B) }));
            AssertFailure(SettlementFailure.NoEligibleWinner, () => PotSettlement.AwardUncontested(ledger, A));
            Assert.That(ledger.TotalCommitted, Is.EqualTo(260));
        }

        [Test]
        public void SettledLedgerCannotPayTheSameOutstandingPotAgain()
        {
            var hands = new[] { Strong(A), Medium(B) };
            PotSettlement result = PotSettlement.ShowdownByValue(Paid(20, 20), hands);
            AssertFailure(SettlementFailure.NothingToAward, () => PotSettlement.ShowdownByValue(result.Ledger, hands));
            AssertFailure(SettlementFailure.NothingToAward, () => PotSettlement.AwardUncontested(result.Ledger, A));
            Assert.That(result.TotalAwarded, Is.EqualTo(40));
        }

        [Test]
        public void OldSourceCanStillProduceIndependentCandidatesNotCommandDeduplication()
        {
            ChipLedger source = Paid(5, 5, 1);
            var hands = new[] { Strong(A), Strong(B, Suit.Hearts) };
            PotSettlement x = PotSettlement.ShowdownByValue(source, hands, new[] { A, B });
            PotSettlement y = PotSettlement.ShowdownByValue(source, hands, new[] { B, A });
            Assert.That(x.GetAwardedTo(A), Is.EqualTo(6));
            Assert.That(y.GetAwardedTo(A), Is.EqualTo(5));
            Assert.That(source.TotalCommitted, Is.EqualTo(11));
        }

        [Test]
        public void TotalAtInt64BoundaryDoesNotOverflow()
        {
            long half = long.MaxValue / 2;
            PotSettlement result = PotSettlement.ShowdownByValue(Paid(half, half, 1),
                new[] { Strong(A), Strong(B, Suit.Hearts) }, new[] { B, A });
            Assert.That(result.GetAwardedTo(B), Is.EqualTo(half + 1));
            Assert.That(result.GetAwardedTo(A), Is.EqualTo(half));
            AssertSettled(result, long.MaxValue);
        }

        [Test]
        public void UncommittedStacksAndUninvolvedZeroSeatArePreserved()
        {
            ChipLedger ledger = Starting(100, 80, 0).Contribute(A, 10).Contribute(B, 10);
            PotSettlement result = PotSettlement.ShowdownByValue(ledger, new[] { Strong(A), Medium(B) });
            Assert.That(result.Ledger.GetChips(A).Stack, Is.EqualTo(110));
            Assert.That(result.Ledger.GetChips(B).Stack, Is.EqualTo(70));
            Assert.That(result.Ledger.GetChips(C).Stack, Is.Zero);
        }

        [Test]
        public void InputListsCanBeChangedAfterResultWithoutChangingIt()
        {
            var hands = new List<SeatHandValue> { Strong(A), Strong(B, Suit.Hearts) };
            var priority = new List<SeatId> { B, A };
            PotSettlement result = PotSettlement.ShowdownByValue(Paid(5, 5, 1), hands, priority);
            hands.Clear(); priority.Clear();
            Assert.That(result.GetAwardedTo(B), Is.EqualTo(6));
            Assert.That(result.GetPot(0).EligibleSeatCount, Is.EqualTo(2));
        }

        [Test]
        public void NullEmptyDuplicateUnknownOrNonfundedLiveHandsAreRejected()
        {
            ChipLedger ledger = Paid(5, 5, 0);
            Assert.Throws<ArgumentNullException>(() => PotSettlement.ShowdownByValue(null, new[] { Strong(A), Medium(B) }));
            Assert.Throws<ArgumentNullException>(() => PotSettlement.ShowdownByValue(ledger, null));
            Assert.Throws<ArgumentException>(() => PotSettlement.ShowdownByValue(ledger, Array.Empty<SeatHandValue>()));
            Assert.Throws<ArgumentException>(() => PotSettlement.ShowdownByValue(ledger, new[] { Strong(A) }));
            Assert.Throws<ArgumentException>(() => PotSettlement.ShowdownByValue(ledger, new[] { Strong(A), Strong(A) }));
            Assert.Throws<ArgumentException>(() => PotSettlement.ShowdownByValue(ledger, new[] { Strong(A), null }));
            Assert.Throws<KeyNotFoundException>(() => PotSettlement.ShowdownByValue(ledger, new[] { Strong(A), Medium(D) }));
            Assert.Throws<ArgumentException>(() => PotSettlement.ShowdownByValue(ledger, new[] { Strong(A), Medium(C) }));
        }

        [Test]
        public void PriorityMustContainEveryLiveSeatExactlyOnceAndNoFoldedSeat()
        {
            ChipLedger ledger = Paid(5, 5, 1);
            var hands = new[] { Strong(A), Strong(B, Suit.Hearts) };
            foreach (SeatId[] invalid in new[] { new SeatId[0], new[] { A }, new[] { A, A }, new[] { A, C },
                new[] { A, default(SeatId) }, new[] { A, B, C } })
                Assert.Throws<ArgumentException>(() => PotSettlement.ShowdownByValue(ledger, hands, invalid));
            Assert.That(ledger.TotalCommitted, Is.EqualTo(11));
        }

        [Test]
        public void UncontestedRejectsNullDefaultAndUnknownWinner()
        {
            Assert.Throws<ArgumentNullException>(() => PotSettlement.AwardUncontested(null, A));
            Assert.Throws<ArgumentException>(() => PotSettlement.AwardUncontested(Paid(5, 5), default));
            Assert.Throws<KeyNotFoundException>(() => PotSettlement.AwardUncontested(Paid(5, 5), D));
        }

        [Test]
        public void ReadErrorsCannotPublishPartialAward()
        {
            ChipLedger ledger = Paid(5, 5);
            var failure = new ApplicationException("Input read failed.");
            PotSettlement result = null;
            Assert.That(Assert.Throws<ApplicationException>(() => result = PotSettlement.ShowdownByValue(ledger,
                new ThrowingHands(failure))), Is.SameAs(failure));
            Assert.That(result, Is.Null);
            Assert.That(ledger.TotalCommitted, Is.EqualTo(10));
        }

        [Test]
        public void ResultQueriesGuardIndicesAndSeatIds()
        {
            PotSettlement result = PotSettlement.ShowdownByValue(Paid(5, 5), new[] { Strong(A), Medium(B) });
            Assert.Throws<ArgumentOutOfRangeException>(() => result.GetPot(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => result.GetPot(1));
            Assert.Throws<ArgumentOutOfRangeException>(() => result.GetPot(0).GetEligibleSeat(2));
            Assert.Throws<ArgumentOutOfRangeException>(() => result.GetPot(0).GetPayout(1));
            Assert.Throws<ArgumentException>(() => result.GetAwardedTo(default));
            Assert.Throws<KeyNotFoundException>(() => result.GetAwardedTo(E));
        }

        [Test]
        public void RankedSeatRejectsInvalidOwnerAndUnevaluatedValue()
        {
            HandValue value = HandEvaluator.Evaluate(Run(Suit.Spades, 14));
            Assert.Throws<ArgumentException>(() => new SeatHandValue(default, value));
            Assert.Throws<ArgumentException>(() => new SeatHandValue(A, default));
            var ranked = new SeatHandValue(A, value);
            Assert.That(ranked.Owner, Is.EqualTo(A));
            Assert.That(ranked.Value, Is.EqualTo(value));
        }

        private static SeatHandValue Strong(SeatId seat, Suit suit = Suit.Spades) => new SeatHandValue(seat, HandEvaluator.Evaluate(Run(suit, 14)));
        private static SeatHandValue Medium(SeatId seat) => new SeatHandValue(seat, HandEvaluator.Evaluate(Run(Suit.Hearts, 13)));
        private static SeatHandValue Weak(SeatId seat, Suit suit = Suit.Diamonds) => new SeatHandValue(seat, HandEvaluator.Evaluate(Run(suit, 12)));
        private static Card[] Run(Suit suit, int high)
        {
            var cards = new Card[5];
            for (int i = 0; i < 5; i++) cards[i] = new Card((Rank)(high - 4 + i), suit);
            return cards;
        }
        private static ChipLedger Starting(params long[] chips)
        {
            var balances = new SeatChips[chips.Length];
            for (int i = 0; i < chips.Length; i++) balances[i] = new SeatChips(Seats[i], chips[i]);
            return ChipLedger.Create(balances);
        }
        private static ChipLedger Paid(params long[] chips)
        {
            ChipLedger ledger = Starting(chips);
            for (int i = 0; i < chips.Length; i++) if (chips[i] > 0) ledger = ledger.Contribute(Seats[i], chips[i]);
            return ledger;
        }
        private static void AssertFailure(SettlementFailure reason, TestDelegate action) =>
            Assert.That(Assert.Throws<SettlementException>(action).Reason, Is.EqualTo(reason));
        private static List<SeatId> LiveSeats(BettingRound round)
        {
            var seats = new List<SeatId>();
            for (int i = 0; i < round.SeatCount; i++) if (!round.IsFolded(round.GetSeatAt(i))) seats.Add(round.GetSeatAt(i));
            return seats;
        }
        private static BettingRound FinishRandomly(BettingRound round, Random random)
        {
            int count = 0;
            while (!round.IsComplete)
            {
                Assert.That(++count, Is.LessThan(2000));
                LegalBettingActions legal = round.GetLegalActions();
                var actions = new List<BettingAction> { BettingAction.Fold() };
                if (legal.CanCheck) actions.Add(BettingAction.Check());
                if (legal.CanCall) actions.Add(BettingAction.Call());
                if (legal.CanBet)
                {
                    actions.Add(BettingAction.BetTo(legal.MinimumAggressiveTarget.Value));
                    actions.Add(BettingAction.BetTo(legal.MaximumAggressiveTarget.Value));
                }
                if (legal.CanRaise)
                {
                    actions.Add(BettingAction.RaiseTo(legal.MinimumAggressiveTarget.Value));
                    actions.Add(BettingAction.RaiseTo(legal.MaximumAggressiveTarget.Value));
                }
                round = round.Apply(round.CurrentSeat.Value, actions[random.Next(actions.Count)]);
            }
            return round;
        }
        private static void AssertSettled(PotSettlement result, long amount)
        {
            Assert.That(result.TotalAwarded, Is.EqualTo(amount));
            Assert.That(result.Ledger.TotalCommitted, Is.Zero);
            decimal stacks = 0, paid = 0, pots = 0;
            for (int i = 0; i < result.Ledger.SeatCount; i++)
            {
                SeatId seat = result.Ledger.GetSeatAt(i);
                stacks += result.Ledger.GetChips(seat).Stack;
                paid += result.GetAwardedTo(seat);
            }
            for (int i = 0; i < result.PotCount; i++) pots += result.GetPot(i).Amount;
            Assert.That(stacks, Is.EqualTo((decimal)result.Ledger.TotalChips));
            Assert.That(paid, Is.EqualTo((decimal)amount));
            Assert.That(pots, Is.EqualTo((decimal)amount));
        }
        private sealed class ThrowingHands : IReadOnlyList<SeatHandValue>
        {
            private readonly Exception failure;
            public ThrowingHands(Exception failure) { this.failure = failure; }
            public int Count => 2;
            public SeatHandValue this[int index] => index == 0 ? Strong(A) : throw failure;
            public IEnumerator<SeatHandValue> GetEnumerator() { throw new NotSupportedException(); }
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
        private sealed class ThrowingPriority : IReadOnlyList<SeatId>
        {
            private readonly Exception failure;
            public ThrowingPriority(Exception failure) { this.failure = failure; }
            public int Count => 2;
            public SeatId this[int index] => index == 0 ? A : throw failure;
            public IEnumerator<SeatId> GetEnumerator() { throw new NotSupportedException(); }
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
