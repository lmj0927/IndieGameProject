using System;
using System.Linq;
using NUnit.Framework;
using Poker.Application;
using Poker.Online;
using Poker.Transport;
using UnityEngine;

namespace Poker.Foundation.Tests
{
    public sealed partial class HoldemRoomChatTests
    {
        // Seat 1 receives AA, seat 4 receives 23; the five-card board cannot tie them.
        // Later hands use the ordered deck for the separate request-application assertion.
        private sealed class OpeningHandRandom : IRandomSource
        {
            private readonly System.Collections.Generic.Queue<int> swaps = new System.Collections.Generic.Queue<int>();
            public OpeningHandRandom()
            {
                int[] prefix = { 0, 1, 2, 48, 4, 5, 6, 49, 8, 12, 21, 30, 39, 44 };
                var desired = prefix.Concat(Enumerable.Range(0, 52).Except(prefix)).ToArray();
                var deck = Enumerable.Range(0, 52).ToArray();
                for (int i = 51; i > 0; i--)
                {
                    int at = Array.IndexOf(deck, desired[i]);
                    swaps.Enqueue(at);
                    int old = deck[i]; deck[i] = deck[at]; deck[at] = old;
                }
            }
            public int NextInt(int maximum) => swaps.Count > 0 ? swaps.Dequeue() : maximum - 1;
        }
        private HoldemSnapshot ChatState => room.Read(peers[0]).Game;
        private void ChatAction(BettingAction action)
        {
            var state = ChatState;
            Assert.That(room.Submit(peers[state.CurrentSeat.Value.Value - 1], new HoldemRoomAction(state.SessionId,
                state.HandId, Guid.NewGuid(), state.SessionVersion, action)).Accepted, Is.True);
        }
        private void ChatRunout()
        {
            for (int step = 0; ChatState.Result == null && step < 10; step++)
            {
                var state = ChatState;
                if (state.IsDealPending)
                    Assert.That(room.DealUnchanged(peers[0], room.ReadPendingDealerTurn(peers[0]).Turn
                        .CreateUnchangedCommand(Guid.NewGuid())).Accepted, Is.True);
                else if (state.IsRevealPending)
                    Assert.That(room.ResumeAfterReveal(peers[0], new HoldemRevealCommand(state.SessionId,
                        state.HandId, Guid.NewGuid(), state.SessionVersion, state.Street)).Accepted, Is.True);
                else if (state.IsSettlementPending)
                    Assert.That(room.ResolveSettlement(peers[0], Guid.NewGuid(), state.SessionVersion,
                        HoldemOddChipRule.ClockwiseFromButton).Accepted, Is.True);
                else Assert.Fail($"Unexpected runout phase: street={state.Street}, settlement={state.IsSettlementPending}, actor={state.CurrentSeat}");
            }
            Assert.That(ChatState.Result, Is.Not.Null);
        }
        private void AssertSpectatorCanRequestAndAffectCards(int seat, bool busted)
        {
            var before = room.Read(peers[seat - 1]);
            var ownSeat = Enumerable.Range(0, before.Game.SeatCount).Select(before.Game.GetSeatAt).Single(s => s.Seat.Value == seat);
            Assert.That(ownSeat.Status, Is.EqualTo(busted ? HoldemSeatStatus.Busted : HoldemSeatStatus.Folded));
            Assert.That(before.Game.CanAccuseOnTurn, Is.False);
            Assert.That(before.Game.CurrentSeat?.Value, Is.Not.EqualTo(seat));
            string text = "<b>럭키 세븐</b>";
            Assert.That(before.OwnUtterances.CanSubmit, Is.True);
            var packet = JsonUtility.FromJson<HoldemRoomPacket>(JsonUtility.ToJson(HoldemRoomPacketMapper.Create(before)));
            Assert.That(HoldemUtterancePacketReader.Read(packet).CanSubmit, Is.True);
            if (busted)
            {
                Assert.That(packet.game.seats.Single(s => s.seat == seat).visibleCards, Is.Empty);
                Assert.That(ownSeat.WasDealtIn, Is.False);
            }
            Assert.That(room.SubmitChat(peers[seat - 1], ToDealer(seat, text)).Accepted, Is.True);
            Assert.That(room.SubmitChat(peers[seat - 1], ToDealer(seat, "두 번째 요청")).Accepted, Is.False, "Existing per-street quota stays in force");
            CloseBetting();
            var turn = room.ReadPendingDealerTurn(peers[0]).Turn;
            var entry = turn.SourceUtterances.GetEntry(0);
            Assert.That(entry.Speaker.Value, Is.EqualTo(seat));
            var deal = turn.TargetDeal;
            var command = new HoldemDealCommand(deal.SessionId, deal.HandId, deal.WindowId, Guid.NewGuid(),
                turn.ExpectedVersion, deal.Street, new HoldemCardChange(entry.WindowId, entry.CommandId, entry.Speaker,
                    deal.BoardStart + deal.CardCount - 1, Card.FromId(45), HoldemCardSourceScope.UndealtOutsideCurrentHandRunout));
            Assert.That(room.ApplyDealerDeal(peers[0], command).Accepted, Is.True);
            Assert.That(ChatState.GetBoardCard(ChatState.BoardCount - 1).Id, Is.EqualTo(45));
            for (int i = 0; i < peers.Length; i++)
            {
                var view = room.Read(peers[i]);
                Assert.That(view.DealerSelectionCount, Is.EqualTo(i == seat - 1 ? 1 : 0));
                Assert.That(Lines(view), Does.Contain(text), "The original sentence remains public");
                var afterPacket = JsonUtility.FromJson<HoldemRoomPacket>(JsonUtility.ToJson(HoldemRoomPacketMapper.Create(view)));
                Assert.DoesNotThrow(() => new SpeechHistoryAssembler().Accept(afterPacket));
                Assert.DoesNotThrow(() => HoldemUtterancePacketReader.Read(afterPacket));
                Assert.That(view.Game.LegalActions, Is.Null);
                if (i == seat - 1)
                {
                    Assert.That(view.Game.CanAccuseOnTurn, Is.False);
                    Assert.That(view.OwnCardChange, Is.Not.Null);
                }
            }
        }

        [Test]
        public void FoldedPlayerRequestsThroughPublicChatWithoutRegainingPlayRights()
        {
            Create(policy: HoldemLlmTableDealer.CreateUtterancePolicy()); Start();
            int folded = ChatState.CurrentSeat.Value.Value;
            ChatAction(BettingAction.Fold());
            AssertSpectatorCanRequestAndAffectCards(folded, false);
        }

        [Test]
        public void BustedPlayerCanRequestInNextHandAndChangeItsBoard()
        {
            Create(policy: HoldemLlmTableDealer.CreateUtterancePolicy(), random: new OpeningHandRandom()); Start();
            ChatAction(BettingAction.RaiseTo(100));
            ChatAction(BettingAction.Call());
            while (ChatState.CurrentSeat.HasValue) ChatAction(BettingAction.Fold());
            ChatRunout();
            var busted = Enumerable.Range(0, ChatState.SeatCount).Select(ChatState.GetSeatAt).Where(s => s.Stack == 0).ToArray();
            Assert.That(busted.Length, Is.EqualTo(1), string.Join(", ", Enumerable.Range(0, ChatState.SeatCount).Select(i => $"{ChatState.GetSeatAt(i).Seat}:{ChatState.GetSeatAt(i).Stack}")));
            int seat = busted[0].Seat.Value;
            Start();
            AssertSpectatorCanRequestAndAffectCards(seat, true);
        }
    }
}
