using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;

namespace Poker.Foundation.Tests
{
    public class BettingRoundTests
    {
        private static readonly SeatId A = new SeatId(42);
        private static readonly SeatId B = new SeatId(7);
        private static readonly SeatId C = new SeatId(999);
        private static readonly SeatId D = new SeatId(55);

        [Test]
        public void SharedAnteHasNoBlindOrPreflopActorAndDoesNotCountAsStreetPayment()
        {
            ChipLedger original = Ledger(100, 100, 100, 100);
            BettingRound opening = BettingRound.BeginSharedAnte(original, new[] { A, B, C, D }, 5, 5);
            Assert.That(opening.IsComplete, Is.True);
            Assert.That(opening.CurrentSeat, Is.Null);
            Assert.That(opening.PostedAnte, Is.EqualTo(5));
            Assert.That(opening.ContributionLimit, Is.EqualTo(100));
            Assert.That(opening.Ledger.TotalCommitted, Is.EqualTo(20));
            Assert.That(opening.CurrentBet, Is.Zero);
            foreach (SeatId seat in new[] { A, B, C, D })
            {
                Assert.That(opening.Ledger.GetChips(seat).Committed, Is.EqualTo(5));
                Assert.That(opening.GetStreetContribution(seat), Is.Zero);
            }
            Assert.That(original.TotalCommitted, Is.Zero);
        }

        [Test]
        public void SharedAnteCapsEverySeatEquallyAtLowestStartingStack()
        {
            BettingRound opening = BettingRound.BeginSharedAnte(Ledger(100, 50, 3), new[] { A, B, C }, 5, 5);
            Assert.That(opening.PostedAnte, Is.EqualTo(3));
            Assert.That(opening.ContributionLimit, Is.EqualTo(3));
            Assert.That(opening.Ledger.TotalCommitted, Is.EqualTo(9));
            Assert.That(opening.IsAllIn(C), Is.True);
            Assert.That(opening.ActiveSeatCount, Is.EqualTo(3));
            BettingRound skipped = BettingRound.BeginFixed(opening.Ledger, new[] { C, A, B }, 5, 3);
            Assert.That(skipped.IsComplete, Is.True);
            Assert.That(skipped.FixedIncrement, Is.Zero);
            Assert.That(skipped.Ledger.TotalCommitted, Is.EqualTo(9));
        }

        [Test]
        public void SharedAnteRejectsExistingContributionsAndUnfundedOrMissingSeats()
        {
            ChipLedger original = Ledger(100, 100);
            Assert.Throws<ArgumentException>(() => BettingRound.BeginSharedAnte(original.Contribute(A, 1), new[] { A, B }, 5, 5));
            Assert.Throws<ArgumentException>(() => BettingRound.BeginSharedAnte(Ledger(100, 0), new[] { A, B }, 5, 5));
            Assert.Throws<ArgumentException>(() => BettingRound.BeginSharedAnte(original, new[] { A }, 5, 5));
            Assert.Throws<ArgumentOutOfRangeException>(() => BettingRound.BeginSharedAnte(original, new[] { A, B }, 0, 5));
            Assert.Throws<ArgumentOutOfRangeException>(() => BettingRound.BeginSharedAnte(original, new[] { A, B }, 5, 0));
            Assert.That(original.TotalCommitted, Is.Zero);
        }

        [Test]
        public void FixedProposalsStayPrivateAndUnpaidUntilHostClosesTheWindow()
        {
            BettingRound original = FixedRound();
            BettingRound selected = original.SubmitFixedProposal(C, FixedBettingProposal.Increase);
            Assert.That(original.GetOwnFixedProposal(C), Is.Null);
            Assert.That(selected.GetOwnFixedProposal(C), Is.EqualTo(FixedBettingProposal.Increase));
            Assert.That(selected.CurrentSeat, Is.Null);
            Assert.That(selected.IsComplete, Is.False);
            Assert.That(selected.Ledger.TotalCommitted, Is.EqualTo(20));
            Assert.Throws<InvalidOperationException>(() => selected.IsSealedRaiser(C));
            Assert.That(selected.GetLegalActions().CanCheck, Is.False);
            Assert.Throws<InvalidOperationException>(() => selected.Apply(A, BettingAction.BetTo(5)));
            BettingRound sealedRound = selected.SealFixedProposals();
            Assert.That(sealedRound.FixedPhase, Is.EqualTo(FixedBettingPhase.Response));
            Assert.That(sealedRound.IsSealedRaiser(C), Is.True);
            Assert.Throws<InvalidOperationException>(() => sealedRound.SubmitFixedProposal(A, FixedBettingProposal.Increase));
            Assert.Throws<InvalidOperationException>(() => sealedRound.SubmitFixedResponse(C, FixedBettingResponse.Fold));
        }

        [Test]
        public void FixedNoIncreaseOrMissingProposalCompletesWithoutChargingOrFolding()
        {
            BettingRound completed = FixedRound().SubmitFixedProposal(A, FixedBettingProposal.Keep).SealFixedProposals();
            Assert.That(completed.IsComplete, Is.True);
            Assert.That(completed.ActiveSeatCount, Is.EqualTo(4));
            Assert.That(completed.Ledger.TotalCommitted, Is.EqualTo(20));
            Assert.That(completed.CurrentBet, Is.Zero);
            Assert.Throws<InvalidOperationException>(() => completed.SealFixedResponses());
        }

