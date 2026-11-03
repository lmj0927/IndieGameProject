using System;
using System.Collections.Generic;

namespace Poker.Application
{
    public sealed partial class HoldemRoom
    {
        public const int MaximumChatTextLength = 128;
        public const int RetainedChatEntries = 30;
        private const int RetainedChatReceipts = 256;
        private readonly bool chatEnabled;
        private readonly List<HoldemChatEntry> chat = new List<HoldemChatEntry>();
        private readonly Dictionary<Guid, ChatAcceptance> chatReceipts = new Dictionary<Guid, ChatAcceptance>();
        private readonly Queue<Guid> chatReceiptOrder = new Queue<Guid>();
        private long chatSequence;

        private sealed class ChatAcceptance
        {
            public Member Speaker;
            public string Text;
            public bool ToDealer;
            public HoldemRoomChatReceipt Receipt;
        }

        public bool ChatEnabled => chatEnabled;

        /// <summary>
        /// Always-open public chat. With a dealer request the text is first submitted to the current
        /// speech window; a rejected request posts nothing, so the sender can keep or rewrite it.
        /// </summary>
        public HoldemRoomChatReceipt SubmitChat(Guid connection, HoldemRoomChat input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            lock (gate)
            {
                var error = FindConnected(connection, out var member);
                if (error != HoldemRoomError.None) return new HoldemRoomChatReceipt(error, HoldemChatError.None);
                if (!chatEnabled) return ChatReject(HoldemChatError.Disabled);
                if (chatReceipts.TryGetValue(input.CommandId, out var previous))
                    return previous.Speaker == member && previous.Text == input.Text && previous.ToDealer == input.ToDealer
                        ? previous.Receipt : ChatReject(HoldemChatError.CommandConflict);
                if (string.IsNullOrWhiteSpace(input.Text)) return ChatReject(HoldemChatError.EmptyText);
                if (input.Text.Length > MaximumChatTextLength) return ChatReject(HoldemChatError.TextTooLong);
                if (!HoldemPlayerText.IsValidSingleLine(input.Text)) return ChatReject(HoldemChatError.InvalidText);
                HoldemUtteranceReceipt request = null;
                if (input.ToDealer)
                {
                    var speech = SubmitUtterance(connection, input.DealerRequest);
                    request = speech.Utterance;
                    if (!speech.Accepted)
                        return new HoldemRoomChatReceipt(speech.Error == HoldemRoomError.UtteranceRejected
                            ? HoldemRoomError.ChatRejected : speech.Error, HoldemChatError.DealerRequestRejected, request);
                }
                chat.Add(new HoldemChatEntry(++chatSequence, member.Seat, input.Text));
                if (chat.Count > RetainedChatEntries) chat.RemoveAt(0);
                revision++;
                var receipt = new HoldemRoomChatReceipt(HoldemRoomError.None, HoldemChatError.None, request);
                chatReceipts.Add(input.CommandId, new ChatAcceptance
                    { Speaker = member, Text = input.Text, ToDealer = input.ToDealer, Receipt = receipt });
                chatReceiptOrder.Enqueue(input.CommandId);
                if (chatReceiptOrder.Count > RetainedChatReceipts) chatReceipts.Remove(chatReceiptOrder.Dequeue());
                return receipt;
            }
        }

        // Called under the room gate.
        private HoldemChatLog ReadChat() => chatEnabled ? new HoldemChatLog(MaximumChatTextLength, chat.ToArray()) : null;

        private static HoldemRoomChatReceipt ChatReject(HoldemChatError error)
            => new HoldemRoomChatReceipt(HoldemRoomError.ChatRejected, error);
    }
}
