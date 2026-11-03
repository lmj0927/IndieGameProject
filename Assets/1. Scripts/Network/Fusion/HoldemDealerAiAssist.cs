using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Poker.Application;
using Poker.Foundation;
using Poker.Llm;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Poker.Online
{
    /// <summary>Feeds validated interpretations to the host dealer coordinator.</summary>
    internal sealed class HoldemDealerAiAssist : IDisposable
    {
        private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
        private readonly CryptographicRandomSource random = new CryptographicRandomSource();
        private readonly HoldemLlmDealer dealer;
        private readonly HashSet<Guid> logged = new HashSet<Guid>();
        private HoldemDealerWork owned;
        private HoldemDealerInterpretationBatch batch;
        private HoldemMentInterpretation interpretation;
        private HoldemDealerSelection lastDecision;
        private bool reposting;

        internal HoldemDealerAiAssist(IMentAnalyzer analyzer) { dealer = new HoldemLlmDealer(analyzer, random, Deadline); }

        internal bool IsAnalyzing => dealer.IsBusy;
        internal bool IsReady => dealer.IsReady;
        internal bool Owns(HoldemDealerWork work) => owned != null && work != null
            && owned.TargetDeal.WindowId == work.TargetDeal.WindowId && owned.ExpectedVersion == work.ExpectedVersion;

        /// <summary>Starts analysing a dealer request the moment the host accepts it.</summary>
        internal void Prefetch(HoldemUtteranceEntry entry)
        {
            if (dealer.Prefetch(entry))
                EditorLog("[AI 딜러] 딜러 요청 수신 → 즉시 LLM 호출 · 자리 " + entry.Speaker.Value + " \"" + entry.Text + "\""
                    + (dealer.IsReady ? "" : " (모델 로딩 중 · 준비되는 즉시 시작)"));
        }

        /// <summary>Logs each analysis as soon as it finishes, before the card is drawn.</summary>
        [Conditional("UNITY_EDITOR")]
        internal void LogFinishedAnalyses()
        {
            for (var report = dealer.TakeFinishedReport(); report != null; report = dealer.TakeFinishedReport())
                LogReport(report);
        }

        internal bool Begin(HoldemDealerWork work)
        {
            Release();
            if (!dealer.Begin(work)) return false;
            owned = work;
            return true;
        }

        /// <summary>Owner thread, after Poll. Posts a finished analysis once; failures post an unchanged deal.</summary>
        internal bool TryPost(HoldemDealerTurnCoordinator coordinator, HoldemDealerWork current, out string summary)
        {
            summary = null;
            if (!Owns(current) || !dealer.TryTake(out var result)) return false;
            if (result == null)
            {
                Debug.LogWarning("[AI 딜러] 해석 실패 · 변경 없이 공개합니다 (" + dealer.LastFault?.GetType().Name + ").");
                summary = "AI 해석 실패 · 변경 없이 공개해요.";
                Release();
                return coordinator.TryPostUnchanged(current);
            }
            interpretation = result;
            batch = result.Batch;
            summary = Describe(result);
            return coordinator.TryPostInterpretations(current, batch);
        }

        internal HoldemDealerSelection Select(HoldemDealerWork work, HoldemDealerInterpretationBatch interpretations)
            => lastDecision = dealer.Select(work, interpretations);

        /// <summary>The proposed card was not in the reserve: exclude it and reopen the same deal window.</summary>
        internal bool TryReopen(HoldemDealerTurnCoordinator coordinator, HoldemRoomReceipt receipt)
        {
            if (owned == null || batch == null || !dealer.TryRetryAfterRejection(receipt, owned)) return false;
            reposting = coordinator.ReopenRejectedTurn();
            return reposting;
        }

        /// <summary>Owner thread: the reopened window produced fresh work; post the same batch to it.</summary>
        internal bool TryRepost(HoldemDealerTurnCoordinator coordinator, HoldemDealerWork work)
        {
            if (!reposting || !Owns(work)) return false;
            reposting = false; owned = work;
            if (coordinator.TryPostInterpretations(work, batch)) return true;
            Release();
            return false;
        }

        /// <summary>Outcome text for an accepted AI deal, then forgets the window.</summary>
        internal string Complete()
        {
            bool changed = lastDecision?.Kind == HoldemDealerSelectionKind.Change;
            string text = changed
                ? "AI 요청 반영 · 바뀐 카드는 요청자에게만 표시돼요."
                : "AI 해석 반영 · 반영할 요청이나 카드가 없어 변경 없이 공개했어요.";
            EditorLog("[AI 딜러] 최종 선택 → " + (lastDecision == null ? "변경 없음 (정상 카드 공개)" : DescribeDecision(lastDecision)));
            Release();
            return text;
        }

        internal void Release()
        {
            dealer.Abandon();
            owned = null; batch = null; interpretation = null; lastDecision = null; reposting = false;
        }

        // Host console only: per-utterance analysis must never reach a player.
        private string Describe(HoldemMentInterpretation interpretation)
        {
            int requests = 0, failures = 0;
            for (int i = 0; i < interpretation.Count; i++)
            {
                var report = interpretation.GetReport(i);
                if (!report.Result.IsValid) failures++;
                else if (report.Result.Analysis.Intent == MentIntent.CardRequest) requests++;
                LogReport(report);
            }
            return "AI 해석 완료 · 멘트 " + interpretation.Count + "개 · 카드 요청 " + requests + "개"
                + (failures > 0 ? " · 실패 " + failures + "개" : "");
        }

        // Once per utterance: either when its analysis finished early or, failing that, at the deal.
        [Conditional("UNITY_EDITOR")]
        private void LogReport(HoldemMentReport report)
        {
            if (logged.Count > 64) logged.Clear();
            if (!logged.Add(report.UtteranceId)) return;
            var result = report.Result;
            var log = new StringBuilder("[AI 딜러] 해석 · 자리 ").Append(report.Speaker.Value).Append(" \"").Append(report.Text)
                .Append("\" · ").Append((int)report.Elapsed.TotalMilliseconds).Append("ms");
            log.Append("\n  해석: ").Append(result.Interpretation?.Replace("\n", " / ") ?? "(해석 단계 없음)");
            log.Append("\n  JSON: ").Append(result.RawResponse ?? "(없음)");
            if (!result.IsValid) log.Append("\n  → ").Append(result.Status).Append(" · 요청 없음으로 처리");
            Debug.Log(log.ToString());
        }

        private string DescribeDecision(HoldemDealerSelection decision)
        {
            if (decision.Kind != HoldemDealerSelectionKind.Change) return "변경 없음 (정상 카드 공개)";
            var log = new StringBuilder();
            if (owned != null) log.Append(owned.TargetDeal.Street).Append(' ');
            log.Append(decision.BoardIndex + 1).Append("번째 카드 = ").Append(CardText(decision.Card));
            for (int i = 0; interpretation != null && i < interpretation.Count; i++)
            {
                var report = interpretation.GetReport(i);
                if (report.UtteranceId != decision.UtteranceId) continue;
                log.Append(" (자리 ").Append(report.Speaker.Value).Append(" \"").Append(report.Text).Append("\")");
                break;
            }
            return log.ToString();
        }

        private static string CardText(Card card) => card.Suit + " " + card.Rank;

        // Editor-only: raw player text and the dealer's choice never print in a player build.
        [Conditional("UNITY_EDITOR")]
        internal static void EditorLog(string message) => Debug.Log(message);

        public void Dispose()
        {
            Release();
            dealer.Dispose();
            random.Dispose();
        }
    }
}