        [Test]
        public void FixedMultipleRaisersApplyOneIncrementAndRequireNoFurtherConsent()
        {
            BettingRound round = FixedRound();
            foreach (SeatId seat in new[] { D, A, C, B }) round = round.SubmitFixedProposal(seat, FixedBettingProposal.Increase);
            round = round.SealFixedProposals().SealFixedResponses();
            Assert.That(round.IsComplete, Is.True);
            Assert.That(round.Ledger.TotalCommitted, Is.EqualTo(40));
            foreach (SeatId seat in new[] { A, B, C, D })
            {
                Assert.That(round.Ledger.GetChips(seat).Committed, Is.EqualTo(10));
                Assert.That(round.GetStreetContribution(seat), Is.EqualTo(5));
            }
            Assert.That(round.RefundedAmount, Is.Zero);
        }

        [Test]
        public void FixedMatchedResponsesKeepDeadMoneyAndDefaultMissingInputToFold()
        {
            BettingRound awaiting = FixedRound().SubmitFixedProposal(A, FixedBettingProposal.Increase).SealFixedProposals();
            BettingRound completed = awaiting.SubmitFixedResponse(B, FixedBettingResponse.Match)
                .SubmitFixedResponse(C, FixedBettingResponse.Fold).SealFixedResponses();
            Assert.That(completed.IsFolded(C), Is.True);
            Assert.That(completed.IsFolded(D), Is.True);
            Assert.That(completed.ActiveSeatCount, Is.EqualTo(2));
            Assert.That(completed.Ledger.TotalCommitted, Is.EqualTo(30));
            Assert.That(completed.Ledger.GetChips(C).Committed, Is.EqualTo(5));
            Assert.That(completed.Ledger.GetChips(D).Committed, Is.EqualTo(5));
            Assert.That(awaiting.IsFolded(C), Is.False);
            Assert.That(awaiting.Ledger.TotalCommitted, Is.EqualTo(20));
            Assert.Throws<InvalidOperationException>(() => completed.SealFixedResponses());
        }

        [Test]
        public void FixedUnmatchedIncreaseIsNotChargedAndAnteIsNeverRefunded()
        {
            BettingRound completed = FixedRound().SubmitFixedProposal(B, FixedBettingProposal.Increase)
                .SealFixedProposals().SealFixedResponses();
            Assert.That(completed.ActiveSeatCount, Is.EqualTo(1));
            Assert.That(completed.IsFolded(B), Is.False);
            Assert.That(completed.GetStreetContribution(B), Is.Zero);
            Assert.That(completed.Ledger.TotalCommitted, Is.EqualTo(20));
            Assert.That(completed.Ledger.GetChips(B).Stack, Is.EqualTo(95));
            Assert.That(completed.RefundedAmount, Is.Zero);
        }

        [Test]
        public void FixedLiveAllInIsNotFoldedWhenTheSharedLimitIsReached()
        {
            BettingRound opening = BettingRound.BeginSharedAnte(Ledger(100, 50, 8), new[] { A, B, C }, 5, 5);
            BettingRound round = BettingRound.BeginFixed(opening.Ledger, new[] { A, B, C }, 5, opening.ContributionLimit);
            Assert.That(round.FixedIncrement, Is.EqualTo(3));
            foreach (SeatId seat in new[] { C, B, A }) round = round.SubmitFixedProposal(seat, FixedBettingProposal.Increase);
            round = round.SealFixedProposals().SealFixedResponses();
            Assert.That(round.Ledger.TotalCommitted, Is.EqualTo(24));
            Assert.That(round.IsAllIn(C), Is.True);
            Assert.That(round.ActiveSeatCount, Is.EqualTo(3));
            BettingRound next = BettingRound.BeginFixed(round.Ledger, new[] { A, B, C }, 5, opening.ContributionLimit);
            Assert.That(next.IsComplete, Is.True);
            Assert.That(next.ActiveSeatCount, Is.EqualTo(3));
            Assert.That(next.Ledger.TotalChips, Is.EqualTo(158));
        }

        [Test]
        public void FixedWindowOwnsItsOrderAndAllowsPrivateEditsOnlyBeforeSealing()
        {
            var order = new List<SeatId> { A, B, C, D };
            BettingRound round = BettingRound.BeginFixed(FixedRound().Ledger, order, 5, 100);
            order.Clear();
            round = round.SubmitFixedProposal(A, FixedBettingProposal.Increase)
                .SubmitFixedProposal(A, FixedBettingProposal.Keep)
                .SubmitFixedProposal(B, FixedBettingProposal.Increase).SealFixedProposals();
            round = round.SubmitFixedResponse(A, FixedBettingResponse.Fold)
                .SubmitFixedResponse(A, FixedBettingResponse.Match).SealFixedResponses();
            Assert.That(round.GetSeatAt(0), Is.EqualTo(A));
            Assert.That(round.IsFolded(A), Is.False);
            Assert.That(round.Ledger.TotalCommitted, Is.EqualTo(30));
        }

        [Test]
        public void FixedInputOrderDoesNotChangeSealedPaymentsOrFolds()
        {
            BettingRound first = FixedRound().SubmitFixedProposal(A, FixedBettingProposal.Increase)
                .SubmitFixedProposal(C, FixedBettingProposal.Increase).SealFixedProposals()
                .SubmitFixedResponse(B, FixedBettingResponse.Match).SubmitFixedResponse(D, FixedBettingResponse.Fold)
                .SealFixedResponses();
            BettingRound second = FixedRound().SubmitFixedProposal(C, FixedBettingProposal.Increase)
                .SubmitFixedProposal(A, FixedBettingProposal.Increase).SealFixedProposals()
                .SubmitFixedResponse(D, FixedBettingResponse.Fold).SubmitFixedResponse(B, FixedBettingResponse.Match)
                .SealFixedResponses();
            foreach (SeatId seat in new[] { A, B, C, D })
            {
                Assert.That(first.Ledger.GetChips(seat).Stack, Is.EqualTo(second.Ledger.GetChips(seat).Stack));
                Assert.That(first.IsFolded(seat), Is.EqualTo(second.IsFolded(seat)));
            }
        }

