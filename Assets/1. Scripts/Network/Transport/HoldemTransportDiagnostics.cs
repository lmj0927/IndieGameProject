namespace Poker.Transport
{
    /// <summary>The server path that observed/initiated closure, not a claim about its underlying cause.</summary>
    public enum HoldemPeerCloseTrigger { ChannelObserved, RateLimit, InvalidEnvelope, SendFailed }

    /// <summary>
    /// Immutable host-local observation. Contains no identity, address, key, text, cards or wire payload.
    /// ChannelReason is captured before disposal; None is valid for a server-initiated close.
    /// ConnectionLost does not distinguish timeout from IO failure; Backpressure does not identify direction.
    /// </summary>
    public sealed class HoldemPeerCloseRecord
    {
        internal HoldemPeerCloseRecord(long sequence, long elapsedMilliseconds, int seat, bool wasAdmitted,
            bool wasHost, HoldemPeerCloseTrigger trigger, HoldemTransportCloseReason channelReason)
        {
            Sequence = sequence; ElapsedMilliseconds = elapsedMilliseconds; Seat = seat;
            WasAdmitted = wasAdmitted; WasHost = wasHost; Trigger = trigger; ChannelReason = channelReason;
        }
        public long Sequence { get; }
        public long ElapsedMilliseconds { get; }
        /// <summary>Bound seat when admitted, otherwise zero. Not a reconnect capability.</summary>
        public int Seat { get; }
        public bool WasAdmitted { get; }
        public bool WasHost { get; }
        public HoldemPeerCloseTrigger Trigger { get; }
        public HoldemTransportCloseReason ChannelReason { get; }
    }
}
