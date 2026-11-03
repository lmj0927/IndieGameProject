using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using Poker.Application;

namespace Poker.Foundation.Tests
{
    public sealed partial class HoldemDealerTurnTests
    {
        private sealed class TimedPort : IHoldemDealerTurnPort
        {
            public Func<HoldemDealerTurnRead> Read;
            public Func<HoldemDealCommand, HoldemRoomReceipt> Complete;
            public readonly List<HoldemDealCommand> Commands = new List<HoldemDealCommand>();
            public HoldemDealerTurnRead ReadPendingTurn() => Read();
            public HoldemRoomReceipt DealUnchanged(HoldemDealCommand command)
            { Commands.Add(command); return Complete(command); }
        }
        private TimedPort TimerPort() => new TimedPort {
            Read = () => room.ReadPendingDealerTurn(peers[0]),
            Complete = command => room.DealUnchanged(peers[0], command)
        };
        private void RestorePeer(int seat)
        {
            peers[seat] = Guid.NewGuid();
            Assert.That(room.Reconnect(peers[seat], admissions[seat].ResumeToken).Accepted, Is.True);
        }

        [Test]
        public void TimeoutStartsAtFirstObservationAndDoesNotConsumeOrInterpretSpeech()
        {
            Create(); Start(); Speak(2, "하트가 필요하네요");
            var port = TimerPort(); double seconds = 0;
            using var timer = new HoldemDealerTurnTimeout(port, TimeSpan.FromSeconds(5), () => TimeSpan.FromSeconds(seconds));
            Assert.That(timer.Poll(), Is.Null); EndBetting();
            seconds = 100; Assert.That(timer.Poll(), Is.Null);
            var first = Read();
            seconds = 104.999; Assert.That(timer.Poll(), Is.Null);
            Assert.That(State.IsDealPending, Is.True); Assert.That(port.Commands, Is.Empty);
            seconds = 105; Assert.That(timer.Poll().Accepted, Is.True);
            Assert.That(State.IsRevealPending, Is.True); Assert.That(State.BoardCount, Is.EqualTo(3));
            Assert.That(State.SessionVersion, Is.EqualTo(first.ExpectedVersion + 1));
            Assert.That(room.ReadClosedDesignatedBatches(peers[0]).Count, Is.EqualTo(1));
            Assert.That(first.SourceUtterances.GetEntry(0).Text, Is.EqualTo("하트가 필요하네요"));
            seconds = 1000; Assert.That(timer.Poll(), Is.Null); Assert.That(port.Commands.Count, Is.EqualTo(1));
        }

        [TestCase(0)]
        [TestCase(1)]
        public void TimeoutPreservesElapsedWaitButExcludesObservedDisconnectAndResumeIntervals(int disconnected)
        {
            Create(); Start(); EndBetting(); var port = TimerPort(); double seconds = 0;
            using var timer = new HoldemDealerTurnTimeout(port, TimeSpan.FromSeconds(5), () => TimeSpan.FromSeconds(seconds));
            timer.Poll(); seconds = 2; timer.Poll();
            room.Disconnect(peers[disconnected]);
            seconds = 100; Assert.That(timer.Poll(), Is.Null);
            seconds = 200; Assert.That(timer.Poll(), Is.Null);
            RestorePeer(disconnected);
            seconds = 300; Assert.That(timer.Poll(), Is.Null);
            seconds = 302.999; Assert.That(timer.Poll(), Is.Null);
            seconds = 303; Assert.That(timer.Poll().Accepted, Is.True);
            Assert.That(port.Commands.Count, Is.EqualTo(1)); Assert.That(State.IsRevealPending, Is.True);
        }

        [TestCase(0)]
        [TestCase(1)]
        public void TimeoutRetriesExactCommandIfDisconnectIsObservedAtCompletion(int disconnected)
        {
            Create(); Start(); EndBetting(); var port = TimerPort(); double seconds = 0;
            port.Complete = command => { room.Disconnect(peers[disconnected]); return room.DealUnchanged(peers[0], command); };
            using var timer = new HoldemDealerTurnTimeout(port, TimeSpan.FromSeconds(5), () => TimeSpan.FromSeconds(seconds));
            timer.Poll(); seconds = 5;
            Assert.That(timer.Poll().Error, Is.EqualTo(disconnected == 0 ? HoldemRoomError.Disconnected : HoldemRoomError.Paused));
            seconds = 500; Assert.That(timer.Poll(), Is.Null);
            RestorePeer(disconnected); port.Complete = command => room.DealUnchanged(peers[0], command);
            Assert.That(timer.Poll().Accepted, Is.True);
            Assert.That(port.Commands.Count, Is.EqualTo(2)); Assert.That(port.Commands[1], Is.SameAs(port.Commands[0]));
            Assert.That(State.IsRevealPending, Is.True);
        }

