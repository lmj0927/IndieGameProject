using System;
using System.Collections.Generic;
using System.Threading;
using Poker.Foundation;

namespace Poker.Application
{
    public enum DealerFeatureOutcomeKind { Pending, ValidFeatures, InvalidResponse, ProviderFailure, ExpiredAtDeadline }

    [Flags]
    public enum DealerFeatureBatchIssues { None = 0, Pending = 1, InvalidResponse = 2, ProviderFailure = 4, ExpiredAtDeadline = 8 }

    /// <summary>Immutable, host-only model input for one designated sentence. No port or game authority.</summary>
    public sealed class DealerFeatureWork
    {
        internal DealerFeatureWork(DealerFeatureWindow window, HoldemUtteranceEntry entry)
        {
            var source = window.Source;
            SessionId = source.SessionId; HandId = source.HandId; WindowId = source.WindowId;
            Street = source.Street; Audience = source.Audience; Entry = entry; Lifetime = window.Lifetime;
        }
        public Guid SessionId { get; }
        public Guid HandId { get; }
        public Guid WindowId { get; }
        public HoldemStreet Street { get; }
        public HoldemSpeechAudience Audience { get; }
        public HoldemUtteranceEntry Entry { get; }
        internal DealerInputLifetime Lifetime { get; }
    }

    public sealed class DealerFeatureOutcome
    {
        internal DealerFeatureOutcome(DealerFeatureWork work, DealerFeatureOutcomeKind kind, HoldemDealerFeatures features)
        { Work = work; Kind = kind; Features = features; }
        public DealerFeatureWork Work { get; }
        public DealerFeatureOutcomeKind Kind { get; }
        public HoldemDealerFeatures Features { get; }
    }

    /// <summary>
    /// Optional feature-only integration seam. Observe, TakeNewWork, ReadOutcomes and Dispose belong
    /// to the owner thread; only TryPost methods are worker-safe. No model, normalization, card
    /// selection or deal is started here. Consume final outcomes after freezing and before dealing.
    /// A final source read registers last-moment designations as expired, not silently missing input.
    /// </summary>
    public sealed class DealerFeatureMailbox : IDisposable
    {
        private sealed class Record
        {
            internal DealerFeatureWork Work;
            internal bool Issued;
            internal DealerFeatureOutcomeKind Kind;
            internal HoldemDealerFeatures Features;
        }
        private readonly IDealerFeatureSourcePort source;
        private readonly int ownerThread = Thread.CurrentThread.ManagedThreadId;
        private readonly object gate = new object();
        private readonly Dictionary<(HoldemSpeechAudience, Guid, Guid), Record> records = new Dictionary<(HoldemSpeechAudience, Guid, Guid), Record>();
        private bool disposed;

        public DealerFeatureMailbox(IDealerFeatureSourcePort source)
        { this.source = source ?? throw new ArgumentNullException(nameof(source)); }

        public void Observe() => ObserveAvailable();

        private bool ObserveAvailable()
        {
            CheckOwner();
            // Never acquire room authority while holding a worker mailbox lock.
            var input = source.ReadFeatureInput() ?? throw new InvalidOperationException("Missing feature source read.");
            if (input.Error == HoldemRoomError.Paused || input.Error == HoldemRoomError.Disconnected) return false;
            if (input.Error != HoldemRoomError.None) throw new InvalidOperationException("Feature input unavailable: " + input.Error);
            lock (gate)
            {
                var seen = new HashSet<(HoldemSpeechAudience, Guid, Guid)>();
                Register(input.Table, seen); Register(input.Eliminated, seen);
                var stale = new List<(HoldemSpeechAudience, Guid, Guid)>();
                foreach (var key in records.Keys) if (!seen.Contains(key)) stale.Add(key);
                foreach (var key in stale) records.Remove(key);
            }
            return true;
        }

