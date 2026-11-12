using System;
using System.Collections.Generic;
using Poker.Foundation;

namespace Poker.Application
{
    /// <summary>Immutable own-text projection from the same already-validated room/game packet.</summary>
    public static class HoldemUtterancePacketReader
    {
        public static HoldemEliminatedSpeechView ReadEliminatedView(HoldemRoomPacket room, HoldemPublicUtterances previous = null)
        {
            var own = Read(room, true);
            var messages = HoldemPublicUtterancePacketReader.Read(room, previous, eliminated: true);
            return own == null ? null : new HoldemEliminatedSpeechView(own, messages, room.EliminatedSpeechRevision);
        }

        public static HoldemUtteranceView Read(HoldemRoomPacket room, bool eliminated = false)
        {
            if (room == null) throw new ArgumentNullException(nameof(room));
            if (eliminated)
            {
                bool viewerEliminated = false;
                if (room.game?.seats != null)
                    foreach (var seat in room.game.seats)
                        if (seat != null && seat.seat == room.viewerSeat && seat.viewer
                            && seat.status == (int)HoldemSeatStatus.Busted) viewerEliminated = true;
                bool expected = room.hasGame && room.game != null && viewerEliminated
                    && room.game.bettingPolicy == (int)HoldemBettingPolicy.SharedAnteSequential;
                if (room.hasEliminatedSpeech != expected || !expected && !EmptyEliminatedProjection(room.eliminatedSpeech)) throw Invalid();
                if (!expected) return null;
                if (room.eliminatedSpeech == null || room.eliminatedSpeech.revision < 0) throw Invalid();
            }
            else if (!room.hasOwnUtterances) return null;
            var input = eliminated ? room.eliminatedSpeech.own : room.ownUtterances;
            if (room.protocol != HoldemRoomPacketMapper.ProtocolVersion || !room.hasGame || room.game == null || input == null || input.entries == null
                || input.entries.Length > HoldemUtteranceInbox.MaximumRetainedEntries || room.viewerSeat < 1 || room.viewerSeat > 4
                || room.game.street < 0 || room.game.street > (int)HoldemStreet.Complete
                || input.maximumTextLength < 1 || input.maximumTextLength > 4096 || input.remaining < 0 || input.remaining > 32)
                throw Invalid();
            Guid session = Id(room.sessionId), hand = Id(room.game.handId);
            bool shared = room.game.bettingPolicy == (int)HoldemBettingPolicy.SharedAnteFixed
                || room.game.bettingPolicy == (int)HoldemBettingPolicy.SharedAnteSequential;
            bool unlimited = room.game.bettingPolicy == (int)HoldemBettingPolicy.SharedAnteSequential;
            if (input.unlimitedMessages != unlimited || unlimited && input.remaining != 0
                || !unlimited && input.entries.Length > (shared ? 256 : 128)
                || (eliminated ? room.eliminatedSpeech.hasPage : room.hasSpeechPage)) throw Invalid();
            int streetLimit = unlimited ? HoldemUtteranceInbox.MaximumRetainedEntries : shared ? 64 : 32;
            if (!shared && input.entries.Length > 128) throw Invalid();
            if (shared && (!room.hasRules || room.rules == null || room.rules.bettingPolicy != room.game.bettingPolicy)) throw Invalid();
            Guid window = Id(input.windowId, true);
            if (window == Guid.Empty && (input.canSubmit || input.canDesignate || input.remaining != 0)) throw Invalid();
            if (window != Guid.Empty && (room.game.street > (int)HoldemStreet.River || room.game.hasResult
                || room.game.isOver || !shared && (room.game.dealPending || room.game.revealPending || room.game.currentSeat == 0)
                || room.game.settlementState == (int)HoldemSettlementState.AwaitingOddChipPriority
                )) throw Invalid();
            if (input.canSubmit && (room.paused || !unlimited && (input.backlogged || input.remaining == 0))) throw Invalid();
            if (input.canDesignate && (room.paused || input.backlogged || room.game.street >= (int)HoldemStreet.River)) throw Invalid();
            if (input.canDesignate && room.game.bettingPolicy == (int)HoldemBettingPolicy.SharedAnteFixed
                && !room.game.dealPending) throw Invalid();
            if (input.canDesignate && unlimited && room.game.revealPending) throw Invalid();
            HoldemSeatPacket viewer = null;
            if (room.game.seats != null)
                foreach (var seat in room.game.seats) if (seat != null && seat.seat == room.viewerSeat) viewer = seat;
            if (viewer == null || !viewer.viewer || (input.canSubmit || input.canDesignate)
                && (eliminated ? viewer.dealtIn || viewer.status != (int)HoldemSeatStatus.Busted
                    : (!viewer.dealtIn || viewer.status == (int)HoldemSeatStatus.Busted)
                        && !(room.game.bettingPolicy == (int)HoldemBettingPolicy.NoLimit
                            && !viewer.dealtIn && viewer.status == (int)HoldemSeatStatus.Busted)))
                throw Invalid();
            if (eliminated && viewer.dealtIn && input.entries.Length > 0) throw Invalid();
            if (shared && input.canDesignate && viewer.status == (int)HoldemSeatStatus.Folded) throw Invalid();
            var commands = new HashSet<Guid>();
            var windows = new Dictionary<int, Guid>();
            var windowStreets = new Dictionary<Guid, int>();
            var streetCounts = new int[4];
            var designatedStreets = new HashSet<int>();
            var designationIds = new HashSet<Guid>();
            var entries = new HoldemUtteranceEntry[input.entries.Length];
            int previousStreet = -1;
            for (int i = 0; i < entries.Length; i++)
            {
                var item = input.entries[i];
                if (item == null || item.street < previousStreet || item.street < 0 || item.street > 3
                    || item.street > room.game.street || string.IsNullOrWhiteSpace(item.text)
                    || item.text.Length > input.maximumTextLength || !HoldemUtteranceInbox.ValidText(item.text)) throw Invalid();
                Guid command = Id(item.commandId), entryWindow = Id(item.windowId);
                if (!commands.Add(command) || ++streetCounts[item.street] > streetLimit
                    || windows.TryGetValue(item.street, out var known) && known != entryWindow
                    || windowStreets.TryGetValue(entryWindow, out var knownStreet) && knownStreet != item.street
                    || item.street == room.game.street && window != Guid.Empty && window != entryWindow) throw Invalid();
                windows[item.street] = entryWindow; windowStreets[entryWindow] = item.street; previousStreet = item.street;
                var source = new HoldemUtteranceCommand(session, hand, entryWindow, command,
                    (HoldemStreet)item.street, new SeatId(room.viewerSeat), item.text);
                entries[i] = new HoldemUtteranceEntry(source, 0, !shared || item.designationEligible);
                Guid designation = string.IsNullOrEmpty(item.designationId) ? Guid.Empty : Id(item.designationId, true);
                if (designation != Guid.Empty)
                {
                    if (!entries[i].DesignationEligible || item.street >= (int)HoldemStreet.River
                        || !designatedStreets.Add(item.street) || !designationIds.Add(designation)) throw Invalid();
                    entries[i] = new HoldemUtteranceEntry(entries[i], designation);
                }
            }
            foreach (var designation in designationIds) if (commands.Contains(designation)) throw Invalid();
            if (designationIds.Count != input.designationRevision
                || input.canDesignate && designatedStreets.Contains(room.game.street)) throw Invalid();
            if (room.game.street <= (int)HoldemStreet.River && streetCounts[room.game.street] + input.remaining > streetLimit)
                throw Invalid();
            return new HoldemUtteranceView(session, hand, (HoldemStreet)room.game.street, new SeatId(room.viewerSeat),
                window, input.canSubmit, input.maximumTextLength, input.remaining, entries, input.backlogged, input.canDesignate, unlimited);
        }

