using System.Threading;
using System.Threading.Tasks;

namespace Poker.Llm
{
    /// <summary>Host-only. Turns one raw utterance into a validated analysis; failures are results, not exceptions.</summary>
    public interface IMentAnalyzer
    {
        bool IsReady { get; }

        Task<MentAnalysisResult> AnalyzeAsync(string ment, CancellationToken cancellationToken = default);
    }
}
