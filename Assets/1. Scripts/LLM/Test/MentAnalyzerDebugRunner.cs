using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

public class MentAnalyzerDebugRunner : MonoBehaviour
{
    [SerializeField] private LlmUnityMentAnalyzer analyzer;
    [SerializeField] private bool runOnStart = true;

    [SerializeField] private string[] samples =
    {
        "오늘 날씨 좋네요.",
        "오늘은 빨간색이 좋네.",
        "하트가 하나만 더 있으면 좋겠는데.",
        "하트 카드 한 장 주세요.",
        "하트 7 한 장 줘.",
        "스페이드 에이스 한 장만 나와라 제발",
        "7이면 좋겠다",
        "오늘따라 검은색이 끌리네",
        "킹이나 한 장 떨어져라",
        "배고픈데 끝나고 치킨 먹을 사람"
    };

    private bool _running;

    private async void Start()
    {
        if (!runOnStart || analyzer == null) return;

        float waited = 0f;
        while (!analyzer.IsReady && waited < 120f)
        {
            await Awaitable.WaitForSecondsAsync(0.5f);
            waited += 0.5f;
        }
        RunSamples();
    }

    [ContextMenu("Run Samples")]
    public async void RunSamples()
    {
        if (_running || analyzer == null) return;
        _running = true;

        try
        {
            foreach (string sample in samples)
            {
                var stopwatch = Stopwatch.StartNew();
                MentAnalysisResult result = await analyzer.AnalyzeAsync(sample, destroyCancellationToken);
                stopwatch.Stop();
                Debug.Log($"[MentAnalyzer] {stopwatch.ElapsedMilliseconds,5}ms | {sample} => {result}");
            }
        }
        finally
        {
            _running = false;
        }
    }
}