        private static Guid Id(string text, bool allowEmpty = false)
        {
            if (text == null || text.Length != 32 || !Guid.TryParseExact(text, "N", out var id)
                || !allowEmpty && id == Guid.Empty) throw Invalid();
            return id;
        }
        // JsonUtility may materialize a null serializable object as an all-default subtree.
        // Only that empty shape is tolerated for a non-member; private payload is never ignored.
        private static bool EmptyEliminatedProjection(HoldemEliminatedSpeechPacket channel)
        {
            if (channel == null) return true;
            var own = channel.own; var messages = channel.messages;
            return channel.revision == 0 && !channel.hasPage && channel.start == 0 && channel.total == 0
                && (channel.designationPatches == null || channel.designationPatches.Length == 0)
                && (messages == null || !messages.hasHistoryOrder && (messages.entries == null || messages.entries.Length == 0))
                && (own == null || string.IsNullOrEmpty(own.windowId) && !own.canSubmit && !own.canDesignate
                    && !own.backlogged && !own.unlimitedMessages && own.designationRevision == 0
                    && own.maximumTextLength == 0 && own.remaining == 0 && (own.entries == null || own.entries.Length == 0));
        }
        private static ArgumentException Invalid() => new ArgumentException("Invalid own-text projection.");
    }
}
