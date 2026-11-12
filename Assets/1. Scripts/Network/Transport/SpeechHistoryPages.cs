using System;
using System.Collections.Generic;
using System.Text;
using Poker.Application;
using Poker.Foundation;
using UnityEngine;

namespace Poker.Transport
{
    /// <summary>Bounded wire delivery of current-hand speech; cursor is public message count, not dealer ordering.</summary>
    public static class SpeechHistoryPages
    {
        private const int MaximumPageEntries = 64;
        private const int EnvelopeReserve = 2048;

        public static HoldemRoomPacket Create(HoldemRoomPacket packet, int after, int eliminatedAfter = 0)
        {
            if (packet?.ownUtterances?.unlimitedMessages != true) return packet;
            var table = new PageSource(new SpeechPacketStream(packet, false), after);
            var eliminated = packet.hasEliminatedSpeech
                ? new PageSource(new SpeechPacketStream(packet, true), eliminatedAfter) : null;
            while (true)
            {
                table.Apply(packet.viewerSeat); eliminated?.Apply(packet.viewerSeat);
                if (Encoding.UTF8.GetByteCount(JsonUtility.ToJson(packet)) <= HoldemFrameCodec.MaximumBytes - EnvelopeReserve)
                    return packet;
                if (!table.Shrink() && !(eliminated?.Shrink() ?? false))
                    throw new InvalidOperationException("Speech page cannot fit the transport frame.");
            }
        }

        private sealed class PageSource
        {
            private readonly SpeechPacketStream stream;
            private readonly HoldemPublicUtteranceEntryPacket[] raw;
            private readonly HoldemOwnUtteranceEntryPacket[] own;
            private readonly int after;
            private int count;
            internal PageSource(SpeechPacketStream stream, int after)
            {
                this.stream = stream; this.after = after;
                raw = stream.Messages?.entries ?? throw new ArgumentException("Missing channel speech.");
                own = stream.Own?.entries ?? throw new ArgumentException("Missing own channel speech.");
                if (after < 0 || after > raw.Length) throw new ArgumentOutOfRangeException(nameof(after));
                var patches = new List<HoldemDesignationPatchPacket>();
                foreach (var entry in own)
                    if (Guid.TryParseExact(entry.designationId, "N", out var id) && id != Guid.Empty)
                        patches.Add(new HoldemDesignationPatchPacket { commandId = entry.commandId, designationId = entry.designationId });
                if (patches.Count > 3) throw new ArgumentException("Too many designated streets.");
                stream.HasPage = true; stream.Start = after; stream.Total = raw.Length; stream.Patches = patches.ToArray();
                count = Math.Min(MaximumPageEntries, raw.Length - after);
            }
            internal void Apply(int viewer)
            {
                var pageOwn = new List<HoldemOwnUtteranceEntryPacket>(); int ownIndex = 0;
                for (int i = 0; i < after + count; i++)
                    if (raw[i].seat == viewer)
                    {
                        if (ownIndex >= own.Length) throw new ArgumentException("Own speech is missing.");
                        if (i >= after) pageOwn.Add(own[ownIndex]);
                        ownIndex++;
                    }
                var page = new HoldemPublicUtteranceEntryPacket[count]; Array.Copy(raw, after, page, 0, count);
                stream.Messages.entries = page; stream.Own.entries = pageOwn.ToArray();
            }
            internal bool Shrink() { if (count <= 1) return false; count /= 2; return true; }
        }
    }

    // One algorithm, two deliberately separate wire subtrees/cursors/audiences.
    internal sealed class SpeechPacketStream
    {
        private readonly HoldemRoomPacket packet;
        private readonly bool eliminated;
        internal SpeechPacketStream(HoldemRoomPacket packet, bool eliminated) { this.packet = packet; this.eliminated = eliminated; }
        internal HoldemOwnUtterancePacket Own => eliminated ? packet.eliminatedSpeech?.own : packet.ownUtterances;
        internal HoldemPublicUtterancePacket Messages => eliminated ? packet.eliminatedSpeech?.messages : packet.publicUtterances;
        internal bool HasPage { get => eliminated ? packet.eliminatedSpeech?.hasPage == true : packet.hasSpeechPage;
            set { if (eliminated) packet.eliminatedSpeech.hasPage = value; else packet.hasSpeechPage = value; } }
        internal int Start { get => eliminated ? packet.eliminatedSpeech.start : packet.speechStart;
            set { if (eliminated) packet.eliminatedSpeech.start = value; else packet.speechStart = value; } }
        internal int Total { get => eliminated ? packet.eliminatedSpeech.total : packet.speechTotal;
            set { if (eliminated) packet.eliminatedSpeech.total = value; else packet.speechTotal = value; } }
        internal HoldemDesignationPatchPacket[] Patches { get => eliminated ? packet.eliminatedSpeech.designationPatches : packet.designationPatches;
            set { if (eliminated) packet.eliminatedSpeech.designationPatches = value; else packet.designationPatches = value; } }
    }

