using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Poker.Application;
using Poker.Llm;

namespace Poker.Foundation.Tests
{
    public sealed partial class HoldemDealerTurnTests
    {
        private sealed class ScriptedAnalyzer : IMentAnalyzer
        {
            public readonly Dictionary<string, Func<CancellationToken, Task<MentAnalysisResult>>> Replies =
                new Dictionary<string, Func<CancellationToken, Task<MentAnalysisResult>>>();
            public bool IsReady { get; set; } = true;
            public Task<MentAnalysisResult> AnalyzeAsync(string ment, CancellationToken token)
                => Replies.TryGetValue(ment, out var reply) ? reply(token) : Task.FromResult(MentAnalysisResult.Success(MentAnalysis.NoIntent, "{}"));
            public ScriptedAnalyzer Says(string ment, MentIntent intent, Rank? rank, Suit? suit, int confidence, int explicitness)
            {
                var result = MentAnalysisResult.Success(new MentAnalysis(intent, rank, suit, confidence, explicitness), "{}");
                Replies[ment] = _ => Task.FromResult(result);
                return this;
            }
        }

        private sealed class QueuedRandom : IRandomSource
        {
            private readonly Queue<int> values;
            public QueuedRandom(params int[] values) { this.values = new Queue<int>(values); }
            public int NextInt(int exclusiveMax) => values.Count > 0 ? values.Dequeue() % exclusiveMax : exclusiveMax - 1;
        }

        private static HoldemDealerInterpretation Confident(HoldemDealerWork work, int index, double confidence,
            double explicitness = .5, int card = 45)
            => new HoldemDealerInterpretation(work.SourceUtterances.GetEntry(index).CommandId,
                HoldemDealerIntent.CardPreference, Card.FromId(card).Rank, Card.FromId(card).Suit, explicitness, confidence);

        // Drives one AI deal through the coordinator, reopening after each definitive card rejection.
        private HoldemRoomReceipt RunLlmDeal(HoldemLlmDealer dealer, CardPort port, out HoldemMentInterpretation result)
        {
            using var coordinator = new HoldemDealerTurnCoordinator(port, dealer);
            coordinator.Poll(out var work);
            Assert.That(dealer.Begin(work), Is.True);
            Assert.That(dealer.TryTake(out result), Is.True, "Scripted analysis completes synchronously.");
            Assert.That(coordinator.TryPostInterpretations(work, result.Batch), Is.True);
            for (int attempt = 0; attempt < 16; attempt++)
            {
                var receipt = coordinator.Poll(out _);
                if (receipt.Accepted || !dealer.TryRetryAfterRejection(receipt, work)) return receipt;
                Assert.That(coordinator.ReopenRejectedTurn(), Is.True);
                Assert.That(coordinator.Poll(out work), Is.Null);
                Assert.That(coordinator.TryPostInterpretations(work, result.Batch), Is.True);
            }
            throw new AssertionException("The AI dealer did not settle.");
        }

        [Test]
        public void HighestConfidenceWinsRegardlessOfExplicitness()
        {
            Create(); Start(); Speak(1, "첫 멘트"); Speak(2, "둘째 멘트"); Speak(3, "셋째 멘트"); EndBetting();
            using var coordinator = new HoldemDealerTurnCoordinator(CardTestPort(), SelectTestCard());
            coordinator.Poll(out var work);
            var batch = Scored(work, Confident(work, 0, .5, 1), Confident(work, 1, .75, 0), Confident(work, 2, .25, 1));
            Assert.That(HoldemHighestConfidenceSelectionPolicy.Choose(work, batch, new QueuedRandom()),
                Is.SameAs(batch.GetEntry(1)));
        }

        [TestCase(0, 0)] [TestCase(1, 1)]
        public void ConfidenceTieIsDrawnOnceByTheHostAndKeptOnRetry(int draw, int expected)
        {
            Create(); Start(); Speak(1, "첫 멘트"); Speak(2, "둘째 멘트"); EndBetting();
            using var coordinator = new HoldemDealerTurnCoordinator(CardTestPort(), SelectTestCard());
            coordinator.Poll(out var work);
            var batch = Scored(work, Confident(work, 0, .75), Confident(work, 1, .75));
            // Reservoir sampling: the second tie replaces the first only when the draw is 0.
            var random = new QueuedRandom(draw == 1 ? 0 : 1);
            var picks = new List<Guid>();
            var policy = new HoldemHighestConfidenceSelectionPolicy(random, (w, selected) => {
                picks.Add(selected.UtteranceId); return HoldemDealerSelection.Unchanged(); });
            policy.Select(work, batch); policy.Select(work, batch);
            Assert.That(picks, Is.EqualTo(new[] { batch.GetEntry(expected).UtteranceId, batch.GetEntry(expected).UtteranceId }));
        }

