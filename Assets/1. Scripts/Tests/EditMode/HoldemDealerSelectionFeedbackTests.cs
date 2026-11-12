using System;
using System.Linq;
using NUnit.Framework;
using Poker.Application;
using Poker.Online;
using Poker.Transport;
using UnityEngine;

namespace Poker.Foundation.Tests
{
    public sealed partial class HoldemDealerTurnTests
    {
        private HoldemDealCommand SelectSentence(int card = 45)
        {
            var turn = Read();
            var entry = turn.SourceUtterances.GetEntry(0);
            var deal = turn.TargetDeal;
            return new HoldemDealCommand(deal.SessionId, deal.HandId, deal.WindowId, Guid.NewGuid(),
                turn.ExpectedVersion, deal.Street, new HoldemCardChange(entry.WindowId, entry.CommandId,
                    entry.Speaker, deal.BoardStart + deal.CardCount - 1, Card.FromId(card),
                    HoldemCardSourceScope.UndealtOutsideCurrentHandRunout));
        }

        private HoldemRoomPacket FeedbackPacket(int peer)
        {
            var packet = JsonUtility.FromJson<HoldemRoomPacket>(JsonUtility.ToJson(HoldemRoomPacketMapper.Create(room.Read(peers[peer]))));
            Assert.DoesNotThrow(() => HoldemDealerSelectionPacketReader.Read(packet));
            Assert.DoesNotThrow(() => new SpeechHistoryAssembler().Accept(packet));
            return packet;
        }

        [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(5)]
        [TestCase(6)] [TestCase(7)] [TestCase(8)] [TestCase(9)] [TestCase(10)]
        public void SelectedSentencesStayPrivateUntilFinalResultForEveryRevealPattern(int pattern)
        {
            Create(policy: HoldemLlmTableDealer.CreateUtterancePolicy(), pattern: pattern); Start();
            int selected = 0;
            while (State.BoardCount < 5)
            {
                string text = "내 요청 " + selected;
                Speak(2, text, designate: false); EndBetting();
                var command = SelectSentence(45 - selected);
                Assert.That(room.ApplyDealerDeal(peers[0], command).Accepted, Is.True);
                Assert.That(room.ApplyDealerDeal(peers[0], command).Accepted, Is.True, "Idempotent replay");
                selected++;
                for (int i = 0; i < 4; i++)
                {
                    var packet = FeedbackPacket(i);
                    Assert.That(packet.dealerSelections.Length, Is.EqualTo(i == 1 ? selected : 0));
                    if (i == 1)
                    {
                        Assert.That(packet.dealerSelections.Last().text, Is.EqualTo(text));
                        Assert.That(packet.dealerSelections.Last().cardChanged, Is.True);
                        Assert.That(packet.dealerSelections.Last().hasAppliedCard, Is.True);
                        Assert.That(packet.dealerSelections.Last().boardIndex, Is.EqualTo(command.CardChange.BoardIndex));
                        Assert.That(packet.dealerSelections.Last().card, Is.EqualTo(command.CardChange.Card.Id));
                        Assert.That(packet.dealerSelections.Last().dealWindowId, Is.EqualTo(command.WindowId.ToString("N")));
                    }
                }
                Resume();
                Assert.That(FeedbackPacket(0).dealerSelections, Is.Empty, "Betting after a reveal is still private");
            }
            EndBetting();
            Assert.That(State.Result, Is.Not.Null);
            foreach (int i in Enumerable.Range(0, 4))
            {
                var packet = FeedbackPacket(i);
                Assert.That(packet.dealerSelections.Length, Is.EqualTo(selected));
                for (int j = 0; j < selected; j++)
                {
                    var item = packet.dealerSelections[j];
                    Assert.That(item.text, Is.EqualTo("내 요청 " + j));
                    Assert.That(item.card, Is.EqualTo(45 - j));
                    Assert.That(item.card, Is.EqualTo(packet.game.board[item.boardIndex]));
                }
            }
            Start();
            foreach (int i in Enumerable.Range(0, 4)) Assert.That(FeedbackPacket(i).dealerSelections, Is.Empty);
        }

