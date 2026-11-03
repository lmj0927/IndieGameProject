using System;
using Poker.Foundation;

namespace Poker.Application
{
    public sealed partial class HoldemDealerTurnCoordinator
    {
        private readonly IHoldemDealerDealApplicationPort applicationPort;
        private readonly IHoldemDealerSelectionPolicy selectionPolicy;
        private readonly TimeSpan? maximumActiveWait;
        private readonly TimeSpan minimumRevealDelay;
        private readonly Func<TimeSpan> clock;
        private HoldemDealerInterpretationBatch interpretations;
        private TimeSpan elapsed;
        private TimeSpan? lastTime, lastActiveAt;

        public HoldemDealerTurnCoordinator(IHoldemDealerDealApplicationPort port, IHoldemDealerSelectionPolicy selectionPolicy,
            TimeSpan? maximumActiveWait = null, Func<TimeSpan> monotonicClock = null,
            TimeSpan minimumRevealDelay = default) : this((IHoldemDealerTurnPort)port)
        {
            applicationPort = port;
            this.selectionPolicy = selectionPolicy ?? throw new ArgumentNullException(nameof(selectionPolicy));
            if (maximumActiveWait.HasValue != (monotonicClock != null)
                || maximumActiveWait.HasValue && maximumActiveWait.Value <= TimeSpan.Zero)
                throw new ArgumentException("A positive active wait and monotonic clock must be supplied together.");
            if (minimumRevealDelay < TimeSpan.Zero || minimumRevealDelay > TimeSpan.Zero
                && (!maximumActiveWait.HasValue || minimumRevealDelay > maximumActiveWait.Value))
                throw new ArgumentOutOfRangeException(nameof(minimumRevealDelay));
            this.maximumActiveWait = maximumActiveWait; clock = monotonicClock;
            this.minimumRevealDelay = minimumRevealDelay;
        }

        /// <summary>Worker-safe, host-only. First completion wins; only an identical result can be re-posted.</summary>
        public bool TryPostInterpretations(HoldemDealerWork work, HoldemDealerInterpretationBatch result)
        {
            lock (gate)
            {
                if (applicationPort == null || disposed || finished || work == null || !ReferenceEquals(work, current)
                    || result == null || !result.MatchesWork(work)) return false;
                if (posted) return interpretations != null && interpretations.Matches(result);
                interpretations = result; posted = true; return true;
            }
        }

        /// <summary>
        /// Explicit owner recovery after a definitive rejection/invalid selection, not after an uncertain attempt.
        /// The next Poll emits fresh work; old workers cannot replace the chosen recovery.
        /// No fallback policy, no card change and no no-cheating record are implied.
        /// </summary>
        public bool ReopenRejectedTurn()
        {
            CheckOwner();
            if (polling) throw new InvalidOperationException("Cannot reopen during polling.");
            lock (gate)
                if (disposed || !finished || !rejected || uncertain) return false;
            polling = true;
            try
            {
                var read = port.ReadPendingTurn();
                lock (gate)
                {
                    if (read == null || read.Error != HoldemRoomError.None || !SameWindow(current, read.Turn)
                        || current.ExpectedVersion != read.Turn.ExpectedVersion) return false;
                    Clear(); return true;
                }
            }
            finally { polling = false; }
        }

        private HoldemDealCommand CreateCommand(HoldemDealerWork work, HoldemDealerInterpretationBatch result)
        {
            HoldemCardChange change = null;
            if (result != null)
            {
                var decision = selectionPolicy.Select(work, result)
                    ?? throw new InvalidOperationException("No explicit dealer selection was supplied.");
                if (decision.Kind == HoldemDealerSelectionKind.Change)
                {
                    var interpreted = result.Find(decision.UtteranceId);
                    if (interpreted == null || interpreted.Intent != HoldemDealerIntent.CardPreference
                        || !interpreted.MatchesCard(decision.Card))
                        throw new InvalidOperationException("Selected card is not supported by the correlated interpretation.");
                    HoldemUtteranceEntry source = null;
                    for (int i = 0; i < work.SourceUtterances.Count; i++)
                        if (work.SourceUtterances.GetEntry(i).CommandId == decision.UtteranceId)
                            source = work.SourceUtterances.GetEntry(i);
                    if (source == null) throw new InvalidOperationException("Selected utterance is absent from the input.");
                    change = new HoldemCardChange(source.WindowId, source.CommandId, source.Speaker,
                        decision.BoardIndex, decision.Card, decision.SourceScope);
                }
            }
            var deal = work.TargetDeal;
            return new HoldemDealCommand(deal.SessionId, deal.HandId, deal.WindowId, Guid.NewGuid(),
                work.ExpectedVersion, deal.Street, change);
        }

        private TimeSpan? ReadClock()
        {
            if (clock == null) return null;
            var now = clock();
            if (now < TimeSpan.Zero || lastTime.HasValue && now < lastTime.Value)
                throw new InvalidOperationException("Dealer wait requires a nonnegative monotonic clock.");
            lastTime = now; return now;
        }

        // Called under the same lock as worker posting. Only continuously observed active time is charged.
        private void ObserveDeadline(TimeSpan? now)
        {
            if (!now.HasValue || current == null || finished) return;
            if (lastActiveAt.HasValue)
            {
                var delta = now.Value - lastActiveAt.Value;
                elapsed = delta >= maximumActiveWait.Value - elapsed ? maximumActiveWait.Value : elapsed + delta;
            }
            lastActiveAt = now;
            if (!posted && elapsed >= maximumActiveWait.Value) posted = true;
        }
    }
}
