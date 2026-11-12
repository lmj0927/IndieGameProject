using System;
using Poker.Foundation;

namespace Poker.Application
{
    public sealed partial class HoldemRoom
    {
        public HoldemRoomError CloseDealerInput(Guid hostConnection, Guid dealWindowId)
        {
            lock (gate)
            {
                var error = Guard(hostConnection, true, out _);
                if (error != HoldemRoomError.None) return error;
                if (utterances == null) return HoldemRoomError.UtteranceRejected;
                int count = utterances.ReadClosedDesignatedBatches().Count;
                if (!utterances.FreezeDealerInput(dealWindowId)) return HoldemRoomError.UtteranceRejected;
                eliminatedSpeech?.FreezeDealerInput(dealWindowId);
                SynchronizeUtterances();
                if (utterances.ReadClosedDesignatedBatches().Count != count) revision++;
                return HoldemRoomError.None;
            }
        }

        /// <summary>Host-process only. Never expose selected cards or this command on a player wire.</summary>
        public HoldemRoomReceipt ApplyDealerDeal(Guid hostConnection, HoldemDealCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            return HostOperation(hostConnection, () =>
            {
                var source = HoldemDealerSourceValidation.Find(command, session.PendingDeal, utterances?.ReadClosedDesignatedBatches());
                if (command.CardChange != null && (!RetainsDealerInput || source == null)) return command.RejectCardChange();
                long beforeVersion = session.Version;
                var receipt = session.ApplyDealerDeal(command);
                if (receipt.Accepted && session.Version > beforeVersion && source != null)
                {
                    var changed = session.ReadCurrentRevealCardChange(source.Speaker);
                    int index = command.CardChange.BoardIndex;
                    var revealed = session.GetSnapshot(source.Speaker).GetBoardCard(index);
                    dealerSelections.Add(new HoldemDealerSelectionFeedback(command.HandId, command.WindowId,
                        command.Street, source.Speaker, source.Text, changed?.DealWindowId == command.WindowId,
                        index, revealed));
                }
                return receipt;
            });
        }

        /// <summary>Read the current deal and its preceding raw input atomically, without consuming either.</summary>
        public HoldemDealerTurnRead ReadPendingDealerTurn(Guid hostConnection)
        {
            lock (gate)
            {
                var error = Guard(hostConnection, true, out var host);
                if (error != HoldemRoomError.None) return new HoldemDealerTurnRead(error);
                if (!session.CurrentHandId.HasValue) return new HoldemDealerTurnRead(HoldemRoomError.None);
                var snapshot = session.GetSnapshot(host.Seat);
                var deal = snapshot.PendingDeal;
                if (!snapshot.IsDealPending || deal == null) return new HoldemDealerTurnRead(HoldemRoomError.None);
                HoldemUtteranceBatch matching = null;
                // The current inbox retains frozen windows even after the separate delivery queue is acknowledged.
                // Never guess from the oldest/newest queued batch: it may belong to a previous hand or street.
                if (RetainsDealerInput && utterances != null)
                    foreach (var batch in utterances.ReadClosedDesignatedBatches())
                        if (batch.SessionId == deal.SessionId && batch.HandId == deal.HandId
                            && batch.Street == deal.SourceStreet) matching = batch;
                if (snapshot.BettingPolicy != HoldemBettingPolicy.NoLimit && RetainsDealerInput && matching == null)
                    return new HoldemDealerTurnRead(HoldemRoomError.None);
                return new HoldemDealerTurnRead(HoldemRoomError.None,
                    new HoldemDealerTurn(deal, snapshot.SessionVersion, RetainsDealerInput, matching));
            }
        }
    }
}
