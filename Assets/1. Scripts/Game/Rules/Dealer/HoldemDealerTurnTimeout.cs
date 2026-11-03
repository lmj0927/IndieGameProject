using System;
using Poker.Foundation;

namespace Poker.Application
{
    /// <summary>
    /// Optional host-side fallback, polled on the serialized owning game loop. The caller supplies the
    /// maximum active wait and a nonnegative monotonic clock; constructing this does not start a timer.
    /// Only intervals between consecutive readable observations of the same deal count as active time.
    /// Poll while disconnected too: unobserved disconnects between polls cannot be timed by this helper.
    /// Dispose when its room/port is closed. No input acknowledgement, AI verdict or penalty is produced.
    /// </summary>
    public sealed class HoldemDealerTurnTimeout : IDisposable
    {
        private readonly IHoldemDealerTurnPort port;
        private readonly Func<TimeSpan> clock;
        private readonly TimeSpan maximumWait;
        private HoldemDealerTurn turn;
        private HoldemDealCommand command;
        private TimeSpan elapsed;
        private TimeSpan? lastActiveAt;
        private TimeSpan? lastTime;
        private bool finished;
        private bool disposed;
        private bool polling;

        public HoldemDealerTurnTimeout(IHoldemDealerTurnPort port, TimeSpan maximumActiveWait,
            Func<TimeSpan> monotonicClock)
        {
            this.port = port ?? throw new ArgumentNullException(nameof(port));
            clock = monotonicClock ?? throw new ArgumentNullException(nameof(monotonicClock));
            if (maximumActiveWait <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(maximumActiveWait));
            maximumWait = maximumActiveWait;
        }

        /// <returns>
        /// Null if no command was attempted; otherwise its authoritative receipt. An accepted or
        /// definitively rejected command is attempted only once per window. Paused/disconnected or
        /// uncertain (throwing) completion retains the exact command for retry after a fresh read.
        /// Exceptions propagate; they must not be interpreted as an AI failure or successful fallback.
        /// </returns>
        public HoldemRoomReceipt Poll()
        {
            if (disposed) throw new ObjectDisposedException(nameof(HoldemDealerTurnTimeout));
            if (polling) throw new InvalidOperationException("Dealer timeout polling must not be reentrant.");
            polling = true;
            try
            {
                TimeSpan now = clock();
                if (now < TimeSpan.Zero || (lastTime.HasValue && now < lastTime.Value))
                    throw new InvalidOperationException("The dealer timeout requires a nonnegative monotonic clock.");
                lastTime = now;
                var read = port.ReadPendingTurn();
                if (read == null) throw new InvalidOperationException("The dealer port returned no read result.");
                if (read.Error == HoldemRoomError.Paused || read.Error == HoldemRoomError.Disconnected)
                { lastActiveAt = null; return null; }
                if (read.Error != HoldemRoomError.None)
                { Clear(); throw new InvalidOperationException("Dealer turn is unavailable: " + read.Error); }
                if (read.Turn == null)
                { Clear(); return null; }

                if (!SameWindow(turn, read.Turn))
                { Clear(); turn = read.Turn; lastActiveAt = now; return null; }
                if (turn.ExpectedVersion != read.Turn.ExpectedVersion)
                    throw new InvalidOperationException("A pending dealer turn changed version without changing its window.");
                if (finished) return null;
                if (lastActiveAt.HasValue)
                {
                    // Saturate rather than overflowing on long waits or near TimeSpan.MaxValue.
                    TimeSpan delta = now - lastActiveAt.Value;
                    elapsed = delta >= maximumWait - elapsed ? maximumWait : elapsed + delta;
                }
                lastActiveAt = now;
                if (elapsed < maximumWait) return null;
                if (command == null) command = turn.CreateUnchangedCommand(Guid.NewGuid());
                var receipt = port.DealUnchanged(command);
                if (receipt == null) throw new InvalidOperationException("The dealer port returned no completion result.");
                if (receipt.Error == HoldemRoomError.Paused || receipt.Error == HoldemRoomError.Disconnected)
                    lastActiveAt = null;
                else finished = true;
                return receipt;
            }
            catch (ObjectDisposedException) { Dispose(); throw; }
            catch { lastActiveAt = null; throw; }
            finally { polling = false; }
        }

        private static bool SameWindow(HoldemDealerTurn previous, HoldemDealerTurn current)
            => previous != null && previous.TargetDeal.SessionId == current.TargetDeal.SessionId
                && previous.TargetDeal.HandId == current.TargetDeal.HandId
                && previous.TargetDeal.WindowId == current.TargetDeal.WindowId
                && previous.TargetDeal.Street == current.TargetDeal.Street;

        private void Clear()
        { turn = null; command = null; elapsed = TimeSpan.Zero; lastActiveAt = null; finished = false; }

        public void Dispose() { Clear(); disposed = true; }
    }
}
