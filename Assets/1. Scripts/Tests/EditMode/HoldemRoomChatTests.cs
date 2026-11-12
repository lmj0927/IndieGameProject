using System;
using System.Linq;
using NUnit.Framework;
using Poker.Application;
using Poker.Transport;

namespace Poker.Foundation.Tests
{
    public sealed partial class HoldemRoomChatTests
    {
        private HoldemRoom room;
        private Guid[] peers;

        private sealed class StableRandom : IRandomSource
        {
            public int NextInt(int exclusiveMax) => exclusiveMax - 1;
        }

        private static HoldemConfig LlmConfig() => new HoldemConfig(100, 1, 2, HoldemRevealPolicy.PauseAfterCommunityReveal,
            HoldemAccusationMode.Disabled, HoldemDealPolicy.WaitForHost);

        private static HoldemUtterancePolicy PrivateRequests() => new HoldemUtterancePolicy(128, 1,
            HoldemUtteranceSeats.Active | HoldemUtteranceSeats.AllIn, HoldemUtteranceVisibility.OwnOnly,
            HoldemUtteranceBatchRetention.UntilHostAcknowledges);

        private void Create(bool chat = true, HoldemUtterancePolicy policy = null, IRandomSource random = null)
        {
            peers = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
            room = HoldemRoom.Create(Guid.NewGuid(), peers[0], "방장", LlmConfig(), new SeatId(1), random ?? new StableRandom(),
                out _, utterancePolicy: policy ?? PrivateRequests(), chatEnabled: chat);
            for (int i = 1; i < 4; i++) room.Join(peers[i], "참가자 " + i);
            foreach (var peer in peers) room.SetReady(peer, true);
        }

        private void Start()
        {
            var view = room.Read(peers[0]);
            Assert.That(room.StartHand(peers[0], new HoldemStartCommand(view.SessionId, Guid.NewGuid(),
                Guid.NewGuid(), view.Game?.SessionVersion ?? 0)).Accepted, Is.True);
        }

        private HoldemRoomChat ToDealer(int seat, string text, Guid? commandId = null)
        {
            var window = room.ReadUtterances(peers[seat - 1]);
            return new HoldemRoomChat(commandId ?? Guid.NewGuid(), text, window.SessionId, window.HandId, window.WindowId, window.Street);
        }

        private void CloseBetting()
        {
            for (int step = 0; step < 8 && room.Read(peers[0]).Game.CurrentSeat.HasValue; step++)
            {
                var actor = peers[room.Read(peers[0]).Game.CurrentSeat.Value.Value - 1];
                var state = room.Read(actor).Game;
                Assert.That(room.Submit(actor, new HoldemRoomAction(state.SessionId, state.HandId, Guid.NewGuid(),
                    state.SessionVersion, state.LegalActions.CanCheck ? BettingAction.Check() : BettingAction.Call())).Accepted, Is.True);
            }
            Assert.That(room.Read(peers[0]).Game.IsDealPending, Is.True);
        }

        private static string[] Lines(HoldemRoomView view)
            => Enumerable.Range(0, view.Chat.Count).Select(i => view.Chat.GetEntry(i).Text).ToArray();

        [Test]
        public void ChatIsPublicAtAnyTimeAndNeverChangesPokerState()
        {
            Create();
            Assert.That(room.SubmitChat(peers[1], new HoldemRoomChat(Guid.NewGuid(), "안녕하세요")).Accepted, Is.True);
            Start();
            long version = room.Read(peers[0]).Game.SessionVersion;
            Assert.That(room.SubmitChat(peers[2], new HoldemRoomChat(Guid.NewGuid(), "좋은 패 왔으면")).Accepted, Is.True);
            CloseBetting();
            Assert.That(room.SubmitChat(peers[3], new HoldemRoomChat(Guid.NewGuid(), "딜 기다리는 중")).Accepted, Is.True);
            foreach (var peer in peers)
            {
                var view = room.Read(peer);
                CollectionAssert.AreEqual(new[] { "안녕하세요", "좋은 패 왔으면", "딜 기다리는 중" }, Lines(view));
                CollectionAssert.AreEqual(new[] { 2, 3, 4 }, Enumerable.Range(0, 3).Select(i => view.Chat.GetEntry(i).Speaker.Value));
            }
            Assert.That(room.Read(peers[0]).Game.SessionVersion, Is.GreaterThanOrEqualTo(version));
            Assert.That(room.ReadClosedDesignatedBatches(peers[0]).Sum(b => b.Count), Is.Zero,
                "Ordinary chat must never reach the dealer.");
        }

