using System.Collections.Generic;
using Poker.Foundation;

namespace Poker.Application
{
    internal static class HoldemDealerSourceValidation
    {
        internal static bool Matches(HoldemDealCommand command, IReadOnlyList<HoldemUtteranceBatch> batches)
        {
            var change = command.CardChange;
            if (change == null) return true;
            if (batches == null) return false;
            foreach (var batch in batches)
            {
                if (batch.Audience != HoldemSpeechAudience.Table || batch.SessionId != command.SessionId || batch.HandId != command.HandId
                    || batch.WindowId != change.UtteranceWindowId || (int)batch.Street + 1 != (int)command.Street) continue;
                for (int i = 0; i < batch.Count; i++)
                {
                    var entry = batch.GetEntry(i);
                    if (entry.CommandId == change.UtteranceId && entry.Speaker == change.Speaker) return true;
                }
            }
            return false;
        }
    }
}
