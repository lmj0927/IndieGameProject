using System;
using System.Linq;
using NUnit.Framework;
using Poker.Application;
using Poker.Online;
using Poker.Transport;

namespace Poker.Foundation.Tests
{
    public sealed class HoldemHostCommandBridgeTests
    {
        private HoldemHostCommandBridge bridge;
        private HoldemClientIdentity[] identities;
        private Guid[] connections;

        [SetUp]
        public void SetUp()
        { CreateRoom(2); }

        [Test]
        public void EscalatingRoomRejectsAnOlderFixedBettingClientBeforeBindingASeat()
        {
            var hostIdentity = new HoldemClientIdentity("방장");
            using (var social = new HoldemHostCommandBridge(hostIdentity,
                HoldemConfig.CreateSharedAnteFixed(100, 5, 5, HoldemAccusationMode.Disabled, new long[] { 5, 8 }, true),
                new SeatId(1), new StableRandom()))
            {
                Guid connection = Guid.NewGuid();
                var request = hostIdentity.AdmissionRequest(); request.supportsAnteProgression = false;
                Assert.That(social.Dispatch(connection, request).Response.error, Is.EqualTo("ClientUpgradeRequired"));
                Assert.That(social.TryGetBinding(connection, out _), Is.False);
                request = hostIdentity.AdmissionRequest();
                Assert.That(social.Dispatch(connection, request).Response.accepted, Is.True);
                Assert.That(social.TryGetBinding(connection, out var binding), Is.True);
                Assert.That(binding.IsHost, Is.True);
            }
        }

        [Test]
        public void SpectatorRequestRoomRejectsOlderClientBeforeBinding()
        {
            var host = new HoldemClientIdentity("Host");
            using var updated = new HoldemHostCommandBridge(host, HoldemLlmTableDealer.CreateConfig(),
                new SeatId(1), new StableRandom(), utterancePolicy: HoldemLlmTableDealer.CreateUtterancePolicy(), chatEnabled: true);
            var connection = Guid.NewGuid();
            var old = host.AdmissionRequest(); old.supportsSpectatorDealerRequests = false;
            Assert.That(updated.Dispatch(connection, old).Response.error, Is.EqualTo("ClientUpgradeRequired"));
            Assert.That(updated.TryGetBinding(connection, out _), Is.False);
            Assert.That(updated.Dispatch(connection, host.AdmissionRequest()).Response.accepted, Is.True);
        }

        private void CreateRoom(int capacity)
        {
            bridge?.Dispose();
            identities = Enumerable.Range(0, capacity).Select(i => new HoldemClientIdentity("참가자" + i)).ToArray();
            connections = Enumerable.Range(0, capacity).Select(i => Guid.NewGuid()).ToArray();
            bridge = new HoldemHostCommandBridge(identities[0], new HoldemConfig(100, 1, 2),
                new SeatId(1), new StableRandom(), seatCapacity: capacity);
            for (int i = 0; i < capacity; i++) Admit(i);
        }

        [TearDown]
        public void TearDown() => bridge?.Dispose();

        [Test]
        public void HeadlessDealerWithoutAnAnalyzerStillDealsTheFlop()
        {
            bridge.Dispose();
            identities = new[] { new HoldemClientIdentity("Host"), new HoldemClientIdentity("Guest") };
            bridge = new HoldemHostCommandBridge(identities[0], HoldemLlmTableDealer.CreateConfig(),
                new SeatId(1), new StableRandom(), utterancePolicy: HoldemLlmTableDealer.CreateUtterancePolicy(),
                seatCapacity: 2, chatEnabled: true);
            for (int i = 0; i < 2; i++) { Admit(i); Ready(i); }
            var start = Request(identities[0], "start");
            start.handId = Guid.NewGuid().ToString("N");
            AssertAccepted(bridge.Dispatch(connections[0], start));

            for (int i = 0; i < 2; i++)
            {
                Assert.That(bridge.TryCreateProjection(connections[0], out var host), Is.True);
                int actor = host.game.currentSeat - 1;
                Assert.That(bridge.TryCreateProjection(connections[actor], out var own), Is.True);
                var act = Request(identities[actor], "act");
                act.handId = own.game.handId;
                act.version = own.game.version;
                act.action = own.game.legal.check ? 1 : 2;
                AssertAccepted(bridge.Dispatch(connections[actor], act));
            }

            Assert.That(bridge.TryCreateProjection(connections[0], out var before), Is.True);
            Assert.That(before.game.board, Is.Empty);
            using (var dealer = new HoldemLlmTableDealer(bridge, null))
            {
                dealer.Tick();
                dealer.Tick();
            }
            Assert.That(bridge.TryCreateProjection(connections[1], out var after), Is.True);
            Assert.That(after.game.street, Is.EqualTo((int)HoldemStreet.Flop));
            Assert.That(after.game.board, Has.Length.EqualTo(3));
            Assert.That(after.game.board.Distinct().Count(), Is.EqualTo(3));
            Assert.That(after.game.seats[0].visibleCards, Is.Empty);
        }