        [Test]
        public void FixedRulesRejectInvalidEnumsSeatsUnequalPaymentsAndTooSmallLimits()
        {
            BettingRound round = FixedRound();
            Assert.Throws<ArgumentOutOfRangeException>(() => round.SubmitFixedProposal(A, (FixedBettingProposal)0));
            Assert.Throws<KeyNotFoundException>(() => round.SubmitFixedProposal(new SeatId(88), FixedBettingProposal.Increase));
            Assert.Throws<ArgumentException>(() => BettingRound.BeginFixed(round.Ledger, new[] { A, A }, 5, 100));
            Assert.Throws<ArgumentException>(() => BettingRound.BeginFixed(round.Ledger.Contribute(A, 1), new[] { A, B }, 5, 100));
            Assert.Throws<ArgumentException>(() => BettingRound.BeginFixed(round.Ledger, new[] { A, B }, 5, 4));
            Assert.Throws<ArgumentOutOfRangeException>(() => BettingRound.BeginFixed(round.Ledger, new[] { A, B }, 0, 100));
            Assert.Throws<InvalidOperationException>(() => round.SubmitFixedResponse(A, FixedBettingResponse.Match));
            BettingRound responses = round.SubmitFixedProposal(A, FixedBettingProposal.Increase).SealFixedProposals();
            Assert.Throws<ArgumentOutOfRangeException>(() => responses.SubmitFixedResponse(B, (FixedBettingResponse)99));
            Assert.That(round.Ledger.TotalCommitted, Is.EqualTo(20));
        }

        private static BettingRound FixedRound()
        {
            BettingRound opening = BettingRound.BeginSharedAnte(Ledger(100, 100, 100, 100), new[] { A, B, C, D }, 5, 5);
            return BettingRound.BeginFixed(opening.Ledger, new[] { A, B, C, D }, 5, opening.ContributionLimit);
        }

        [Test]
        public void ActionFactoriesSeparateZeroCostActionsFromPositiveStreetTargets()
        {
            Assert.That(BettingAction.Fold().Kind, Is.EqualTo(BettingActionKind.Fold));
            Assert.That(BettingAction.Check().Target, Is.Zero);
            Assert.That(BettingAction.Call().Target, Is.Zero);
            Assert.That(BettingAction.BetTo(10).Target, Is.EqualTo(10));
            Assert.That(BettingAction.RaiseTo(long.MaxValue).Kind, Is.EqualTo(BettingActionKind.RaiseTo));
        }

