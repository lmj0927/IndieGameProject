using System;
using Poker.Foundation;

namespace Poker.Application
{
    public enum HoldemDealerInputKind { Disabled, NoBettingWindow, ClosedBettingWindow }

    /// <summary>
    /// Frozen host-only work context. The input window and upcoming deal window are different IDs.
    /// Contains raw text and correlation only, never private cards, deck order, AI interpretation or outcome.
    /// </summary>
    public sealed class HoldemDealerTurn
    {
        internal HoldemDealerTurn(HoldemDealWindow deal, long version, bool receivesUtterances,
            HoldemUtteranceBatch utterances)
        {
            TargetDeal = deal; ExpectedVersion = version; SourceUtterances = utterances;
            InputKind = !receivesUtterances ? HoldemDealerInputKind.Disabled
                : utterances == null ? HoldemDealerInputKind.NoBettingWindow : HoldemDealerInputKind.ClosedBettingWindow;
        }
        public HoldemDealWindow TargetDeal { get; }
        public long ExpectedVersion { get; }
        public HoldemDealerInputKind InputKind { get; }
        /// <summary>Null if disabled or no betting window opened. A closed window may have zero entries.</summary>
        public HoldemUtteranceBatch SourceUtterances { get; }

        /// <summary>
        /// Retain and retry the same command after an uncertain result. This permits only unchanged dealing;
        /// it is neither an AI verdict nor proof that no manipulation occurred in another implementation.
        /// </summary>
        public HoldemDealCommand CreateUnchangedCommand(Guid commandId)
            => new HoldemDealCommand(TargetDeal.SessionId, TargetDeal.HandId, TargetDeal.WindowId, commandId,
                ExpectedVersion, TargetDeal.Street);
    }

    public sealed class HoldemDealerTurnRead
    {
        internal HoldemDealerTurnRead(HoldemRoomError error, HoldemDealerTurn turn = null)
        { Error = error; Turn = turn; }
        public HoldemRoomError Error { get; }
        /// <summary>Null outside a pending deal or when authority is unavailable. Never interpret either as AI failure.</summary>
        public HoldemDealerTurn Turn { get; }
    }

    /// <summary>
    /// In-process host integration only. Use on the serialized owning game loop, not from an AI worker thread.
    /// Reading does not acknowledge input or advance play. Completion revalidates the captured correlation.
    /// </summary>
    public interface IHoldemDealerTurnPort
    {
        HoldemDealerTurnRead ReadPendingTurn();
        HoldemRoomReceipt DealUnchanged(HoldemDealCommand command);
    }

    /// <summary>Explicit request deadline, separate from reading/interpreting the frozen dealer turn.</summary>
    public interface IHoldemDealerInputDeadlinePort
    {
        bool CloseDealerInput(Guid dealWindowId);
    }
}