        [Test]
        public void ExactAvailableRequestChangesTheBoardForTheSpeakerOnly()
        {
            Create(); Start(); Speak(2, "하트 7 한 장 줘."); Speak(3, "오늘 날씨 좋네요."); EndBetting();
            var analyzer = new ScriptedAnalyzer().Says("하트 7 한 장 줘.", MentIntent.CardRequest, Rank.Seven, Suit.Hearts, 4, 4);
            using var dealer = new HoldemLlmDealer(analyzer, new StableRandom(), TimeSpan.FromSeconds(5));
            var port = CardTestPort();
            var receipt = RunLlmDeal(dealer, port, out var result);
            Assert.That(receipt.Accepted, Is.True);
            Assert.That(port.Attempts.Count, Is.EqualTo(1));
            var change = port.Attempts[0].CardChange;
            Assert.That(change.Card, Is.EqualTo(new Card(Rank.Seven, Suit.Hearts)));
            Assert.That(State.GetBoardCard(change.BoardIndex), Is.EqualTo(change.Card));
            for (int seat = 0; seat < peers.Length; seat++)
                Assert.That(room.Read(peers[seat]).OwnCardChange != null, Is.EqualTo(seat == 1));
            Assert.That(result.Batch.GetEntry(1).Intent, Is.EqualTo(HoldemDealerIntent.NoRequest));
        }

        [Test]
        public void UnavailableCardsAreExcludedUntilTheDealFallsBackUnchanged()
        {
            // The ordered deck deals every rank-2 card as hole cards, so no 2 is in the reserve.
            Create(); Start(); Speak(2, "둘이면 좋겠다"); EndBetting();
            var analyzer = new ScriptedAnalyzer().Says("둘이면 좋겠다", MentIntent.CardRequest, Rank.Two, null, 3, 1);
            using var dealer = new HoldemLlmDealer(analyzer, new StableRandom(), TimeSpan.FromSeconds(5));
            var port = CardTestPort();
            Assert.That(RunLlmDeal(dealer, port, out _).Accepted, Is.True);
            Assert.That(port.Attempts.Count, Is.EqualTo(5));
            Assert.That(port.Attempts.Take(4).Select(a => a.CardChange.Card.Rank), Is.All.EqualTo(Rank.Two));
            Assert.That(port.Attempts.Take(4).Select(a => a.CardChange.Card).Distinct().Count(), Is.EqualTo(4));
            Assert.That(port.Attempts[4].CardChange, Is.Null);
            Assert.That(peers.All(p => room.Read(p).OwnCardChange == null), Is.True);
        }

        [Test]
        public void SuitOnlyRequestUsesAMatchingReserveCard()
        {
            Create(); Start(); Speak(2, "요즘 연애하고 싶어서 마음이 설레"); EndBetting();
            var analyzer = new ScriptedAnalyzer().Says("요즘 연애하고 싶어서 마음이 설레", MentIntent.CardRequest, null, Suit.Hearts, 3, 1);
            using var dealer = new HoldemLlmDealer(analyzer, new StableRandom(), TimeSpan.FromSeconds(5));
            var port = CardTestPort();
            Assert.That(RunLlmDeal(dealer, port, out _).Accepted, Is.True);
            Assert.That(room.Read(peers[1]).OwnCardChange.Card.Suit, Is.EqualTo(Suit.Hearts));
        }

        [Test]
        public void FailedAnalysesBecomeNoRequestAndNeverStopTheDeal()
        {
            Create(); Start(); Speak(1, "깨진 응답"); Speak(2, "시간 초과"); Speak(3, "예외"); Speak(4, "럭키"); EndBetting();
            var analyzer = new ScriptedAnalyzer().Says("럭키", MentIntent.CardRequest, Rank.Seven, null, 2, 1);
            analyzer.Replies["깨진 응답"] = _ => Task.FromResult(MentAnalysisResult.InvalidResponse("Invalid JSON", "{"));
            analyzer.Replies["시간 초과"] = async token => { await Task.Delay(Timeout.Infinite, token); return default; };
            analyzer.Replies["예외"] = _ => throw new InvalidOperationException("adapter fault");
            using var coordinator = new HoldemDealerTurnCoordinator(CardTestPort(), SelectTestCard());
            coordinator.Poll(out var work);
            // Off the editor synchronization context so the deadline continuation can run while this thread waits.
            var task = Task.Run(() => HoldemMentInterpreter.InterpretAsync(work, analyzer, TimeSpan.FromMilliseconds(200)));
            Assert.That(task.Wait(5000), Is.True);
            var result = task.Result;
            Assert.That(result.Count, Is.EqualTo(4));
            Assert.That(Enumerable.Range(0, 4).Select(i => result.GetReport(i).Result.Status), Is.EqualTo(new[] {
                MentAnalysisStatus.InvalidResponse, MentAnalysisStatus.Timeout, MentAnalysisStatus.Unavailable, MentAnalysisStatus.Success }));
            for (int i = 0; i < 3; i++) Assert.That(result.Batch.GetEntry(i).Intent, Is.EqualTo(HoldemDealerIntent.NoRequest));
            var lucky = result.Batch.GetEntry(3);
            Assert.That(lucky.Intent, Is.EqualTo(HoldemDealerIntent.CardPreference));
            Assert.That(lucky.Rank, Is.EqualTo(Rank.Seven)); Assert.That(lucky.Suit, Is.Null);
            Assert.That(lucky.Confidence, Is.EqualTo(.5)); Assert.That(lucky.Explicitness, Is.EqualTo(.25));
            Assert.That(coordinator.TryPostInterpretations(work, result.Batch), Is.True);
        }

