using System;
using System.Threading;
using System.Threading.Tasks;
using LLMUnity;
using UnityEngine;

// 1단계(interpreterAgent)가 짧은 연상 메모를 쓰고, 2단계(agent)가 멘트와 메모를 JSON으로 변환한다.
// 1단계가 없거나 실패하면 멘트만으로 2단계를 실행한다.
public class LlmUnityMentAnalyzer : MonoBehaviour, IMentAnalyzer
{
    private const int MaxInterpretationLength = 120;

    [Tooltip("JSON 변환 에이전트")]
    [SerializeField] private LLMAgent agent;
    [Tooltip("연상 메모 에이전트. 비워두면 해석 단계를 건너뛴다. agent와 다른 컴포넌트여야 한다.")]
    [SerializeField] private LLMAgent interpreterAgent;
    [SerializeField, Min(0.1f)] private float timeoutSeconds = 5f;
    [SerializeField, Min(0.1f)] private float interpreterTimeoutSeconds = 4f;
    [SerializeField, Min(1)] private int interpreterMaxTokens = 80;
    [SerializeField] private bool warmupOnStart = true;

    // LLMAgent 하나는 요청을 동시에 처리하지 못하므로 에이전트별로 순서대로 보낸다.
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
    private readonly SemaphoreSlim _interpreterGate = new SemaphoreSlim(1, 1);

    public bool IsReady => IsAgentReady(agent);

    private bool UsesInterpreter => interpreterAgent != null && interpreterAgent != agent;

    private void Awake()
    {
        if (agent == null)
        {
            Debug.LogError($"[{nameof(LlmUnityMentAnalyzer)}] LLMAgent is not assigned.", this);
            return;
        }

        agent.systemPrompt = MentAnalysisPrompt.SystemPrompt;
        agent.grammar = MentAnalysisPrompt.JsonSchema;
        agent.temperature = 0f;
        agent.numPredict = 64;
        agent.save = "";

        if (interpreterAgent == agent)
        {
            Debug.LogWarning($"[{nameof(LlmUnityMentAnalyzer)}] interpreterAgent must be a different LLMAgent. Interpretation is disabled.", this);
        }
        if (!UsesInterpreter) return;

        interpreterAgent.systemPrompt = MentAnalysisPrompt.InterpreterSystemPrompt;
        interpreterAgent.grammar = "";
        interpreterAgent.temperature = 0f;
        interpreterAgent.numPredict = interpreterMaxTokens;
        interpreterAgent.save = "";
    }

    private async void Start()
    {
        if (agent == null || !warmupOnStart) return;

        try
        {
            if (UsesInterpreter) await Task.WhenAll(agent.Warmup(), interpreterAgent.Warmup());
            else await agent.Warmup();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[{nameof(LlmUnityMentAnalyzer)}] Warmup failed: {ex.Message}", this);
        }
    }

    public async Task<MentAnalysisResult> AnalyzeAsync(string ment, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ment)) return MentAnalysisResult.Success(MentAnalysis.NoIntent, null);
        if (agent == null) return MentAnalysisResult.Unavailable("LLMAgent is not assigned.");

        string interpretation;
        try
        {
            interpretation = await InterpretAsync(ment, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return MentAnalysisResult.Cancelled();
        }

        MentAnalysisResult result = await ConvertAsync(ment, interpretation, cancellationToken);
        return result.WithInterpretation(interpretation);
    }

    // 해석은 보조 단계이므로 호출자 취소 외의 실패는 null로 넘기고 2단계를 계속한다.
    private async Task<string> InterpretAsync(string ment, CancellationToken cancellationToken)
    {
        if (!UsesInterpreter || !IsAgentReady(interpreterAgent)) return null;

        try
        {
            string reply = await ChatAsync(interpreterAgent, _interpreterGate,
                MentAnalysisPrompt.BuildInterpreterMessage(ment), interpreterTimeoutSeconds, cancellationToken);
            return CleanInterpretation(reply);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[{nameof(LlmUnityMentAnalyzer)}] Interpretation skipped: {ex.Message}", this);
            return null;
        }
    }

    private async Task<MentAnalysisResult> ConvertAsync(string ment, string interpretation, CancellationToken cancellationToken)
    {
        if (!IsReady) return MentAnalysisResult.Unavailable("LLM is not ready.");

        string raw;
        try
        {
            raw = await ChatAsync(agent, _gate,
                MentAnalysisPrompt.BuildUserMessage(ment, interpretation), timeoutSeconds, cancellationToken);
        }
        catch (TimeoutException)
        {
            return MentAnalysisResult.Timeout();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return MentAnalysisResult.Cancelled();
        }
        catch (Exception ex)
        {
            return MentAnalysisResult.Unavailable(ex.Message);
        }

        return MentAnalysisParser.TryParse(raw, out MentAnalysis analysis, out string error)
            ? MentAnalysisResult.Success(analysis, raw)
            : MentAnalysisResult.InvalidResponse(error, raw);
    }

    // 타임아웃은 대기열을 통과해 실제 요청을 보낸 뒤부터 잰다. 대기열 대기는 호출자 토큰으로만 끊는다.
    private static async Task<string> ChatAsync(LLMAgent target, SemaphoreSlim gate, string message,
        float timeoutSeconds, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            Task<string> chat = target.Chat(message, null, null, false);
            Task deadline = Task.Delay(Timeout.Infinite, timeoutCts.Token);

            if (await Task.WhenAny(chat, deadline) != chat)
            {
                target.CancelRequests();
                await IgnoreFailure(chat);
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException($"{target.name} timed out.");
            }

            return await chat;
        }
        finally
        {
            gate.Release();
        }
    }

    private static string CleanInterpretation(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;

        int thinkEnd = reply.LastIndexOf("</think>", StringComparison.Ordinal);
        if (thinkEnd >= 0) reply = reply.Substring(thinkEnd + "</think>".Length);

        foreach (string line in reply.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            return trimmed.Length > MaxInterpretationLength ? trimmed.Substring(0, MaxInterpretationLength) : trimmed;
        }
        return null;
    }

    private static bool IsAgentReady(LLMAgent target) =>
        target != null && target.llm != null && target.llm.started && !target.llm.failed;

    private static async Task IgnoreFailure(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception)
        {
        }
    }
}
