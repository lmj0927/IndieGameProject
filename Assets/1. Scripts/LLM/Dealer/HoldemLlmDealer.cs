using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Poker.Application;
using Poker.Foundation;

namespace Poker.Llm
{
    /// <summary>
    /// Host-only AI dealer for one table, used on the owning game thread. Prefetch may start each utterance
    /// while its window is still open; Begin collects (or starts) the analyses of a closed utterance window.
    /// Once TryTake returns, the caller posts the batch to its
    /// HoldemDealerTurnCoordinator and uses this object as that coordinator's selection policy.
    /// </summary>
    public sealed class HoldemLlmDealer : IHoldemDealerSelectionPolicy, IDisposable
    {
        private readonly IMentAnalyzer analyzer;
        private readonly TimeSpan deadline;
        private readonly HoldemRequestedCardResolver resolver;
        private readonly HoldemHighestConfidenceSelectionPolicy policy;
        private readonly Dictionary<Guid, Started> started = new Dictionary<Guid, Started>();
        private readonly HashSet<Guid> prefetched = new HashSet<Guid>();
        private Guid prefetchHand;
        private HoldemDealerWork runningWork;
        private Task<HoldemMentInterpretation> running;
        private bool disposed;

        private sealed class Started
        {
            public HoldemUtteranceEntry Entry;
            public CancellationTokenSource Run;
            public Task<HoldemMentRun> Analysis;
            public bool Reported;
            public bool Armed;
        }

        public HoldemLlmDealer(IMentAnalyzer analyzer, IRandomSource random, TimeSpan deadline)
        {
            this.analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
            if (random == null) throw new ArgumentNullException(nameof(random));
            if (deadline <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(deadline));
            this.deadline = deadline;
            resolver = new HoldemRequestedCardResolver(random);
            policy = new HoldemHighestConfidenceSelectionPolicy(random, resolver.Resolve);
        }

        public bool IsReady => analyzer.IsReady;
        public bool IsBusy => running != null;
        /// <summary>Set when the last analysis faulted instead of producing a batch.</summary>
        public Exception LastFault { get; private set; }

        /// <summary>
        /// Starts one accepted utterance as soon as it arrives. A loading model is awaited without a deadline;
        /// the deadline starts with the model's work, or at Begin if that comes first. Begin later reuses the
        /// result; only the closed batch decides which results a deal may use. False when already seen.
        /// </summary>
        public bool Prefetch(HoldemUtteranceEntry entry)
        {
            if (disposed || entry == null) return false;
            if (entry.HandId != prefetchHand)
            {
                foreach (var id in new List<Guid>(started.Keys)) Forget(id);
                prefetched.Clear(); prefetchHand = entry.HandId;
            }
            if (!prefetched.Add(entry.CommandId) || started.ContainsKey(entry.CommandId)) return false;
            started.Add(entry.CommandId, Start(entry));
            return true;
        }

        /// <summary>Host diagnostics: the next started analysis that finished since the last call, or null.</summary>
        public HoldemMentReport TakeFinishedReport()
        {
            foreach (var analysis in started.Values)
            {
                if (analysis.Reported || !analysis.Analysis.IsCompleted) continue;
                analysis.Reported = true;
                if (analysis.Analysis.Status != TaskStatus.RanToCompletion) continue;
                var run = analysis.Analysis.Result;
                return new HoldemMentReport(analysis.Entry, run.Result, run.Elapsed);
            }
            return null;
        }

        public bool Begin(HoldemDealerWork work)
        {
            if (disposed || running != null || work == null || work.SourceUtterances == null
                || work.InputKind != HoldemDealerInputKind.ClosedBettingWindow) return false;
            LastFault = null;
            var source = work.SourceUtterances;
            var analyses = new Task<HoldemMentRun>[source.Count];
            for (int i = 0; i < source.Count; i++)
            {
                var entry = source.GetEntry(i);
                if (!started.TryGetValue(entry.CommandId, out var analysis))
                    started.Add(entry.CommandId, analysis = Start(entry));
                Arm(analysis);
                analyses[i] = analysis.Analysis;
            }
            runningWork = work;
            running = HoldemMentInterpreter.CollectAsync(work, analyses);
            return true;
        }

        /// <summary>Drops the running analysis, e.g. after the deal completed without it.</summary>
        public void Abandon()
        {
            if (running == null) return;
            ForgetSource(runningWork);
            runningWork = null; running = null;
        }

        /// <summary>True once the analysis has finished. A null result means it faulted: deal unchanged.</summary>
        public bool TryTake(out HoldemMentInterpretation result)
        {
            result = null;
            if (running == null || !running.IsCompleted) return false;
            var task = running; running = null;
            ForgetSource(runningWork); runningWork = null;
            if (task.Status == TaskStatus.RanToCompletion) result = task.Result;
            else LastFault = task.Exception?.GetBaseException() ?? new OperationCanceledException();
            return true;
        }

        private Started Start(HoldemUtteranceEntry entry)
        {
            var analysis = new Started { Entry = entry, Run = new CancellationTokenSource() };
            analysis.Analysis = AnalyzeWhenReadyAsync(analysis);
            return analysis;
        }

        private async Task<HoldemMentRun> AnalyzeWhenReadyAsync(Started analysis)
        {
            try { while (!analyzer.IsReady) await Task.Delay(200, analysis.Run.Token); }
            catch (OperationCanceledException) { return new HoldemMentRun(MentAnalysisResult.Timeout(), TimeSpan.Zero); }
            Arm(analysis);
            return await HoldemMentInterpreter.AnalyzeAsync(analyzer, analysis.Entry.Text, analysis.Run.Token, CancellationToken.None);
        }

        // Once only: re-arming would extend a deadline that is already running.
        private void Arm(Started analysis)
        {
            if (analysis.Armed) return;
            analysis.Armed = true;
            analysis.Run.CancelAfter(deadline);
        }

        private void ForgetSource(HoldemDealerWork work)
        {
            var source = work?.SourceUtterances;
            for (int i = 0; source != null && i < source.Count; i++) Forget(source.GetEntry(i).CommandId);
        }

        private void Forget(Guid utteranceId)
        {
            if (!started.TryGetValue(utteranceId, out var analysis)) return;
            started.Remove(utteranceId);
            analysis.Run.Cancel();
            // The adapter may still observe the token until its call unwinds.
            analysis.Analysis.ContinueWith(_ => analysis.Run.Dispose(), TaskScheduler.Default);
        }

        public HoldemDealerSelection Select(HoldemDealerWork work, HoldemDealerInterpretationBatch interpretations)
            => policy.Select(work, interpretations);

        /// <summary>
        /// After the core definitively rejected the proposed card (not in the reserve), excludes it.
        /// True means the caller may reopen the same deal window and post the same batch again.
        /// </summary>
        public bool TryRetryAfterRejection(HoldemRoomReceipt receipt, HoldemDealerWork work)
            => receipt != null && work != null && receipt.Error == HoldemRoomError.CoreRejected
                && receipt.Poker?.Error == HoldemCommandError.InvalidCardChange
                && resolver.RejectProposal(work.TargetDeal.WindowId);

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Abandon();
            foreach (var id in new List<Guid>(started.Keys)) Forget(id);
        }
    }
}