        [Test]
        public void RequestAnalysedOnArrivalIsReusedWhenTheCardIsDrawn()
        {
            Create(); Start(); Speak(2, "하트 7 한 장 줘.");
            var open = room.ReadOpenUtterances(peers[0]);
            Assert.That(open.Count, Is.EqualTo(1));
            Assert.Throws<InvalidOperationException>(() => room.ReadOpenUtterances(peers[1]));
            int calls = 0;
            var analyzer = new ScriptedAnalyzer();
            var reply = MentAnalysisResult.Success(new MentAnalysis(MentIntent.CardRequest, Rank.Seven, Suit.Hearts, 4, 4), "{}");
            analyzer.Replies["하트 7 한 장 줘."] = _ => { calls++; return Task.FromResult(reply); };
            using var dealer = new HoldemLlmDealer(analyzer, new StableRandom(), TimeSpan.FromSeconds(5));
            Assert.That(dealer.Prefetch(open[0]), Is.True);
            Assert.That(dealer.Prefetch(open[0]), Is.False);
            Assert.That(calls, Is.EqualTo(1), "Analysis starts when the request arrives.");
            EndBetting();
            Assert.That(room.ReadOpenUtterances(peers[0]), Is.Empty);
            Assert.That(RunLlmDeal(dealer, CardTestPort(), out var result).Accepted, Is.True);
            Assert.That(calls, Is.EqualTo(1), "The deal reuses the stored result.");
            Assert.That(result.Batch.GetEntry(0).Intent, Is.EqualTo(HoldemDealerIntent.CardPreference));
            Assert.That(dealer.Prefetch(open[0]), Is.False, "A consumed request is never analysed again.");
        }

        [Test]
        public void RequestSentWhileTheModelLoadsStartsAsSoonAsItIsReady()
        {
            Create(); Start(); Speak(2, "하트 7 한 장 줘.");
            var entry = room.ReadOpenUtterances(peers[0])[0];
            int calls = 0;
            var analyzer = new ScriptedAnalyzer { IsReady = false };
            var reply = MentAnalysisResult.Success(new MentAnalysis(MentIntent.CardRequest, Rank.Seven, Suit.Hearts, 4, 4), "{}");
            analyzer.Replies["하트 7 한 장 줘."] = _ => { Interlocked.Increment(ref calls); return Task.FromResult(reply); };
            // A 50 ms deadline would expire during loading if it covered the wait.
            using var dealer = new HoldemLlmDealer(analyzer, new StableRandom(), TimeSpan.FromMilliseconds(50));
            // Off the editor synchronization context so the readiness poll can resume while this thread waits.
            Assert.That(Task.Run(() => dealer.Prefetch(entry)).Result, Is.True);
            Thread.Sleep(300);
            Assert.That(Volatile.Read(ref calls), Is.Zero);
            analyzer.IsReady = true;
            Assert.That(SpinWait.SpinUntil(() => Volatile.Read(ref calls) == 1, 3000), Is.True);
            Assert.That(SpinWait.SpinUntil(() => dealer.TakeFinishedReport() is HoldemMentReport report
                && report.Result.IsValid, 3000), Is.True);
        }

        [Test]
        public void AbandonedAnalysisDoesNotBlockTheNextOne()
        {
            Create(); Start(); Speak(2, "대기"); EndBetting();
            var analyzer = new ScriptedAnalyzer();
            analyzer.Replies["대기"] = async token => { await Task.Delay(Timeout.Infinite, token); return default; };
            using var dealer = new HoldemLlmDealer(analyzer, new StableRandom(), TimeSpan.FromSeconds(30));
            using var coordinator = new HoldemDealerTurnCoordinator(CardTestPort(), dealer);
            coordinator.Poll(out var work);
            Assert.That(dealer.Begin(work), Is.True);
            Assert.That(dealer.Begin(work), Is.False);
            Assert.That(dealer.TryTake(out _), Is.False);
            dealer.Abandon();
            Assert.That(dealer.IsBusy, Is.False);
            analyzer.Replies.Clear();
            Assert.That(dealer.Begin(work), Is.True);
            Assert.That(dealer.TryTake(out var result), Is.True);
            Assert.That(result.Batch.GetEntry(0).Intent, Is.EqualTo(HoldemDealerIntent.NoRequest));
        }
    }
}
