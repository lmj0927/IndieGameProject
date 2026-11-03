using System;
using Poker.Application;
using Poker.Foundation;

namespace Poker.Online
{
    /// <summary>Explicit pacing choices: street floors or individual action turns, never both.</summary>
    public readonly struct SequentialFlowTiming
    {
        public readonly double Preflop, Flop, Turn, River, Reveal, ActionTurn;
        public readonly SequentialConversationClockOrigin Origin;
        public SequentialFlowTiming(double preflop, double flop, double turn, double river, double reveal,
            SequentialConversationClockOrigin origin, double actionTurn = 0)
        {
            foreach (double value in new[] { preflop, flop, turn, river, reveal, actionTurn })
                if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
                    throw new ArgumentOutOfRangeException(nameof(preflop));
            if (origin != SequentialConversationClockOrigin.StreetEntry && origin != SequentialConversationClockOrigin.AfterBetting
                && origin != SequentialConversationClockOrigin.ActionTurn)
                throw new ArgumentOutOfRangeException(nameof(origin), "Choose how the conversation clock relates to betting.");
            if (origin == SequentialConversationClockOrigin.ActionTurn
                ? actionTurn <= 0 || preflop != 0 || flop != 0 || turn != 0 || river != 0 : actionTurn != 0)
                throw new ArgumentException("Choose either street conversation floors or a positive action-turn limit.");
            Preflop = preflop; Flop = flop; Turn = turn; River = river; Reveal = reveal; Origin = origin;
            ActionTurn = actionTurn;
        }
        public double ConversationFor(HoldemStreet street) => street == HoldemStreet.Preflop ? Preflop
            : street == HoldemStreet.Flop ? Flop : street == HoldemStreet.Turn ? Turn
            : street == HoldemStreet.River ? River : throw new ArgumentOutOfRangeException(nameof(street));
        public static SequentialFlowTiming FromProfile(SequentialPacingProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            return new SequentialFlowTiming(profile.PreflopMilliseconds / 1000.0, profile.FlopMilliseconds / 1000.0,
                profile.TurnMilliseconds / 1000.0, profile.RiverMilliseconds / 1000.0,
                profile.RevealMilliseconds / 1000.0, profile.Origin, profile.ActionTurnMilliseconds / 1000.0);
        }
    }

    /// <summary>
    /// One host-owned pacing driver for local/LAN/Fusion. Only a new action turn resets a turn clock.
    /// External dealer work stays external; only an explicit neutral preview may deal unchanged.
    /// </summary>
    public sealed class SequentialProgressDriver
    {
        private readonly ISequentialProgressHostPort host;
        private readonly SequentialFlowTiming timing;
        private readonly bool neutralDealer;
        private Guid hand, frozenDeal;
        private HoldemStreet street;
        private int phase;
        private long turnHandVersion;
        private SeatId? turnSeat;
        private double elapsed, lastNow;
        private double? activeAt;
        private bool observedTime;
        private double? remainingSeconds;
        public double? RemainingSeconds
        {
            get
            {
                if (timing.Origin != SequentialConversationClockOrigin.ActionTurn || phase != 3) return remainingSeconds;
                return host.TryReadSequentialProgress(out var game, out _) && game != null
                    && game.HandId == hand && game.HandVersion == turnHandVersion && game.CurrentSeat == turnSeat
                    ? remainingSeconds : null;
            }
            private set => remainingSeconds = value;
        }

        public SequentialProgressDriver(ISequentialProgressHostPort host, SequentialFlowTiming timing,
            bool neutralDealerPreview = false)
        {
            this.host = host ?? throw new ArgumentNullException(nameof(host));
            // A default struct must not bypass the required origin policy.
            this.timing = new SequentialFlowTiming(timing.Preflop, timing.Flop, timing.Turn, timing.River, timing.Reveal,
                timing.Origin, timing.ActionTurn);
            neutralDealer = neutralDealerPreview;
        }

        public void Suspend() => activeAt = null;

