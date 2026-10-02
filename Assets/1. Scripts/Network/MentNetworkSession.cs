using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Fusion;
using Fusion.Sockets;
using Newtonsoft.Json;
using UnityEngine;

// Fusion Host Mode 세션. NetworkObject 없이 reliable data로 메시지를 주고받는다.
// Client는 멘트 원문만 Host에 보내고, Host가 분석·선택한 라운드 상태를 모든 Client에 보낸다.
public class MentNetworkSession : MonoBehaviour, INetworkRunnerCallbacks
{
    public const int MaxPlayers = 4;

    private enum MessageKind
    {
        SubmitMent = 1,
        RoundState = 2
    }

    [SerializeField] private string sessionName = "OneMoreCard-Test";
    [SerializeField] private LlmUnityMentAnalyzer analyzer;
    [Tooltip("Host로 시작할 때만 활성화할 오브젝트. LLM 오브젝트를 비활성화해 두고 지정하면 Client는 모델을 로딩하지 않는다.")]
    [SerializeField] private GameObject hostOnlyObject;
    [SerializeField, Min(1f)] private float selectionWaitSeconds = 20f;

    private NetworkRunner _runner;
    private MentRoundHost _round;
    private int _sendSequence;

    public string Status { get; private set; } = "연결 안 됨";
    public bool IsStarting { get; private set; }
    public bool IsRunning => _runner != null && _runner.IsRunning;
    public bool IsHost => IsRunning && _runner.IsServer;
    public int LocalPlayerId => IsRunning ? _runner.LocalPlayer.PlayerId : MentRoundState.NoPlayer;
    public IEnumerable<int> PlayerIds => IsRunning ? _runner.ActivePlayers.Select(player => player.PlayerId) : Enumerable.Empty<int>();
    public bool IsAnalyzerReady => analyzer != null && analyzer.IsReady;
    // Host는 자신의 라운드 상태를, Client는 Host에게 마지막으로 받은 상태를 가진다.
    public MentRoundState State { get; private set; }

    public async void StartHost() => await StartAsync(GameMode.Host);

    public async void StartClient() => await StartAsync(GameMode.Client);

    public async void Leave()
    {
        if (_runner == null) return;
        await _runner.Shutdown();
    }

    public void SubmitMent(string ment)
    {
        if (!IsRunning || string.IsNullOrWhiteSpace(ment)) return;

        if (IsHost) HandleSubmission(LocalPlayerId, ment);
        else _runner.SendReliableDataToServer(NextKey(MessageKind.SubmitMent), Encoding.UTF8.GetBytes(ment));
    }