        [Test]
        public void OnlyMarkedLineReachesDealerAndTheMarkStaysWithTheSender()
        {
            Create(); Start();
            Assert.That(room.SubmitChat(peers[1], new HoldemRoomChat(Guid.NewGuid(), "그냥 하는 말")).Accepted, Is.True);
            var request = room.SubmitChat(peers[2], ToDealer(3, "하트 에이스 한 장만"));
            Assert.That(request.Accepted, Is.True);
            Assert.That(request.DealerRequest.Accepted, Is.True);

            foreach (var peer in peers)
            {
                var view = room.Read(peer);
                CollectionAssert.AreEqual(new[] { "그냥 하는 말", "하트 에이스 한 장만" }, Lines(view));
                Assert.That(view.PublicUtterances, Is.Null.Or.Property("Count").EqualTo(0));
                Assert.That(room.ReadUtterances(peer).Count, Is.EqualTo(peer == peers[2] ? 1 : 0));
                var packet = HoldemRoomPacketMapper.Create(view);
                CollectionAssert.AreEqual(new[] { "그냥 하는 말", "하트 에이스 한 장만" }, packet.chat.entries.Select(e => e.text));
                Assert.That(packet.ownUtterances.entries.Length, Is.EqualTo(peer == peers[2] ? 1 : 0));
                Assert.That(packet.hasPublicUtterances && packet.publicUtterances.entries.Length > 0, Is.False);
            }
            CollectionAssert.AreEquivalent(new[] { "sequence", "seat", "text" },
                typeof(HoldemChatEntryPacket).GetFields().Select(f => f.Name), "A chat line must not carry a dealer marker.");

            CloseBetting();
            var batches = room.ReadClosedDesignatedBatches(peers[0]);
            Assert.That(batches.Count, Is.EqualTo(1));
            Assert.That(batches[0].Count, Is.EqualTo(1));
            Assert.That(batches[0].GetEntry(0).Text, Is.EqualTo("하트 에이스 한 장만"));
            Assert.That(batches[0].GetEntry(0).Speaker.Value, Is.EqualTo(3));
        }

        [Test]
        public void RejectedDealerRequestPostsNothingAndAllowsOrdinaryChat()
        {
            Create(); Start();
            Assert.That(room.SubmitChat(peers[1], ToDealer(2, "스페이드 주세요")).Accepted, Is.True);
            long revision = room.Read(peers[0]).Revision;
            var second = room.SubmitChat(peers[1], ToDealer(2, "한 번 더"));
            Assert.That(second.Accepted, Is.False);
            Assert.That(second.Chat, Is.EqualTo(HoldemChatError.DealerRequestRejected));
            Assert.That(second.DealerRequest.Error, Is.EqualTo(HoldemUtteranceError.LimitReached));
            Assert.That(room.Read(peers[0]).Revision, Is.EqualTo(revision));
            Assert.That(room.Read(peers[0]).Chat.Count, Is.EqualTo(1));
            Assert.That(room.SubmitChat(peers[1], new HoldemRoomChat(Guid.NewGuid(), "한 번 더")).Accepted, Is.True);

            var stale = ToDealer(3, "늦은 요청");
            CloseBetting();
            var late = room.SubmitChat(peers[2], stale);
            Assert.That(late.Chat, Is.EqualTo(HoldemChatError.DealerRequestRejected));
            CollectionAssert.AreEqual(new[] { "스페이드 주세요", "한 번 더" }, Lines(room.Read(peers[3])));
        }

        [Test]
        public void RetriedCommandIsIdempotentAndConflictingReuseIsRejected()
        {
            Create(); Start();
            var id = Guid.NewGuid();
            var input = ToDealer(2, "다이아 킹", id);
            var first = room.SubmitChat(peers[1], input);
            Assert.That(first.Accepted, Is.True);
            Assert.That(room.SubmitChat(peers[1], input), Is.SameAs(first));
            Assert.That(room.Read(peers[0]).Chat.Count, Is.EqualTo(1));
            Assert.That(room.SubmitChat(peers[1], new HoldemRoomChat(id, "다른 말")).Chat, Is.EqualTo(HoldemChatError.CommandConflict));
            Assert.That(room.SubmitChat(peers[2], new HoldemRoomChat(id, "다이아 킹")).Chat, Is.EqualTo(HoldemChatError.CommandConflict));
        }

