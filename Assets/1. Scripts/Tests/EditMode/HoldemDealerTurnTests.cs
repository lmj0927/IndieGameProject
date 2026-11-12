using System;
using System.Linq;
using NUnit.Framework;
using Poker.Application;
using UnityEngine;

namespace Poker.Foundation.Tests
{
    public sealed partial class HoldemDealerTurnTests
    {
        private HoldemRoom room;
        private Guid[] peers;
        private HoldemRoomAdmission[] admissions;
        private sealed class StableRandom : IRandomSource
        {
            private int calls;
            private readonly int pattern;
            public StableRandom(int pattern = 3) { this.pattern = pattern; }
            public int NextInt(int exclusiveMax) => ++calls % 52 == 0 ? pattern : exclusiveMax - 1;
        }

        private void Create(bool speech = true, bool manual = true, HoldemUtterancePolicy policy = null, int pattern = 3)
        {
            peers = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
            admissions = new HoldemRoomAdmission[4];
            room = HoldemRoom.Create(Guid.NewGuid(), peers[0], "방장", new HoldemConfig(100, 1, 2,
                HoldemRevealPolicy.PauseAfterCommunityReveal, dealPolicy: manual ? HoldemDealPolicy.WaitForHost
                    : HoldemDealPolicy.Automatic), new SeatId(1), new StableRandom(pattern), out admissions[0],
                utterancePolicy: speech ? policy ?? new HoldemUtterancePolicy(128, 2, HoldemUtteranceSeats.Active) : null);
            for (int i = 1; i < 4; i++) admissions[i] = room.Join(peers[i], "참가자 " + i).Admission;
            foreach (var peer in peers) room.SetReady(peer, true);
        }
        private HoldemSnapshot State => room.Read(peers[0]).Game;
        private void Start()
        {
            var view = room.Read(peers[0]);
            Assert.That(room.StartHand(peers[0], new HoldemStartCommand(view.SessionId, Guid.NewGuid(),
                Guid.NewGuid(), view.Game?.SessionVersion ?? 0)).Accepted, Is.True);
        }
        private void Act(bool fold = false, bool shove = false)
        {
            int actor = State.CurrentSeat.Value.Value - 1;
            var s = room.Read(peers[actor]).Game;
            Assert.That(room.Submit(peers[actor], new HoldemRoomAction(s.SessionId, s.HandId, Guid.NewGuid(),
                s.SessionVersion, fold ? BettingAction.Fold() : shove ? BettingAction.RaiseTo(100)
                    : s.LegalActions.CanCheck ? BettingAction.Check() : BettingAction.Call())).Accepted, Is.True);
        }
        private void EndBetting()
        {
            for (int i = 0; i < 8 && State.CurrentSeat.HasValue; i++) Act();
            Assert.That(State.CurrentSeat, Is.Null);
        }
        private Guid Speak(int seat, string text, bool designate = true)
        {
            var v = room.ReadUtterances(peers[seat - 1]);
            Assert.That(room.SubmitUtterance(peers[seat - 1], new HoldemRoomUtterance(v.SessionId, v.HandId,
                v.WindowId, Guid.NewGuid(), v.Street, text)).Accepted, Is.True);
            if (designate)
            {
                var message = room.ReadUtterances(peers[seat - 1]).GetEntry(v.Count);
                Assert.That(room.SubmitUtterance(peers[seat - 1], new HoldemRoomUtterance(v.SessionId, v.HandId,
                    v.WindowId, Guid.NewGuid(), v.Street, "", message.CommandId)).Accepted, Is.True);
            }
            return v.WindowId;
        }
        private HoldemDealerTurn Read()
        {
            var read = room.ReadPendingDealerTurn(peers[0]);
            Assert.That(read.Error, Is.EqualTo(HoldemRoomError.None));
            Assert.That(read.Turn, Is.Not.Null);
            return read.Turn;
        }
        private void Resume()
        {
            var s = State;
            Assert.That(room.ResumeAfterReveal(peers[0], new HoldemRevealCommand(s.SessionId, s.HandId,
                Guid.NewGuid(), s.SessionVersion, s.Street)).Accepted, Is.True);
        }