        [Test]
        public void CoreRejectsAValidSeatIdOutsideItsRosterWithoutChangingTheDeal()
        {
            var first = new SeatId(1); var second = new SeatId(2);
            var session = new HoldemSession(Guid.NewGuid(), HoldemLlmTableDealer.CreateConfig(), first, second, new StableRandom());
            Assert.That(session.StartNextHand(Guid.NewGuid(), Guid.NewGuid(), 0).Accepted, Is.True);
            for (int step = 0; session.PendingDeal == null && step < 4; step++)
            {
                var state = session.GetSnapshot(first);
                var actor = state.CurrentSeat.Value;
                var own = session.GetSnapshot(actor);
                Assert.That(session.Submit(actor, HoldemCommand.Act(session.SessionId, own.HandId, Guid.NewGuid(),
                    actor, own.SessionVersion, own.LegalActions.CanCheck ? BettingAction.Check() : BettingAction.Call())).Accepted, Is.True);
            }
            var deal = session.PendingDeal;
            Assert.That(deal, Is.Not.Null);
            long before = session.Version;
            var change = new HoldemCardChange(Guid.NewGuid(), Guid.NewGuid(), new SeatId(3), deal.BoardStart,
                Card.FromId(45), HoldemCardSourceScope.UndealtOutsideCurrentHandRunout);
            var rejected = session.ApplyDealerDeal(new HoldemDealCommand(session.SessionId, deal.HandId, deal.WindowId,
                Guid.NewGuid(), before, deal.Street, change));
            Assert.That(rejected.Error, Is.EqualTo(HoldemCommandError.InvalidCardChange));
            Assert.That(session.Version, Is.EqualTo(before));
            Assert.That(session.PendingDeal.WindowId, Is.EqualTo(deal.WindowId));
            Assert.That(session.GetSnapshot(first).BoardCount, Is.Zero);
        }

        [Test]
        public void RejectedOrFallbackDealsNeverClaimAnAcceptedSentence()
        {
            Create(policy: HoldemLlmTableDealer.CreateUtterancePolicy()); Start();
            Speak(2, "하트", designate: false); EndBetting();
            var invalid = SelectSentence(0); // Already a player's hand card.
            Assert.That(room.ApplyDealerDeal(peers[0], invalid).Accepted, Is.False);
            Assert.That(FeedbackPacket(1).dealerSelections, Is.Empty);
            Assert.That(room.ApplyDealerDeal(peers[0], Read().CreateUnchangedCommand(Guid.NewGuid())).Accepted, Is.True);
            Assert.That(FeedbackPacket(1).dealerSelections, Is.Empty);
        }

        [Test]
        public void AcceptedSameCardIsSelectionWithoutClaimingMutation()
        {
            Create(); Start(); EndBetting();
            Assert.That(room.DealUnchanged(peers[0], Read().CreateUnchangedCommand(Guid.NewGuid())).Accepted, Is.True);
            int scheduled = State.GetBoardCard(State.BoardCount - 1).Id;
            Create(policy: HoldemLlmTableDealer.CreateUtterancePolicy()); Start();
            Speak(2, "이미 나올 카드", designate: false); EndBetting();
            Assert.That(room.ApplyDealerDeal(peers[0], SelectSentence(scheduled)).Accepted, Is.True);
            var own = FeedbackPacket(1);
            Assert.That(own.dealerSelections.Single().text, Is.EqualTo("이미 나올 카드"));
            Assert.That(own.dealerSelections.Single().cardChanged, Is.False);
            Assert.That(own.dealerSelections.Single().hasAppliedCard, Is.True);
            Assert.That(own.dealerSelections.Single().card, Is.EqualTo(scheduled));
            Assert.That(own.hasOwnCardChange, Is.False);
        }

        [Test]
        public void ThreeDifferentSpeakersAreAllRevealedAtSettlementAndSurviveReconnect()
        {
            Create(policy: HoldemLlmTableDealer.CreateUtterancePolicy(), pattern: 3); Start();
            for (int i = 0; i < 3; i++)
            {
                Speak(i + 1, "각자 요청 " + i, designate: false); EndBetting();
                Assert.That(room.ApplyDealerDeal(peers[0], SelectSentence(45 - i)).Accepted, Is.True);
                for (int peer = 0; peer < 4; peer++)
                {
                    var items = FeedbackPacket(peer).dealerSelections;
                    Assert.That(items.Length, Is.EqualTo(peer <= i ? 1 : 0));
                    Assert.That(items.All(item => item.speaker == peer + 1), Is.True);
                }
                Resume();
            }
            EndBetting();
            foreach (int peer in Enumerable.Range(0, 4))
            {
                var items = FeedbackPacket(peer).dealerSelections;
                Assert.That(items.Select(item => item.speaker), Is.EqualTo(new[] { 1, 2, 3 }));
                Assert.That(items.Select(item => item.card), Is.EqualTo(new[] { 45, 44, 43 }));
                Assert.That(items.Select(item => item.boardIndex), Is.EqualTo(new[] { 2, 3, 4 }));
            }
            room.Disconnect(peers[3]);
            peers[3] = Guid.NewGuid();
            Assert.That(room.Reconnect(peers[3], admissions[3].ResumeToken).Accepted, Is.True);
            Assert.That(FeedbackPacket(3).dealerSelections.Length, Is.EqualTo(3));
        }

