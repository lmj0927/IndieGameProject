using System;

namespace Poker.Online
{
    /// <summary>Adjustable prototype pacing, not an interpretation or card-selection policy.</summary>
    public readonly struct SocialFlowTiming
    {
        public readonly double Request, Accusation, Proposal, Response, Result;
        public SocialFlowTiming(double request, double accusation, double proposal, double response, double result)
        {
            foreach (double value in new[] { request, accusation, proposal, response, result })
                if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
                    throw new ArgumentOutOfRangeException(nameof(request), "Stage times must be finite and nonnegative.");
            Request = request; Accusation = accusation; Proposal = proposal; Response = response; Result = result;
        }
    }
}
