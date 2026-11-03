using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Poker.Transport
{
    public enum HoldemTransportCloseReason { None, LocalClosed, RemoteClosed, InvalidFrame, ConnectionLost, Backpressure }

    /// <summary>Bounded duplex stream. Worker threads touch bytes only; game/UI work belongs to the caller's pump.</summary>
    public sealed class HoldemTcpChannel : IDisposable
    {
        private const int QueueCapacity = 32;
        private readonly TcpClient client;
        private readonly NetworkStream stream;
        private readonly BlockingCollection<byte[]> outgoing = new BlockingCollection<byte[]>(QueueCapacity);
        private readonly ConcurrentQueue<string> incoming = new ConcurrentQueue<string>();
        private readonly Thread reader, writer;
        private readonly int frameDeadline;
        private int incomingCount, closed;
        public bool IsClosed => Volatile.Read(ref closed) != 0;
        public int PendingMessageCount => Math.Max(0, Volatile.Read(ref incomingCount));
        public HoldemTransportCloseReason CloseReason => (HoldemTransportCloseReason)Volatile.Read(ref closed);

        public HoldemTcpChannel(TcpClient connectedClient, int frameDeadlineMilliseconds = 30000)
        {
            client = connectedClient ?? throw new ArgumentNullException(nameof(connectedClient));
            if (frameDeadlineMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(frameDeadlineMilliseconds));
            frameDeadline = frameDeadlineMilliseconds;
            client.NoDelay = true; stream = client.GetStream(); stream.WriteTimeout = 5000;
            reader = new Thread(ReadLoop) { IsBackground = true, Name = "Poker receive" };
            writer = new Thread(WriteLoop) { IsBackground = true, Name = "Poker send" };
            reader.Start(); writer.Start();
        }

        public bool TrySend(string json)
        {
            if (IsClosed) return false;
            byte[] frame = HoldemFrameCodec.Encode(json);
            try { if (outgoing.TryAdd(frame)) return true; }
            catch (InvalidOperationException) { return false; }
            Close(HoldemTransportCloseReason.Backpressure);
            return false;
        }

        public bool TryRead(out string json)
        {
            if (!incoming.TryDequeue(out json)) return false;
            Interlocked.Decrement(ref incomingCount);
            return true;
        }

        private void ReadLoop()
        {
            try
            {
                while (!IsClosed)
                {
                    string json = HoldemFrameCodec.Read(stream, frameDeadline);
                    if (json == null) { Close(HoldemTransportCloseReason.RemoteClosed); return; }
                    if (Interlocked.Increment(ref incomingCount) > QueueCapacity)
                    { Close(HoldemTransportCloseReason.Backpressure); return; }
                    incoming.Enqueue(json);
                }
            }
            catch (InvalidDataException) { Close(HoldemTransportCloseReason.InvalidFrame); }
            catch (DecoderFallbackException) { Close(HoldemTransportCloseReason.InvalidFrame); }
            catch (IOException) { Close(HoldemTransportCloseReason.ConnectionLost); }
            catch (SocketException) { Close(HoldemTransportCloseReason.ConnectionLost); }
            catch (ObjectDisposedException) { Close(HoldemTransportCloseReason.ConnectionLost); }
        }

        private void WriteLoop()
        {
            try
            {
                foreach (byte[] frame in outgoing.GetConsumingEnumerable())
                {
                    if (IsClosed) return;
                    stream.Write(frame, 0, frame.Length);
                }
            }
            catch (IOException) { Close(HoldemTransportCloseReason.ConnectionLost); }
            catch (SocketException) { Close(HoldemTransportCloseReason.ConnectionLost); }
            catch (ObjectDisposedException) { Close(HoldemTransportCloseReason.ConnectionLost); }
        }

        private void Close(HoldemTransportCloseReason reason)
        {
            if (Interlocked.CompareExchange(ref closed, (int)reason, 0) != 0) return;
            outgoing.CompleteAdding();
            client.Close();
        }

        public void Dispose()
        {
            Close(HoldemTransportCloseReason.LocalClosed);
            if (Thread.CurrentThread != reader) reader.Join(100);
            if (Thread.CurrentThread != writer) writer.Join(100);
        }
    }
}
