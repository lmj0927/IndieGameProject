using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

// Host/Join, 멘트 제출, Host의 선택 결과를 확인하는 테스트 화면.
// 입력은 MentAnalyzerTestGUI와 같이 Input System 텍스트 이벤트와 클립보드 붙여넣기로 받는다.
public class MentNetworkTestGUI : MonoBehaviour
{
    [SerializeField] private MentNetworkSession session;
    [SerializeField, Range(1f, 3f)] private float guiScale = 1.5f;

    private string _ment = "";
    private string _composition = "";
    private int _compositionChangedFrame = -1;
    private Keyboard _keyboard;
    private GUIStyle _inputStyle;
    private Vector2 _scroll;

    private void Awake()
    {
        if (session == null) session = FindAnyObjectByType<MentNetworkSession>();
    }

    private void OnEnable()
    {
        _keyboard = Keyboard.current;
        if (_keyboard == null) return;

        _keyboard.onTextInput += OnTextInput;
        _keyboard.onIMECompositionChange += OnImeCompositionChange;
        _keyboard.SetIMEEnabled(true);
    }

    private void OnDisable()
    {
        if (_keyboard == null) return;

        _keyboard.onTextInput -= OnTextInput;
        _keyboard.onIMECompositionChange -= OnImeCompositionChange;
        _keyboard.SetIMEEnabled(false);
        _keyboard = null;
    }

    private void Update()
    {
        if (_keyboard == null || _composition.Length > 0 || _compositionChangedFrame == Time.frameCount) return;

        if (_keyboard.ctrlKey.isPressed && _keyboard.vKey.wasPressedThisFrame) PasteFromClipboard();
        if (_keyboard.backspaceKey.wasPressedThisFrame && _ment.Length > 0) _ment = _ment.Substring(0, _ment.Length - 1);
        if (_keyboard.enterKey.wasPressedThisFrame || _keyboard.numpadEnterKey.wasPressedThisFrame) Submit();
    }

    private void OnTextInput(char character)
    {
        if (char.IsControl(character) || _ment.Length >= MentRoundHost.MaxMentLength) return;
        _ment += character;
    }

    private void OnImeCompositionChange(IMECompositionString composition)
    {
        _composition = composition.ToString();
        _compositionChangedFrame = Time.frameCount;
    }

    private void PasteFromClipboard()
    {
        foreach (char character in GUIUtility.systemCopyBuffer ?? "")
        {
            if (_ment.Length >= MentRoundHost.MaxMentLength) break;
            if (char.IsWhiteSpace(character)) _ment += ' ';
            else if (!char.IsControl(character)) _ment += character;
        }
    }

    private void OnGUI()
    {
        GUI.matrix = Matrix4x4.Scale(new Vector3(guiScale, guiScale, 1f));
        float width = Screen.width / guiScale;
        float height = Screen.height / guiScale;
        _inputStyle ??= new GUIStyle(GUI.skin.textField) { alignment = TextAnchor.MiddleLeft };

        GUILayout.BeginArea(new Rect(10f, 10f, width - 20f, height - 20f), GUI.skin.box);

        if (session == null)
        {
            GUILayout.Label($"{nameof(MentNetworkSession)}를 찾을 수 없습니다.");
            GUILayout.EndArea();
            return;
        }

        GUILayout.Label($"네트워크: {session.Status}");
        if (!session.IsRunning) DrawLobby();
        else DrawRoom();

        GUILayout.EndArea();
    }

    private void DrawLobby()
    {
        GUI.enabled = !session.IsStarting;
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Host", GUILayout.Width(120f), GUILayout.Height(40f))) session.StartHost();
        if (GUILayout.Button("Join", GUILayout.Width(120f), GUILayout.Height(40f))) session.StartClient();
        GUILayout.EndHorizontal();
        GUI.enabled = true;
    }