        [Test]
        public void LateReplayCannotAppendSelectionOrConsumeTheNextDealWindow()
        {
            Create(policy: HoldemLlmTableDealer.CreateUtterancePolicy(), pattern: 3); Start();
            Speak(2, "첫 요청", designate: false); EndBetting();
            var first = SelectSentence(45);
            Assert.That(room.ApplyDealerDeal(peers[0], first).Accepted, Is.True);
            Resume();
            Speak(3, "다음 요청", designate: false); EndBetting();
            var pending = Read();
            long version = pending.ExpectedVersion;

            Assert.That(room.ApplyDealerDeal(peers[0], first).Accepted, Is.True, "Stored receipt is idempotent");
            Assert.That(Read().ExpectedVersion, Is.EqualTo(version));
            Assert.That(Read().TargetDeal.WindowId, Is.EqualTo(pending.TargetDeal.WindowId));
            Assert.That(FeedbackPacket(1).dealerSelections.Length, Is.EqualTo(1));
            Assert.That(FeedbackPacket(2).dealerSelections, Is.Empty);

            Assert.That(room.ApplyDealerDeal(peers[0], SelectSentence(44)).Accepted, Is.True);
            Assert.That(FeedbackPacket(1).dealerSelections.Length, Is.EqualTo(1));
            Assert.That(FeedbackPacket(2).dealerSelections.Single().text, Is.EqualTo("다음 요청"));
        }

        [Test]
        public void AppliedCardFeedbackRejectsHiddenOrMismatchedCardsButAllowsOlderSenders()
        {
            Create(policy: HoldemLlmTableDealer.CreateUtterancePolicy()); Start();
            Speak(2, "하트 요청", designate: false); EndBetting();
            Assert.That(room.ApplyDealerDeal(peers[0], SelectSentence()).Accepted, Is.True);
            var packet = FeedbackPacket(1);
            packet.dealerSelections[0].card = 46;
            Assert.Throws<ArgumentException>(() => HoldemDealerSelectionPacketReader.Read(packet));
            packet = FeedbackPacket(1);
            packet.dealerSelections[0].boardIndex = packet.game.board.Length;
            Assert.Throws<ArgumentException>(() => HoldemDealerSelectionPacketReader.Read(packet));
            packet = FeedbackPacket(1);
            packet.dealerSelections[0].hasAppliedCard = false;
            var legacy = HoldemDealerSelectionPacketReader.Read(packet).Single();
            Assert.That(legacy.AppliedCard, Is.Null);
            Assert.That(legacy.Text, Is.EqualTo("하트 요청"));
        }

        [Test]
        public void FeedbackRejectsPrematureOtherSpeakerAndSurvivesOwnerReconnect()
        {
            Create(policy: HoldemLlmTableDealer.CreateUtterancePolicy()); Start();
            Speak(2, "나만 채택 확인", designate: false); EndBetting();
            Assert.That(room.ApplyDealerDeal(peers[0], SelectSentence()).Accepted, Is.True);
            var owner = FeedbackPacket(1);
            var other = FeedbackPacket(0);
            other.dealerSelections = owner.dealerSelections;
            Assert.Throws<ArgumentException>(() => new SpeechHistoryAssembler().Accept(other));
            room.Disconnect(peers[1]);
            var connection = Guid.NewGuid();
            Assert.That(room.Reconnect(connection, admissions[1].ResumeToken).Accepted, Is.True);
            peers[1] = connection;
            Assert.That(FeedbackPacket(1).dealerSelections.Single().text, Is.EqualTo("나만 채택 확인"));
        }
    }
}
