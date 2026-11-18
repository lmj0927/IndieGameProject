using System;
using Poker.Application;
using Poker.Foundation;
using Poker.Online;
using Poker.Transport;
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class Play2RoomSession : MonoBehaviour
{
    internal const string TitleScene = "Title";
    internal const string LobbyScene = "Lobby";
    internal const string GameScene = "InGame";

    internal static Play2RoomSession Current { get; private set; }

    private readonly Guid[] previewSeats = new Guid[4];
    private FusionClient online;
    private HoldemHostCommandBridge preview;
    private CryptographicRandomSource previewRandom;
    private HoldemLlmTableDealer previewDealer;
    private Poker.Llm.Unity.LlmUnityMentAnalyzer roomAnalyzer;
    private AsyncOperation sceneLoad;
    private HoldemRoomPacket state;
    private string status = "";
    private bool connecting;
    private bool leaving;
    private HoldemClientIdentity reconnectIdentity;
    private string reconnectRoom;
    private HoldemWireRequest pendingAction;
    private bool actionAccepted;
    internal event Action<HoldemWireResponse> ChatReceived;

    internal HoldemRoomPacket State => state;
    internal string Status => status;
    internal bool IsConnecting => connecting || (online != null && state == null);
    internal bool IsLocal => preview != null;
    internal bool WatchingPractice { get; private set; }
    internal bool IsHost => IsLocal || (online != null && online.IsHost);
    internal string RoomCode => online?.RoomName ?? "";
    internal bool CanReconnect => reconnectIdentity != null && state == null;
    internal string ReconnectRoom => reconnectRoom ?? "";
    internal string ChatDraft { get; set; } = "";
    internal bool ChatDraftToDealer { get; set; }
    internal bool ChatDeliveryUnconfirmed { get; set; }
    internal bool ActionPending => pendingAction != null;

    internal static Play2RoomSession GetOrCreate()
    {
        if (Current != null) return Current;
        Current = new GameObject("Holdem Room Session").AddComponent<Play2RoomSession>();
        DontDestroyOnLoad(Current.gameObject);
        return Current;
    }

    private void Update()
    {
        if (leaving) return;
        if (online != null)
        {
            online.Poll();
            while (online.TryReadResponse(out var response))
                ReadReceipt(response);
            if (online.IsClosed)
            {
                // Fusion can signal shutdown before StartGame supplies the room admission error.
                if (connecting) return;
                status = online.ConnectionError ?? online.AdmissionError
                    ?? (string.IsNullOrEmpty(status) ? "Disconnected" : status);
                if (!online.RequiresFreshParticipation && !HoldemOnlineText.RoomIsMissing(online.ConnectionError)
                    && !online.IsHost && online.Identity?.Seat > 0)
                {
                    reconnectIdentity = online.Identity;
                    reconnectRoom = online.RoomName;
                }
                else ClearReconnect();
                Destroy(online);
                online = null;
                connecting = false;
                DestroyRoomAnalyzer();
                state = null;
                ClearPendingAction();
            }
            else state = online.Latest;
        }
        else if (preview != null)
        {
            previewDealer.Tick();
            if (preview.TryCreateProjection(previewSeats[0], out var packet, state?.revision ?? -1))
                state = packet;
        }

        ReleaseCompletedAction();
        string scene = state == null ? TitleScene : state.hasGame ? GameScene : LobbyScene;
        if ((sceneLoad == null || sceneLoad.isDone) && SceneManager.GetActiveScene().name != scene)
            sceneLoad = SceneManager.LoadSceneAsync(scene);
    }

    internal void ToggleReady()
    {
        if (online == null || state == null) return;
        foreach (var member in state.members)
        {
            if (member.seat != state.viewerSeat) continue;
            online.Send(new HoldemWireRequest { type = "ready", ready = !member.ready });
            break;
        }
    }

    private sealed class PracticeRandom : IRandomSource
    {
        private readonly System.Random generator = new System.Random();
        public int NextInt(int exclusiveMax) => generator.Next(exclusiveMax);
    }

    private readonly HoldemNpcPolicy practicePolicy = new HoldemNpcPolicy(32);
    private readonly PracticeRandom practiceRandom = new PracticeRandom();

    internal bool PlayPreviewOpponent()
    {
        var game = state?.game;
        if (preview == null || game == null || game.currentSeat <= 1 || game.dealPending
            || game.revealPending || game.hasResult) return false;
        Guid actor = previewSeats[game.currentSeat - 1];
        return preview.TryPlayPracticeOpponent(actor, practicePolicy, practiceRandom);
    }

    internal bool Submit(HoldemWireRequest request)
    {
        if (leaving || state == null) return false;
        bool changesGame = request.type is "act" or "turn-accusation" or "start" or "settle" or "rematch";
        if (changesGame && ActionPending) return false;
        status = "";
        request.id = request.id ?? Guid.NewGuid().ToString("N");
        if (changesGame)
        {
            pendingAction = request;
            actionAccepted = false;
        }
        if (preview == null)
        {
            if (online?.Send(request) != null) return true;
            if (changesGame) ClearPendingAction();
            return false;
        }
        request.protocol = HoldemRoomPacketMapper.ProtocolVersion;
        request.sessionId = state.sessionId;
        var reply = preview.Dispatch(previewSeats[0], request).Response;
        ReadReceipt(reply);
        return reply.accepted;
    }

    private void ReadReceipt(HoldemWireResponse response)
    {
        if ((response.type == "receipt" || response.type == "rejected")
            && pendingAction != null && response.id == pendingAction.id)
        {
            if (response.accepted) actionAccepted = true;
            else ClearPendingAction();
        }
        if (response.type == "chat-receipt")
        {
            status = !response.accepted ? ChatFailure(response)
                : response.hasUtteranceReceipt && response.utteranceError != "None"
                    ? "대화는 전송됐지만 딜러가 요청을 받지 못했습니다." : "";
            ChatReceived?.Invoke(response);
        }
        else if (!response.accepted && (response.type == "receipt" || response.type == "rejected"))
        {
            status = (response.receipt?.error ?? response.error) switch
            {
                "WrongTurn" => "아직 내 차례가 아닙니다.",
                "VersionMismatch" or "WrongHand" => "진행 상황이 바뀌었습니다. 현재 화면에서 다시 선택해 주세요.",
                "IllegalAction" => "지금 선택할 수 없는 행동입니다. 차례와 금액을 확인해 주세요.",
                "InvalidAccusationTarget" or "AccusationNotAllowed" => "지금은 해당 플레이어를 고발할 수 없습니다.",
                "PlayersNotReady" or "WaitingForPlayers" => "모두 참가하고 준비해야 시작할 수 있습니다.",
                "Paused" => "연결이 돌아올 때까지 잠시 기다려 주세요.",
                _ => "입력이 처리되지 않았습니다. 현재 화면에서 다시 시도해 주세요."
            };
            // A transport-level rejection can also belong to the pending chat command.
            // The composer matches the id before releasing its input and preserving the draft.
            if (response.type == "rejected") ChatReceived?.Invoke(response);
        }
    }

    private void ReleaseCompletedAction()
    {
        if (!actionAccepted || pendingAction == null || state?.game == null) return;
        // A start or rematch names the new hand, while other commands name the current hand.
        bool startsHand = pendingAction.type is "start" or "rematch";
        bool received = startsHand ? state.game.handId == pendingAction.handId
            : state.game.handId != pendingAction.handId || state.game.version > pendingAction.version;
        if (received)
            ClearPendingAction();
    }

    private void ClearPendingAction()
    {
        pendingAction = null;
        actionAccepted = false;
    }

    private static string ChatFailure(HoldemWireResponse response)
    {
        string error = response.chatError == "DealerRequestRejected" ? response.utteranceError : response.chatError;
        return error switch
        {
            "WindowClosed" or "WrongWindow" or "WrongHand" => "요청 시간이 끝나 전송되지 않았습니다. 딜러 요청을 끄면 대화로 보낼 수 있습니다.",
            "LimitReached" or "RateLimited" or "BacklogFull" => "요청이 많아 전송되지 않았습니다. 잠시 뒤 다시 시도해 주세요.",
            "TextTooLong" => "문장이 너무 깁니다. 내용을 줄여 다시 보내 주세요.",
            "EmptyText" => "보낼 문장을 입력해 주세요.",
            "InvalidText" => "전송할 수 없는 문자가 있습니다. 문장을 수정해 주세요.",
            _ => "문장을 전송하지 못했습니다. 내용을 확인하고 다시 시도해 주세요."
        };
    }

    private Poker.Llm.Unity.LlmUnityMentAnalyzer CreateRoomAnalyzer(Poker.Llm.Unity.LlmUnityMentAnalyzer hostAnalyzer)
    {
        if (hostAnalyzer == null) return null;
        string model = hostAnalyzer.GetComponent<LLMUnity.LLM>().model;
        if (string.IsNullOrEmpty(model) || !System.IO.File.Exists(LLMUnity.LLM.GetLLMManagerAssetRuntime(model)))
            return null;
        roomAnalyzer = Instantiate(hostAnalyzer, transform);
        return roomAnalyzer;
    }

    private void DestroyRoomAnalyzer()
    {
        if (roomAnalyzer != null) Destroy(roomAnalyzer.gameObject);
        roomAnalyzer = null;
    }

    internal void Connect(bool host, string room, string nickname, int capacity,
        Poker.Llm.Unity.LlmUnityMentAnalyzer hostAnalyzer)
    {
        if (online != null || preview != null || connecting) return;
        ClearReconnect();
        ConnectClient(host, room.Trim().ToUpperInvariant(), new HoldemClientIdentity(nickname.Trim()), capacity, hostAnalyzer);
    }

    internal void Reconnect()
    {
        if (!CanReconnect || online != null || preview != null || connecting) return;
        ConnectClient(false, reconnectRoom, reconnectIdentity, HoldemRoom.Capacity, null);
    }

    private async void ConnectClient(bool host, string room, HoldemClientIdentity identity, int capacity,
        Poker.Llm.Unity.LlmUnityMentAnalyzer hostAnalyzer)
    {
        connecting = true;
        status = "";
        var client = gameObject.AddComponent<FusionClient>();
        online = client;
        try
        {
            await client.Connect(host, room, identity,
                capacity: capacity, llmDealer: host, hostAnalyzer: host ? CreateRoomAnalyzer(hostAnalyzer) : null);
        }
        catch (ArgumentException error)
        {
            if (ReferenceEquals(online, client)) status = error.Message;
            if (client != null) client.Dispose();
        }
        finally
        {
            if (ReferenceEquals(online, client)) connecting = false;
        }
    }

    internal void StartPreview(Poker.Llm.Unity.LlmUnityMentAnalyzer hostAnalyzer)
    {
        if (preview != null || online != null || connecting) return;
        ClearReconnect();
        status = "";
        var analyzer = CreateRoomAnalyzer(hostAnalyzer);
        if (analyzer != null) analyzer.gameObject.SetActive(true);
        CreatePracticeTable();
    }

    private void CreatePracticeTable()
    {
        WatchingPractice = false;
        previewRandom = new CryptographicRandomSource();
        var nickname = PlayerPrefs.GetString("Play2.Nickname", "").Trim();
        var host = new HoldemClientIdentity(nickname.Length == 0 ? "플레이어" : nickname);
        preview = new HoldemHostCommandBridge(host, HoldemLlmTableDealer.CreateConfig(),
            new SeatId(1), previewRandom, utterancePolicy: HoldemLlmTableDealer.CreateUtterancePolicy(), chatEnabled: true);
        previewDealer = new HoldemLlmTableDealer(preview, roomAnalyzer);
        string[] opponents = { "황소", "고양이", "부엉이" };
        for (int i = 0; i < previewSeats.Length; i++)
        {
            var identity = i == 0 ? host : new HoldemClientIdentity(opponents[i - 1]);
            previewSeats[i] = Guid.NewGuid();
            preview.Dispatch(previewSeats[i], identity.AdmissionRequest());
            preview.Dispatch(previewSeats[i], PreviewRequest("ready"));
        }
    }

    internal void WatchRemainingPractice()
    {
        if (!IsLocal || state?.game?.hasResult != true || !state.game.canContinue || state.paused || ActionPending
            || !Array.Exists(state.game.seats, seat => seat.viewer && seat.stack == 0)) return;
        WatchingPractice = true;
        StartHand();
    }

    internal void NewPracticeAfterBust(string completedHand)
    {
        // A fresh bot table is local to this player. A zero stack during an all-in is still live.
        if (!IsLocal || leaving || ActionPending || state?.game?.hasResult != true || state.paused
            || state.game.handId != completedHand) return;
        var self = Array.Find(state.game.seats, seat => seat.seat == state.viewerSeat);
        if (self == null || self.stack != 0) return;

        DisposePracticeTable();
        CreatePracticeTable();
        if (preview.TryCreateProjection(previewSeats[0], out state)) StartHand();
    }

    private HoldemWireRequest PreviewRequest(string type) => new HoldemWireRequest
    {
        protocol = HoldemRoomPacketMapper.ProtocolVersion, type = type, ready = true,
        id = Guid.NewGuid().ToString("N"), sessionId = preview.SessionId.ToString("N")
    };

    internal void StartHand()
    {
        if (!IsHost) return;
        Submit(new HoldemWireRequest { type = "start", handId = Guid.NewGuid().ToString("N"),
            version = state?.hasGame == true ? state.game.version : 0 });
    }

    internal void ToggleRematchReady()
    {
        if (state?.game?.isOver != true || !state.rematchSupported || state.paused) return;
        var member = Array.Find(state.members, value => value.seat == state.viewerSeat);
        if (member == null) return;
        bool ready = !member.rematchReady;
        if (!Submit(new HoldemWireRequest { type = "rematch-ready", handId = state.game.handId,
            version = state.game.version, rematchRevision = member.rematchRevision, ready = ready })) return;
        if (preview == null) return;
        // Local preview seats follow the player's choice without starting a new match automatically.
        for (int i = 1; i < previewSeats.Length; i++)
        {
            if (!preview.TryCreateProjection(previewSeats[i], out var packet)) continue;
            var participant = Array.Find(packet.members, value => value.seat == packet.viewerSeat);
            var request = PreviewRequest("rematch-ready");
            request.handId = packet.game.handId;
            request.version = packet.game.version;
            request.rematchRevision = participant.rematchRevision;
            request.ready = ready;
            preview.Dispatch(previewSeats[i], request);
        }
    }

    internal void RestartMatch()
    {
        if (!IsHost || state?.game?.isOver != true) return;
        Submit(new HoldemWireRequest { type = "rematch", handId = Guid.NewGuid().ToString("N"),
            completedHandId = state.game.handId, version = state.game.version });
    }

    internal async void Leave()
    {
        if (leaving) return;
        status = "";
        leaving = true;
        try
        {
            if (online != null && online.IsHost) await online.EndHostedRoom();
        }
        catch (Exception error) { Debug.LogException(error); }
        finally
        {
            leaving = false;
            if (this != null) CloseRoom();
        }
    }

    private void ClearReconnect()
    {
        reconnectIdentity = null;
        reconnectRoom = null;
        ClearChatDraft();
    }

    internal void ClearChatDraft()
    {
        ChatDraft = "";
        ChatDraftToDealer = false;
        ChatDeliveryUnconfirmed = false;
    }

    private void CloseRoom()
    {
        ClearReconnect();
        ClearPendingAction();
        connecting = false;
        if (online != null)
        {
            online.Dispose();
            Destroy(online);
            online = null;
        }

        DisposePracticeTable();
        DestroyRoomAnalyzer();
        state = null;
    }

    private void DisposePracticeTable()
    {
        previewDealer?.Dispose();
        previewDealer = null;
        preview?.Dispose();
        preview = null;
        previewRandom?.Dispose();
        previewRandom = null;
    }

    private void OnDestroy()
    {
        if (Current != this) return;
        CloseRoom();
        Current = null;
    }
}