        [TestCase(2), TestCase(3), TestCase(4)]
        public void BoardPreviewDealsOnlyOneStreetAndKeepsHoleCardsPrivate(int capacity)
        {
            CreateRoom(capacity);
            for (int i = 0; i < capacity; i++) Ready(i);
            var start = Request(identities[0], "start");
            start.handId = Guid.NewGuid().ToString("N");
            AssertAccepted(bridge.Dispatch(connections[0], start));

            foreach (int expected in new[] { 3, 4, 5 })
            {
                Assert.That(bridge.AdvanceBoardPreview(), Is.True);
                Assert.That(bridge.TryCreateProjection(connections[0], out var host), Is.True);
                Assert.That(bridge.TryCreateProjection(connections[1], out var guest), Is.True);
                Assert.That(host.game.board, Has.Length.EqualTo(expected));
                Assert.That(guest.game.board, Is.EqualTo(host.game.board));
                Assert.That(guest.game.seats[0].visibleCards, Is.Empty);
                Assert.That(guest.game.seats[1].visibleCards, Has.Length.EqualTo(2));
            }
            Assert.That(bridge.AdvanceBoardPreview(), Is.False);
        }

        [TestCase(2), TestCase(3), TestCase(4)]
        public void DispatchUsesCallerBoundSeatsAndPrivateProjectionsThroughPayout(int capacity)
        {
            CreateRoom(capacity);
            for (int i = 0; i < capacity; i++) Ready(i);
            var start = Request(identities[0], "start");
            start.handId = Guid.NewGuid().ToString("N");
            AssertAccepted(bridge.Dispatch(connections[0], start));

            for (int viewer = 0; viewer < capacity; viewer++)
            {
                Assert.That(bridge.TryCreateProjection(connections[viewer], out var packet), Is.True);
                Assert.That(packet.viewerSeat, Is.EqualTo(viewer + 1));
                Assert.That(packet.game.seats.Sum(s => s.visibleCards.Length), Is.EqualTo(2));
                Assert.That(packet.game.seats[viewer].visibleCards.Length, Is.EqualTo(2));
                for (int opponent = 0; opponent < capacity; opponent++)
                    if (opponent != viewer) Assert.That(packet.game.seats[opponent].visibleCards, Is.Empty);
            }

            HoldemRoomPacket host = null;
            for (int step = 0; step < 40; step++)
            {
                Assert.That(bridge.TryCreateProjection(connections[0], out host), Is.True);
                if (host.game.hasResult) break;
                int actor = host.game.currentSeat - 1;
                Assert.That(bridge.TryCreateProjection(connections[actor], out var own), Is.True);
                var act = Request(identities[actor], "act");
                act.handId = own.game.handId; act.version = own.game.version;
                act.action = own.game.legal.check ? 1 : 2;
                AssertAccepted(bridge.Dispatch(connections[actor], act));
            }

            Assert.That(host.game.hasResult, Is.True);
            Assert.That(host.game.result.kind, Is.EqualTo((int)HoldemResultKind.Showdown));
            Assert.That(host.game.result.pots.Sum(p => p.payouts.Sum(a => a.amount)), Is.EqualTo(host.game.result.pot));
            Assert.That(host.game.seats.Sum(s => s.stack), Is.EqualTo(100 * capacity));
        }

        [TestCase(2), TestCase(3), TestCase(4)]
        public void DisconnectAndPrivateAdmissionKeyRestoreSameSeatWithoutLeakingOtherCards(int capacity)
        {
            CreateRoom(capacity);
            for (int i = 0; i < capacity; i++) Ready(i);
            var start = Request(identities[0], "start"); start.handId = Guid.NewGuid().ToString("N");
            AssertAccepted(bridge.Dispatch(connections[0], start));
            Assert.That(bridge.TryCreateProjection(connections[1], out var before), Is.True);

            Guid oldConnection = connections[1];
            Assert.That(bridge.Disconnect(oldConnection), Is.EqualTo(HoldemRoomError.None));
            Assert.That(bridge.TryCreateProjection(oldConnection, out _), Is.False);
            Assert.That(bridge.TryCreateProjection(connections[0], out var paused), Is.True);
            Assert.That(paused.paused, Is.True);

            connections[1] = Guid.NewGuid();
            var request = identities[1].AdmissionRequest();
            var resumed = bridge.Dispatch(connections[1], request);
            Assert.That(resumed.CloseConnection, Is.False);
            Assert.That(identities[1].TryConfirmAdmission(resumed.Response, request.id), Is.True);
            Assert.That(resumed.Response.seat, Is.EqualTo(2));
            Assert.That(resumed.CallerProjection.paused, Is.False);
            Assert.That(resumed.CallerProjection.game.version, Is.EqualTo(before.game.version));
            Assert.That(resumed.CallerProjection.game.seats[1].visibleCards,
                Is.EqualTo(before.game.seats[1].visibleCards));
            for (int i = 0; i < capacity; i++)
                if (i != 1) Assert.That(resumed.CallerProjection.game.seats[i].visibleCards, Is.Empty);
        }