        [Test]
        public void TimeoutRetriesSameCommandAfterUncertainCompletionWithoutChangingPayload()
        {
            Create(); Start(); EndBetting(); var port = TimerPort(); double seconds = 0;
            port.Complete = _ => throw new IOException("Completion unknown");
            using var timer = new HoldemDealerTurnTimeout(port, TimeSpan.FromSeconds(5), () => TimeSpan.FromSeconds(seconds));
            timer.Poll(); seconds = 5; Assert.Throws<IOException>(() => timer.Poll());
            port.Complete = command => room.DealUnchanged(peers[0], command);
            seconds = 500; Assert.That(timer.Poll().Accepted, Is.True);
            Assert.That(port.Commands.Count, Is.EqualTo(2)); Assert.That(port.Commands[1], Is.SameAs(port.Commands[0]));
        }

        [Test]
        public void TimeoutDoesNotRetryLostSuccessIntoTheNextWindowOrReuseItsElapsedTime()
        {
            Create(); Start(); EndBetting(); var port = TimerPort(); double seconds = 0;
            port.Complete = command => { Assert.That(room.DealUnchanged(peers[0], command).Accepted, Is.True);
                throw new IOException("Reply lost after commit"); };
            using var timer = new HoldemDealerTurnTimeout(port, TimeSpan.FromSeconds(5), () => TimeSpan.FromSeconds(seconds));
            timer.Poll(); seconds = 5; Assert.Throws<IOException>(() => timer.Poll());
            var original = port.Commands[0]; Assert.That(State.IsRevealPending, Is.True);
            Resume(); EndBetting(); var next = Read();
            port.Complete = command => room.DealUnchanged(peers[0], command);
            seconds = 100; Assert.That(timer.Poll(), Is.Null);
            Assert.That(room.DealUnchanged(peers[0], original).Accepted, Is.True);
            Assert.That(Read().TargetDeal.WindowId, Is.EqualTo(next.TargetDeal.WindowId));
            seconds = 104.999; Assert.That(timer.Poll(), Is.Null);
            seconds = 105; Assert.That(timer.Poll().Accepted, Is.True);
            Assert.That(port.Commands.Count, Is.EqualTo(2));
            Assert.That(port.Commands[1].CommandId, Is.Not.EqualTo(original.CommandId));
            Assert.That(port.Commands[1].WindowId, Is.EqualTo(next.TargetDeal.WindowId));
        }

        [Test]
        public void TimeoutReadFailureDoesNotChargeUnknownIntervalOrInvokeFallback()
        {
            Create(); Start(); EndBetting(); var port = TimerPort(); double seconds = 0;
            using var timer = new HoldemDealerTurnTimeout(port, TimeSpan.FromSeconds(5), () => TimeSpan.FromSeconds(seconds));
            timer.Poll(); seconds = 2; timer.Poll();
            port.Read = () => throw new IOException("Cannot observe");
            seconds = 100; Assert.Throws<IOException>(() => timer.Poll());
            port.Read = () => room.ReadPendingDealerTurn(peers[0]);
            seconds = 200; Assert.That(timer.Poll(), Is.Null);
            seconds = 203; Assert.That(timer.Poll().Accepted, Is.True);
            Assert.That(port.Commands.Count, Is.EqualTo(1));
        }

        [Test]
        public void TimeoutDoesNotSkipADealWhenAnotherCompletionWinsDuringSubmit()
        {
            Create(); Start(); EndBetting(); var port = TimerPort(); double seconds = 0;
            port.Complete = command =>
            {
                Assert.That(room.DealUnchanged(peers[0], Read().CreateUnchangedCommand(Guid.NewGuid())).Accepted, Is.True);
                return room.DealUnchanged(peers[0], command);
            };
            using var timer = new HoldemDealerTurnTimeout(port, TimeSpan.FromSeconds(5), () => TimeSpan.FromSeconds(seconds));
            timer.Poll(); seconds = 5;
            var receipt = timer.Poll();
            Assert.That(receipt.Error, Is.EqualTo(HoldemRoomError.CoreRejected));
            Assert.That(receipt.Poker.Error, Is.EqualTo(HoldemCommandError.VersionMismatch));
            Assert.That(State.BoardCount, Is.EqualTo(3));
            Assert.That(State.IsRevealPending, Is.True);
            seconds = 100; Assert.That(timer.Poll(), Is.Null);
            Assert.That(port.Commands.Count, Is.EqualTo(1));
        }