    /// <summary>Transport-local catch-up. No UI state is published until a full current-hand prefix is available.</summary>
    public sealed class SpeechHistoryAssembler
    {
        private readonly List<HoldemPublicUtteranceEntryPacket> published = new List<HoldemPublicUtteranceEntryPacket>();
        private readonly List<HoldemOwnUtteranceEntryPacket> own = new List<HoldemOwnUtteranceEntryPacket>();
        private string session, hand;
        private int viewer;
        private long latestRevision = -1, bytes;
        private long latestEliminatedRevision = -1;
        private readonly bool eliminated;
        public SpeechHistoryAssembler(bool eliminated = false) { this.eliminated = eliminated; }
        public bool NeedsCatchUp { get; private set; }
        public int Cursor => published.Count;
        public string HandId => hand;

        public HoldemRoomPacket Accept(HoldemRoomPacket packet)
        {
            if (packet == null) throw Invalid();
            if (!eliminated) HoldemDealerSelectionPacketReader.Read(packet);
            if (eliminated && !packet.hasEliminatedSpeech)
            {
                HoldemUtterancePacketReader.Read(packet, true);
                published.Clear(); own.Clear(); bytes = 0; hand = null; NeedsCatchUp = false;
                latestEliminatedRevision = -1;
                return packet;
            }
            var stream = new SpeechPacketStream(packet, eliminated);
            if (!stream.HasPage)
            {
                if (stream.Own?.unlimitedMessages == true) throw Invalid();
                return packet;
            }
            if (stream.Own?.unlimitedMessages != true || packet.game == null
                || packet.game.bettingPolicy != (int)HoldemBettingPolicy.SharedAnteSequential
                || stream.Messages?.entries == null || stream.Own.entries == null
                || stream.Messages.hasHistoryOrder || stream.Patches == null
                || stream.Patches.Length > 3 || stream.Messages.entries.Length > 64
                || stream.Own.entries.Length > stream.Messages.entries.Length
                || stream.Start < 0 || stream.Total < 0 || stream.Start > stream.Total
                || stream.Total > HoldemUtteranceInbox.MaximumRetainedEntries
                || stream.Start + stream.Messages.entries.Length > stream.Total)
                throw Invalid();
            if (eliminated && (packet.eliminatedSpeech.revision < 0 || !IsEliminatedSeat(packet, packet.viewerSeat, false))) throw Invalid();
            if (session != null && (session != packet.sessionId || viewer != packet.viewerSeat)) throw Invalid();
            if (packet.revision < latestRevision || eliminated && packet.revision == latestRevision
                && packet.EliminatedSpeechRevision < latestEliminatedRevision) return null;
            if (hand != packet.game.handId)
            {
                if (stream.Start != 0) throw Invalid();
                published.Clear(); own.Clear(); bytes = 0;
                session = packet.sessionId; viewer = packet.viewerSeat; hand = packet.game.handId;
                latestEliminatedRevision = -1;
            }
            if (stream.Start > published.Count || stream.Total < published.Count) throw Invalid();
            int ownIndex = 0;
            int ownPosition = 0;
            for (int i = 0; i < stream.Start; i++) if (published[i].seat == viewer) ownPosition++;
            int beforeCount = published.Count;
            var knownCommands = new HashSet<string>();
            foreach (var item in own) knownCommands.Add(item.commandId);
            for (int i = 0; i < stream.Messages.entries.Length; i++)
            {
                var item = stream.Messages.entries[i];
                if (item == null || item.seat < 1 || item.seat > 4 || item.street < 0 || item.street > 3
                    || item.street > packet.game.street || string.IsNullOrWhiteSpace(item.text)
                    || item.text.Length > stream.Own.maximumTextLength
                    || !HoldemPlayerText.IsValidSingleLine(item.text) || item.historyPosition != 0) throw Invalid();
                if (eliminated && !IsEliminatedSeat(packet, item.seat, true)) throw Invalid();
                int index = stream.Start + i;
                if (index < published.Count)
                {
                    var prior = published[index];
                    if (prior.seat != item.seat || prior.street != item.street || prior.text != item.text) throw Invalid();
                }
                else
                {
                    if (published.Count > 0 && published[published.Count - 1].street > item.street) throw Invalid();
                    bytes += 2L * item.text.Length + 512;
                    if (bytes > HoldemUtteranceInbox.MaximumRetainedBytes) throw Invalid();
                    published.Add(item);
                }
                if (item.seat != viewer) continue;
                if (ownIndex >= stream.Own.entries.Length) throw Invalid();
                var entry = stream.Own.entries[ownIndex++];
                if (entry == null || entry.street != item.street || entry.text != item.text
                    || !ValidId(entry.commandId) || !ValidId(entry.windowId)) throw Invalid();
                if (ownPosition < own.Count)
                {
                    var prior = own[ownPosition];
                    if (prior.commandId != entry.commandId || prior.windowId != entry.windowId || prior.street != entry.street || prior.text != entry.text
                        || prior.designationEligible != entry.designationEligible
                        || NonemptyId(prior.designationId) && prior.designationId != entry.designationId) throw Invalid();
                }
                else
                {
                    // A replayed public prefix cannot introduce a new own command identity.
                    if (index < beforeCount) throw Invalid();
                    if (!knownCommands.Add(entry.commandId)) throw Invalid();
                    own.Add(entry);
                }
                ownPosition++;
            }
            if (ownIndex != stream.Own.entries.Length) throw Invalid();
            var selected = new HashSet<string>();
            foreach (var patch in stream.Patches)
            {
                if (patch == null || !ValidId(patch.commandId) || !ValidId(patch.designationId)
                    || patch.commandId == patch.designationId || !selected.Add(patch.commandId)) throw Invalid();
                int index = own.FindIndex(e => e.commandId == patch.commandId);
                if (index < 0) continue; // The designated source may be on a later page.
                var entry = own[index];
                if (NonemptyId(entry.designationId) && entry.designationId != patch.designationId) throw Invalid();
                own[index] = new HoldemOwnUtteranceEntryPacket { commandId = entry.commandId, windowId = entry.windowId,
                    street = entry.street, text = entry.text, designationEligible = entry.designationEligible, designationId = patch.designationId };
            }
            latestRevision = packet.revision;
            latestEliminatedRevision = packet.EliminatedSpeechRevision;
            NeedsCatchUp = published.Count < stream.Total;
            if (NeedsCatchUp)
            {
                if (stream.Messages.entries.Length == 0) throw Invalid();
                return null;
            }
            if (stream.Patches.Length != stream.Own.designationRevision) throw Invalid();
            foreach (var patch in stream.Patches)
                if (!own.Exists(e => e.commandId == patch.commandId && e.designationId == patch.designationId)) throw Invalid();
            stream.HasPage = false; stream.Start = stream.Total = 0; stream.Patches = null;
            stream.Messages.entries = published.ToArray(); stream.Own.entries = own.ToArray();
            // Do not allow a malformed final page to become a committed transport snapshot.
            HoldemUtterancePacketReader.Read(packet, eliminated);
            HoldemPublicUtterancePacketReader.Read(packet, eliminated: eliminated);
            return packet;
        }

        private static bool IsEliminatedSeat(HoldemRoomPacket packet, int seat, bool mustNotBeDealt)
        {
            if (packet.game.seats != null)
                foreach (var item in packet.game.seats)
                    if (item != null && item.seat == seat && item.status == (int)HoldemSeatStatus.Busted
                        && (!mustNotBeDealt || !item.dealtIn)) return true;
            return false;
        }

        private static bool ValidId(string value) => Guid.TryParseExact(value, "N", out var id) && id != Guid.Empty;
        private static bool NonemptyId(string value) => Guid.TryParseExact(value, "N", out var id) && id != Guid.Empty;
        private static ArgumentException Invalid() => new ArgumentException("Invalid speech history page.");
    }
}