        [TestCase(0, "2,2,1")] [TestCase(1, "2,1,2")] [TestCase(2, "1,2,2")]
        [TestCase(3, "3,1,1")] [TestCase(4, "1,3,1")] [TestCase(5, "1,1,3")]
        [TestCase(6, "4,1")] [TestCase(7, "1,4")] [TestCase(8, "3,2")]
        [TestCase(9, "2,3")] [TestCase(10, "5")]
        public void RandomRevealPatternsKeepRequestsBoundToEachDeal(int pattern, string counts)
        {
            Create(pattern: pattern); Start();
            int[] expected = counts.Split(',').Select(int.Parse).ToArray();
            int boardCount = 0;
            for (int i = 0; i < expected.Length; i++)
            {
                var sourceStreet = State.Street;
                Speak(2, "카드 요청 " + i); EndBetting();
                var turn = Read();
                Assert.That(turn.TargetDeal.SourceStreet, Is.EqualTo(sourceStreet));
                Assert.That(turn.TargetDeal.BoardStart, Is.EqualTo(boardCount));
                Assert.That(turn.TargetDeal.CardCount, Is.EqualTo(expected[i]));
                Assert.That(turn.SourceUtterances.Street, Is.EqualTo(sourceStreet));
                var resolver = new HoldemRequestedCardResolver(new StableRandom());
                var policy = new ExplicitTestSelection { SelectResult = (work, batch) => resolver.Resolve(work, batch.GetEntry(0)) };
                using (var coordinator = new HoldemDealerTurnCoordinator(CardTestPort(), policy))
                {
                    coordinator.Poll(out var work);
                    Assert.That(coordinator.TryPostInterpretations(work, Interpret(work, 45 - i)), Is.True);
                    Assert.That(coordinator.Poll(out _).Accepted, Is.True);
                }
                boardCount += expected[i];
                Assert.That(State.BoardCount, Is.EqualTo(boardCount));
                Assert.That(State.GetBoardCard(boardCount - 1).Id, Is.EqualTo(45 - i));
                Assert.That(State.Street == HoldemStreet.River, Is.EqualTo(i == expected.Length - 1));
                var hostPacket = HoldemRoomPacketMapper.Create(room.Read(peers[0]));
                var guestPacket = HoldemRoomPacketMapper.Create(room.Read(peers[1]));
                Assert.That(guestPacket.game.board, Is.EqualTo(hostPacket.game.board));
                Assert.That(guestPacket.game.seats[0].visibleCards, Is.Empty);
                Resume();
            }
            EndBetting();
            Assert.That(State.Result, Is.Not.Null);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DealerTurnPairsEachClosedBettingWindowWithoutConsumingTextOrChangingPoker(bool speak)
        {
            Create();
            Assert.That(room.ReadPendingDealerTurn(peers[0]).Turn, Is.Null);
            Start();
            Assert.That(room.ReadPendingDealerTurn(peers[0]).Turn, Is.Null);
            for (int street = 0; street < 3; street++)
            {
                Guid speechWindow = room.ReadUtterances(peers[1]).WindowId;
                if (speak) Speak(2, "단계 원문 " + street);
                EndBetting();
                string before = JsonUtility.ToJson(HoldemRoomPacketMapper.Create(room.Read(peers[0])));
                var turn = Read();
                Assert.That(turn.InputKind, Is.EqualTo(HoldemDealerInputKind.ClosedBettingWindow));
                Assert.That(turn.TargetDeal.Street, Is.EqualTo((HoldemStreet)(street + 1)));
                Assert.That(turn.SourceUtterances.Street, Is.EqualTo((HoldemStreet)street));
                Assert.That(turn.SourceUtterances.WindowId, Is.EqualTo(speechWindow));
                Assert.That(turn.TargetDeal.WindowId, Is.Not.EqualTo(speechWindow));
                Assert.That(turn.SourceUtterances.Count, Is.EqualTo(speak ? 1 : 0));
                Assert.That(turn.SourceUtterances.HandId, Is.EqualTo(turn.TargetDeal.HandId));
                Assert.That(JsonUtility.ToJson(HoldemRoomPacketMapper.Create(room.Read(peers[0]))), Is.EqualTo(before));
                Assert.That(Read().SourceUtterances, Is.SameAs(turn.SourceUtterances));
                if (speak)
                {
                    Assert.That(room.AcknowledgeUtteranceBatch(peers[0], speechWindow), Is.True);
                    Assert.That(room.ReadClosedDesignatedBatches(peers[0]), Is.Empty);
                    Assert.That(Read().SourceUtterances, Is.SameAs(turn.SourceUtterances));
                    Assert.That(turn.SourceUtterances.GetEntry(0).Text, Is.EqualTo("단계 원문 " + street));
                }
                var command = turn.CreateUnchangedCommand(Guid.NewGuid());
                Assert.That(command.ExpectedVersion, Is.EqualTo(turn.ExpectedVersion));
                Assert.That(room.DealUnchanged(peers[0], command).Accepted, Is.True);
                long version = State.SessionVersion;
                Assert.That(room.ReadPendingDealerTurn(peers[0]).Turn, Is.Null);
                Assert.That(room.DealUnchanged(peers[0], command).Accepted, Is.True);
                Assert.That(State.SessionVersion, Is.EqualTo(version));
                Resume();
            }
            EndBetting();
            Assert.That(State.Result, Is.Not.Null);
            Assert.That(room.ReadPendingDealerTurn(peers[0]).Turn, Is.Null);
        }

        [Test]
        public void PublicOnlyRemarksAreNotAvailableThroughTheDealerPortEvenWithManualDealing()
        {
            Create(policy: new HoldemUtterancePolicy(128, 1, HoldemUtteranceSeats.Active,
                HoldemUtteranceVisibility.PublicRaw, HoldemUtteranceBatchRetention.None));
            Start(); Speak(2, "이 멘트는 공개 대화에만 남아요", false); EndBetting();
            var turn = Read();
            Assert.That(turn.InputKind, Is.EqualTo(HoldemDealerInputKind.Disabled));
            Assert.That(turn.SourceUtterances, Is.Null);
            Assert.That(room.ReadClosedDesignatedBatches(peers[0]), Is.Empty);
            Assert.That(room.Read(peers[0]).PublicUtterances.Count, Is.EqualTo(1));
            Assert.That(room.DealUnchanged(peers[0], turn.CreateUnchangedCommand(Guid.NewGuid())).Accepted, Is.True);
        }

        [Test]
        public void DealerTurnDistinguishesDisabledInputAndSkippedAllInBettingWindows()
        {
            Create(false); Start(); EndBetting();
            Assert.That(Read().InputKind, Is.EqualTo(HoldemDealerInputKind.Disabled));
            Assert.That(Read().SourceUtterances, Is.Null);
            Create(); Start(); Speak(1, "프리플랍에만 접수"); Act(shove: true); EndBetting();
            var first = Read();
            Assert.That(first.InputKind, Is.EqualTo(HoldemDealerInputKind.ClosedBettingWindow));
            Assert.That(first.SourceUtterances.Count, Is.EqualTo(1));
            for (int street = 1; street <= 3; street++)
            {
                var turn = Read();
                Assert.That(turn.TargetDeal.Street, Is.EqualTo((HoldemStreet)street));
                if (street > 1)
                {
                    Assert.That(turn.InputKind, Is.EqualTo(HoldemDealerInputKind.NoBettingWindow));
                    Assert.That(turn.SourceUtterances, Is.Null, "Never replay preflop speech for the turn/river.");
                }
                Assert.That(room.DealUnchanged(peers[0], turn.CreateUnchangedCommand(Guid.NewGuid())).Accepted, Is.True);
                Resume();
            }
            Assert.That(State.Result, Is.Not.Null);
            Assert.That(room.ReadClosedDesignatedBatches(peers[0]).Single(), Is.SameAs(first.SourceUtterances),
                "Deal completion must not acknowledge retained raw input.");
        }

        [Test]
        public void DealerTurnDoesNotTreatPastHandOrFoldedOutInputAsCurrentWork()
        {
            Create(); Start(); Speak(1, "종료된 판의 원문");
            for (int i = 0; i < 3; i++) Act(fold: true);
            Assert.That(room.ReadPendingDealerTurn(peers[0]).Turn, Is.Null);
            var old = room.ReadClosedDesignatedBatches(peers[0]).Single();
            Start(); EndBetting();
            var current = Read();
            Assert.That(current.SourceUtterances.Count, Is.Zero);
            Assert.That(current.SourceUtterances.HandId, Is.Not.EqualTo(old.HandId));
            Assert.That(room.ReadClosedDesignatedBatches(peers[0]).Single(), Is.SameAs(old));
            Create(manual: false); Start(); Speak(1, "자동 공개"); EndBetting();
            Assert.That(room.ReadPendingDealerTurn(peers[0]).Turn, Is.Null);
        }

        [TestCase(0)]
        [TestCase(1)]
        public void DealerTurnPreservesHostAuthorityAndSameWorkAcrossReconnect(int disconnected)
        {
            Create(); Start(); Speak(2, "접속 복구에도 같은 원문"); EndBetting();
            var turn = Read(); var command = turn.CreateUnchangedCommand(Guid.NewGuid());
            Assert.That(room.ReadPendingDealerTurn(peers[1]).Error, Is.EqualTo(HoldemRoomError.HostOnly));
            Assert.That(room.ReadPendingDealerTurn(Guid.NewGuid()).Error, Is.EqualTo(HoldemRoomError.UnknownConnection));
            Guid old = peers[disconnected]; room.Disconnect(old);
            Assert.That(room.ReadPendingDealerTurn(peers[0]).Error, Is.EqualTo(disconnected == 0
                ? HoldemRoomError.Disconnected : HoldemRoomError.Paused));
            Assert.That(room.ReadPendingDealerTurn(peers[0]).Turn, Is.Null);
            Assert.That(room.DealUnchanged(peers[0], command).Accepted, Is.False);
            peers[disconnected] = Guid.NewGuid();
            Assert.That(room.Reconnect(peers[disconnected], admissions[disconnected].ResumeToken).Accepted, Is.True);
            Assert.That(Read().TargetDeal.WindowId, Is.EqualTo(turn.TargetDeal.WindowId));
            Assert.That(Read().SourceUtterances, Is.SameAs(turn.SourceUtterances));
            Assert.That(room.ReadPendingDealerTurn(old).Error, Is.EqualTo(HoldemRoomError.UnknownConnection));
            Assert.That(room.DealUnchanged(peers[0], command).Accepted, Is.True);
        }

        [Test]
        public void DealerTurnLateOrConflictingCompletionCannotCloseTheNextGate()
        {
            Create(); Start(); Speak(1, "첫 창"); EndBetting();
            var first = Read(); var accepted = first.CreateUnchangedCommand(Guid.NewGuid());
            Assert.That(room.DealUnchanged(peers[0], accepted).Accepted, Is.True);
            Resume(); EndBetting();
            var next = Read(); long version = State.SessionVersion;
            Assert.That(room.DealUnchanged(peers[0], first.CreateUnchangedCommand(Guid.NewGuid())).Accepted, Is.False);
            Assert.That(room.DealUnchanged(peers[0], next.CreateUnchangedCommand(accepted.CommandId)).Accepted, Is.False);
            Assert.That(room.DealUnchanged(peers[0], accepted).Accepted, Is.True);
            Assert.That(State.SessionVersion, Is.EqualTo(version));
            Assert.That(Read().TargetDeal.WindowId, Is.EqualTo(next.TargetDeal.WindowId));
            Assert.Throws<ArgumentException>(() => next.CreateUnchangedCommand(Guid.Empty));
            Assert.That(room.DealUnchanged(peers[1], next.CreateUnchangedCommand(Guid.NewGuid())).Error,
                Is.EqualTo(HoldemRoomError.HostOnly));
        }
    }
}