        [TestCase(2), TestCase(3), TestCase(4)]
        public void FullRoomDoesNotGiveADisconnectedSeatToANewIdentity(int capacity)
        {
            CreateRoom(capacity);
            bridge.Disconnect(connections[1]);
            var intruder = new HoldemClientIdentity("새 참가자");
            var denied = bridge.Dispatch(Guid.NewGuid(), intruder.AdmissionRequest());
            Assert.That(denied.Response.accepted, Is.False);
            Assert.That(denied.Response.error, Is.EqualTo("Full"));
            Assert.That(denied.CallerProjection, Is.Null);
            connections[1] = Guid.NewGuid();
            Admit(1);
            Assert.That(identities[1].Seat, Is.EqualTo(2));
        }

        [Test]
        public void InvalidEnvelopeIsFatalAndAdmissionConfirmationRejectsForeignResponse()
        {
            var fatal = bridge.Dispatch(Guid.NewGuid(), new HoldemWireRequest
            { protocol = 999, type = "ready", id = Guid.NewGuid().ToString("N") });
            Assert.That(fatal.CloseConnection, Is.True);
            Assert.That(fatal.Response, Is.Null);
            Assert.That(fatal.CallerProjection, Is.Null);

            var identity = new HoldemClientIdentity("새 손님");
            var request = identity.AdmissionRequest();
            var foreign = new HoldemWireResponse
            {
                protocol = HoldemRoomPacketMapper.ProtocolVersion,
                type = "admitted", id = Guid.NewGuid().ToString("N"), accepted = true,
                sessionId = Guid.NewGuid().ToString("N"), seat = 2
            };
            Assert.That(identity.TryConfirmAdmission(foreign, request.id), Is.False);
            Assert.That(identity.SessionId, Is.Null);
            Assert.That(identity.Seat, Is.Zero);
        }

        private void Admit(int index)
        {
            var request = identities[index].AdmissionRequest();
            var result = bridge.Dispatch(connections[index], request);
            Assert.That(result.CloseConnection, Is.False);
            Assert.That(result.Response.type, Is.EqualTo("admitted"));
            Assert.That(result.CallerProjection, Is.Not.Null);
            Assert.That(identities[index].TryConfirmAdmission(result.Response, request.id), Is.True);
        }

        [Test]
        public void SharedAdmissionRequiresCapabilityWithoutTakingOrReplacingASeat()
        {
            bridge.Dispose();
            // These are fresh identities: they must not carry the previous room's session.
            identities = new[] { new HoldemClientIdentity("방장"), new HoldemClientIdentity("손님") };
            bridge = new HoldemHostCommandBridge(identities[0], HoldemConfig.CreateSharedAnteFixed(100, 5, 5,
                HoldemAccusationMode.Disabled), new SeatId(1), new StableRandom(), seatCapacity: 2);
            var request = identities[0].AdmissionRequest(); request.supportsFixedBetting = false;
            Assert.That(bridge.Dispatch(connections[0], request).Response.error, Is.EqualTo("ClientUpgradeRequired"));
            Admit(0);
            request = identities[1].AdmissionRequest(); request.supportsFixedBetting = false;
            Assert.That(bridge.Dispatch(connections[1], request).Response.error, Is.EqualTo("ClientUpgradeRequired"));
            Assert.That(bridge.TryCreateProjection(connections[0], out var before), Is.True);
            Assert.That(before.members.Length, Is.EqualTo(1));
            Admit(1); bridge.Disconnect(connections[1]); connections[1] = Guid.NewGuid();
            request = identities[1].AdmissionRequest(); request.supportsFixedBetting = false;
            Assert.That(bridge.Dispatch(connections[1], request).Response.error, Is.EqualTo("ClientUpgradeRequired"));
            Admit(1);
            Assert.That(identities[1].Seat, Is.EqualTo(2));
            Assert.That(bridge.TryCreateProjection(connections[1], out var resumed), Is.True);
            Assert.That(resumed.rules.bettingPolicy, Is.EqualTo((int)HoldemBettingPolicy.SharedAnteFixed));
        }

        private void Ready(int index)
        {
            var request = Request(identities[index], "ready"); request.ready = true;
            AssertAccepted(bridge.Dispatch(connections[index], request));
        }

        private static HoldemWireRequest Request(HoldemClientIdentity identity, string type) => new HoldemWireRequest
        {
            protocol = HoldemRoomPacketMapper.ProtocolVersion,
            type = type,
            id = Guid.NewGuid().ToString("N"),
            sessionId = identity.SessionId
        };

        private static void AssertAccepted(HoldemHostDispatchResult result)
        {
            Assert.That(result.CloseConnection, Is.False);
            Assert.That(result.Response.type, Is.EqualTo("receipt"));
            Assert.That(result.Response.accepted, Is.True, result.Response.error);
        }

        private sealed class StableRandom : IRandomSource
        {
            private int calls;
            public int NextInt(int exclusiveMax) => ++calls % 52 == 0 ? 3 : exclusiveMax - 1;
        }
    }
}
