namespace Poker.Foundation
{
    /// <summary>
    /// Supplies an integer in [0, exclusiveMax). Callers pass a positive bound.
    /// A fair shuffle requires independent, uniformly distributed results.
    /// Range validation alone cannot establish fairness or unpredictability.
    /// </summary>
    public interface IRandomSource
    {
        int NextInt(int exclusiveMax);
    }
}
