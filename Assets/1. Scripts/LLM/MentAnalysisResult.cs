public enum MentAnalysisStatus
{
    Success,
    InvalidResponse,
    Timeout,
    Cancelled,
    Unavailable
}

public readonly struct MentAnalysisResult
{
    public readonly MentAnalysisStatus Status;
    public readonly MentAnalysis Analysis;
    public readonly string RawResponse;
    public readonly string Error;
    // 해석 단계가 남긴 연상 메모. 해석 단계를 건너뛰었거나 실패하면 null
    public readonly string Interpretation;

    public bool IsValid => Status == MentAnalysisStatus.Success;

    private MentAnalysisResult(MentAnalysisStatus status, MentAnalysis analysis, string rawResponse, string error, string interpretation = null)
    {
        Status = status;
        Analysis = analysis;
        RawResponse = rawResponse;
        Error = error;
        Interpretation = interpretation;
    }

    public static MentAnalysisResult Success(MentAnalysis analysis, string rawResponse) =>
        new MentAnalysisResult(MentAnalysisStatus.Success, analysis, rawResponse, null);

    public static MentAnalysisResult InvalidResponse(string error, string rawResponse) =>
        new MentAnalysisResult(MentAnalysisStatus.InvalidResponse, default, rawResponse, error);

    public static MentAnalysisResult Timeout() =>
        new MentAnalysisResult(MentAnalysisStatus.Timeout, default, null, "Timed out.");

    public static MentAnalysisResult Cancelled() =>
        new MentAnalysisResult(MentAnalysisStatus.Cancelled, default, null, "Cancelled.");

    public static MentAnalysisResult Unavailable(string error) =>
        new MentAnalysisResult(MentAnalysisStatus.Unavailable, default, null, error);

    public MentAnalysisResult WithInterpretation(string interpretation) =>
        new MentAnalysisResult(Status, Analysis, RawResponse, Error, interpretation);

    public override string ToString()
    {
        string body = IsValid ? $"{Status}: {Analysis}" : $"{Status}: {Error} raw={RawResponse ?? "null"}";
        return Interpretation == null ? body : $"{body} | 해석: {Interpretation}";
    }
}