        private void Register(DealerFeatureWindow window, HashSet<(HoldemSpeechAudience, Guid, Guid)> seen)
        {
            if (window == null) return;
            for (int i = 0; i < window.Source.Count; i++)
            {
                var entry = window.Source.GetEntry(i);
                var key = (window.Source.Audience, window.Source.WindowId, entry.DesignationId);
                seen.Add(key);
                if (!records.TryGetValue(key, out var record))
                {
                    record = new Record { Work = new DealerFeatureWork(window, entry) };
                    records.Add(key, record);
                }
                if (!ReferenceEquals(record.Work.Lifetime, window.Lifetime))
                    throw new InvalidOperationException("A feature window changed lifetime without changing identity.");
                lock (window.Lifetime.Gate)
                    if (window.Lifetime.Closed && record.Kind == DealerFeatureOutcomeKind.Pending)
                        record.Kind = DealerFeatureOutcomeKind.ExpiredAtDeadline;
            }
        }

        public IReadOnlyList<DealerFeatureWork> TakeNewWork() => TakeNewWorkCore(null);

        /// <summary>Issue only this channel's work without consuming the other channel's work.</summary>
        public IReadOnlyList<DealerFeatureWork> TakeNewWork(HoldemSpeechAudience audience)
        {
            CheckOwner();
            if (audience != HoldemSpeechAudience.Table && audience != HoldemSpeechAudience.Eliminated)
                throw new ArgumentOutOfRangeException(nameof(audience));
            return TakeNewWorkCore(audience);
        }

        private IReadOnlyList<DealerFeatureWork> TakeNewWorkCore(HoldemSpeechAudience? audience)
        {
            if (!ObserveAvailable()) return Array.Empty<DealerFeatureWork>();
            lock (gate)
            {
                var result = new List<DealerFeatureWork>();
                foreach (var record in records.Values)
                {
                    if (audience.HasValue && record.Work.Audience != audience.Value) continue;
                    lock (record.Work.Lifetime.Gate)
                        if (!record.Issued && !record.Work.Lifetime.Closed && record.Kind == DealerFeatureOutcomeKind.Pending)
                        { record.Issued = true; result.Add(record.Work); }
                }
                return result.AsReadOnly();
            }
        }

        public IReadOnlyList<DealerFeatureOutcome> ReadOutcomes()
        {
            Observe();
            lock (gate)
            {
                var result = new List<DealerFeatureOutcome>();
                foreach (var record in records.Values)
                    result.Add(new DealerFeatureOutcome(record.Work, record.Kind, record.Features));
                return result.AsReadOnly();
            }
        }

        /// <summary>
        /// Pure conversion of a frozen Table input, in its original order. Does not read a port,
        /// post a response, choose cards or invent fallback policy. Failed outcomes stay distinct;
        /// the caller decides how to handle them. Eliminated outcomes are not live candidates.
        /// </summary>
        public static bool TryBuildTableBatch(HoldemDealerWork frozen, IReadOnlyList<DealerFeatureOutcome> outcomes,
            double minimumConfidence, out HoldemDealerInterpretationBatch batch, out DealerFeatureBatchIssues issues)
            => TryBuildTableBatch(frozen, outcomes, minimumConfidence, false, out batch, out issues);

        /// <summary>
        /// V3 sentence-level fallback after the same strict identity checks. Terminal failures become
        /// Level 0 without discarding valid candidates or changing their feature outcomes. Pending
        /// work is not complete; missing, duplicate or foreign work remains a protocol error.
        /// Issues are host-only diagnostics, not a player-visible success/failure signal.
        /// </summary>
        public static bool TryBuildTableBatchWithFallback(HoldemDealerWork frozen,
            IReadOnlyList<DealerFeatureOutcome> outcomes, double minimumConfidence,
            out HoldemDealerInterpretationBatch batch, out DealerFeatureBatchIssues issues)
            => TryBuildTableBatch(frozen, outcomes, minimumConfidence, true, out batch, out issues);

