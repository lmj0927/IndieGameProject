using System.Threading;
using System.Threading.Tasks;

public interface IMentAnalyzer
{
    bool IsReady { get; }

    Task<MentAnalysisResult> AnalyzeAsync(string ment, CancellationToken cancellationToken = default);
}
