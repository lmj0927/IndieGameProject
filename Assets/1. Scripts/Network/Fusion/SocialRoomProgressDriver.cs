using System;
using Poker.Application;
using Poker.Foundation;

namespace Poker.Online
{
    /// <summary>Shared local/online active-time clock. Edits do not reset a window; disconnection time is excluded.</summary>
    internal sealed class SocialStageClock
    {
        private Guid hand, window;
        private int stage;
        private double elapsed;
        private double? activeAt;
        public double RemainingSeconds { get; private set; }
        public void Suspend() => activeAt = null;
        public bool IsDue(HoldemSnapshot view, double now, SocialFlowTiming timing, out int currentStage, out Guid currentWindow)
        {
            int next = view.IsSettlementPending ? 7
                : view.PendingDeal != null ? 1
                : view.Accusations?.Phase == HoldemAccusationPhase.Collecting ? 2
                : view.Accusations?.Phase == HoldemAccusationPhase.AwaitingVerdicts ? 3
                : view.IsRevealPending && (view.Accusations == null
                    || view.Accusations.Phase == HoldemAccusationPhase.ClosedWithoutClaims
                    || view.Accusations.Phase == HoldemAccusationPhase.ConsequencesApplied) ? 4
                : view.FixedBetting?.Phase == FixedBettingPhase.Proposal ? 5
                : view.FixedBetting?.Phase == FixedBettingPhase.Response ? 6 : 0;
            Guid nextWindow = next == 1 ? view.PendingDeal.WindowId
                : next == 2 || next == 3 || next == 4 ? view.Accusations?.WindowId ?? Guid.Empty
                : next == 5 || next == 6 ? view.FixedBetting.WindowId : Guid.Empty;
            if (stage != next || hand != view.HandId || window != nextWindow)
            { stage = next; hand = view.HandId; window = nextWindow; elapsed = 0; activeAt = now; }
            else
            {
                if (activeAt.HasValue)
                {
                    if (now < activeAt.Value) throw new ArgumentOutOfRangeException(nameof(now), "Clock must not regress.");
                    elapsed += now - activeAt.Value;
                }
                activeAt = now;
            }
            currentStage = stage; currentWindow = window;
            double duration = stage == 1 ? timing.Request : stage == 2 ? timing.Accusation
                : stage == 4 ? timing.Result : stage == 5 ? timing.Proposal : stage == 6 ? timing.Response : 0;
            RemainingSeconds = Math.Max(0, duration - elapsed);
            return stage != 0 && elapsed >= duration;
        }
    }

    /// <summary>Opt-in non-AI prototype pacing. It uses current host authority, never a delayed client packet.</summary>
    public sealed class SocialRoomProgressDriver
    {
        private readonly IHoldemSocialHostPort host;
        private readonly SocialFlowTiming timing;
        private readonly SocialStageClock clock = new SocialStageClock();
        public double RemainingSeconds => clock.RemainingSeconds;

        public SocialRoomProgressDriver(IHoldemSocialHostPort host, SocialFlowTiming timing)
        { this.host = host ?? throw new ArgumentNullException(nameof(host)); this.timing = timing; }

        public bool Tick(double now)
        {
            if (double.IsNaN(now) || double.IsInfinity(now)) throw new ArgumentOutOfRangeException(nameof(now));
            if (!host.TryReadProgress(out var room) || room.Paused || room.Game == null)
            { clock.Suspend(); return false; }
            var view = room.Game;
            if (view.BettingPolicy != HoldemBettingPolicy.SharedAnteFixed)
                throw new InvalidOperationException("Social progression requires shared-ante rules.");
            bool due = clock.IsDue(view, now, timing, out int stage, out Guid window);
            if (host is IHoldemProgressClockHostPort publisher)
            {
                bool timed = stage == HoldemProgressClock.Requests || stage == HoldemProgressClock.Accusations
                    || stage == HoldemProgressClock.Proposal || stage == HoldemProgressClock.Response;
                // One public sample per displayed second, not a full room broadcast every frame.
                int remaining = (int)Math.Min(int.MaxValue, Math.Ceiling(clock.RemainingSeconds) * 1000);
                var sample = timed ? new HoldemProgressClock(view.HandId, window, stage, remaining) : null;
                if (!publisher.PublishProgressClock(sample)) { clock.Suspend(); return false; }
            }
            if (!due) return false;
            Guid hand = view.HandId;
            HoldemRoomReceipt receipt;
            Guid consumedInputWindow = Guid.Empty;
            switch (stage)
            {
                case 1:
                    if (room.Rules.ReceivesUtterances && !host.CloseDealerInput(window))
                    {
                        if (WaitingForConnection()) return false;
                        throw new InvalidOperationException("Could not freeze the dealer input.");
                    }
                    consumedInputWindow = host.ReadPendingTurn().Turn?.SourceUtterances?.WindowId ?? Guid.Empty;
                    receipt = host.DealUnchanged(new HoldemDealCommand(view.SessionId, hand, window,
                        Guid.NewGuid(), view.SessionVersion, view.PendingDeal.Street));
                    break;
                case 2:
                    receipt = host.CloseAccusations(new HoldemRoomAccusationClose(view.SessionId, hand, window,
                        Guid.NewGuid(), view.SessionVersion, view.Street));
                    break;
                case 3:
                    var resolution = host.ResolveAccusations();
                    if (IsConnectionWait(resolution.Error)) { clock.Suspend(); return false; }
                    if (resolution.Error != HoldemRoomError.None || resolution.RecordedCount != 1 || resolution.PendingCount != 0)
                        throw new InvalidOperationException("Recorded dealer evidence is unavailable; no inferred verdict applied.");
                    return true;
                case 4:
                    receipt = host.ResumeAfterReveal(new HoldemRevealCommand(view.SessionId, hand,
                        Guid.NewGuid(), view.SessionVersion, view.Street));
                    break;
                case 7:
                    receipt = host.ResolveSettlement(hand, Guid.NewGuid(), view.SessionVersion);
                    break;
                default:
                    receipt = host.SealFixedBetting(new FixedBettingHostCommand(view.SessionId, hand, window,
                        Guid.NewGuid(), view.SessionVersion, view.Street, view.FixedBetting.Phase));
                    break;
            }
            if (IsConnectionWait(receipt.Error)) { clock.Suspend(); return false; }
            if (!receipt.Accepted) throw new InvalidOperationException("Automatic stage rejected: " + receipt.Error);
            if (stage == 1)
            {
                // Only this neutral prototype's committed request window. Never consume external AI work.
                foreach (var batch in host.ReadPendingUtterances())
                    if (batch.WindowId == consumedInputWindow && !host.AcknowledgeUtteranceBatch(consumedInputWindow))
                        throw new InvalidOperationException("Could not acknowledge committed neutral input.");
            }
            return true;
        }
        private static bool IsConnectionWait(HoldemRoomError error) => error == HoldemRoomError.Paused
            || error == HoldemRoomError.Disconnected || error == HoldemRoomError.UnknownConnection;
        private bool WaitingForConnection()
        {
            if (host.TryReadProgress(out var latest) && !latest.Paused) return false;
            clock.Suspend(); return true;
        }
    }
}