        private static bool TryBuildTableBatch(HoldemDealerWork frozen, IReadOnlyList<DealerFeatureOutcome> outcomes,
            double minimumConfidence, bool useSentenceFallback,
            out HoldemDealerInterpretationBatch batch, out DealerFeatureBatchIssues issues)
        {
            batch = null; issues = DealerFeatureBatchIssues.None;
            if (frozen == null) throw new ArgumentNullException(nameof(frozen));
            if (outcomes == null) throw new ArgumentNullException(nameof(outcomes));
            if (double.IsNaN(minimumConfidence) || double.IsInfinity(minimumConfidence)
                || minimumConfidence < 0 || minimumConfidence > 1)
                throw new ArgumentOutOfRangeException(nameof(minimumConfidence));
            var source = frozen.SourceUtterances; var deal = frozen.TargetDeal;
            if (frozen.InputKind != HoldemDealerInputKind.ClosedBettingWindow || source == null || deal == null
                || source.Audience != HoldemSpeechAudience.Table || source.SessionId != deal.SessionId
                || source.HandId != deal.HandId || source.WindowId == Guid.Empty || source.WindowId == deal.WindowId
                || (int)source.Street + 1 != (int)deal.Street)
                throw new ArgumentException("A frozen Table input and its next deal are required.", nameof(frozen));

            var byMessage = new Dictionary<Guid, DealerFeatureOutcome>();
            foreach (var outcome in outcomes)
            {
                if (outcome == null || outcome.Work == null)
                    throw new ArgumentException("Null feature outcome.", nameof(outcomes));
                var work = outcome.Work; var entry = work.Entry;
                if (work.Audience == HoldemSpeechAudience.Eliminated) continue;
                if (work.Audience != HoldemSpeechAudience.Table || work.SessionId != source.SessionId
                    || work.HandId != source.HandId || work.WindowId != source.WindowId || work.Street != source.Street
                    || entry == null || entry.SessionId != work.SessionId || entry.HandId != work.HandId
                    || entry.WindowId != work.WindowId || entry.Street != work.Street || entry.DesignationId == Guid.Empty)
                    throw new ArgumentException("Feature outcome belongs to another input.", nameof(outcomes));
                if (byMessage.ContainsKey(entry.CommandId))
                    throw new ArgumentException("Duplicate feature outcome.", nameof(outcomes));
                byMessage.Add(entry.CommandId, outcome);
            }
            if (byMessage.Count != source.Count)
                throw new ArgumentException("Every frozen Table sentence needs exactly one outcome.", nameof(outcomes));

            var entries = new HoldemDealerInterpretation[source.Count];
            var seen = new HashSet<Guid>();
            for (int i = 0; i < source.Count; i++)
            {
                var expected = source.GetEntry(i);
                if (expected == null || expected.SessionId != source.SessionId || expected.HandId != source.HandId
                    || expected.WindowId != source.WindowId || expected.Street != source.Street
                    || !seen.Add(expected.CommandId) || !byMessage.TryGetValue(expected.CommandId, out var outcome)
                    || expected.DesignationId == Guid.Empty || expected.DesignationId != outcome.Work.Entry.DesignationId
                    || expected.Speaker != outcome.Work.Entry.Speaker || expected.Text != outcome.Work.Entry.Text
                    || expected.Ordinal != outcome.Work.Entry.Ordinal)
                    throw new ArgumentException("Feature identity does not match the frozen designation.", nameof(outcomes));
                switch (outcome.Kind)
                {
                    case DealerFeatureOutcomeKind.ValidFeatures:
                        if (outcome.Features == null || outcome.Features.UtteranceId != expected.CommandId)
                            throw new ArgumentException("Feature identity does not match its sentence.", nameof(outcomes));
                        entries[i] = outcome.Features.Normalize(minimumConfidence); break;
                    case DealerFeatureOutcomeKind.Pending: issues |= DealerFeatureBatchIssues.Pending; break;
                    case DealerFeatureOutcomeKind.InvalidResponse: issues |= DealerFeatureBatchIssues.InvalidResponse; break;
                    case DealerFeatureOutcomeKind.ProviderFailure: issues |= DealerFeatureBatchIssues.ProviderFailure; break;
                    case DealerFeatureOutcomeKind.ExpiredAtDeadline: issues |= DealerFeatureBatchIssues.ExpiredAtDeadline; break;
                    default: throw new ArgumentException("Unknown feature outcome.", nameof(outcomes));
                }
                if (outcome.Kind != DealerFeatureOutcomeKind.ValidFeatures && outcome.Features != null)
                    throw new ArgumentException("Failed outcomes cannot carry accepted features.", nameof(outcomes));
                if (useSentenceFallback && outcome.Kind != DealerFeatureOutcomeKind.ValidFeatures
                    && outcome.Kind != DealerFeatureOutcomeKind.Pending)
                    entries[i] = new HoldemDealerInterpretation(expected.CommandId,
                        HoldemDealerIntent.NoRequest, null, null, 0, 0, level: 0);
            }
            if ((issues & DealerFeatureBatchIssues.Pending) != 0
                || !useSentenceFallback && issues != DealerFeatureBatchIssues.None) return false;
            batch = new HoldemDealerInterpretationBatch(deal.SessionId, deal.HandId, deal.WindowId,
                frozen.ExpectedVersion, deal.Street, source.WindowId, entries);
            return true;
        }