        [Test]
        public void TimeoutDoesNotRepeatDefinitiveRejectionForTheSameWindow()
        {
            Create(); Start(); EndBetting(); var port = TimerPort(); double seconds = 0;
            // Real host-only rejection without altering the game.
            port.Complete = command => room.DealUnchanged(peers[1], command);
            using var timer = new HoldemDealerTurnTimeout(port, TimeSpan.FromSeconds(5), () => TimeSpan.FromSeconds(seconds));
            timer.Poll(); seconds = 5; Assert.That(timer.Poll().Error, Is.EqualTo(HoldemRoomError.HostOnly));
            seconds = 500; Assert.That(timer.Poll(), Is.Null);
            Assert.That(port.Commands.Count, Is.EqualTo(1)); Assert.That(State.IsDealPending, Is.True);
        }

        [Test]
        public void TimeoutDoesNotRepeatAcceptedCommandEvenIfReadSnapshotLags()
        {
            Create(); Start(); EndBetting(); var port = TimerPort(); double seconds = 0;
            var frozen = room.ReadPendingDealerTurn(peers[0]); port.Read = () => frozen;
            using var timer = new HoldemDealerTurnTimeout(port, TimeSpan.FromSeconds(5), () => TimeSpan.FromSeconds(seconds));
            timer.Poll(); seconds = 5; Assert.That(timer.Poll().Accepted, Is.True);
            seconds = 500; Assert.That(timer.Poll(), Is.Null); Assert.That(port.Commands.Count, Is.EqualTo(1));
        }

        [Test]
        public void TimeoutSurfacesInvalidHostAuthorityAndClearsOldElapsedWait()
        {
            Create(); Start(); EndBetting(); var port = TimerPort(); double seconds = 0;
            using var timer = new HoldemDealerTurnTimeout(port, TimeSpan.FromSeconds(5), () => TimeSpan.FromSeconds(seconds));
            timer.Poll(); seconds = 4; timer.Poll();
            port.Read = () => room.ReadPendingDealerTurn(peers[1]); seconds = 100;
            StringAssert.Contains("HostOnly", Assert.Throws<InvalidOperationException>(() => timer.Poll()).Message);
            Assert.That(port.Commands, Is.Empty);
            port.Read = () => room.ReadPendingDealerTurn(peers[0]); seconds = 200; Assert.That(timer.Poll(), Is.Null);
            seconds = 204; Assert.That(timer.Poll(), Is.Null);
            seconds = 205; Assert.That(timer.Poll().Accepted, Is.True);
        }