        [Test]
        public void InvalidTextAndDisabledRoomsAreRejectedAndLogIsBounded()
        {
            Create(false);
            Assert.That(room.Read(peers[0]).Chat, Is.Null);
            Assert.That(room.SubmitChat(peers[0], new HoldemRoomChat(Guid.NewGuid(), "안녕")).Chat, Is.EqualTo(HoldemChatError.Disabled));

            Create();
            Assert.That(room.SubmitChat(peers[0], new HoldemRoomChat(Guid.NewGuid(), "  ")).Chat, Is.EqualTo(HoldemChatError.EmptyText));
            Assert.That(room.SubmitChat(peers[0], new HoldemRoomChat(Guid.NewGuid(), "줄\n바꿈")).Chat, Is.EqualTo(HoldemChatError.InvalidText));
            Assert.That(room.SubmitChat(peers[0], new HoldemRoomChat(Guid.NewGuid(),
                new string('가', HoldemRoom.MaximumChatTextLength + 1))).Chat, Is.EqualTo(HoldemChatError.TextTooLong));
            for (int i = 0; i < HoldemRoom.RetainedChatEntries + 5; i++)
                Assert.That(room.SubmitChat(peers[i % 4], new HoldemRoomChat(Guid.NewGuid(), "메시지 " + i)).Accepted, Is.True);
            var chat = room.Read(peers[0]).Chat;
            Assert.That(chat.Count, Is.EqualTo(HoldemRoom.RetainedChatEntries));
            Assert.That(chat.GetEntry(chat.Count - 1).Text, Is.EqualTo("메시지 " + (HoldemRoom.RetainedChatEntries + 4)));
            Assert.That(chat.GetEntry(0).Sequence, Is.EqualTo(6));
        }

        [Test]
        public void BridgeRoutesChatAndDealerFlagWithoutEchoingIt()
        {
            var identities = Enumerable.Range(0, 2).Select(i => new HoldemClientIdentity("참가자" + i)).ToArray();
            var connections = Enumerable.Range(0, 2).Select(_ => Guid.NewGuid()).ToArray();
            using (var bridge = new HoldemHostCommandBridge(identities[0], LlmConfig(), new SeatId(1), new StableRandom(),
                utterancePolicy: PrivateRequests(), seatCapacity: 2, chatEnabled: true))
            {
                for (int i = 0; i < 2; i++)
                {
                    var admit = identities[i].AdmissionRequest();
                    Assert.That(identities[i].TryConfirmAdmission(bridge.Dispatch(connections[i], admit).Response, admit.id), Is.True);
                    var ready = Request(identities[i], "ready"); ready.ready = true;
                    Assert.That(bridge.Dispatch(connections[i], ready).Response.accepted, Is.True);
                }
                var start = Request(identities[0], "start"); start.handId = Guid.NewGuid().ToString("N");
                Assert.That(bridge.Dispatch(connections[0], start).Response.accepted, Is.True);

                Assert.That(bridge.TryCreateProjection(connections[1], out var guest), Is.True);
                var chat = Request(identities[1], "chat");
                chat.text = "클로버 7"; chat.toDealer = true;
                chat.handId = guest.game.handId;
                chat.windowId = guest.ownUtterances.windowId; chat.street = (int)HoldemStreet.Preflop;
                var response = bridge.Dispatch(connections[1], chat).Response;
                Assert.That(response.type, Is.EqualTo("chat-receipt"));
                Assert.That(response.accepted, Is.True, response.chatError + "/" + response.utteranceError);
                Assert.That(response.hasUtteranceReceipt, Is.True);

                var plain = Request(identities[0], "chat"); plain.text = "잘 부탁해요";
                Assert.That(bridge.Dispatch(connections[0], plain).Response.accepted, Is.True);

                Assert.That(bridge.TryCreateProjection(connections[0], out var host), Is.True);
                Assert.That(host.hasChat, Is.True);
                CollectionAssert.AreEqual(new[] { "클로버 7", "잘 부탁해요" }, host.chat.entries.Select(e => e.text));
                Assert.That(host.ownUtterances.entries, Is.Empty);
                Assert.That(bridge.TryCreateProjection(connections[1], out guest), Is.True);
                Assert.That(guest.ownUtterances.entries.Single().text, Is.EqualTo("클로버 7"));

                var malformed = Request(identities[1], "chat"); malformed.text = "창 없음"; malformed.toDealer = true;
                Assert.That(bridge.Dispatch(connections[1], malformed).Response.error, Is.EqualTo("MalformedRequest"));
            }
        }

        private static HoldemWireRequest Request(HoldemClientIdentity identity, string type) => new HoldemWireRequest
        {
            protocol = HoldemRoomPacketMapper.ProtocolVersion, type = type,
            id = Guid.NewGuid().ToString("N"), sessionId = identity.SessionId
        };
    }
}