        /// <summary>Parse-and-discard. Invalid output is terminal InvalidResponse, never valid NoRequest.</summary>
        public bool TryPostJson(DealerFeatureWork work, string json)
        {
            if (work == null) return false;
            return DealerResponseParser.TryParse(work.Entry.CommandId, json, out var features)
                ? TryPost(work, DealerFeatureOutcomeKind.ValidFeatures, features)
                : TryPost(work, DealerFeatureOutcomeKind.InvalidResponse, null);
        }
        public bool TryPostFeatures(DealerFeatureWork work, HoldemDealerFeatures features)
            => features != null && work != null && features.UtteranceId == work.Entry.CommandId
                && TryPost(work, DealerFeatureOutcomeKind.ValidFeatures, features);
        public bool TryPostProviderFailure(DealerFeatureWork work)
            => TryPost(work, DealerFeatureOutcomeKind.ProviderFailure, null);

        private bool TryPost(DealerFeatureWork work, DealerFeatureOutcomeKind kind, HoldemDealerFeatures features)
        {
            lock (gate)
            {
                if (disposed || work == null || !records.TryGetValue((work.Audience,
                    work.WindowId, work.Entry.DesignationId), out var record)
                    || !ReferenceEquals(work, record.Work) || !record.Issued) return false;
                // Intake freeze and response commit share this lock, giving a real deadline boundary.
                lock (work.Lifetime.Gate)
                {
                    if (work.Lifetime.Closed) return false;
                    if (record.Kind != DealerFeatureOutcomeKind.Pending)
                        return record.Kind == kind && SameFeatures(record.Features, features);
                    record.Kind = kind; record.Features = features; return true;
                }
            }
        }
        private static bool SameFeatures(HoldemDealerFeatures left, HoldemDealerFeatures right)
            => left == null || right == null ? left == right : left.UtteranceId == right.UtteranceId
                && left.Rank == right.Rank && left.Suit == right.Suit && left.Color == right.Color
                && left.Directness == right.Directness && left.HasRequestForm == right.HasRequestForm
                && left.Confidence == right.Confidence;
        private void CheckOwner()
        {
            if (Thread.CurrentThread.ManagedThreadId != ownerThread)
                throw new InvalidOperationException("Feature observation and disposal belong to the owner thread.");
            if (disposed) throw new ObjectDisposedException(nameof(DealerFeatureMailbox));
        }
        public void Dispose()
        {
            if (Thread.CurrentThread.ManagedThreadId != ownerThread)
                throw new InvalidOperationException("Feature disposal belongs to the owner thread.");
            lock (gate) { disposed = true; records.Clear(); }
        }
    }
}
