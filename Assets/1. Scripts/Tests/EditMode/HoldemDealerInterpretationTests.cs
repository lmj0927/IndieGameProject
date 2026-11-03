using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Poker.Application;

namespace Poker.Foundation.Tests
{
    public sealed partial class HoldemDealerTurnTests
    {
        private sealed class CardPort : IHoldemDealerDealApplicationPort
        {
            public Func<HoldemDealerTurnRead> Read;
            public Func<HoldemDealCommand, HoldemRoomReceipt> Apply;
            public Func<HoldemDealCommand, HoldemRoomReceipt> Unchanged;
            public readonly List<HoldemDealCommand> Attempts = new List<HoldemDealCommand>();
            public HoldemDealerTurnRead ReadPendingTurn() => Read();
            public HoldemRoomReceipt DealUnchanged(HoldemDealCommand c) { Attempts.Add(c); return Unchanged(c); }
            public HoldemRoomReceipt ApplyDealerDeal(HoldemDealCommand c) { Attempts.Add(c); return Apply(c); }
        }
        private sealed class ExplicitTestSelection : IHoldemDealerSelectionPolicy
        {
            public int Calls;
            public Func<HoldemDealerWork, HoldemDealerInterpretationBatch, HoldemDealerSelection> SelectResult;
            public HoldemDealerSelection Select(HoldemDealerWork work, HoldemDealerInterpretationBatch batch)
            { Calls++; return SelectResult(work, batch); }
        }
        private CardPort CardTestPort() => new CardPort {
            Read = () => room.ReadPendingDealerTurn(peers[0]),
            Apply = c => room.ApplyDealerDeal(peers[0], c), Unchanged = c => room.DealUnchanged(peers[0], c)
        };
        private static ExplicitTestSelection SelectTestCard(int card = 45) => new ExplicitTestSelection {
            SelectResult = (work, batch) => HoldemDealerSelection.Change(batch.GetEntry(0).UtteranceId, 0,
                Card.FromId(card), HoldemCardSourceScope.UndealtOutsideCurrentHandRunout)
        };
        private static HoldemDealerInterpretationBatch Interpret(HoldemDealerWork work, int card = 45, double confidence = .8)
        {
            var target = work.TargetDeal;
            return new HoldemDealerInterpretationBatch(target.SessionId, target.HandId, target.WindowId, work.ExpectedVersion,
                target.Street, work.SourceUtterances.WindowId, Enumerable.Range(0, work.SourceUtterances.Count)
                    .Select(i => new HoldemDealerInterpretation(work.SourceUtterances.GetEntry(i).CommandId,
                        HoldemDealerIntent.CardPreference, Card.FromId(card).Rank, Card.FromId(card).Suit, .3, confidence)).ToArray());
        }

