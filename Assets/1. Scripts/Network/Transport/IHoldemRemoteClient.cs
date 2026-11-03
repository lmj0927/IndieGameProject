using System;
using Poker.Application;

namespace Poker.Transport
{
    /// <summary>Optional transport knowledge that the retained membership cannot be resumed.</summary>
    public interface IHoldemReconnectPolicy
    {
        bool RequiresFreshParticipation { get; }
    }

    /// <summary>Read-only admitted seat identity. Each transport keeps its private credentials separately.</summary>
    public interface IHoldemRemoteIdentity
    {
        string SessionId { get; }
        int Seat { get; }
    }

    /// <summary>
    /// Main-thread, seat-bound connection used by the table UI. Implementations own connection/admission
    /// validation and deliver only this participant's packet. They never own or predict poker state.
    /// Keep the same Identity object and command IDs when recovering an uncertain request.
    /// </summary>
    public interface IHoldemRemoteClient : IDisposable
    {
        IHoldemRemoteIdentity Identity { get; }
        bool IsAdmitted { get; }
        bool IsClosed { get; }
        string AdmissionError { get; }
        HoldemRoomPacket Latest { get; }
        void Poll();
        string Send(HoldemWireRequest request);
        bool TryReadResponse(out HoldemWireResponse response);
    }
}
