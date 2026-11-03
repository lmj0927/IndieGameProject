using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Fusion;
using Fusion.Sockets;
using Fusion.Photon.Realtime;
using Poker.Application;
using Poker.Foundation;
using Poker.Llm;
using Poker.Transport;
using UnityEngine;

namespace Poker.Online
{
    /// <summary>Fusion carries commands and viewer-specific projections; only the host owns poker state.</summary>
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "Poker.Online", "Poker.Online", "HoldemFusionClient")]
    [UnityEngine.AddComponentMenu("")]
    public sealed class FusionClient : MonoBehaviour, IHoldemRemoteClient, IHoldemReconnectPolicy, INetworkRunnerCallbacks
    {
        private const int Magic = 0x4f4d4331;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        [Serializable] private sealed class Envelope
        {
            public int sequence;
            public int kind;
            public string connection;
            public HoldemWireRequest request;
            public HoldemWireResponse[] replies;
        }
        private sealed class Peer
        {
            public readonly Guid Connection = Guid.NewGuid();
            public readonly HoldemReliableInbox<Envelope> Inbox = new HoldemReliableInbox<Envelope>();
            public int Sent;
            public readonly HoldemFusionIngress Ingress = new HoldemFusionIngress();
            public long PublishedRevision = -1;
            public int PublishedDesignationRevision;
            public long PublishedEliminatedSpeechRevision;
            public string Token;
            public double JoinedAt = Time.realtimeSinceStartupAsDouble;
        }
        private readonly Dictionary<PlayerRef, Peer> peers = new Dictionary<PlayerRef, Peer>();
        private readonly HoldemReliableInbox<Envelope> inbox = new HoldemReliableInbox<Envelope>();
        private readonly Queue<HoldemWireResponse> responses = new Queue<HoldemWireResponse>();
        private readonly Queue<HoldemWireRequest> localRequests = new Queue<HoldemWireRequest>();
        private readonly SpeechHistoryAssembler speechHistory = new SpeechHistoryAssembler();
        private readonly SpeechHistoryAssembler eliminatedSpeechHistory = new SpeechHistoryAssembler(true);
        private double nextSpeechSync;
        private NetworkRunner runner;
        private GameObject runnerOwner;
        private HoldemHostCommandBridge authority;
        private HoldemLlmTableDealer dealer;
        private SequentialProgressDriver sequentialProgress;
        private SocialRoomProgressDriver socialProgress;
        private GameObject activatedLlmRoot;
        private CryptographicRandomSource random;
        private HoldemWireRequest admission;
        private bool hosting, started, closed, admitted, admissionSent;
        private int sent;
        private double connectedAt, retryAt = -1;
        private string connectionToken;
        private Task shutdownTask;
        private Task<StartGameResult> startupTask;
        public HoldemClientIdentity Identity { get; private set; }
        IHoldemRemoteIdentity IHoldemRemoteClient.Identity => Identity;
        public bool IsAdmitted => admitted && !closed;
        public bool HasSentAdmission => admissionSent;
        public bool IsClosed => closed;
        public string AdmissionError { get; private set; }
        public string ConnectionError { get; private set; }
        public bool RequiresFreshParticipation => closed && hosting
            || HoldemOnlineText.RequiresFreshParticipation(AdmissionError)
            || HoldemOnlineText.RequiresFreshParticipation(ConnectionError);
        public HoldemRoomPacket Latest { get; private set; }
        public bool IsHost => hosting;
        /// <summary>Host-process integration only. Never sent to a participant over Fusion.</summary>
        public IHoldemHostDealerPort HostDealer => hosting && IsAdmitted ? authority : null;
        public IHoldemSocialHostPort SocialHost => hosting && IsAdmitted ? authority : null;
        public bool IsRunning => started && !closed;
        public string RoomName { get; private set; }
        public string Region { get; private set; }
        public string ConnectionPath => runner != null && started ? runner.CurrentConnectionType.ToString() : "None";

        public async Task<bool> Connect(bool host, string room, HoldemClientIdentity identity, bool relayOnly = false,
            int capacity = HoldemRoom.Capacity,
            HoldemConfig hostConfig = null, HoldemUtterancePolicy hostUtterancePolicy = null,
            SequentialPacingProfile hostSequentialPacing = null, bool llmDealer = false,
            MonoBehaviour hostAnalyzer = null, SocialFlowTiming? hostSocialTiming = null)
        {
            if (host && llmDealer && hostAnalyzer != null && !(hostAnalyzer is IMentAnalyzer))
                throw new ArgumentException("The host analyzer must implement IMentAnalyzer.", nameof(hostAnalyzer));
            if (runner != null || Identity != null) throw new InvalidOperationException("Use a fresh runner for every connection.");
            if (string.IsNullOrWhiteSpace(room) || room.Length < 6 || room.Length > 24)
                throw new ArgumentException("A room code of 6-24 characters is required.", nameof(room));
            foreach (char c in room) if (!(c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '-'))
                throw new ArgumentException("Invalid room code.", nameof(room));
            if (host) HoldemRoomRules.ValidateSeatCapacity(capacity);
            Identity = identity ?? throw new ArgumentNullException(nameof(identity));
            hosting = host; RoomName = room; Region = "kr";
            admission = Identity.AdmissionRequest();
            connectionToken = Guid.NewGuid().ToString("N");
            try
            {
                var settings = PhotonAppSettings.Global.AppSettings.GetCopy();
                if (!Guid.TryParse(settings.AppIdFusion, out var id) || id == Guid.Empty)
                { RecordConnectionError("MissingAppId"); Dispose(); return false; }
                settings.FixedRegion = Region; settings.AppVersion = "omc-host-v1";
                // The official native encryptor is a separate Photon support download, not in this SDK.
                // This development transport must not be described as encrypted or release-ready.
                settings.encryptionMode = Photon.Realtime.EncryptionMode.PayloadEncryption;
                if (host && llmDealer)
                {
                    authority = new HoldemHostCommandBridge(identity, HoldemLlmTableDealer.CreateConfig(), new SeatId(1),
                        random = new CryptographicRandomSource(), utterancePolicy: HoldemLlmTableDealer.CreateUtterancePolicy(),
                        seatCapacity: capacity, chatEnabled: true);
                }
                else if (host)
                {
                    random = new CryptographicRandomSource();
                    var holdemConfig = hostConfig ?? new HoldemConfig(100, 1, 2);
                    var speech = hostConfig != null ? hostUtterancePolicy : new HoldemUtterancePolicy(128, 1,
                            HoldemUtteranceSeats.Active | HoldemUtteranceSeats.AllIn | HoldemUtteranceSeats.Folded,
                            HoldemUtteranceVisibility.PublicRaw, HoldemUtteranceBatchRetention.None);
                    authority = new HoldemHostCommandBridge(identity,
                        holdemConfig, new SeatId(1), random, utterancePolicy: speech, seatCapacity: capacity,
                        accusationEvidenceScope: holdemConfig.AccusationMode == HoldemAccusationMode.CollectLatestChoiceUntilHostCloses
                            || holdemConfig.AccusationMode == HoldemAccusationMode.SingleClaimSelectedAtClose
                            ? HoldemAccusationEvidenceScope.CurrentRevealOnly : (HoldemAccusationEvidenceScope?)null,
                        sequentialPacing: hostSequentialPacing);
                    if (holdemConfig.BettingPolicy == HoldemBettingPolicy.SharedAnteSequential && hostSequentialPacing != null)
                        sequentialProgress = new SequentialProgressDriver(authority, SequentialFlowTiming.FromProfile(hostSequentialPacing),
                            hostSequentialPacing.NeutralDealerPreview);
                    if (holdemConfig.BettingPolicy == HoldemBettingPolicy.SharedAnteFixed && hostSocialTiming.HasValue)
                        socialProgress = new SocialRoomProgressDriver(authority, hostSocialTiming.Value);
                }
                // Fusion can destroy its runner object after remote shutdown. Keep the client
                // and its reconnect identity alive independently of that SDK-owned lifetime.
                runnerOwner = new GameObject("Fusion runner");
                runner = runnerOwner.AddComponent<NetworkRunner>(); runner.ProvideInput = false; runner.AddCallbacks(this);
                var config = NetworkProjectConfig.Deserialize(NetworkProjectConfig.Serialize(NetworkProjectConfig.Global));
                config.Network.ReliableDataTransferModes = NetworkConfiguration.ReliableDataTransfers.ClientToServer;
                config.EncryptionConfig.EnableEncryption = false;
                startupTask = runner.StartGame(new StartGameArgs {
                    GameMode = host ? GameMode.Host : GameMode.Client, SessionName = room,
                    PlayerCount = host ? (int?)authority.SeatCapacity : null, IsVisible = false, IsOpen = true, EnableClientSessionCreation = false,
                    CustomPhotonAppSettings = settings, DisableNATPunchthrough = relayOnly, Config = config,
                    ConnectionToken = Utf8.GetBytes(connectionToken)
                });
                var result = await startupTask;
                if (closed) { await Shutdown(); return false; }
                if (!result.Ok) { RecordConnectionError(result.ShutdownReason.ToString()); Dispose(); return false; }
                if (host && llmDealer)
                {
                    if (hostAnalyzer != null && !hostAnalyzer.gameObject.activeSelf)
                    {
                        activatedLlmRoot = hostAnalyzer.gameObject;
                        activatedLlmRoot.SetActive(true);
                    }
                    dealer = new HoldemLlmTableDealer(authority, hostAnalyzer as IMentAnalyzer);
                }
                started = true; connectedAt = Time.realtimeSinceStartupAsDouble;
                if (host && !peers.ContainsKey(runner.LocalPlayer)) peers.Add(runner.LocalPlayer, new Peer { Token = connectionToken });
                return true;
            }
            catch (Exception ex)
            {
                RecordConnectionError(ex is InvalidOperationException ? ex.Message : ex.GetType().Name);
                Dispose(); return false;
            }
        }

        public bool AdvanceBoardPreview() => IsHost && IsAdmitted && authority.AdvanceBoardPreview();

        public void Poll()
        {
            if (!started || closed) return;
            double now = Time.realtimeSinceStartupAsDouble;
            if (!admitted && now - connectedAt > 45) { RecordConnectionError("AdmissionTimeout"); Dispose(); return; }
            if (!admissionSent || retryAt >= 0 && now >= retryAt)
            { admissionSent = true; retryAt = -1; SendRequest(admission); }
            if (hosting)
            {
                // Disconnect callbacks retire a mapping before its queued actions can be processed here.
                foreach (var entry in new List<KeyValuePair<PlayerRef, Peer>>(peers))
                {
                    if (now - entry.Value.JoinedAt > 30 && !authority.TryGetBinding(entry.Value.Connection, out _))
                    { Drop(entry.Key); continue; }
                    if (entry.Key == runner.LocalPlayer)
                        while (localRequests.Count != 0 && !closed) Dispatch(entry.Key, entry.Value, localRequests.Dequeue());
                    while (entry.Value.Inbox.TryRead(out var envelope) && !closed)
                        Dispatch(entry.Key, entry.Value, envelope.request);
                }
                if (IsAdmitted)
                {
                    dealer?.Tick();
                    sequentialProgress?.Tick(now);
                    socialProgress?.Tick(now);
                }
                Publish();
            }
            while (inbox.TryRead(out var received) && !closed)
                foreach (var response in received.replies) Receive(response);
            if (IsAdmitted && (speechHistory.NeedsCatchUp || eliminatedSpeechHistory.NeedsCatchUp) && now >= nextSpeechSync)
            { nextSpeechSync = now + 0.25; Send(new HoldemWireRequest { type = "sync" }); }
        }

        public string Send(HoldemWireRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.type == "admit" || request.type == "ping") throw new ArgumentException("Managed request.");
            if (!IsAdmitted) return null;
            request.protocol = HoldemRoomPacketMapper.ProtocolVersion;
            request.id = request.id ?? Guid.NewGuid().ToString("N");
            request.sessionId = request.sessionId ?? Identity.SessionId;
            if (request.type == "sync")
            { request.speechHandId = speechHistory.HandId; request.speechCursor = speechHistory.Cursor;
                request.eliminatedSpeechHandId = eliminatedSpeechHistory.HandId;
                request.eliminatedSpeechCursor = eliminatedSpeechHistory.Cursor; }
            return SendRequest(request) ? request.id : null;
        }
        private bool SendRequest(HoldemWireRequest request)
        {
            if (closed || !started) return false;
            if (hosting)
            {
                if (localRequests.Count >= 64) { RecordConnectionError("LocalInputOverflow"); Dispose(); return false; }
                localRequests.Enqueue(request); return true;
            }
            try
            {
                var envelope = new Envelope { sequence = checked(++sent), kind = 1, request = request, connection = connectionToken };
                byte[] bytes = Encode(envelope);
                runner.SendReliableDataToServer(Key(1, envelope.sequence), bytes); return true;
            }
            catch (Exception) { RecordConnectionError("SendFailed"); Dispose(); return false; }
        }
        private void Dispatch(PlayerRef player, Peer peer, HoldemWireRequest request)
        {
            if (!peers.ContainsKey(player)) return;
            var result = authority.Dispatch(peer.Connection, request);
            if (result.CloseConnection) { Drop(player); return; }
            if (result.CallerProjection != null)
            {
                SendReplies(player, peer, result.Response, State(result.CallerProjection));
                peer.PublishedRevision = result.CallerProjection.revision;
                peer.PublishedDesignationRevision = result.CallerProjection.OwnDesignationRevision;
                peer.PublishedEliminatedSpeechRevision = result.CallerProjection.EliminatedSpeechRevision;
            }
            else SendReplies(player, peer, result.Response);
        }
        private void Publish()
        {
            foreach (var entry in new List<KeyValuePair<PlayerRef, Peer>>(peers))
                if (authority.TryCreateProjection(entry.Value.Connection, out var packet,
                    entry.Value.PublishedRevision, entry.Value.PublishedDesignationRevision,
                    entry.Value.PublishedEliminatedSpeechRevision))
                {
                    // Never send this detached private view to any other PlayerRef.
                    SendReplies(entry.Key, entry.Value, State(packet));
                    entry.Value.PublishedRevision = packet.revision;
                    entry.Value.PublishedDesignationRevision = packet.OwnDesignationRevision;
                    entry.Value.PublishedEliminatedSpeechRevision = packet.EliminatedSpeechRevision;
                }
        }
        private static HoldemWireResponse State(HoldemRoomPacket packet) => new HoldemWireResponse {
            protocol = HoldemRoomPacketMapper.ProtocolVersion, type = "state", state = packet
        };
        private void SendReplies(PlayerRef player, Peer peer, params HoldemWireResponse[] items)
        {
            if (closed) return;
            if (player == runner.LocalPlayer) { foreach (var item in items) Receive(item); return; }
            if (!runner.IsPlayerCommitted(player) || !peers.TryGetValue(player, out var current) || current != peer) return;
            try
            {
                var envelope = new Envelope { sequence = checked(++peer.Sent), kind = 2, replies = items, connection = peer.Token };
                runner.SendReliableDataToPlayer(player, Key(2, envelope.sequence), Encode(envelope));
            }
            catch (Exception) { Drop(player); }
        }
        private void Receive(HoldemWireResponse response)
        {
            if (response == null || response.protocol != HoldemRoomPacketMapper.ProtocolVersion)
            { RecordConnectionError("InvalidServerReply"); Dispose(); return; }
            if (response.type == "admitted")
            {
                if (!Identity.TryConfirmAdmission(response, admission.id)) { RecordConnectionError("InvalidAdmissionReply"); Dispose(); return; }
                admitted = true; retryAt = -1;
            }
            if (!admitted && response.id == admission.id && response.error == "MemberStillConnected")
                retryAt = Time.realtimeSinceStartupAsDouble + 1;
            else if (!admitted && response.id == admission.id && !string.IsNullOrEmpty(response.error) && response.error != "None")
            { AdmissionError = response.error; Dispose(); return; }
            if (response.type == "state")
            {
                var packet = response.state;
                if (!IsAdmitted || packet == null || packet.protocol != HoldemRoomPacketMapper.ProtocolVersion
                    || packet.sessionId != Identity.SessionId || packet.viewerSeat != Identity.Seat)
                { RecordConnectionError("InvalidServerState"); Dispose(); return; }
                try
                {
                    var assembled = speechHistory.Accept(packet);
                    var privateAssembled = eliminatedSpeechHistory.Accept(packet);
                    if (assembled != null && privateAssembled != null && assembled.IsNewerThan(Latest)) Latest = assembled;
                }
                catch (ArgumentException) { RecordConnectionError("InvalidSpeechHistory"); Dispose(); return; }
                if (string.IsNullOrEmpty(response.id)) return;
            }
            if (responses.Count >= 64) { RecordConnectionError("ResponseOverflow"); Dispose(); return; }
            responses.Enqueue(response);
        }
        public bool TryReadResponse(out HoldemWireResponse response)
        {
            if (responses.Count == 0) { response = null; return false; }
            response = responses.Dequeue(); return true;
        }
        private static byte[] Encode(Envelope envelope)
        {
            var data = Utf8.GetBytes(JsonUtility.ToJson(envelope));
            if (data.Length > HoldemFrameCodec.MaximumBytes) throw new InvalidOperationException("Packet too large.");
            return data;
        }
        private static ReliableKey Key(int direction, int sequence) => ReliableKey.FromInts(Magic, direction, sequence, 0);
        public void OnReliableDataReceived(NetworkRunner source, PlayerRef player, ReliableKey key, ReadOnlySpan<byte> data)
        {
            if (source != runner || closed) return;
            if (data.Length < 1 || data.Length > HoldemFrameCodec.MaximumBytes) { Drop(player); return; }
            Peer peer = null;
            if (hosting)
            {
                if (!runner.IsServer || !player.IsRealPlayer || !runner.IsPlayerCommitted(player)
                    || player == runner.LocalPlayer)
                { Drop(player); return; }
                if (!peers.TryGetValue(player, out peer))
                {
                    // Fusion can commit a remote player before delivering the scene's join callback.
                    if (!RegisterPeer(player)) { Drop(player); return; }
                    peer = peers[player];
                }
                // Stale-generation messages still cost decoding work. Count before inspecting their JSON.
                if (!peer.Ingress.AcceptMessage(Time.realtimeSinceStartupAsDouble))
                { Debug.LogWarning("OMC_FUSION_PACKET_REJECT rate-limit"); Drop(player); return; }
            }
            try
            {
                var envelope = JsonUtility.FromJson<Envelope>(Utf8.GetString(data.ToArray()));
                if (envelope == null || envelope.sequence < 1 || key != Key(hosting ? 1 : 2, envelope.sequence))
                { Drop(player); return; }
                if (hosting)
                {
                    if (envelope.kind != 1 || envelope.request == null || envelope.replies != null && envelope.replies.Length > 0)
                    { Debug.LogWarning("OMC_FUSION_PACKET_REJECT host-envelope"); Drop(player); return; }
                    if (envelope.connection != peer.Token) return; // Previous connection generation.
                    if (!peer.Inbox.Accept(envelope.sequence, envelope, data.Length)) Drop(player);
                }
                else
                {
                    // Fusion 2.1.3 Host->Client streams report the destination's PlayerRef here
                    // (verified with the official two-process relay). Peer proxy forwarding is disabled
                    // in Config; this lane also requires our connection generation and server envelope kind.
                    if (envelope.connection != connectionToken) return;
                    if (player != runner.LocalPlayer || envelope.kind != 2 || envelope.replies == null
                        || envelope.replies.Length < 1 || envelope.replies.Length > 2
                        || !inbox.Accept(envelope.sequence, envelope, data.Length))
                    { RecordConnectionError("InvalidServerEnvelope"); Debug.LogWarning("OMC_FUSION_PACKET_REJECT client-envelope source=" + player); Dispose(); }
                }
            }
            catch (Exception) { Drop(player); }
        }
        private void Drop(PlayerRef player)
        {
            if (!hosting || runner == null || player == runner.LocalPlayer) { Dispose(); return; }
            if (peers.TryGetValue(player, out var peer)) { authority?.Disconnect(peer.Connection); peers.Remove(player); }
            if (player.IsRealPlayer && runner.IsPlayerCommitted(player)) runner.Disconnect(player);
        }
        public void OnPlayerJoined(NetworkRunner source, PlayerRef player)
        {
            if (source == runner && hosting && !closed && !peers.ContainsKey(player) && !RegisterPeer(player))
                Drop(player);
        }
        private bool RegisterPeer(PlayerRef player)
        {
            var token = player == runner.LocalPlayer ? Utf8.GetBytes(connectionToken ?? "") : runner.GetPlayerConnectionToken(player);
            if (!HoldemFusionIngress.TryReadToken(token, out var text))
            {
                Debug.LogWarning("OMC_FUSION_PACKET_REJECT connection-token");
                return false;
            }
            peers.Add(player, new Peer { Token = text });
            return true;
        }
        public void OnPlayerLeft(NetworkRunner source, PlayerRef player)
        {
            if (source == runner && hosting && peers.TryGetValue(player, out var peer))
            { authority?.Disconnect(peer.Connection); peers.Remove(player); }
        }
        public void Dispose() { _ = Shutdown(); }
        public Task Shutdown() => shutdownTask ?? (shutdownTask = ShutdownCore());
        private async Task ShutdownCore()
        {
            closed = true; admitted = false;
            RevokeAuthority();
            // A cancelled connection may finish starting later. Do not release its owner before
            // StartGame has settled, or a late runner can outlive the menu which created it.
            try
            {
                if (startupTask != null)
                    try { await startupTask; } catch (Exception) { /* Reported by Connect. */ }
                if (runner != null && !runner.IsShutdown) await runner.Shutdown(destroyGameObject: false);
            }
            finally
            {
                RevokeAuthority();
                if (runnerOwner != null) Destroy(runnerOwner);
            }
        }
        private void OnDestroy() { Dispose(); }
        private void RevokeAuthority()
        {
            dealer?.Dispose(); dealer = null;
            sequentialProgress = null; socialProgress = null;
            if (activatedLlmRoot != null) activatedLlmRoot.SetActive(false);
            activatedLlmRoot = null;
            var old = authority; authority = null; old?.Dispose();
            random?.Dispose(); random = null;
        }
        private void RecordConnectionError(string reason)
        {
            // Later SDK shutdown notifications must not turn a known ended membership into a retryable failure.
            if (HoldemOnlineText.RequiresFreshParticipation(ConnectionError)) return;
            // Preserve the actual application failure when SDK cleanup adds a generic disconnect reason.
            if (!HoldemConnectionDiagnostics.IsLocalFailure(ConnectionError)
                || HoldemOnlineText.RequiresFreshParticipation(reason)) ConnectionError = reason;
        }
        public void OnShutdown(NetworkRunner source, ShutdownReason reason)
        {
            if (source != runner) return;
            closed = true; admitted = false; RevokeAuthority();
            if (reason != ShutdownReason.Ok) RecordConnectionError(reason.ToString());
        }
        public void OnDisconnectedFromServer(NetworkRunner source, NetDisconnectReason reason)
        { if (source == runner) { RecordConnectionError(reason.ToString()); Dispose(); } }
        public void OnConnectFailed(NetworkRunner source, NetAddress address, NetConnectFailedReason reason)
        { if (source == runner) RecordConnectionError(reason.ToString()); }
        public void OnConnectRequest(NetworkRunner source, NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token)
        {
            if (source == runner && !closed && hosting && authority != null && !HoldemFusionIngress.TryReadToken(token, out _))
            { Debug.LogWarning("OMC_FUSION_PACKET_REJECT connection-token"); request.Refuse(); return; }
            if (source == runner && !closed && hosting && authority != null && peers.Count < authority.SeatCapacity) request.Accept();
            else request.Refuse();
        }
        public void OnHostMigration(NetworkRunner source, HostMigrationToken token)
        { if (source == runner) { RecordConnectionError("HostEnded"); Dispose(); } }
        public void OnConnectedToServer(NetworkRunner source) { }
        public void OnReliableDataProgress(NetworkRunner source, PlayerRef player, ReliableKey key, float progress) { }
        public void OnInput(NetworkRunner source, NetworkInput input) { }
        public void OnInputMissing(NetworkRunner source, PlayerRef player, NetworkInput input) { }
        public void OnObjectEnterAOI(NetworkRunner source, NetworkObject obj, PlayerRef player) { }
        public void OnObjectExitAOI(NetworkRunner source, NetworkObject obj, PlayerRef player) { }
        public void OnSessionListUpdated(NetworkRunner source, List<SessionInfo> sessions) { }
        public void OnCustomAuthenticationResponse(NetworkRunner source, Dictionary<string, object> data) { }
        public void OnSceneLoadStart(NetworkRunner source) { }
        public void OnSceneLoadDone(NetworkRunner source) { }
    }
}