    public async void SelectNow()
    {
        if (!IsHost) return;

        try
        {
            await _round.SelectAsync(selectionWaitSeconds);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex, this);
        }
    }

    public void NextRound()
    {
        if (IsHost) _round.NextRound();
    }

    private async Task StartAsync(GameMode mode)
    {
        if (_runner != null || IsStarting) return;

        if (mode == GameMode.Host)
        {
            if (analyzer == null)
            {
                Status = $"Host 시작 불가: {nameof(LlmUnityMentAnalyzer)}가 지정되지 않음";
                return;
            }
            if (hostOnlyObject != null) hostOnlyObject.SetActive(true);
        }

        IsStarting = true;
        Status = mode == GameMode.Host ? "Host 시작 중..." : "접속 중...";

        if (mode == GameMode.Host)
        {
            _round = new MentRoundHost(analyzer, new System.Random());
            _round.Changed += OnRoundChanged;
            State = _round.State;
        }

        _runner = new GameObject($"NetworkRunner ({mode})").AddComponent<NetworkRunner>();
        _runner.ProvideInput = false;
        _runner.AddCallbacks(this);

        string error;
        try
        {
            StartGameResult result = await _runner.StartGame(new StartGameArgs
            {
                GameMode = mode,
                SessionName = sessionName,
                PlayerCount = MaxPlayers
            });
            error = result.Ok ? null : $"{result.ShutdownReason} {result.ErrorMessage}";
        }
        catch (Exception ex)
        {
            Debug.LogException(ex, this);
            error = ex.Message;
        }
        IsStarting = false;

        if (error != null)
        {
            ReleaseRound();
            if (_runner != null) Destroy(_runner.gameObject);
            _runner = null;
            State = null;
            Status = $"시작 실패: {error}";
            return;
        }

        Status = mode == GameMode.Host ? $"Host 중 (세션: {sessionName})" : $"Client 접속됨 (세션: {sessionName})";
    }

    private void HandleSubmission(int playerId, string ment)
    {
        if (!_round.TrySubmit(playerId, ment)) return;
        if (_round.State.Entries.Count >= _runner.ActivePlayers.Count()) SelectNow();
    }

    private void OnRoundChanged()
    {
        State = _round.State;
        if (!IsRunning) return;

        byte[] data = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(State));
        foreach (PlayerRef player in _runner.ActivePlayers)
        {
            if (player != _runner.LocalPlayer) SendState(player, data);
        }
    }

    private void SendState(PlayerRef player, byte[] data) =>
        _runner.SendReliableDataToPlayer(player, NextKey(MessageKind.RoundState), data);

    private ReliableKey NextKey(MessageKind kind) => ReliableKey.FromInts((int)kind, ++_sendSequence, 0, 0);

    private void OnDestroy()
    {
        _round?.Cancel();
        if (_runner != null) _runner.Shutdown();
    }

    void INetworkRunnerCallbacks.OnPlayerJoined(NetworkRunner runner, PlayerRef player)
    {
        if (runner.IsServer && _round != null && player != runner.LocalPlayer)
        {
            SendState(player, Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(_round.State)));
        }
    }

    void INetworkRunnerCallbacks.OnPlayerLeft(NetworkRunner runner, PlayerRef player)
    {
        if (!runner.IsServer || _round == null) return;
        int remaining = runner.ActivePlayers.Count(active => active != player);
        if (_round.State.Phase == MentRoundPhase.Collecting && _round.State.Entries.Count > 0 && _round.State.Entries.Count >= remaining)
        {
            SelectNow();
        }
    }

    void INetworkRunnerCallbacks.OnReliableDataReceived(NetworkRunner runner, PlayerRef player, ReliableKey key, ReadOnlySpan<byte> data)
    {
        key.GetInts(out int kind, out _, out _, out _);
        string text = Encoding.UTF8.GetString(data.ToArray());

        if (runner.IsServer)
        {
            // 보낸 플레이어는 payload가 아니라 Fusion이 알려준 PlayerRef로 판단한다.
            if (kind == (int)MessageKind.SubmitMent && _round != null) HandleSubmission(player.PlayerId, text);
            return;
        }

        if (kind != (int)MessageKind.RoundState) return;
        try
        {
            State = JsonConvert.DeserializeObject<MentRoundState>(text);
        }
        catch (JsonException ex)
        {
            Debug.LogWarning($"[{nameof(MentNetworkSession)}] Invalid round state: {ex.Message}", this);
        }
    }

    private void ReleaseRound()
    {
        if (_round == null) return;
        _round.Changed -= OnRoundChanged;
        _round.Cancel();
        _round = null;
    }

    void INetworkRunnerCallbacks.OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason)
    {
        ReleaseRound();
        _runner = null;
        State = null;
        Status = $"연결 종료: {shutdownReason}";
    }

    void INetworkRunnerCallbacks.OnDisconnectedFromServer(NetworkRunner runner, NetDisconnectReason reason) =>
        Status = $"Host와 연결 끊김: {reason}";

    void INetworkRunnerCallbacks.OnConnectFailed(NetworkRunner runner, NetAddress remoteAddress, NetConnectFailedReason reason) =>
        Status = $"접속 실패: {reason}";

    void INetworkRunnerCallbacks.OnConnectRequest(NetworkRunner runner, NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token) { }
    void INetworkRunnerCallbacks.OnReliableDataProgress(NetworkRunner runner, PlayerRef player, ReliableKey key, float progress) { }
    void INetworkRunnerCallbacks.OnObjectExitAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
    void INetworkRunnerCallbacks.OnObjectEnterAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
    void INetworkRunnerCallbacks.OnInput(NetworkRunner runner, NetworkInput input) { }
    void INetworkRunnerCallbacks.OnInputMissing(NetworkRunner runner, PlayerRef player, NetworkInput input) { }
    void INetworkRunnerCallbacks.OnConnectedToServer(NetworkRunner runner) { }
    void INetworkRunnerCallbacks.OnSessionListUpdated(NetworkRunner runner, List<SessionInfo> sessionList) { }
    void INetworkRunnerCallbacks.OnCustomAuthenticationResponse(NetworkRunner runner, Dictionary<string, object> data) { }
    void INetworkRunnerCallbacks.OnHostMigration(NetworkRunner runner, HostMigrationToken hostMigrationToken) { }
    void INetworkRunnerCallbacks.OnSceneLoadDone(NetworkRunner runner) { }
    void INetworkRunnerCallbacks.OnSceneLoadStart(NetworkRunner runner) { }
}