        public bool Tick(double now)
        {
            if (double.IsNaN(now) || double.IsInfinity(now) || observedTime && now < lastNow)
                throw new ArgumentOutOfRangeException(nameof(now), "Clock must be finite and monotone.");
            lastNow = now; observedTime = true;
            if (!host.TryReadSequentialProgress(out var game, out bool paused) || paused || game == null)
            { Suspend(); return false; }
            if (game.BettingPolicy != HoldemBettingPolicy.SharedAnteSequential)
                throw new InvalidOperationException("Sequential progression requires sequential shared-ante rules.");

            bool actionTurns = timing.Origin == SequentialConversationClockOrigin.ActionTurn;
            int nextPhase = game.Result != null || game.IsOver || game.IsSettlementPending ? 0
                : game.IsRevealPending ? 2
                : actionTurns ? (game.CurrentSeat.HasValue ? 3 : 1)
                : timing.Origin == SequentialConversationClockOrigin.StreetEntry || !game.CurrentSeat.HasValue ? 1 : 0;
            if (hand != game.HandId || street != game.Street || phase != nextPhase
                || nextPhase == 3 && (turnHandVersion != game.HandVersion || turnSeat != game.CurrentSeat))
            {
                hand = game.HandId; street = game.Street; phase = nextPhase;
                turnHandVersion = game.HandVersion;
                turnSeat = game.CurrentSeat;
                elapsed = 0; activeAt = now;
            }
            else
            {
                if (activeAt.HasValue) elapsed += now - activeAt.Value;
                activeAt = now;
            }
            double duration = phase == 0 ? 0 : phase == 2 ? timing.Reveal
                : phase == 3 ? timing.ActionTurn : timing.ConversationFor(game.Street);
            RemainingSeconds = phase == 3 || phase == 1 && !actionTurns ? Math.Max(0, duration - elapsed) : (double?)null;
            if (host is IHoldemProgressClockHostPort publisher)
            {
                // Publish at second boundaries, not every frame; a short configured stage must
                // never be rounded above its immutable room profile.
                int remaining = (int)Math.Min(int.MaxValue, Math.Min(duration * 1000,
                    Math.Ceiling(RemainingSeconds ?? 0) * 1000));
                var sample = RemainingSeconds.HasValue ? new HoldemProgressClock(game.HandId, game.HandId,
                    HoldemProgressClock.SequentialPreflop + (int)game.Street, remaining,
                    phase == 3 ? game.CurrentSeat : null, phase == 3 ? game.HandVersion : 0) : null;
                if (!publisher.PublishProgressClock(sample)) { Suspend(); return false; }
            }
            if (phase == 0 || elapsed < duration) return false;
            if (phase == 3)
            {
                bool expired = host.TryExpireTurn(game.HandId, game.HandVersion, game.CurrentSeat.Value);
                if (expired) { RemainingSeconds = null; Suspend(); }
                return expired;
            }
            if (phase == 2)
                return Accept(host.ResumeAfterReveal(new HoldemRevealCommand(game.SessionId, game.HandId,
                    Guid.NewGuid(), game.SessionVersion, game.Street)));

            // Legacy street floors may expire during betting, but never choose an action.
            if (game.CurrentSeat.HasValue) return false;
            if (game.IsFinalConversationPending)
                return Accept(host.CloseFinalConversation(new FinalConversationCommand(game.SessionId,
                    game.HandId, Guid.NewGuid(), game.SessionVersion)));
            if (game.PendingDeal == null) return false;
            bool closedNow = false;
            if (frozenDeal != game.PendingDeal.WindowId)
            {
                if (!host.CloseDealerInput(game.PendingDeal.WindowId))
                {
                    if (WaitingForAuthority()) return false;
                    throw new InvalidOperationException("Could not freeze sequential dealer input.");
                }
                frozenDeal = game.PendingDeal.WindowId; closedNow = true;
            }
            if (!neutralDealer) return closedNow;
            var read = host.ReadPendingTurn();
            if (read.Error != HoldemRoomError.None)
            {
                if (WaitingForAuthority()) return false;
                throw new InvalidOperationException("Sequential dealer input is unavailable: " + read.Error);
            }
            if (read.Turn == null) throw new InvalidOperationException("The closed sequential dealer batch is missing.");
            var sourceWindow = read.Turn.SourceUtterances?.WindowId ?? Guid.Empty;
            bool dealt = Accept(host.DealUnchanged(new HoldemDealCommand(game.SessionId, game.HandId,
                game.PendingDeal.WindowId, Guid.NewGuid(), game.SessionVersion, game.PendingDeal.Street)));
            if (dealt && host is IHoldemHostDealerPort inputHost)
                foreach (var batch in inputHost.ReadPendingUtterances())
                    if (batch.WindowId == sourceWindow && !inputHost.AcknowledgeUtteranceBatch(sourceWindow))
                        throw new InvalidOperationException("Could not acknowledge the neutral preview's committed input.");
            if (dealt && host is IHoldemEliminatedSpeechHostPort eliminatedHost)
                foreach (var batch in eliminatedHost.ReadClosedEliminatedBatches())
                    if (batch.HandId == game.HandId && batch.Street == game.Street
                        && !eliminatedHost.AcknowledgeEliminatedBatch(batch.WindowId))
                        throw new InvalidOperationException("Could not acknowledge the neutral preview's eliminated input.");
            return dealt;
        }

        private bool Accept(HoldemRoomReceipt receipt)
        {
            if (receipt.Accepted) return true;
            if (WaitingForAuthority()) return false;
            throw new InvalidOperationException("Sequential host progression rejected: " + receipt.Error
                + (receipt.Poker == null ? "" : "/" + receipt.Poker.Error));
        }
        private bool WaitingForAuthority()
        {
            if (host.TryReadSequentialProgress(out _, out bool paused) && !paused) return false;
            Suspend(); return true;
        }
    }
}