        [Test]
        public void TimeoutRejectsChangedVersionWithinSameWindowWithoutRenewingItsCommandAuthority()
        {
            Create(); Start(); EndBetting(); var port = TimerPort(); double seconds = 0;
            using var timer = new HoldemDealerTurnTimeout(port, TimeSpan.FromSeconds(5), () => TimeSpan.FromSeconds(seconds));
            timer.Poll(); var original = Read();
            // A malformed integration adapter must not silently replace the captured command version.
            var changed = (HoldemDealerTurn)Activator.CreateInstance(typeof(HoldemDealerTurn),
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[] { original.TargetDeal, original.ExpectedVersion + 1, true, original.SourceUtterances }, null);
            var read = (HoldemDealerTurnRead)Activator.CreateInstance(typeof(HoldemDealerTurnRead),
                BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { HoldemRoomError.None, changed }, null);
            port.Read = () => read; seconds = 1;
            Assert.Throws<InvalidOperationException>(() => timer.Poll());
            seconds = 100; Assert.Throws<InvalidOperationException>(() => timer.Poll());
            Assert.That(port.Commands, Is.Empty); Assert.That(State.SessionVersion, Is.EqualTo(original.ExpectedVersion));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TimeoutStopsAfterOwnerDisposalOrPortClosure(bool portClosed)
        {
            Create(); Start(); EndBetting(); var port = TimerPort(); double seconds = 0;
            using var timer = new HoldemDealerTurnTimeout(port, TimeSpan.FromSeconds(5), () => TimeSpan.FromSeconds(seconds));
            timer.Poll(); seconds = 4; timer.Poll();
            if (portClosed) port.Read = () => throw new ObjectDisposedException("room");
            else timer.Dispose();
            seconds = 500; Assert.Throws<ObjectDisposedException>(() => timer.Poll());
            port.Read = () => room.ReadPendingDealerTurn(peers[0]);
            Assert.Throws<ObjectDisposedException>(() => timer.Poll());
            Assert.That(port.Commands, Is.Empty); Assert.That(State.IsDealPending, Is.True);
        }

        [TestCase(-1)]
        [TestCase(1)]
        public void TimeoutRejectsNegativeOrReversingClockWithoutDealing(double invalid)
        {
            Create(); Start(); EndBetting(); var port = TimerPort(); double seconds = 2;
            using var timer = new HoldemDealerTurnTimeout(port, TimeSpan.FromSeconds(5), () => TimeSpan.FromSeconds(seconds));
            timer.Poll(); seconds = invalid;
            Assert.Throws<InvalidOperationException>(() => timer.Poll()); Assert.That(port.Commands, Is.Empty);
            seconds = 100; Assert.That(timer.Poll(), Is.Null);
            seconds = 105; Assert.That(timer.Poll().Accepted, Is.True);
        }

        [Test]
        public void TimeoutValidatesInputsAndSaturatesLongElapsedTime()
        {
            Create(); Start(); EndBetting(); var port = TimerPort(); TimeSpan now = TimeSpan.Zero;
            Assert.Throws<ArgumentNullException>(() => new HoldemDealerTurnTimeout(null, TimeSpan.FromSeconds(5), () => now));
            Assert.Throws<ArgumentNullException>(() => new HoldemDealerTurnTimeout(port, TimeSpan.FromSeconds(5), null));
            Assert.Throws<ArgumentOutOfRangeException>(() => new HoldemDealerTurnTimeout(port, TimeSpan.Zero, () => now));
            using var timer = new HoldemDealerTurnTimeout(port, TimeSpan.MaxValue, () => now);
            timer.Poll(); now = TimeSpan.FromTicks(long.MaxValue - 1); Assert.That(timer.Poll(), Is.Null);
            now = TimeSpan.MaxValue; Assert.That(timer.Poll().Accepted, Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TimeoutDoesNotTreatDisabledInputOrSkippedBettingAsFailureAndWaitsEachAllInStreet(bool speech)
        {
            Create(speech); Start(); Act(shove: true); EndBetting(); var port = TimerPort(); double seconds = 0;
            using var timer = new HoldemDealerTurnTimeout(port, TimeSpan.FromSeconds(5), () => TimeSpan.FromSeconds(seconds));
            for (int i = 0; i < 3; i++)
            {
                Assert.That(Read().InputKind, Is.EqualTo(!speech ? HoldemDealerInputKind.Disabled : i == 0
                    ? HoldemDealerInputKind.ClosedBettingWindow : HoldemDealerInputKind.NoBettingWindow));
                Assert.That(timer.Poll(), Is.Null); seconds += 5;
                Assert.That(timer.Poll().Accepted, Is.True); Resume();
            }
            Assert.That(State.Result, Is.Not.Null); Assert.That(timer.Poll(), Is.Null);
            Assert.That(port.Commands.Count, Is.EqualTo(3));
        }

        [Test]
        public void TimeoutResetsWhenAnotherOwnerFinishesTheWindowOrStartsAnotherHand()
        {
            Create(); Start(); EndBetting(); var port = TimerPort(); double seconds = 0;
            using var timer = new HoldemDealerTurnTimeout(port, TimeSpan.FromSeconds(5), () => TimeSpan.FromSeconds(seconds));
            timer.Poll(); seconds = 4; timer.Poll();
            Assert.That(room.DealUnchanged(peers[0], Read().CreateUnchangedCommand(Guid.NewGuid())).Accepted, Is.True);
            seconds = 100; Assert.That(timer.Poll(), Is.Null);
            for (int i = 0; i < 3; i++)
            {
                Resume(); EndBetting();
                if (i < 2) Assert.That(room.DealUnchanged(peers[0], Read().CreateUnchangedCommand(Guid.NewGuid())).Accepted, Is.True);
            }
            Assert.That(State.Result, Is.Not.Null); Start(); EndBetting();
            seconds = 200; Assert.That(timer.Poll(), Is.Null);
            seconds = 204; Assert.That(timer.Poll(), Is.Null);
            seconds = 205; Assert.That(timer.Poll().Accepted, Is.True);
            Assert.That(port.Commands.Count, Is.EqualTo(1)); Assert.That(State.HandNumber, Is.EqualTo(2));
        }

        [Test]
        public void TimeoutDoesNotTransferWaitToADifferentSessionAndRejectsReentrantPoll()
        {
            Create(); Start(); EndBetting(); var port = TimerPort(); double seconds = 0;
            using var timer = new HoldemDealerTurnTimeout(port, TimeSpan.FromSeconds(5), () => TimeSpan.FromSeconds(seconds));
            timer.Poll(); seconds = 4; timer.Poll(); var previousSession = State.SessionId;
            Create(); Start(); EndBetting(); Assert.That(State.SessionId, Is.Not.EqualTo(previousSession));
            seconds = 200; Assert.That(timer.Poll(), Is.Null);
            seconds = 204; Assert.That(timer.Poll(), Is.Null);
            port.Complete = command => { Assert.Throws<InvalidOperationException>(() => timer.Poll());
                return room.DealUnchanged(peers[0], command); };
            seconds = 205; Assert.That(timer.Poll().Accepted, Is.True); Assert.That(port.Commands.Count, Is.EqualTo(1));
        }
    }
}
