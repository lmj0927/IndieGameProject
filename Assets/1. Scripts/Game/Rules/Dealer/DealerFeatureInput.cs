namespace Poker.Application
{
    /// <summary>Host-process read only. Neither this capability nor its data belongs on a player wire.</summary>
    public interface IDealerFeatureSourcePort
    {
        DealerFeatureInput ReadFeatureInput();
    }

    /// <summary>One atomic read of the table and eliminated audiences. No private cards or selection policy.</summary>
    public sealed class DealerFeatureInput
    {
        internal DealerFeatureInput(HoldemRoomError error, DealerFeatureWindow table = null,
            DealerFeatureWindow eliminated = null)
        { Error = error; Table = table; Eliminated = eliminated; }
        public HoldemRoomError Error { get; }
        public DealerFeatureWindow Table { get; }
        public DealerFeatureWindow Eliminated { get; }
    }

    /// <summary>
    /// Immutable selected input, including the final frozen snapshot. The internal lifetime is shared
    /// with authority intake so a worker cannot commit a response after that input is frozen.
    /// </summary>
    public sealed class DealerFeatureWindow
    {
        internal DealerFeatureWindow(HoldemUtteranceBatch source, DealerInputLifetime lifetime, bool closed)
        { Source = source; Lifetime = lifetime; IsClosed = closed; }
        public HoldemUtteranceBatch Source { get; }
        public bool IsClosed { get; }
        internal DealerInputLifetime Lifetime { get; }
    }

    internal sealed class DealerInputLifetime
    {
        internal readonly object Gate = new object();
        internal bool Closed;
        internal void Close() { lock (Gate) Closed = true; }
    }
}