        [TestCase(0L)]
        [TestCase(-1L)]
        [TestCase(long.MinValue)]
        public void NonpositiveSizedActionsAreRejected(long target)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => BettingAction.BetTo(target));
            Assert.Throws<ArgumentOutOfRangeException>(() => BettingAction.RaiseTo(target));
        }

        [Test]
        public void OpeningPostsBlindsOnACopyAndBigBlindKeepsItsOption()
        {
            ChipLedger original = Ledger(100, 100, 100);
            BettingRound round = BettingRound.BeginOpening(original, new[] { A, B, C }, B, C, 1, 2);
            Assert.That(original.TotalCommitted, Is.Zero);
            Assert.That(round.CurrentSeat, Is.EqualTo(A));
            Assert.That(round.CurrentBet, Is.EqualTo(2));
            Assert.That(round.GetStreetContribution(B), Is.EqualTo(1));
            Assert.That(round.GetStreetContribution(C), Is.EqualTo(2));
            Assert.That(round.GetLegalActions().CallAmount, Is.EqualTo(2));
            Assert.That(round.GetLegalActions().MinimumAggressiveTarget, Is.EqualTo(4));
            round = Act(round, BettingAction.Call());
            Assert.That(round.GetLegalActions().CallAmount, Is.EqualTo(1));
            round = Act(round, BettingAction.Call());
            Assert.That(round.IsComplete, Is.False);
            Assert.That(round.CurrentSeat, Is.EqualTo(C));
            Assert.That(round.GetLegalActions().CanCheck, Is.True);
            Assert.That(round.GetLegalActions().CanRaise, Is.True);
            round = Act(round, BettingAction.Check());
            AssertComplete(round, 6);
        }

        [Test]
        public void HeadsUpOpeningUsesSmallBlindThenBigBlind()
        {
            BettingRound round = BettingRound.BeginOpening(Ledger(100, 100), new[] { A, B }, A, B, 1, 2);
            Assert.That(round.CurrentSeat, Is.EqualTo(A));
            round = Act(round, BettingAction.Call());
            Assert.That(round.CurrentSeat, Is.EqualTo(B));
            round = Act(round, BettingAction.Check());
            AssertComplete(round, 4);
        }

        [Test]
        public void UnopenedRoundChecksAroundWithoutMovingChips()
        {
            BettingRound round = Unopened(100, 100, 100);
            foreach (SeatId seat in new[] { A, B, C })
            {
                Assert.That(round.CurrentSeat, Is.EqualTo(seat));
                Assert.That(round.GetLegalActions().CanBet, Is.True);
                Assert.That(round.GetLegalActions().CanRaise, Is.False);
                round = Act(round, BettingAction.Check());
            }
            AssertComplete(round, 0);
        }

        [Test]
        public void FullRaiseUsesIncrementAndActionsUseStreetTargets()
        {
            BettingRound round = Act(Unopened(100, 100, 100), BettingAction.BetTo(10));
            round = Act(round, BettingAction.RaiseTo(25));
            Assert.That(round.LastFullRaise, Is.EqualTo(15));
            Assert.That(round.GetLegalActions().MinimumAggressiveTarget, Is.EqualTo(40));
            round = Act(round, BettingAction.Call());
            Assert.That(round.CurrentSeat, Is.EqualTo(A));
            Assert.That(round.GetLegalActions().CallAmount, Is.EqualTo(15));
            round = Act(round, BettingAction.RaiseTo(40));
            Assert.That(round.Ledger.GetChips(A).Committed, Is.EqualTo(40));
            Assert.That(round.Ledger.GetChips(A).Stack, Is.EqualTo(60));
            round = Act(round, BettingAction.Call());
            round = Act(round, BettingAction.Call());
            AssertComplete(round, 120);
        }

        [Test]
        public void ShortAllInDoesNotReopenForPriorBettor()
        {
            BettingRound round = Act(Unopened(100, 15, 100), BettingAction.BetTo(10));
            LegalBettingActions shortOptions = round.GetLegalActions();
            Assert.That(shortOptions.MinimumAggressiveTarget, Is.EqualTo(15));
            Assert.That(shortOptions.MaximumAggressiveTarget, Is.EqualTo(15));
            round = Act(round, BettingAction.RaiseTo(15));
            Assert.That(round.LastFullRaise, Is.EqualTo(10));
            Assert.That(round.GetLegalActions().MinimumAggressiveTarget, Is.EqualTo(25));
            round = Act(round, BettingAction.Call());
            Assert.That(round.CurrentSeat, Is.EqualTo(A));
            Assert.That(round.GetLegalActions().CanRaise, Is.False);
            Assert.That(round.GetLegalActions().CallAmount, Is.EqualTo(5));
            AssertRejected(round, A, BettingAction.RaiseTo(25));
            round = Act(round, BettingAction.Call());
            AssertComplete(round, 45);
        }

        [Test]
        public void CumulativeShortAllInsReopenPerSeatNotGlobally()
        {
            BettingRound round = Act(Unopened(100, 15, 100, 20), BettingAction.BetTo(10));
            round = Act(round, BettingAction.RaiseTo(15));
            round = Act(round, BettingAction.Call());
            round = Act(round, BettingAction.RaiseTo(20));
            Assert.That(round.CurrentSeat, Is.EqualTo(A));
            Assert.That(round.LastFullRaise, Is.EqualTo(10));
            Assert.That(round.GetLegalActions().MinimumAggressiveTarget, Is.EqualTo(30));
            round = Act(round, BettingAction.Call());
            Assert.That(round.CurrentSeat, Is.EqualTo(C));
            Assert.That(round.GetLegalActions().CanRaise, Is.False);
            Assert.That(round.GetLegalActions().CallAmount, Is.EqualTo(5));
            round = Act(round, BettingAction.Call());
            AssertComplete(round, 75);
        }

        [Test]
        public void FoldKeepsMatchedDeadMoneyAndSkipsFoldedSeat()
        {
            BettingRound round = Act(Unopened(100, 100, 100), BettingAction.BetTo(20));
            round = Act(round, BettingAction.Call());
            round = Act(round, BettingAction.RaiseTo(50));
            round = Act(round, BettingAction.Call());
            round = Act(round, BettingAction.Fold());
            AssertComplete(round, 120);
            Assert.That(round.IsFolded(B), Is.True);
            Assert.That(round.Ledger.GetChips(B).Committed, Is.EqualTo(20));
            Assert.That(round.ActiveSeatCount, Is.EqualTo(2));
            Assert.That(round.RefundedAmount, Is.Zero);
        }

        [Test]
        public void FoldsToBigBlindRefundOnlyUnmatchedExcessWithoutAwardingPot()
        {
            BettingRound round = BettingRound.BeginOpening(Ledger(100, 100, 100), new[] { A, B, C }, B, C, 1, 2);
            round = Act(round, BettingAction.Fold());
            round = Act(round, BettingAction.Fold());
            AssertComplete(round, 2);
            Assert.That(round.ActiveSeatCount, Is.EqualTo(1));
            Assert.That(round.RefundedSeat, Is.EqualTo(C));
            Assert.That(round.RefundedAmount, Is.EqualTo(1));
            Assert.That(round.Ledger.GetChips(C).Stack, Is.EqualTo(99));
            Assert.That(round.GetStreetContribution(C), Is.EqualTo(1));
        }

        [Test]
        public void ShortCallRefundsOverbetAndRestoresStackBeforeNextPhase()
        {
            BettingRound pending = Act(Unopened(100, 40), BettingAction.BetTo(100));
            Assert.That(pending.IsAllIn(A), Is.True);
            Assert.That(pending.GetLegalActions().CallAmount, Is.EqualTo(40));
            BettingRound done = Act(pending, BettingAction.Call());
            AssertComplete(done, 80);
            Assert.That(done.RefundedSeat, Is.EqualTo(A));
            Assert.That(done.RefundedAmount, Is.EqualTo(60));
            Assert.That(done.IsAllIn(A), Is.False);
            Assert.That(done.IsAllIn(B), Is.True);
            Assert.That(done.CurrentBet, Is.EqualTo(40));
            Assert.That(done.Ledger.GetChips(A).Stack, Is.EqualTo(60));
            Assert.That(pending.Ledger.GetChips(A).Stack, Is.Zero);
        }

        [Test]
        public void NoExtraBettingIsAllowedAgainstOnlyAllInOpponents()
        {
            BettingRound round = Act(Unopened(40, 100), BettingAction.BetTo(40));
            Assert.That(round.GetLegalActions().CanRaise, Is.False);
            AssertRejected(round, B, BettingAction.RaiseTo(100));
            round = Act(round, BettingAction.Call());
            AssertComplete(round, 80);
        }

        [Test]
        public void PriorHandContributionsRemainSeparateFromNewStreet()
        {
            ChipLedger oldLedger = Ledger(100, 100, 100).Contribute(A, 20).Contribute(B, 20).Contribute(C, 10);
            BettingRound round = BettingRound.BeginUnopened(oldLedger, new[] { B, A }, 2);
            Assert.That(round.GetStreetContribution(A), Is.Zero);
            Assert.That(round.CurrentSeat, Is.EqualTo(B));
            round = Act(round, BettingAction.BetTo(10));
            round = Act(round, BettingAction.Call());
            AssertComplete(round, 70);
            Assert.That(round.Ledger.GetChips(A).Committed, Is.EqualTo(30));
            Assert.That(round.Ledger.GetChips(C).Committed, Is.EqualTo(10));
            Assert.That(oldLedger.TotalCommitted, Is.EqualTo(50));
        }

        [Test]
        public void OneRemainingSeatOrAllAllInSeatsProduceCompletedUnopenedPrimitive()
        {
            ChipLedger ledger = Ledger(10, 10).Contribute(A, 10).Contribute(B, 10);
            AssertComplete(BettingRound.BeginUnopened(ledger, new[] { B, A }, 2), 20);
            AssertComplete(BettingRound.BeginUnopened(Ledger(10, 10), new[] { B }, 2), 0);
        }

        [Test]
        public void InputOrderAndPreviouslyReturnedOptionsAreImmutableSnapshots()
        {
            var order = new List<SeatId> { A, B, C };
            BettingRound initial = BettingRound.BeginUnopened(Ledger(100, 100, 100), order, 2);
            LegalBettingActions options = initial.GetLegalActions();
            order.Reverse();
            order.Clear();
            BettingRound changed = Act(initial, BettingAction.BetTo(20));
            Assert.That(initial.CurrentSeat, Is.EqualTo(A));
            Assert.That(initial.CurrentBet, Is.Zero);
            Assert.That(options.CanBet, Is.True);
            Assert.That(options.MinimumAggressiveTarget, Is.EqualTo(2));
            Assert.That(changed.CurrentSeat, Is.EqualTo(B));
        }

        [Test]
        public void PrimitiveAllowsIndependentBranchesButNeverChangesOriginal()
        {
            BettingRound original = Unopened(100, 100);
            BettingRound first = Act(original, BettingAction.BetTo(10));
            BettingRound branch = Act(original, BettingAction.BetTo(20));
            Assert.That(first.CurrentBet, Is.EqualTo(10));
            Assert.That(branch.CurrentBet, Is.EqualTo(20));
            Assert.That(original.CurrentBet, Is.Zero);
            Assert.That(original.Ledger.TotalCommitted, Is.Zero);
        }

        [Test]
        public void WrongTurnAndIllegalActionKindsLeaveChipsAndTurnUnchanged()
        {
            BettingRound unopened = Unopened(100, 100, 100);
            AssertRejected(unopened, B, BettingAction.Check());
            AssertRejected(unopened, A, BettingAction.Call());
            AssertRejected(unopened, A, BettingAction.RaiseTo(10));
            BettingRound wager = Act(unopened, BettingAction.BetTo(10));
            AssertRejected(wager, B, BettingAction.Check());
            AssertRejected(wager, B, BettingAction.BetTo(20));
            AssertRejected(wager, B, BettingAction.RaiseTo(19));
            AssertRejected(wager, B, BettingAction.RaiseTo(101));
            AssertRejected(wager, B, BettingAction.RaiseTo(long.MaxValue));
        }

        [TestCase(1L)]
        [TestCase(101L)]
        [TestCase(long.MaxValue)]
        public void NonAllInUndersizedAndUnaffordableBetsAreRejected(long target)
        {
            BettingRound round = Unopened(100, 100);
            AssertRejected(round, A, BettingAction.BetTo(target));
        }

        [Test]
        public void NoActionsRemainAfterCompletion()
        {
            BettingRound round = Act(Act(Unopened(100, 100), BettingAction.Check()), BettingAction.Check());
            LegalBettingActions options = round.GetLegalActions();
            Assert.That(options.CanFold || options.CanCheck || options.CanCall || options.CanBet || options.CanRaise, Is.False);
            Assert.That(options.MinimumAggressiveTarget, Is.Null);
            Assert.That(options.MaximumAggressiveTarget, Is.Null);
            foreach (BettingAction action in new[] { BettingAction.Fold(), BettingAction.Check(), BettingAction.Call(),
                BettingAction.BetTo(2), BettingAction.RaiseTo(4) }) AssertRejected(round, A, action);
        }

        [Test]
        public void NullActionAndInvalidOrUnknownSeatsAreRejected()
        {
            BettingRound round = Unopened(100, 100);
            Assert.Throws<ArgumentNullException>(() => round.Apply(A, null));
            Assert.Throws<ArgumentException>(() => round.Apply(default, BettingAction.Check()));
            Assert.Throws<KeyNotFoundException>(() => round.Apply(C, BettingAction.Check()));
            Assert.Throws<ArgumentException>(() => round.GetStreetContribution(default));
            Assert.Throws<KeyNotFoundException>(() => round.IsFolded(C));
            Assert.Throws<KeyNotFoundException>(() => round.IsAllIn(C));
            Assert.Throws<ArgumentOutOfRangeException>(() => round.GetSeatAt(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => round.GetSeatAt(2));
            Assert.That(round.Ledger.TotalCommitted, Is.Zero);
        }

        [Test]
        public void MalformedStartInputsAreRejectedBeforeMovingMoney()
        {
            ChipLedger ledger = Ledger(100, 100, 100);
            Assert.Throws<ArgumentNullException>(() => BettingRound.BeginUnopened(null, new[] { A }, 2));
            Assert.Throws<ArgumentNullException>(() => BettingRound.BeginUnopened(ledger, null, 2));
            Assert.Throws<ArgumentException>(() => BettingRound.BeginUnopened(ledger, Array.Empty<SeatId>(), 2));
            Assert.Throws<ArgumentException>(() => BettingRound.BeginUnopened(ledger, new[] { A, A }, 2));
            Assert.Throws<ArgumentException>(() => BettingRound.BeginUnopened(ledger, new[] { A, default(SeatId) }, 2));
            Assert.Throws<KeyNotFoundException>(() => BettingRound.BeginUnopened(ledger, new[] { A, D }, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => BettingRound.BeginUnopened(ledger, new[] { A, B }, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => BettingRound.BeginUnopened(ledger, new[] { A, B }, -1));
            Assert.That(ledger.TotalCommitted, Is.Zero);
        }

        [Test]
        public void OpeningRejectsWrongBlindPositionsMissingSeatsZeroStacksAndExistingContributions()
        {
            ChipLedger ledger = Ledger(100, 100, 100);
            Assert.Throws<ArgumentException>(() => BettingRound.BeginOpening(ledger, new[] { A, B, C }, A, C, 1, 2));
            Assert.Throws<ArgumentException>(() => BettingRound.BeginOpening(ledger, new[] { A, B }, A, B, 1, 2));
            Assert.Throws<ArgumentException>(() => BettingRound.BeginOpening(Ledger(100), new[] { A }, A, A, 1, 2));
            Assert.Throws<ArgumentException>(() => BettingRound.BeginOpening(Ledger(0, 100), new[] { A, B }, A, B, 1, 2));
            Assert.Throws<ArgumentException>(() => BettingRound.BeginOpening(ledger.Contribute(A, 1), new[] { A, B, C }, B, C, 1, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => BettingRound.BeginOpening(ledger, new[] { A, B, C }, B, C, 0, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => BettingRound.BeginOpening(ledger, new[] { A, B, C }, B, C, 3, 2));
            Assert.That(ledger.TotalCommitted, Is.Zero);
        }

        [Test]
        public void InputReadFailureCannotPublishHalfPostedBlinds()
        {
            ChipLedger ledger = Ledger(100, 100);
            BettingRound result = null;
            var failure = new ApplicationException("Test order failure.");
            Assert.That(Assert.Throws<ApplicationException>(() => result = BettingRound.BeginOpening(
                ledger, new ThrowingOrder(failure), A, B, 1, 2)), Is.SameAs(failure));
            Assert.That(result, Is.Null);
            Assert.That(ledger.TotalCommitted, Is.Zero);
        }

        [Test]
        public void ShortBigBlindKeepsNominalBringInWhenTwoSeatsCanBet()
        {
            BettingRound round = BettingRound.BeginOpening(Ledger(100, 100, 1), new[] { A, B, C }, B, C, 1, 2);
            Assert.That(round.CurrentBet, Is.EqualTo(2));
            Assert.That(round.IsAllIn(C), Is.True);
            Assert.That(round.GetStreetContribution(C), Is.EqualTo(1));
            Assert.That(round.GetLegalActions().CallAmount, Is.EqualTo(2));
            Assert.That(round.GetLegalActions().MinimumAggressiveTarget, Is.EqualTo(4));
            round = Act(round, BettingAction.Call());
            Assert.That(round.GetLegalActions().CallAmount, Is.EqualTo(1));
            round = Act(round, BettingAction.Call());
            AssertComplete(round, 5);
            Assert.That(round.RefundedAmount, Is.Zero);
        }

        [Test]
        public void HeadsUpShortBlindCompletesWithoutDemandingUnmatchableChips()
        {
            BettingRound round = BettingRound.BeginOpening(Ledger(100, 1), new[] { A, B }, A, B, 1, 2);
            AssertComplete(round, 2);
            Assert.That(round.Ledger.GetChips(A).Stack, Is.EqualTo(99));
            Assert.That(round.CurrentBet, Is.EqualTo(1));
            Assert.That(round.RefundedAmount, Is.Zero);
        }

        [Test]
        public void OnlySolventSeatCallsActualShortBlindNotNominalBringIn()
        {
            BettingRound round = BettingRound.BeginOpening(Ledger(100, 1, 1), new[] { A, B, C }, B, C, 1, 2);
            Assert.That(round.CurrentBet, Is.EqualTo(2));
            Assert.That(round.GetLegalActions().CallAmount, Is.EqualTo(1));
            Assert.That(round.GetLegalActions().CanRaise, Is.False);
            round = Act(round, BettingAction.Call());
            AssertComplete(round, 3);
            Assert.That(round.Ledger.GetChips(A).Stack, Is.EqualTo(99));
            Assert.That(round.CurrentBet, Is.EqualTo(1));
        }

        [Test]
        public void FirstShortOpeningGivesUnactedSeatFullRaiseButDoesNotReopenChecker()
        {
            BettingRound round = Act(Unopened(100, 1, 100), BettingAction.Check());
            round = Act(round, BettingAction.BetTo(1));
            Assert.That(round.CurrentSeat, Is.EqualTo(C));
            Assert.That(round.LastFullRaise, Is.EqualTo(2));
            Assert.That(round.GetLegalActions().MinimumAggressiveTarget, Is.EqualTo(3));
            AssertRejected(round, C, BettingAction.RaiseTo(2));
            round = Act(round, BettingAction.Call());
            Assert.That(round.CurrentSeat, Is.EqualTo(A));
            Assert.That(round.GetLegalActions().CanRaise, Is.False);
            AssertRejected(round, A, BettingAction.RaiseTo(3));
            round = Act(round, BettingAction.Call());
            AssertComplete(round, 3);
        }

        [Test]
        public void FullRaiseAfterShortOpeningReopensChecker()
        {
            BettingRound round = Act(Unopened(100, 1, 100), BettingAction.Check());
            round = Act(round, BettingAction.BetTo(1));
            round = Act(round, BettingAction.RaiseTo(3));
            Assert.That(round.LastFullRaise, Is.EqualTo(2));
            Assert.That(round.GetLegalActions().MinimumAggressiveTarget, Is.EqualTo(5));
            round = Act(round, BettingAction.RaiseTo(5));
            round = Act(round, BettingAction.Call());
            AssertComplete(round, 11);
        }

        [Test]
        public void CumulativeShortOpeningToTwoReopensCheckerWithAnotherSolventOpponent()
        {
            BettingRound round = Act(Unopened(100, 1, 2, 100), BettingAction.Check());
            round = Act(round, BettingAction.BetTo(1));
            Assert.That(round.GetLegalActions().MinimumAggressiveTarget, Is.EqualTo(2));
            Assert.That(round.GetLegalActions().MaximumAggressiveTarget, Is.EqualTo(2));
            round = Act(round, BettingAction.RaiseTo(2));
            Assert.That(round.LastFullRaise, Is.EqualTo(2));
            round = Act(round, BettingAction.Call());
            Assert.That(round.CurrentSeat, Is.EqualTo(A));
            Assert.That(round.GetLegalActions().MinimumAggressiveTarget, Is.EqualTo(4));
            round = Act(round, BettingAction.RaiseTo(4));
            round = Act(round, BettingAction.Call());
            AssertComplete(round, 11);
        }

        [Test]
        public void FoldedSecondHighestContributionPreventsOverRefund()
        {
            BettingRound round = Act(Unopened(100, 20, 100), BettingAction.BetTo(60));
            round = Act(round, BettingAction.Call());
            round = Act(round, BettingAction.RaiseTo(100));
            round = Act(round, BettingAction.Fold());
            AssertComplete(round, 140);
            Assert.That(round.RefundedSeat, Is.EqualTo(C));
            Assert.That(round.RefundedAmount, Is.EqualTo(40));
            Assert.That(round.Ledger.GetChips(A).Committed, Is.EqualTo(60));
            Assert.That(round.Ledger.GetChips(C).Stack, Is.EqualTo(40));
        }

        [Test]
        public void RefundDoesNotUsePriorStreetOrExcludedSeatContributions()
        {
            ChipLedger ledger = Ledger(100, 100, 100).Contribute(A, 70).Contribute(B, 20).Contribute(C, 60);
            BettingRound round = BettingRound.BeginUnopened(ledger, new[] { B, A }, 2);
            Assert.That(round.SeatCount, Is.EqualTo(2));
            Assert.That(round.Ledger.SeatCount, Is.EqualTo(3));
            round = Act(round, BettingAction.BetTo(80));
            round = Act(round, BettingAction.Call());
            AssertComplete(round, 210);
            Assert.That(round.RefundedAmount, Is.EqualTo(50));
            Assert.That(round.Ledger.GetChips(B).Committed, Is.EqualTo(50));
            Assert.That(round.Ledger.GetChips(C).Committed, Is.EqualTo(60));
            Assert.That(ledger.TotalCommitted, Is.EqualTo(150));
        }

        [Test]
        public void MinimumRaiseArithmeticDoesNotOverflowAndOnlyWholeStackShortRaisesFit()
        {
            BettingRound round = BettingRound.BeginUnopened(Ledger(10, 15, 20), new[] { A, B, C }, long.MaxValue);
            Assert.That(round.GetLegalActions().MinimumAggressiveTarget, Is.EqualTo(10));
            round = Act(round, BettingAction.BetTo(10));
            Assert.That(round.GetLegalActions().MinimumAggressiveTarget, Is.EqualTo(15));
            AssertRejected(round, B, BettingAction.RaiseTo(14));
            round = Act(round, BettingAction.RaiseTo(15));
            Assert.That(round.GetLegalActions().MinimumAggressiveTarget, Is.Null);
            AssertRejected(round, C, BettingAction.RaiseTo(20));
            round = Act(round, BettingAction.Call());
            AssertComplete(round, 40);
            Assert.That(round.RefundedAmount, Is.Zero);
        }

        [Test]
        public void ShortOpeningRaiseByLessThanBigBlindDoesNotReopenPriorCaller()
        {
            BettingRound round = BettingRound.BeginOpening(Ledger(100, 3, 100, 100),
                new[] { A, B, C, D }, C, D, 1, 2);
            round = Act(round, BettingAction.Call());
            round = Act(round, BettingAction.RaiseTo(3));
            Assert.That(round.LastFullRaise, Is.EqualTo(2));
            Assert.That(round.GetLegalActions().MinimumAggressiveTarget, Is.EqualTo(5));
            round = Act(round, BettingAction.Call());
            round = Act(round, BettingAction.Call());
            Assert.That(round.CurrentSeat, Is.EqualTo(A));
            Assert.That(round.GetLegalActions().CallAmount, Is.EqualTo(1));
            Assert.That(round.GetLegalActions().CanRaise, Is.False);
            AssertRejected(round, A, BettingAction.RaiseTo(5));
            round = Act(round, BettingAction.Call());
            AssertComplete(round, 12);
        }

        [Test]
        public void BoundarySizedStacksPreserveTheInt64Total()
        {
            BettingRound round = Unopened(long.MaxValue - 2, 2);
            round = Act(round, BettingAction.BetTo(long.MaxValue - 2));
            Assert.That(round.GetLegalActions().CallAmount, Is.EqualTo(2));
            round = Act(round, BettingAction.Call());
            AssertComplete(round, 4);
            Assert.That(round.Ledger.TotalChips, Is.EqualTo(long.MaxValue));
            Assert.That(round.RefundedAmount, Is.EqualTo(long.MaxValue - 4));
        }

        [Test]
        public void BlindAmountsAreInputsRatherThanOneAndTwoConstants()
        {
            BettingRound round = BettingRound.BeginOpening(Ledger(100, 100, 100), new[] { A, B, C }, B, C, 3, 7);
            Assert.That(round.MinimumBet, Is.EqualTo(7));
            Assert.That(round.GetLegalActions().CallAmount, Is.EqualTo(7));
            Assert.That(round.GetLegalActions().MinimumAggressiveTarget, Is.EqualTo(14));
            round = Act(round, BettingAction.Call());
            Assert.That(round.GetLegalActions().CallAmount, Is.EqualTo(4));
            round = Act(round, BettingAction.Call());
            round = Act(round, BettingAction.Check());
            AssertComplete(round, 21);
        }

        [Test]
        public void SeededLegalTracesTerminateConserveChipsAndNeverPublishUnmatchedExcess()
        {
            var random = new Random(20260911);
            for (int run = 0; run < 200; run++)
            {
                int count = random.Next(2, 5);
                var stacks = new long[count];
                for (int i = 0; i < count; i++) stacks[i] = random.Next(1, 101);
                ChipLedger ledger = Ledger(stacks);
                var order = new SeatId[count];
                for (int i = 0; i < count; i++) order[i] = ledger.GetSeatAt(i);
                BettingRound round = run % 2 == 0 ? BettingRound.BeginUnopened(ledger, order, 2)
                    : BettingRound.BeginOpening(ledger, order, order[count - 2], order[count - 1], 1, 2);
                int actions = 0;
                while (!round.IsComplete)
                {
                    Assert.That(++actions, Is.LessThan(2000), "Trace must terminate with finite stacks.");
                    SeatId seat = round.CurrentSeat.Value;
                    Assert.That(round.IsFolded(seat) || round.IsAllIn(seat), Is.False);
                    LegalBettingActions legal = round.GetLegalActions();
                    var choices = new List<BettingAction>();
                    if (legal.CanFold) choices.Add(BettingAction.Fold());
                    if (legal.CanCheck) choices.Add(BettingAction.Check());
                    if (legal.CanCall) choices.Add(BettingAction.Call());
                    if (legal.CanBet)
                    {
                        choices.Add(BettingAction.BetTo(legal.MinimumAggressiveTarget.Value));
                        choices.Add(BettingAction.BetTo(legal.MaximumAggressiveTarget.Value));
                    }
                    if (legal.CanRaise)
                    {
                        choices.Add(BettingAction.RaiseTo(legal.MinimumAggressiveTarget.Value));
                        choices.Add(BettingAction.RaiseTo(legal.MaximumAggressiveTarget.Value));
                    }
                    Assert.That(choices, Is.Not.Empty);
                    BettingRound previous = round;
                    long oldCommitted = previous.Ledger.TotalCommitted;
                    round = Act(round, choices[random.Next(choices.Count)]);
                    Assert.That(previous.Ledger.TotalCommitted, Is.EqualTo(oldCommitted));
                    AssertConserved(round);
                    decimal street = 0;
                    for (int i = 0; i < count; i++) street += round.GetStreetContribution(order[i]);
                    Assert.That(street, Is.EqualTo((decimal)round.Ledger.TotalCommitted));
                }
                var paid = new long[count];
                for (int i = 0; i < count; i++) paid[i] = round.GetStreetContribution(order[i]);
                Array.Sort(paid);
                Assert.That(paid[count - 1], Is.EqualTo(paid[count - 2]));
            }
        }

        private static BettingRound Unopened(params long[] stacks)
        {
            ChipLedger ledger = Ledger(stacks);
            var order = new SeatId[ledger.SeatCount];
            for (int i = 0; i < order.Length; i++) order[i] = ledger.GetSeatAt(i);
            return BettingRound.BeginUnopened(ledger, order, 2);
        }
        private static ChipLedger Ledger(params long[] stacks)
        {
            SeatId[] seats = { A, B, C, D };
            var chips = new SeatChips[stacks.Length];
            for (int i = 0; i < stacks.Length; i++) chips[i] = new SeatChips(seats[i], stacks[i]);
            return ChipLedger.Create(chips);
        }
        private static BettingRound Act(BettingRound round, BettingAction action) => round.Apply(round.CurrentSeat.Value, action);
        private static void AssertComplete(BettingRound round, long committed)
        {
            Assert.That(round.IsComplete, Is.True);
            Assert.That(round.CurrentSeat, Is.Null);
            Assert.That(round.Ledger.TotalCommitted, Is.EqualTo(committed));
            AssertConserved(round);
        }
        private static void AssertConserved(BettingRound round)
        {
            decimal total = round.Ledger.TotalCommitted;
            for (int i = 0; i < round.Ledger.SeatCount; i++)
            {
                SeatChips chips = round.Ledger.GetChips(round.Ledger.GetSeatAt(i));
                Assert.That(chips.Stack, Is.GreaterThanOrEqualTo(0));
                Assert.That(chips.Committed, Is.GreaterThanOrEqualTo(0));
                total += chips.Stack;
            }
            Assert.That(total, Is.EqualTo((decimal)round.Ledger.TotalChips));
        }
        private static void AssertRejected(BettingRound round, SeatId seat, BettingAction action)
        {
            ChipLedger original = round.Ledger;
            SeatId? turn = round.CurrentSeat;
            long bet = round.CurrentBet;
            Assert.Throws<InvalidOperationException>(() => round.Apply(seat, action));
            Assert.That(round.Ledger, Is.SameAs(original));
            Assert.That(round.CurrentSeat, Is.EqualTo(turn));
            Assert.That(round.CurrentBet, Is.EqualTo(bet));
            AssertConserved(round);
        }
        private sealed class ThrowingOrder : IReadOnlyList<SeatId>
        {
            private readonly Exception failure;
            public ThrowingOrder(Exception failure) { this.failure = failure; }
            public int Count => 2;
            public SeatId this[int index] => index == 0 ? A : throw failure;
            public IEnumerator<SeatId> GetEnumerator() => throw new NotSupportedException();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
