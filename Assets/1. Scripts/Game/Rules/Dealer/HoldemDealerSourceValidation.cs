using System.Collections.Generic;
using Poker.Foundation;

namespace Poker.Application
{
    internal static class HoldemDealerSourceValidation
    {
        internal static bool Matches(HoldemDealCommand command, HoldemDealWindow deal, IReadOnlyList<HoldemUtteranceBatch> batches)
        => command.CardChange == null || Find(command, deal, batches) != null;

        internal static HoldemUtteranceEntry Find(HoldemDealCommand command, HoldemDealWindow deal, IReadOnlyList<HoldemUtteranceBatch> batches)
        {
            var change = command.CardChange;
            if (change == null || batches == null) return null;
            foreach (var batch in batches)
            {
                if (batch.Audience != HoldemSpeechAudience.Table || batch.SessionId != command.SessionId || batch.HandId != command.HandId
                    || batch.WindowId != change.UtteranceWindowId
                    || deal != null && deal.WindowId == command.WindowId && batch.Street != deal.SourceStreet) continue;
                for (int i = 0; i < batch.Count; i++)
                {
                    var entry = batch.GetEntry(i);
                    if (entry.CommandId == change.UtteranceId && entry.Speaker == change.Speaker) return entry;
                }
            }
            return null;
        }
    }
}
