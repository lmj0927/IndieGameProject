using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Poker.Application;
using Poker.Foundation;
using UnityEngine;

namespace Poker.Transport
{
    /// <summary>
    /// Development transport for loopback or an explicitly selected trusted LAN interface. No TLS, relay or NAT traversal.
    /// Call Pump and host integration methods on one owning game thread; socket workers only handle framed bytes.
    /// </summary>
    public sealed class HoldemTcpServer : IDisposable, IHoldemSocialHostPort, IHoldemProgressClockHostPort, IDealerFeatureSourcePort,
        ISequentialProgressHostPort, IHoldemEliminatedSpeechHostPort
    {
        public const int MaximumCloseRecords = 32;
        private readonly object gate = new object();
        private readonly TcpListener listener;
        private readonly HoldemHostCommandBridge bridge;
        private readonly Guid sessionId;
        private readonly string hostKeyHash;
        private readonly List<Peer> peers = new List<Peer>();
        private readonly IDictionary admissions;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Queue<HoldemPeerCloseRecord> closeRecords = new Queue<HoldemPeerCloseRecord>();
        private long closeSequence;
        private HoldemPeerCloseRecord latestHostClose;
        private bool disposed;
        public IPEndPoint Endpoint { get; }
        /// <summary>A copied, bounded host-local diagnostic view. Normal server shutdown is not a peer failure.</summary>
        public IReadOnlyList<HoldemPeerCloseRecord> ReadCloseRecords()
        { lock (gate) return Array.AsReadOnly(closeRecords.ToArray()); }
        /// <summary>Retained separately so unauthenticated traffic cannot evict the latest host observation.</summary>
        public HoldemPeerCloseRecord LatestHostClose
        { get { lock (gate) return latestHostClose; } }
        public int PendingInputCount
        {
            get { lock (gate) { int count = 0; foreach (Peer peer in peers) count += peer.Channel.PendingMessageCount; return count; } }
        }

        private sealed class Peer
        {
            public Guid Connection = Guid.NewGuid();
            public HoldemTcpChannel Channel;
            public bool Admitted;
            public int Seat;
            public bool CloseRecorded;
            public string KeyHash;
            public long SentRevision = -1, LastRateTime;
            public int SentDesignationRevision;
            public long SentEliminatedSpeechRevision;
            // Leave room for the recoverable 32-message speech burst plus control traffic.
            public double Tokens = 64;
            public Guid LeftCommandId;
        }
        public HoldemTcpServer(HoldemClientIdentity hostIdentity, HoldemConfig config, SeatId initialButton,
            IRandomSource deckRandom, IPEndPoint endpoint = null, bool allowTrustedLan = false,
            HoldemOddChipRule oddChipRule = HoldemOddChipRule.RequireExplicitPriority,
            HoldemUtterancePolicy utterancePolicy = null, int seatCapacity = HoldemRoom.Capacity,
            HoldemAccusationEvidenceScope? accusationEvidenceScope = null, SequentialPacingProfile sequentialPacing = null)
        {
            if (hostIdentity == null) throw new ArgumentNullException(nameof(hostIdentity));
            endpoint = endpoint ?? new IPEndPoint(IPAddress.Loopback, 0);
            ValidateBindAddress(endpoint.Address, allowTrustedLan);
            bridge = new HoldemHostCommandBridge(hostIdentity, config, initialButton, deckRandom,
                oddChipRule, utterancePolicy, seatCapacity, accusationEvidenceScope, sequentialPacing: sequentialPacing);
            sessionId = bridge.SessionId;
            hostKeyHash = bridge.HostKeyHashForDiagnostics;
            admissions = bridge.AdmissionsForDiagnostics;
            listener = new TcpListener(endpoint);
            try { listener.Start(8); Endpoint = (IPEndPoint)listener.LocalEndpoint; }
            catch { listener.Stop(); throw; }
        }

        public void Pump()
        {
            lock (gate)
            {
                if (disposed) return;
                // Process transport closure before any queued player input, including old-socket input after reconnect.
                ObserveClosedPeers();
                for (int accepted = 0; accepted < 8 && listener.Pending(); accepted++)
                {
                    TcpClient socket = listener.AcceptTcpClient();
                    if (peers.Count >= 8) { socket.Close(); continue; }
                    peers.Add(new Peer { Channel = new HoldemTcpChannel(socket), LastRateTime = clock.ElapsedMilliseconds });
                }
                foreach (Peer peer in peers)
                {
                    for (int count = 0; count < 8 && !peer.Channel.IsClosed && peer.Channel.TryRead(out var json); count++)
                    {
                        if (!ConsumeRateToken(peer))
                        {
                            UnityEngine.Debug.LogWarning("Poker connection closed: input rate limit exceeded.");
                            ClosePeer(peer, HoldemPeerCloseTrigger.RateLimit); break;
                        }
                        Handle(peer, json);
                    }
                    if (peer.Channel.IsClosed) ClosePeer(peer);
                }
                // Never reuse one viewer's packet for another socket, even when the public revision is identical.
                foreach (Peer peer in peers)
                {
                    if (!peer.Admitted || peer.Channel.IsClosed) continue;
                    if (!bridge.TryCreateProjection(peer.Connection, out var packet, peer.SentRevision,
                        peer.SentDesignationRevision, peer.SentEliminatedSpeechRevision)) continue;
                    SendState(peer, packet);
                }
            }
        }

        private void Handle(Peer peer, string json)
        {
            HoldemWireRequest request;
            try { request = JsonUtility.FromJson<HoldemWireRequest>(json); }
            catch (ArgumentException) { ClosePeer(peer, HoldemPeerCloseTrigger.InvalidEnvelope); return; }
            var result = bridge.Dispatch(peer.Connection, request);
            if (result.CloseConnection)
            {
                ClosePeer(peer, HoldemPeerCloseTrigger.InvalidEnvelope);
                return;
            }
            RefreshPeer(peer);
            if (!Send(peer, result.Response)) return;
            if (result.Response.type == "state" && result.Response.state != null)
            {
                peer.SentRevision = result.Response.state.revision;
                peer.SentDesignationRevision = result.Response.state.OwnDesignationRevision;
                peer.SentEliminatedSpeechRevision = result.Response.state.EliminatedSpeechRevision;
            }
            if (result.CallerProjection != null) SendState(peer, result.CallerProjection);
        }

        private void RefreshPeer(Peer peer)
        {
            if (!bridge.TryGetTransportBinding(peer.Connection, out var admitted, out var seat,
                out _, out var keyHash, out var leftCommandId)) return;
            peer.Admitted = admitted; peer.Seat = seat; peer.KeyHash = keyHash; peer.LeftCommandId = leftCommandId;
        }
        private bool Send(Peer peer, HoldemWireResponse response)
        {
            if (peer.Channel.TrySend(JsonUtility.ToJson(response))) return true;
            ClosePeer(peer, HoldemPeerCloseTrigger.SendFailed); return false;
        }
        private void SendState(Peer peer, HoldemRoomPacket packet, string requestId = "")
        {
            var response = new HoldemWireResponse { protocol = HoldemRoomPacketMapper.ProtocolVersion,
                type = "state", id = requestId, sessionId = sessionId.ToString("N"), state = packet };
            if (Send(peer, response))
            { peer.SentRevision = packet.revision; peer.SentDesignationRevision = packet.OwnDesignationRevision;
                peer.SentEliminatedSpeechRevision = packet.EliminatedSpeechRevision; }
        }
        private void ClosePeer(Peer peer, HoldemPeerCloseTrigger trigger = HoldemPeerCloseTrigger.ChannelObserved)
        {
            // A rejected input, the same Pump's cleanup and the next Pump can all reach here.
            // Preserve the first observation before Dispose changes an open channel to LocalClosed.
            if (!peer.CloseRecorded)
            {
                peer.CloseRecorded = true;
                var record = new HoldemPeerCloseRecord(++closeSequence, clock.ElapsedMilliseconds,
                    peer.Admitted ? peer.Seat : 0, peer.Admitted, peer.Admitted && peer.KeyHash == hostKeyHash,
                    trigger, peer.Channel.CloseReason);
                if (closeRecords.Count == MaximumCloseRecords) closeRecords.Dequeue();
                closeRecords.Enqueue(record);
                if (record.WasHost) latestHostClose = record;
            }
            // Reflect a known close in authority before processing another player's queued command.
            bridge.Disconnect(peer.Connection); peer.Admitted = false;
            peer.Channel.Dispose();
        }
        private void ObserveClosedPeers()
        {
            // Shared by Pump and in-process commands: Unity component order must not bypass a known close.
            for (int i = peers.Count - 1; i >= 0; i--)
            {
                if (!peers[i].Channel.IsClosed) continue;
                ClosePeer(peers[i]); peers.RemoveAt(i);
            }
        }
        private bool ConsumeRateToken(Peer peer)
        {
            long now = clock.ElapsedMilliseconds;
            peer.Tokens = Math.Min(64, peer.Tokens + (now - peer.LastRateTime) * 16.0 / 1000.0); peer.LastRateTime = now;
            if (peer.Tokens < 1) return false;
            peer.Tokens--; return true;
        }
        /// <summary>In-process only. Neither closing nor supplied verdicts are accepted from the wire.</summary>
        public HoldemRoomReceipt CloseAccusations(HoldemRoomAccusationClose input)
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(HoldemTcpServer));
                ObserveClosedPeers();
                return bridge.CloseAccusations(input);
            }
        }

        public HoldemRoomAccusationResolution ResolveAccusations()
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(HoldemTcpServer));
                ObserveClosedPeers();
                return bridge.ResolveAccusations();
            }
        }

        public HoldemRoomReceipt SealFixedBetting(FixedBettingHostCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(HoldemTcpServer));
                ObserveClosedPeers();
                return bridge.SealFixedBetting(command);
            }
        }

        public bool CloseDealerInput(Guid dealWindowId)
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(HoldemTcpServer));
                ObserveClosedPeers();
                return bridge.CloseDealerInput(dealWindowId);
            }
        }

        public bool TryReadProgress(out HoldemRoomView view)
        {
            lock (gate)
            {
                view = null;
                if (disposed) return false;
                ObserveClosedPeers(); return bridge.TryReadProgress(out view);
            }
        }

        public bool PublishProgressClock(HoldemProgressClock sample)
        {
            lock (gate)
            {
                if (disposed) return false;
                ObserveClosedPeers(); return bridge.PublishProgressClock(sample);
            }
        }

        public bool TryReadSequentialProgress(out HoldemSnapshot game, out bool paused)
        {
            bool found = TryReadProgress(out var view);
            game = view?.Game; paused = !found || view.Paused; return found;
        }
        public bool TryExpireTurn(Guid handId, long handVersion, SeatId actor)
        {
            lock (gate)
            {
                if (disposed) return false;
                ObserveClosedPeers(); return bridge.TryExpireTurn(handId, handVersion, actor);
            }
        }

        public HoldemRoomReceipt ResumeAfterReveal(HoldemRevealCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(HoldemTcpServer));
                ObserveClosedPeers(); return bridge.ResumeAfterReveal(command);
            }
        }

        public HoldemRoomReceipt CloseFinalConversation(FinalConversationCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(HoldemTcpServer));
                ObserveClosedPeers(); return bridge.CloseFinalConversation(command);
            }
        }

        /// <summary>In-process host consumer only. No client request exposes this feed.</summary>
        public HoldemRoomReceipt ResolveSettlement(Guid handId, Guid commandId, long expectedVersion)
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(HoldemTcpServer));
                ObserveClosedPeers(); return bridge.ResolveSettlement(handId, commandId, expectedVersion);
            }
        }

        /// <summary>In-process host consumer only. No client request exposes this feed.</summary>
        public HoldemDealerTurnRead ReadPendingTurn()
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(HoldemTcpServer));
                ObserveClosedPeers();
                return bridge.ReadPendingTurn();
            }
        }

        public DealerFeatureInput ReadFeatureInput()
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(HoldemTcpServer));
                ObserveClosedPeers(); return bridge.ReadFeatureInput();
            }
        }

        /// <summary>Host-process completion; delayed or duplicate commands use the normal authority checks.</summary>
        public HoldemRoomReceipt DealUnchanged(HoldemDealCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(HoldemTcpServer));
                ObserveClosedPeers();
                return bridge.DealUnchanged(command);
            }
        }

        /// <summary>Host-process only. Player packets cannot choose replacement cards or attribution.</summary>
        public HoldemRoomReceipt ApplyDealerDeal(HoldemDealCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(HoldemTcpServer));
                ObserveClosedPeers();
                return bridge.ApplyDealerDeal(command);
            }
        }

        /// <summary>In-process host consumer only. No client request exposes this feed.</summary>
        public IReadOnlyList<HoldemUtteranceEntry> ReadDesignatedUtterances()
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(HoldemTcpServer));
                ObserveClosedPeers();
                return bridge.ReadDesignatedUtterances();
            }
        }

        public IReadOnlyList<HoldemUtteranceBatch> ReadPendingUtterances()
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(HoldemTcpServer));
                return bridge.ReadPendingUtterances();
            }
        }

        public bool AcknowledgeUtteranceBatch(Guid windowId)
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(HoldemTcpServer));
                return bridge.AcknowledgeUtteranceBatch(windowId);
            }
        }

        public IReadOnlyList<HoldemUtteranceEntry> ReadEliminatedDesignations()
        { lock (gate) { if (disposed) throw new ObjectDisposedException(nameof(HoldemTcpServer)); ObserveClosedPeers(); return bridge.ReadEliminatedDesignations(); } }
        public IReadOnlyList<HoldemUtteranceBatch> ReadClosedEliminatedBatches()
        { lock (gate) { if (disposed) throw new ObjectDisposedException(nameof(HoldemTcpServer)); return bridge.ReadClosedEliminatedBatches(); } }
        public bool AcknowledgeEliminatedBatch(Guid windowId)
        { lock (gate) { if (disposed) throw new ObjectDisposedException(nameof(HoldemTcpServer)); return bridge.AcknowledgeEliminatedBatch(windowId); } }

        private static void ValidateBindAddress(IPAddress address, bool allowTrustedLan)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork) throw new ArgumentException("This transport currently uses IPv4.");
            if (IPAddress.IsLoopback(address)) return;
            byte[] b = address.GetAddressBytes();
            bool privateAddress = b[0] == 10 || b[0] == 172 && b[1] >= 16 && b[1] <= 31 || b[0] == 192 && b[1] == 168;
            if (!allowTrustedLan || !privateAddress) throw new ArgumentException("Choose a specific trusted LAN interface explicitly.");
            if (HoldemLanAddresses.IsAssignedLocally(address, NetworkInterface.GetAllNetworkInterfaces)) return;
            throw new ArgumentException("The chosen address is not a local network interface.");
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true; listener.Stop();
                foreach (Peer peer in peers) peer.Channel.Dispose();
                peers.Clear(); bridge.Dispose();
            }
        }
    }
}