    private void DrawRoom()
    {
        int localId = session.LocalPlayerId;
        string players = string.Join(", ", session.PlayerIds.OrderBy(id => id).Select(id => id == localId ? $"P{id}(나)" : $"P{id}"));

        GUILayout.BeginHorizontal();
        GUILayout.Label($"역할: {(session.IsHost ? "Host" : "Client")} | 플레이어 ({session.PlayerIds.Count()}/{MentNetworkSession.MaxPlayers}): {players}");
        if (GUILayout.Button("나가기", GUILayout.Width(100f))) session.Leave();
        GUILayout.EndHorizontal();

        if (session.IsHost) GUILayout.Label($"LLM: {(session.IsAnalyzerReady ? "준비됨" : "로딩 중 (멘트는 준비되면 분석됩니다)")}");

        MentRoundState state = session.State;
        if (state == null)
        {
            GUILayout.Label("Host의 라운드 상태를 기다리는 중...");
            return;
        }

        GUILayout.Label($"라운드 {state.Round} | {PhaseText(state.Phase)}");
        DrawInput(state, localId);
        if (session.IsHost) DrawHostControls(state);
        DrawResult(state);
        DrawEntries(state, localId);
    }

    private void DrawInput(MentRoundState state, int localId)
    {
        bool submitted = state.Find(localId) != null;
        GUILayout.Label(submitted
            ? "이번 라운드 멘트를 제출했습니다."
            : "멘트를 입력하세요. Enter: 제출, Backspace: 지우기, Ctrl+V: 붙여넣기");

        GUILayout.BeginHorizontal();
        GUILayout.Label($"{_ment}{_composition}|", _inputStyle, GUILayout.ExpandWidth(true));
        GUI.enabled = CanSubmit(state, localId);
        if (GUILayout.Button("제출", GUILayout.Width(100f))) Submit();
        GUI.enabled = true;
        if (GUILayout.Button("붙여넣기", GUILayout.Width(100f))) PasteFromClipboard();
        if (GUILayout.Button("지우기", GUILayout.Width(100f))) _ment = "";
        GUILayout.EndHorizontal();
    }

    private void DrawHostControls(MentRoundState state)
    {
        GUILayout.BeginHorizontal();
        GUI.enabled = state.Phase == MentRoundPhase.Collecting && state.Entries.Count > 0;
        if (GUILayout.Button("지금 선택", GUILayout.Width(120f))) session.SelectNow();
        GUI.enabled = state.Phase == MentRoundPhase.Result;
        if (GUILayout.Button("다음 라운드", GUILayout.Width(120f))) session.NextRound();
        GUI.enabled = true;
        GUILayout.EndHorizontal();
    }

    private static void DrawResult(MentRoundState state)
    {
        if (state.Phase != MentRoundPhase.Result) return;

        GUILayout.Label(state.SelectedPlayerId == MentRoundState.NoPlayer
            ? "선택 결과: 유효한 카드 요청 없음 → 정상 덱 진행"
            : $"선택 결과: P{state.SelectedPlayerId} | {state.SelectedSummary}");
    }

    private void DrawEntries(MentRoundState state, int localId)
    {
        GUILayout.Label($"제출된 멘트 ({state.Entries.Count})");
        _scroll = GUILayout.BeginScrollView(_scroll);
        foreach (MentRoundEntry entry in state.Entries)
        {
            string marker = entry.PlayerId == state.SelectedPlayerId ? "★ " : "";
            string me = entry.PlayerId == localId ? "(나)" : "";
            string line = $"{marker}P{entry.PlayerId}{me} \"{entry.Ment}\"\n  {entry.Status}";
            if (entry.Analyzed) line += $" | {entry.Summary}\n  해석: {entry.Interpretation ?? "없음"}";
            GUILayout.Label(line);
        }
        GUILayout.EndScrollView();
    }

    private void Submit()
    {
        if (session == null || session.State == null || !CanSubmit(session.State, session.LocalPlayerId)) return;
        session.SubmitMent(_ment);
        _ment = "";
    }

    private bool CanSubmit(MentRoundState state, int localId) =>
        session.IsRunning
        && state.Phase == MentRoundPhase.Collecting
        && state.Find(localId) == null
        && !string.IsNullOrWhiteSpace(_ment);

    private static string PhaseText(MentRoundPhase phase) => phase switch
    {
        MentRoundPhase.Collecting => "멘트 받는 중",
        MentRoundPhase.Selecting => "분석 마무리 및 선택 중",
        _ => "결과"
    };
}