        [Test]
        public void InterpretationOwnerAppliesOnceAndEqualWorkerPostsDoNotLeakAuthority()
        {
            Create(); Start(); Speak(2, "해석 테스트"); EndBetting();
            var port = CardTestPort(); var policy = SelectTestCard(); int owner = Thread.CurrentThread.ManagedThreadId;
            policy.SelectResult = (w, b) => {
                Assert.That(Thread.CurrentThread.ManagedThreadId, Is.EqualTo(owner));
                return HoldemDealerSelection.Change(b.GetEntry(0).UtteranceId, 0, Card.FromId(45),
                    HoldemCardSourceScope.UndealtOutsideCurrentHandRunout);
            };
            using var coordinator = new HoldemDealerTurnCoordinator(port, policy);
            coordinator.Poll(out var work);
            var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() => coordinator.TryPostInterpretations(work, Interpret(work)))).ToArray();
            Assert.That(Task.WaitAll(tasks, 3000), Is.True); Assert.That(tasks.All(t => t.Result), Is.True);
            Assert.That(port.Attempts, Is.Empty); Assert.That(policy.Calls, Is.Zero);
            Assert.That(coordinator.TryPostInterpretations(work, Interpret(work, confidence: .9)), Is.False);
            Assert.That(coordinator.TryPostUnchanged(work), Is.False);
            Assert.That(coordinator.Poll(out _).Accepted, Is.True);
            Assert.That(policy.Calls, Is.EqualTo(1)); Assert.That(port.Attempts.Count, Is.EqualTo(1));
            Assert.That(State.GetBoardCard(0).Id, Is.EqualTo(45));
            Assert.That(room.Read(peers[1]).OwnCardChange.Card.Id, Is.EqualTo(45));
            Assert.That(room.Read(peers[0]).OwnCardChange, Is.Null);
            Assert.That(coordinator.ReopenRejectedTurn(), Is.False);
            Assert.That(coordinator.TryPostInterpretations(work, Interpret(work)), Is.False);
        }

        [TestCase(false)] [TestCase(true)]
        public void InterpretationAndTimeoutShareOneCompletionSlot(bool resultFirst)
        {
            Create(); Start(); Speak(2, "테스트"); EndBetting(); double seconds = 0;
            var port = CardTestPort(); var policy = SelectTestCard();
            using var coordinator = new HoldemDealerTurnCoordinator(port, policy, TimeSpan.FromSeconds(5), () => TimeSpan.FromSeconds(seconds));
            coordinator.Poll(out var work);
            if (resultFirst) Assert.That(coordinator.TryPostInterpretations(work, Interpret(work)), Is.True);
            seconds = 5; Assert.That(coordinator.Poll(out _).Accepted, Is.True);
            Assert.That(port.Attempts.Count, Is.EqualTo(1));
            Assert.That(port.Attempts[0].CardChange != null, Is.EqualTo(resultFirst));
            Assert.That(policy.Calls, Is.EqualTo(resultFirst ? 1 : 0));
            Assert.That(coordinator.TryPostInterpretations(work, Interpret(work)), Is.False);
            Assert.That(room.Read(peers[1]).OwnCardChange != null, Is.EqualTo(resultFirst));
        }

        [Test]
        public void InterpretationTimeoutExcludesDisconnectedIntervals()
        {
            Create(); Start(); Speak(2, "테스트"); EndBetting(); double seconds = 0;
            var port = CardTestPort();
            using var coordinator = new HoldemDealerTurnCoordinator(port, SelectTestCard(), TimeSpan.FromSeconds(5), () => TimeSpan.FromSeconds(seconds));
            coordinator.Poll(out var work); seconds = 2; coordinator.Poll(out _);
            room.Disconnect(peers[1]); seconds = 100; coordinator.Poll(out _);
            RestorePeer(1); seconds = 200; coordinator.Poll(out _); seconds = 202.99; coordinator.Poll(out _);
            Assert.That(port.Attempts, Is.Empty);
            seconds = 203; Assert.That(coordinator.Poll(out _).Accepted, Is.True);
            Assert.That(port.Attempts.Single().CardChange, Is.Null);
            Assert.That(coordinator.TryPostInterpretations(work, Interpret(work)), Is.False);
        }

        [TestCase(false)] [TestCase(true)]
        public void InterpretationUncertainApplicationReusesExactSelectionAndCommand(bool applied)
        {
            Create(); Start(); Speak(2, "테스트"); EndBetting(); var port = CardTestPort(); var policy = SelectTestCard();
            using var coordinator = new HoldemDealerTurnCoordinator(port, policy);
            coordinator.Poll(out var work); coordinator.TryPostInterpretations(work, Interpret(work));
            port.Apply = c => { if (applied) Assert.That(room.ApplyDealerDeal(peers[0], c).Accepted, Is.True); throw new IOException("lost reply"); };
            Assert.Throws<IOException>(() => coordinator.Poll(out _));
            Assert.That(coordinator.ReopenRejectedTurn(), Is.False);
            Assert.That(coordinator.TryPostUnchanged(work), Is.False);
            if (applied) { Resume(); EndBetting(); }
            long before = State.SessionVersion;
            port.Apply = c => room.ApplyDealerDeal(peers[0], c);
            Assert.That(coordinator.Poll(out _).Accepted, Is.True);
            Assert.That(policy.Calls, Is.EqualTo(1));
            Assert.That(port.Attempts[1], Is.SameAs(port.Attempts[0]));
            Assert.That(State.SessionVersion, Is.EqualTo(applied ? before : before + 1));
        }

        [Test]
        public void InterpretationRejectedUnavailableCardRequiresExplicitFreshRecovery()
        {
            Create(); Start(); Speak(2, "테스트"); EndBetting(); var port = CardTestPort();
            using var coordinator = new HoldemDealerTurnCoordinator(port, SelectTestCard(0));
            coordinator.Poll(out var old); coordinator.TryPostInterpretations(old, Interpret(old, 0));
            var rejection = coordinator.Poll(out _);
            Assert.That(rejection.Poker.Error, Is.EqualTo(HoldemCommandError.InvalidCardChange));
            Assert.That(State.SessionVersion, Is.EqualTo(old.ExpectedVersion));
            Assert.That(State.IsDealPending, Is.True); Assert.That(room.Read(peers[1]).OwnCardChange, Is.Null);
            Assert.That(coordinator.Poll(out _), Is.Null); Assert.That(port.Attempts.Count, Is.EqualTo(1));
            Assert.That(coordinator.ReopenRejectedTurn(), Is.True);
            Assert.That(coordinator.TryPostInterpretations(old, Interpret(old, 0)), Is.False);
            coordinator.Poll(out var fresh); Assert.That(fresh, Is.Not.SameAs(old));
            Assert.That(coordinator.TryPostUnchanged(fresh), Is.True);
            Assert.That(coordinator.Poll(out _).Accepted, Is.True);
            Assert.That(port.Attempts[1].CommandId, Is.Not.EqualTo(port.Attempts[0].CommandId));
            Assert.That(port.Attempts[1].CardChange, Is.Null);
        }

        [TestCase("null")] [TestCase("throw")] [TestCase("other-source")] [TestCase("wrong-card")]
        public void InterpretationInvalidPolicyNeverManufacturesACompletedUnchangedDeal(string fault)
        {
            Create(); Start(); Speak(2, "테스트"); EndBetting(); var port = CardTestPort(); var policy = SelectTestCard();
            policy.SelectResult = (w, b) => fault == "null" ? null : fault == "throw" ? throw new InvalidOperationException("not decided")
                : HoldemDealerSelection.Change(fault == "other-source" ? Guid.NewGuid() : b.GetEntry(0).UtteranceId,
                    0, Card.FromId(fault == "wrong-card" ? 46 : 45), HoldemCardSourceScope.UndealtOutsideCurrentHandRunout);
            using var coordinator = new HoldemDealerTurnCoordinator(port, policy);
            coordinator.Poll(out var work); coordinator.TryPostInterpretations(work, Interpret(work));
            Assert.Throws<InvalidOperationException>(() => coordinator.Poll(out _));
            Assert.That(port.Attempts, Is.Empty); Assert.That(State.SessionVersion, Is.EqualTo(work.ExpectedVersion));
            Assert.That(coordinator.ReopenRejectedTurn(), Is.True);
            Assert.That(coordinator.TryPostInterpretations(work, Interpret(work)), Is.False);
        }

        [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(5)] [TestCase(6)] [TestCase(7)]
        public void InterpretationRejectsForeignOrIncompleteResponseBeforeSelecting(int fault)
        {
            Create(); Start(); Speak(2, "테스트"); EndBetting(); var port = CardTestPort(); var policy = SelectTestCard();
            using var coordinator = new HoldemDealerTurnCoordinator(port, policy);
            coordinator.Poll(out var work); var good = Interpret(work); var entry = good.GetEntry(0);
            var bad = new HoldemDealerInterpretationBatch(fault == 0 ? Guid.NewGuid() : good.SessionId,
                fault == 1 ? Guid.NewGuid() : good.HandId, fault == 2 ? Guid.NewGuid() : good.DealWindowId,
                good.ExpectedVersion + (fault == 3 ? 1 : 0), fault == 4 ? HoldemStreet.Turn : good.Street,
                fault == 5 ? Guid.NewGuid() : good.UtteranceWindowId, fault == 6 ? Array.Empty<HoldemDealerInterpretation>()
                    : new[] { fault == 7 ? new HoldemDealerInterpretation(Guid.NewGuid(), entry.Intent, entry.Rank,
                        entry.Suit, entry.Explicitness, entry.Confidence) : entry });
            Assert.That(coordinator.TryPostInterpretations(work, bad), Is.False);
            Assert.That(coordinator.Poll(out _), Is.Null); Assert.That(policy.Calls, Is.Zero); Assert.That(port.Attempts, Is.Empty);
            Assert.That(coordinator.TryPostInterpretations(work, good), Is.True);
        }

        [Test]
        public void InterpretationContractRejectsInvalidValuesAndCopiesItsCollection()
        {
            Guid id = Guid.NewGuid();
            foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -.1, 1.1 })
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => new HoldemDealerInterpretation(id, HoldemDealerIntent.NoRequest, null, null, invalid, 0));
                Assert.Throws<ArgumentOutOfRangeException>(() => new HoldemDealerInterpretation(id, HoldemDealerIntent.NoRequest, null, null, 0, invalid));
            }
            Assert.Throws<ArgumentException>(() => new HoldemDealerInterpretation(id, HoldemDealerIntent.CardPreference, null, null, 0, 0));
            Assert.Throws<ArgumentException>(() => new HoldemDealerInterpretation(id, HoldemDealerIntent.NoRequest, Rank.Ace, null, 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new HoldemDealerInterpretation(id, HoldemDealerIntent.CardPreference, (Rank)1, null, 0, 0));
            Create(); Start(); Speak(2, "테스트"); EndBetting();
            using var coordinator = new HoldemDealerTurnCoordinator(CardTestPort(), SelectTestCard());
            coordinator.Poll(out var work); var entry = Interpret(work).GetEntry(0); var entries = new[] { entry };
            var d = work.TargetDeal;
            var frozen = new HoldemDealerInterpretationBatch(d.SessionId, d.HandId, d.WindowId, work.ExpectedVersion,
                d.Street, work.SourceUtterances.WindowId, entries);
            entries[0] = null; Assert.That(frozen.GetEntry(0), Is.SameAs(entry));
            Assert.Throws<ArgumentException>(() => new HoldemDealerInterpretationBatch(d.SessionId, d.HandId, d.WindowId,
                work.ExpectedVersion, d.Street, work.SourceUtterances.WindowId, new[] { entry, entry }));
        }
    }
}
