using System;
using System.Collections.Generic;
using System.Diagnostics;
using Poker.Application;
using Poker.Foundation;
using Poker.Llm;

namespace Poker.Online
{
    /// <summary>
    /// Host-only automatic dealer for chat rooms. Only chat lines sent "to the dealer" reach the
    /// closed speech batch; the scene's local LLM interprets them and the deterministic room applies
    /// the card. Any AI failure, delay or rejection falls back to an unchanged deal.
    /// </summary>
    public sealed class HoldemLlmTableDealer : IDisposable, IHoldemDealerSelectionPolicy
    {
        private static readonly TimeSpan MaximumActiveWait = TimeSpan.FromSeconds(30);
        private const double RevealHoldSeconds = 5;
        private readonly IHoldemSocialHostPort host;
        private readonly IHoldemOpenUtterancePort open;
        private readonly HoldemDealerTurnCoordinator coordinator;
        private readonly HoldemDealerAiAssist ai;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private HoldemDealerWork current;
        private Guid fallbackWindow, revealHand;
        private HoldemStreet revealStreet;
        private double revealSeenAt = -1;
        private bool disposed, fallbackPending;

        public static HoldemConfig CreateConfig(long stack = 100, long small = 1, long big = 2)
            => new HoldemConfig(stack, small, big, HoldemRevealPolicy.PauseAfterCommunityReveal,
                HoldemAccusationMode.Disabled, HoldemDealPolicy.WaitForHost, turnAccusations: true);

        // Chat text stays public; OwnOnly hides which lines were designated for the dealer.
        public static HoldemUtterancePolicy CreateUtterancePolicy(int maximumLength = HoldemRoom.MaximumChatTextLength)
            => new HoldemUtterancePolicy(maximumLength, 1, HoldemUtteranceSeats.Active | HoldemUtteranceSeats.AllIn
                | HoldemUtteranceSeats.Folded | HoldemUtteranceSeats.Busted,
                HoldemUtteranceVisibility.OwnOnly, HoldemUtteranceBatchRetention.UntilHostAcknowledges);

        public HoldemLlmTableDealer(IHoldemSocialHostPort host, IMentAnalyzer analyzer)
        {
            this.host = host ?? throw new ArgumentNullException(nameof(host));
            open = host as IHoldemOpenUtterancePort;
            coordinator = new HoldemDealerTurnCoordinator(host, this, MaximumActiveWait, () => clock.Elapsed);
            ai = analyzer == null ? null : new HoldemDealerAiAssist(analyzer);
        }

        public void Tick()
        {
            if (disposed) return;
            PrefetchRequests();
            ai?.LogFinishedAnalyses();
            if (!host.TryReadProgress(out var room) || room.Paused || room.Game == null) return;
            HoldemRoomReceipt receipt;
            HoldemDealerWork work;
            try { receipt = coordinator.Poll(out work); }
            catch (Exception error) when (!(error is ObjectDisposedException))
            {
                UnityEngine.Debug.LogWarning("[AI 딜러] 카드 선택 실패 · 변경 없이 공개합니다 (" + error.GetType().Name + ").");
                FallBack();
                return;
            }
            if (receipt != null && receipt.Accepted)
            {
                // Keep the exact source until the authoritative card application has completed.
                if (current?.SourceUtterances != null) host.AcknowledgeUtteranceBatch(current.SourceUtterances.WindowId);
                if (ai != null && ai.Owns(current)) ai.Complete(); else ai?.Release();
                current = null; fallbackPending = false;
            }
            else if (receipt != null && receipt.Error != HoldemRoomError.Paused && receipt.Error != HoldemRoomError.Disconnected
                && (ai == null || !ai.TryReopen(coordinator, receipt)))
            {
                UnityEngine.Debug.LogWarning("[AI 딜러] 카드 적용 거절 (" + receipt.Error + ") · 변경 없이 공개합니다.");
                FallBack();
            }
            if (work != null && ai != null && ai.TryRepost(coordinator, work)) { current = work; work = null; }
            if (work != null) Begin(work);
            ai?.TryPost(coordinator, current, out _);
            DiscardFinishedHandInput();
            ResumeReveal();
        }

        // Analysis starts when a request is accepted, even while the room is paused; the deal only collects it.
        private void PrefetchRequests()
        {
            if (ai == null || open == null) return;
            IReadOnlyList<HoldemUtteranceEntry> accepted;
            try { accepted = open.ReadOpenUtterances(); }
            catch (InvalidOperationException) { return; }
            for (int i = 0; i < accepted.Count; i++) ai.Prefetch(accepted[i]);
        }

        private void Begin(HoldemDealerWork work)
        {
            ai?.Release();
            current = work;
            bool fallback = fallbackPending && work.TargetDeal.WindowId == fallbackWindow;
            fallbackPending = false;
            int requests = work.SourceUtterances?.Count ?? 0;
            if (!fallback && ai != null && requests > 0 && ai.Begin(work)) return;
            HoldemDealerAiAssist.EditorLog("[AI 딜러] 최종 선택 → " + work.TargetDeal.Street + " 변경 없음 ("
                + (fallback ? "이전 적용이 거절됨" : ai == null ? "씬에 로컬 LLM 없음"
                    : requests == 0 ? "딜러에게 한 말 없음" : "분석 시작 실패") + ")");
            coordinator.TryPostUnchanged(work);
        }

        /// <summary>Reopens a definitively rejected window; its fresh work is then dealt unchanged.</summary>
        private void FallBack()
        {
            ai?.Release();
            var window = current?.TargetDeal.WindowId ?? Guid.Empty;
            if (window != Guid.Empty && window == fallbackWindow)
                throw new InvalidOperationException("The unchanged fallback deal was rejected.");
            fallbackWindow = window; fallbackPending = true;
            coordinator.ReopenRejectedTurn();
        }

        // A fold can finish the hand before there is a dealer turn to consume its closed input.
        // Discard only batches belonging to this authoritatively finished hand, never future input.
        private void DiscardFinishedHandInput()
        {
            if (!host.TryReadProgress(out var room) || room.Game == null) return;
            var view = room.Game;
            if (view.Result == null) return;
            foreach (var batch in host.ReadPendingUtterances())
                if (batch.HandId == view.HandId) host.AcknowledgeUtteranceBatch(batch.WindowId);
        }

        // The requester's private success marker is visible only while the reveal is held.
        private void ResumeReveal()
        {
            if (!host.TryReadProgress(out var room) || room.Game == null) return;
            var view = room.Game;
            if (!view.IsRevealPending) { revealSeenAt = -1; return; }
            if (revealSeenAt < 0 || view.HandId != revealHand || view.Street != revealStreet)
            {
                revealHand = view.HandId; revealStreet = view.Street; revealSeenAt = clock.Elapsed.TotalSeconds;
                return;
            }
            if (clock.Elapsed.TotalSeconds - revealSeenAt >= RevealHoldSeconds)
            {
                revealSeenAt = clock.Elapsed.TotalSeconds;
                host.ResumeAfterReveal(new HoldemRevealCommand(view.SessionId, view.HandId, Guid.NewGuid(),
                    view.SessionVersion, view.Street));
            }
        }

        public bool IsAnalyzing => ai != null && ai.IsAnalyzing;

        public HoldemDealerSelection Select(HoldemDealerWork work, HoldemDealerInterpretationBatch interpretations)
            => ai != null && ai.Owns(work) ? ai.Select(work, interpretations) : HoldemDealerSelection.Unchanged();

        public void Dispose()
        {
            if (disposed) return;
            disposed = true; current = null; ai?.Dispose(); coordinator.Dispose();
        }
    }
}
