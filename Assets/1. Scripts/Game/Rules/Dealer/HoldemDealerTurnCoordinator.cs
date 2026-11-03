using System;
using System.Threading;
using Poker.Foundation;

namespace Poker.Application
{
    /// <summary>Immutable host-only worker input. Contains no port, command, private cards or AI verdict.</summary>
    public sealed class HoldemDealerWork
    {
        internal HoldemDealerWork(HoldemDealerTurn turn)
        {
            TargetDeal = turn.TargetDeal; ExpectedVersion = turn.ExpectedVersion;
            InputKind = turn.InputKind; SourceUtterances = turn.SourceUtterances;
        }
        public HoldemDealWindow TargetDeal { get; }
        public long ExpectedVersion { get; }
        public HoldemDealerInputKind InputKind { get; }
        public HoldemUtteranceBatch SourceUtterances { get; }
    }

    /// <summary>
    /// Optional one-slot mailbox for asynchronous host integrations. Construct, Poll and Dispose on
    /// the owning game thread; TryPost methods alone are worker-safe. No worker or AI is started here.
    /// An optional observed-active timeout shares the same mailbox; do not also run a standalone timeout.
    /// Selection rules must be supplied explicitly. Dispose with the room.
    /// </summary>
    public sealed partial class HoldemDealerTurnCoordinator : IDisposable
    {
        private readonly IHoldemDealerTurnPort port;
        private readonly int ownerThread = Thread.CurrentThread.ManagedThreadId;
        private readonly object gate = new object();
        private HoldemDealerWork current;
        private HoldemDealCommand command;
        private bool posted, finished, rejected, uncertain, disposed, polling;

        public HoldemDealerTurnCoordinator(IHoldemDealerTurnPort port)
        { this.port = port ?? throw new ArgumentNullException(nameof(port)); }

        /// <summary>
        /// Returns new work once per window through the out parameter. A non-null return is the
        /// authoritative completion receipt, not an AI result. Poll even without a callback so stale
        /// work is invalidated. Retryable completion errors retain the exact command; an uncertain
        /// attempt is reconciled with that same ID even if the authoritative window already advanced.
        /// Errors propagate; the caller decides how to display or recover from them.
        /// </summary>
        public HoldemRoomReceipt Poll(out HoldemDealerWork work)
        {
            CheckOwner(); work = null;
            if (disposed) throw new ObjectDisposedException(nameof(HoldemDealerTurnCoordinator));
            if (polling) throw new InvalidOperationException("Dealer coordinator polling must not be reentrant.");
            polling = true;
            try
            {
                TimeSpan? now = ReadClock();
                var read = port.ReadPendingTurn();
                if (read == null) throw new InvalidOperationException("The dealer port returned no read result.");
                if (read.Error == HoldemRoomError.Paused || read.Error == HoldemRoomError.Disconnected)
                { lastActiveAt = null; return null; }
                if (read.Error != HoldemRoomError.None)
                {
                    Close();
                    throw new InvalidOperationException("Dealer turn is unavailable: " + read.Error);
                }

                HoldemDealCommand attempt;
                HoldemDealerWork selectingWork;
                HoldemDealerInterpretationBatch selectingBatch;
                lock (gate)
                {
                    if (SameWindow(current, read.Turn) && current.ExpectedVersion != read.Turn.ExpectedVersion)
                    {
                        Close();
                        throw new InvalidOperationException("A pending dealer turn changed version without changing its window.");
                    }
                    // After an ambiguous attempt only the original command may reconcile its outcome.
                    // Before the first attempt, stale worker completion must never reach the port.
                    if (!uncertain)
                    {
                        if (!SameWindow(current, read.Turn))
                        {
                            Clear();
                            if (read.Turn != null)
                            {
                                current = work = new HoldemDealerWork(read.Turn);
                                lastActiveAt = now;
                            }
                            return null;
                        }
                        ObserveDeadline(now);
                        if (current == null || finished || !posted || elapsed < minimumRevealDelay) return null;
                    }
                    attempt = command;
                    selectingWork = current; selectingBatch = interpretations;
                }
                if (attempt == null)
                {
                    try { attempt = CreateCommand(selectingWork, selectingBatch); }
                    catch { lock (gate) { finished = rejected = true; } throw; }
                    lock (gate) command = attempt;
                }
                lock (gate) uncertain = true;
                // Never call the external port under the worker mailbox lock.
                var receipt = attempt.CardChange == null ? port.DealUnchanged(attempt)
                    : applicationPort.ApplyDealerDeal(attempt);
                if (receipt == null) throw new InvalidOperationException("The dealer port returned no completion result.");
                lock (gate)
                {
                    uncertain = false;
                    if (receipt.Error != HoldemRoomError.Paused && receipt.Error != HoldemRoomError.Disconnected)
                    { finished = true; rejected = !receipt.Accepted; }
                }
                return receipt;
            }
            catch (ObjectDisposedException) { Close(); throw; }
            catch { lastActiveAt = null; throw; }
            finally { polling = false; }
        }

        /// <summary>
        /// Worker-safe. True only means this work's completion is queued (including a duplicate post),
        /// not that cards were dealt. A later owner read may discard it. No port or Unity calls occur.
        /// False for null, foreign, stale, already completed or closed work.
        /// </summary>
        public bool TryPostUnchanged(HoldemDealerWork work)
        {
            lock (gate)
            {
                if (disposed || finished || work == null || !ReferenceEquals(work, current)) return false;
                if (posted && interpretations != null) return false;
                posted = true;
                return true;
            }
        }

        private static bool SameWindow(HoldemDealerWork work, HoldemDealerTurn turn)
            => work != null && turn != null && work.TargetDeal.SessionId == turn.TargetDeal.SessionId
                && work.TargetDeal.HandId == turn.TargetDeal.HandId
                && work.TargetDeal.WindowId == turn.TargetDeal.WindowId
                && work.TargetDeal.Street == turn.TargetDeal.Street;

        private void Clear()
        { current = null; command = null; interpretations = null; posted = false; finished = false;
            rejected = false; uncertain = false; elapsed = TimeSpan.Zero; lastActiveAt = null; }

        private void Close()
        { lock (gate) { Clear(); disposed = true; } }

        private void CheckOwner()
        {
            if (Thread.CurrentThread.ManagedThreadId != ownerThread)
                throw new InvalidOperationException("Poll and Dispose belong to the owning game thread.");
        }

        public void Dispose()
        {
            CheckOwner();
            if (polling) throw new InvalidOperationException("Cannot dispose during dealer coordinator polling.");
            Close();
        }
    }
}
