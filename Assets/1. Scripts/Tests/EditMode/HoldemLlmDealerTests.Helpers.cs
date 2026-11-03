using Poker.Application;

namespace Poker.Foundation.Tests
{
    public sealed partial class HoldemDealerTurnTests
    {
        private static HoldemDealerInterpretationBatch Scored(HoldemDealerWork work,
            params HoldemDealerInterpretation[] entries)
        {
            var deal = work.TargetDeal;
            return new HoldemDealerInterpretationBatch(deal.SessionId, deal.HandId, deal.WindowId,
                work.ExpectedVersion, deal.Street, work.SourceUtterances.WindowId, entries);
        }
    }
}
