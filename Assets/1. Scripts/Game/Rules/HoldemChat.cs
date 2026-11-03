using System;
using Poker.Foundation;

namespace Poker.Application
{
    public enum HoldemChatError { None = 0, Disabled, EmptyText, TextTooLong, InvalidText, CommandConflict, DealerRequestRejected }

    /// <summary>
    /// Seat-free chat input. A dealer request also names the open speech window it targets; the flag
    /// reaches only the host's private inbox and is never part of any player's chat projection.
    /// </summary>
    public sealed class HoldemRoomChat
    {
        public HoldemRoomChat(Guid commandId, string text)
        {
            if (commandId == Guid.Empty) throw new ArgumentException("A command ID is required.", nameof(commandId));
            CommandId = commandId; Text = text ?? throw new ArgumentNullException(nameof(text));
        }

        public HoldemRoomChat(Guid commandId, string text, Guid sessionId, Guid handId, Guid windowId, HoldemStreet street)
            : this(commandId, text)
        {
            DealerRequest = new HoldemRoomUtterance(sessionId, handId, windowId, commandId, street, text);
        }

        public Guid CommandId { get; }
        public string Text { get; }
        public HoldemRoomUtterance DealerRequest { get; }
        public bool ToDealer => DealerRequest != null;
    }

    public sealed class HoldemRoomChatReceipt
    {
        internal HoldemRoomChatReceipt(HoldemRoomError error, HoldemChatError chat, HoldemUtteranceReceipt dealerRequest = null)
        { Error = error; Chat = chat; DealerRequest = dealerRequest; }
        public HoldemRoomError Error { get; }
        public HoldemChatError Chat { get; }
        /// <summary>Only for the sender's own dealer request; null for ordinary chat.</summary>
        public HoldemUtteranceReceipt DealerRequest { get; }
        public bool Accepted => Error == HoldemRoomError.None && Chat == HoldemChatError.None;
    }

    /// <summary>Public chat line. Contains no dealer-request marker by construction.</summary>
    public sealed class HoldemChatEntry
    {
        public HoldemChatEntry(long sequence, SeatId speaker, string text)
        {
            if (sequence < 1) throw new ArgumentOutOfRangeException(nameof(sequence));
            if (!speaker.IsValid) throw new ArgumentException("A speaker seat is required.", nameof(speaker));
            Sequence = sequence; Speaker = speaker; Text = text ?? throw new ArgumentNullException(nameof(text));
        }
        public long Sequence { get; }
        public SeatId Speaker { get; }
        public string Text { get; }
    }

    /// <summary>The most recent public chat lines, oldest first. Identical for every member.</summary>
    public sealed class HoldemChatLog
    {
        private readonly HoldemChatEntry[] entries;
        public HoldemChatLog(int maximumTextLength, HoldemChatEntry[] entries)
        {
            if (maximumTextLength < 1) throw new ArgumentOutOfRangeException(nameof(maximumTextLength));
            MaximumTextLength = maximumTextLength;
            this.entries = (HoldemChatEntry[])(entries ?? throw new ArgumentNullException(nameof(entries))).Clone();
        }
        public int MaximumTextLength { get; }
        public int Count => entries.Length;
        public HoldemChatEntry GetEntry(int index) => entries[index];
    }
}
