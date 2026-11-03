using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Poker.Application;
using Poker.Foundation;

namespace Poker.Llm
{
    /// <summary>Host-only diagnostic for one utterance. Never send it to a player.</summary>
    public sealed class HoldemMentReport
    {
        internal HoldemMentReport(HoldemUtteranceEntry source, MentAnalysisResult result, TimeSpan elapsed)
        {
            UtteranceId = source.CommandId; Speaker = source.Speaker; Text = source.Text;
            Result = result; Elapsed = elapsed;
        }
        public Guid UtteranceId { get; }
        public SeatId Speaker { get; }
        public string Text { get; }
        public MentAnalysisResult Result { get; }
        public TimeSpan Elapsed { get; }
    }

    /// <summary>Host-only result of one closed input window: the correlated batch and per-utterance reports.</summary>
    public sealed class HoldemMentInterpretation
    {
        private readonly HoldemMentReport[] reports;
        internal HoldemMentInterpretation(HoldemDealerInterpretationBatch batch, HoldemMentReport[] reports)
        { Batch = batch; this.reports = reports; }
        public HoldemDealerInterpretationBatch Batch { get; }
        public int Count => reports.Length;
        public HoldemMentReport GetReport(int index) => reports[index];
    }

    public static class HoldemMentInterpreter
    {
        /// <summary>
        /// Analyzes every utterance independently and always returns a complete batch. Timeouts,
        /// cancellations and invalid responses become no-request entries, never card preferences.
        /// </summary>
        public static async Task<HoldemMentInterpretation> InterpretAsync(HoldemDealerWork work, IMentAnalyzer analyzer,
            TimeSpan deadline, CancellationToken cancellationToken = default)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            if (analyzer == null) throw new ArgumentNullException(nameof(analyzer));
            if (work.InputKind != HoldemDealerInputKind.ClosedBettingWindow || work.SourceUtterances == null)
                throw new ArgumentException("Only a closed utterance window can be interpreted.", nameof(work));
            if (deadline <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(deadline));

            var source = work.SourceUtterances;
            var analyses = new Task<HoldemMentRun>[source.Count];
            using (var round = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                round.CancelAfter(deadline);
                for (int i = 0; i < source.Count; i++)
                    analyses[i] = AnalyzeAsync(analyzer, source.GetEntry(i).Text, round.Token, cancellationToken);
                return await CollectAsync(work, analyses);
            }
        }

        /// <summary>Correlates analyses already running, one per source utterance in order, with the closed window.</summary>
        internal static async Task<HoldemMentInterpretation> CollectAsync(HoldemDealerWork work, Task<HoldemMentRun>[] analyses)
        {
            await Task.WhenAll(analyses);
            var source = work.SourceUtterances;
            var entries = new HoldemDealerInterpretation[source.Count];
            var reports = new HoldemMentReport[source.Count];
            for (int i = 0; i < source.Count; i++)
            {
                var entry = source.GetEntry(i);
                var run = analyses[i].Result;
                entries[i] = ToInterpretation(entry.CommandId, run.Result);
                reports[i] = new HoldemMentReport(entry, run.Result, run.Elapsed);
            }
            var deal = work.TargetDeal;
            var batch = new HoldemDealerInterpretationBatch(deal.SessionId, deal.HandId, deal.WindowId,
                work.ExpectedVersion, deal.Street, source.WindowId, entries);
            return new HoldemMentInterpretation(batch, reports);
        }

        /// <summary>Levels 0~4 are normalized to the adapter's 0..1 scale; only valid card requests become preferences.</summary>
        public static HoldemDealerInterpretation ToInterpretation(Guid utteranceId, MentAnalysisResult result)
        {
            if (!result.IsValid || result.Analysis.Intent != MentIntent.CardRequest)
                return new HoldemDealerInterpretation(utteranceId, HoldemDealerIntent.NoRequest, null, null, 0, 0);
            var analysis = result.Analysis;
            return new HoldemDealerInterpretation(utteranceId, HoldemDealerIntent.CardPreference, analysis.Rank, analysis.Suit,
                (double)analysis.Explicitness / MentAnalysis.MaxLevel, (double)analysis.Confidence / MentAnalysis.MaxLevel);
        }

        // Failures are recorded as results; nothing escapes to stop the deal.
        internal static async Task<HoldemMentRun> AnalyzeAsync(IMentAnalyzer analyzer, string text,
            CancellationToken round, CancellationToken caller)
        {
            var stopwatch = Stopwatch.StartNew();
            MentAnalysisResult result;
            try
            {
                while (!analyzer.IsReady) await Task.Delay(200, round);
                result = await analyzer.AnalyzeAsync(text, round);
            }
            catch (OperationCanceledException)
            {
                result = MentAnalysisResult.Cancelled();
            }
            catch (Exception ex)
            {
                result = MentAnalysisResult.Unavailable(ex.Message);
            }
            return new HoldemMentRun(result.Status == MentAnalysisStatus.Cancelled && !caller.IsCancellationRequested
                ? MentAnalysisResult.Timeout() : result, stopwatch.Elapsed);
        }
    }

    internal readonly struct HoldemMentRun
    {
        public HoldemMentRun(MentAnalysisResult result, TimeSpan elapsed) { Result = result; Elapsed = elapsed; }
        public MentAnalysisResult Result { get; }
        public TimeSpan Elapsed { get; }
    }
}
