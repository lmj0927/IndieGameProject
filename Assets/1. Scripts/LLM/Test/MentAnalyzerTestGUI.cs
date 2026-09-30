using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using Debug = UnityEngine.Debug;

// IMGUI TextField는 Input System 전용 설정에서 한글 IME 입력을 받지 못하므로
// Input System의 텍스트 입력/IME 조합 이벤트로 직접 입력을 받는다.
public class MentAnalyzerTestGUI : MonoBehaviour
{
    private const int MaxMentLength = 200;
    private const int MaxLogCount = 50;

    [SerializeField] private LlmUnityMentAnalyzer analyzer;
    [SerializeField, Range(1f, 3f)] private float guiScale = 1.5f;

    private readonly List<string> _logs = new List<string>();
    private string _ment = "";
    private string _composition = "";
    private int _compositionChangedFrame = -1;
    private int _textInputCount;
    private int _compositionEventCount;
    private char _lastTextInput;
    private bool _requesting;
    private Keyboard _keyboard;
    private GUIStyle _inputStyle;
    private Vector2 _scroll;

    private void Awake()
    {
        if (analyzer == null) analyzer = FindAnyObjectByType<LlmUnityMentAnalyzer>();
    }

    private void OnEnable()
    {
        _keyboard = Keyboard.current;
        if (_keyboard == null)
        {
            Debug.LogWarning($"[{nameof(MentAnalyzerTestGUI)}] Keyboard not found.", this);
            return;
        }

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
        // 조합 중이거나 이번 프레임에 조합이 바뀌었다면 Backspace/Enter는 IME가 처리한 것이다.
        if (_keyboard == null || _composition.Length > 0 || _compositionChangedFrame == Time.frameCount) return;

        if (_keyboard.ctrlKey.isPressed && _keyboard.vKey.wasPressedThisFrame)
        {
            PasteFromClipboard();
        }

        if (_keyboard.backspaceKey.wasPressedThisFrame && _ment.Length > 0)
        {
            _ment = _ment.Substring(0, _ment.Length - 1);
        }

        if (_keyboard.enterKey.wasPressedThisFrame || _keyboard.numpadEnterKey.wasPressedThisFrame)
        {
            Submit();
        }
    }

    private void OnTextInput(char character)
    {
        _textInputCount++;
        _lastTextInput = character;
        if (char.IsControl(character) || _ment.Length >= MaxMentLength) return;
        _ment += character;
    }

    // IME가 켜지지 않는 환경에서도 한글 멘트를 테스트할 수 있게 클립보드 붙여넣기를 지원한다.
    private void PasteFromClipboard()
    {
        foreach (char character in GUIUtility.systemCopyBuffer ?? "")
        {
            if (_ment.Length >= MaxMentLength) break;
            if (char.IsWhiteSpace(character)) _ment += ' ';
            else if (!char.IsControl(character)) _ment += character;
        }
    }

    private void OnImeCompositionChange(IMECompositionString composition)
    {
        _composition = composition.ToString();
        _compositionChangedFrame = Time.frameCount;
        _compositionEventCount++;
    }

    private void OnGUI()
    {
        GUI.matrix = Matrix4x4.Scale(new Vector3(guiScale, guiScale, 1f));
        float width = Screen.width / guiScale;
        float height = Screen.height / guiScale;

        _inputStyle ??= new GUIStyle(GUI.skin.textField) { alignment = TextAnchor.MiddleLeft };

        GUILayout.BeginArea(new Rect(10f, 10f, width - 20f, height - 20f), GUI.skin.box);

        GUILayout.Label($"LLM 상태: {GetStatusText()}");
        GUILayout.Label("Game 뷰를 클릭한 뒤 바로 입력하세요. Enter: 분석, Backspace: 한 글자 지우기, Ctrl+V: 붙여넣기");
        GUILayout.Label(GetInputDiagnostics());

        GUILayout.BeginHorizontal();
        GUILayout.Label($"{_ment}{_composition}|", _inputStyle, GUILayout.ExpandWidth(true));
        GUI.enabled = !_requesting;
        if (GUILayout.Button(_requesting ? "분석 중..." : "분석", GUILayout.Width(100f))) Submit();
        GUI.enabled = true;
        if (GUILayout.Button("붙여넣기", GUILayout.Width(100f))) PasteFromClipboard();
        if (GUILayout.Button("입력 지우기", GUILayout.Width(100f))) _ment = "";
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        GUILayout.Label($"결과 로그 ({_logs.Count}/{MaxLogCount}, 최신순)");
        if (GUILayout.Button("로그 지우기", GUILayout.Width(100f))) _logs.Clear();
        GUILayout.EndHorizontal();

        _scroll = GUILayout.BeginScrollView(_scroll);
        for (int i = _logs.Count - 1; i >= 0; i--)
        {
            GUILayout.Label(_logs[i]);
        }
        GUILayout.EndScrollView();

        GUILayout.EndArea();
    }

    private async void Submit()
    {
        if (_requesting) return;
        if (analyzer == null)
        {
            AddLog($"{nameof(LlmUnityMentAnalyzer)}를 찾을 수 없습니다.");
            return;
        }

        string ment = _ment;
        if (string.IsNullOrWhiteSpace(ment)) return;

        _requesting = true;
        _ment = "";

        try
        {
            var stopwatch = Stopwatch.StartNew();
            MentAnalysisResult result = await analyzer.AnalyzeAsync(ment, destroyCancellationToken);
            stopwatch.Stop();
            AddLog(Format(ment, result, stopwatch.ElapsedMilliseconds));
        }
        finally
        {
            _requesting = false;
        }
    }

    private string GetStatusText()
    {
        if (analyzer == null) return $"{nameof(LlmUnityMentAnalyzer)} 없음";
        return analyzer.IsReady ? "준비됨" : "준비 안 됨 (모델 로딩 중이거나 LLM 설정 확인 필요)";
    }

    private string GetInputDiagnostics()
    {
        if (_keyboard == null) return "입력 진단: 키보드 없음";

        string last = _textInputCount == 0 ? "없음" : $"'{_lastTextInput}' U+{(int)_lastTextInput:X4}";
        return $"입력 진단: IME 선택={_keyboard.imeSelected.isPressed}, 텍스트 이벤트={_textInputCount}회 (마지막 {last}), 조합 이벤트={_compositionEventCount}회";
    }

    private static string Format(string ment, MentAnalysisResult result, long elapsedMs)
    {
        string header = $"[{DateTime.Now:HH:mm:ss}] {elapsedMs}ms  \"{ment}\"";
        string body = result.IsValid
            ? $"  {result.Status} | {result.Analysis}"
            : $"  {result.Status} | {result.Error}";
        string interpretation = $"  해석: {result.Interpretation ?? "없음"}";
        string raw = $"  raw: {result.RawResponse ?? "null"}";
        return $"{header}\n{body}\n{interpretation}\n{raw}";
    }

    private void AddLog(string message)
    {
        Debug.Log($"[{nameof(MentAnalyzerTestGUI)}]\n{message}");
        _logs.Add(message);
        if (_logs.Count > MaxLogCount) _logs.RemoveAt(0);
    }
}
