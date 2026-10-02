using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

// Host 전용 라운드 진행. 멘트를 받는 즉시 분석을 시작하고, 선택 시점에 마감 시간까지 기다린 뒤 후보 하나를 고른다.
public sealed class MentRoundHost
{
    public const int MaxMentLength = 200;

    private readonly IMentAnalyzer _analyzer;
    private readonly System.Random _random;
    private readonly Dictionary<int, MentAnalysisResult> _results = new Dictionary<int, MentAnalysisResult>();
    private readonly List<Task> _pending = new List<Task>();
    private CancellationTokenSource _roundCts = new CancellationTokenSource();

    public MentRoundState State { get; private set; } = new MentRoundState();
    public event Action Changed;

    public MentRoundHost(IMentAnalyzer analyzer, System.Random random)
    {
        _analyzer = analyzer;
        _random = random;
    }

    public bool TrySubmit(int playerId, string ment)
    {
        if (State.Phase != MentRoundPhase.Collecting || State.Find(playerId) != null) return false;

        ment = ment?.Trim() ?? "";
        if (ment.Length == 0) return false;
        if (ment.Length > MaxMentLength) ment = ment.Substring(0, MaxMentLength);

        var entry = new MentRoundEntry { PlayerId = playerId, Ment = ment, Status = "분석 중" };
        State.Entries.Add(entry);
        _pending.Add(AnalyzeAsync(entry, State.Round, _roundCts.Token));
        Changed?.Invoke();
        return true;
    }

    public async Task SelectAsync(float waitSeconds)
    {
        if (State.Phase != MentRoundPhase.Collecting || State.Entries.Count == 0) return;

        int round = State.Round;
        State.Phase = MentRoundPhase.Selecting;
        Changed?.Invoke();

        // 마감이 지나면 남은 분석은 취소되어 유효 요청에서 빠진다.
        _roundCts.CancelAfter(TimeSpan.FromSeconds(waitSeconds));
        await Task.WhenAll(_pending.ToArray());
        if (round != State.Round) return;

        var results = new List<MentAnalysisResult>(State.Entries.Count);
        foreach (MentRoundEntry entry in State.Entries)
        {
            results.Add(_results.TryGetValue(entry.PlayerId, out MentAnalysisResult result) ? result : MentAnalysisResult.Timeout());
        }

        int index = MentCandidateSelector.Select(results, _random);
        if (index >= 0)
        {
            State.SelectedPlayerId = State.Entries[index].PlayerId;
            State.SelectedSummary = results[index].Analysis.ToString();
        }
        State.Phase = MentRoundPhase.Result;
        Changed?.Invoke();
    }

    public void NextRound()
    {
        Cancel();
        _roundCts = new CancellationTokenSource();
        _results.Clear();
        _pending.Clear();
        State = new MentRoundState { Round = State.Round + 1 };
        Changed?.Invoke();
    }

    public void Cancel() => _roundCts.Cancel();

    // 실패는 결과로 기록하고 예외를 밖으로 던지지 않는다. 지난 라운드의 결과는 버린다.
    private async Task AnalyzeAsync(MentRoundEntry entry, int round, CancellationToken token)
    {
        MentAnalysisResult result;
        try
        {
            while (!_analyzer.IsReady) await Task.Delay(200, token);
            result = await _analyzer.AnalyzeAsync(entry.Ment, token);
        }
        catch (OperationCanceledException)
        {
            result = MentAnalysisResult.Cancelled();
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            result = MentAnalysisResult.Unavailable(ex.Message);
        }

        if (round != State.Round) return;

        _results[entry.PlayerId] = result;
        entry.Analyzed = true;
        entry.Status = result.Status.ToString();
        entry.Summary = result.IsValid ? result.Analysis.ToString() : result.Error;
        entry.Interpretation = result.Interpretation;
        Changed?.Invoke();
    }
}
