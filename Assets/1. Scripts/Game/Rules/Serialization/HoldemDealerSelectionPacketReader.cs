using System;
using System.Collections.Generic;
using Poker.Foundation;

namespace Poker.Application
{
    public static class HoldemDealerSelectionPacketReader
    {
        public static IReadOnlyList<HoldemDealerSelectionFeedback> Read(HoldemRoomPacket packet)
        {
            if (packet == null) throw new ArgumentNullException(nameof(packet));
            var items = packet.dealerSelections;
            // An absent field is an older protocol-1 host with no sentence feedback.
            if (items == null || items.Length == 0) return Array.Empty<HoldemDealerSelectionFeedback>();
            var game = packet.game;
            if (packet.protocol != HoldemRoomPacketMapper.ProtocolVersion || !packet.hasGame || game == null
                || items.Length > 3 || packet.members == null || packet.viewerSeat < 1 || packet.viewerSeat > 4)
                throw Invalid();
            var windows = new HashSet<Guid>();
            var result = new HoldemDealerSelectionFeedback[items.Length];
            int previousStreet = 0;
            int previousBoardIndex = -1;
            for (int i = 0; i < items.Length; i++)
            {
                var item = items[i];
                if (item == null || item.handId != game.handId || !Id(item.handId, out var hand)
                    || !Id(item.dealWindowId, out var window) || !windows.Add(window)
                    || item.street <= previousStreet || item.street > (int)HoldemStreet.River || item.street > game.street
                    || string.IsNullOrWhiteSpace(item.text) || item.text.Length > 4096 || !HoldemUtteranceInbox.ValidText(item.text)
                    || !game.hasResult && item.speaker != packet.viewerSeat
                    || game.hasResult && game.street != (int)HoldemStreet.Complete
                    || !Array.Exists(packet.members, m => m != null && m.seat == item.speaker)) throw Invalid();
                previousStreet = item.street;
                if (item.hasAppliedCard)
                {
                    if (game.board == null || item.boardIndex <= previousBoardIndex
                        || item.boardIndex < 0 || item.boardIndex >= game.board.Length || item.boardIndex > 4
                        || item.card < 0 || item.card > 51 || game.board[item.boardIndex] != item.card) throw Invalid();
                    previousBoardIndex = item.boardIndex;
                }
                result[i] = new HoldemDealerSelectionFeedback(hand, window, (HoldemStreet)item.street,
                    new SeatId(item.speaker), item.text, item.cardChanged,
                    item.hasAppliedCard ? (int?)item.boardIndex : null,
                    item.hasAppliedCard ? (Card?)Card.FromId(item.card) : null);
            }
            return Array.AsReadOnly(result);
        }
        private static bool Id(string value, out Guid id) => Guid.TryParseExact(value, "N", out id) && id != Guid.Empty;
        private static ArgumentException Invalid() => new ArgumentException("Invalid dealer selection feedback.");
    }
}
