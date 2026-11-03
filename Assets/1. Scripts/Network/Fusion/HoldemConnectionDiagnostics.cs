using System;
using System.Collections.Generic;
using Fusion;
using Fusion.Sockets;
using Poker.Application;
using Poker.Transport;

namespace Poker.Online
{
    public enum HoldemConnectionStage { Joining, Rejoining, Lobby, Table }

    /// <summary>Shareable allow-listed metadata only. Never accepts room, identity, cards or speech.</summary>
    public static class HoldemConnectionDiagnostics
    {
        private static readonly HashSet<string> KnownReasons = Reasons();

        private static HashSet<string> Reasons()
        {
            var result = new HashSet<string>(StringComparer.Ordinal) {
                "SessionGone", "HostEnded", "LobbyLeft", "InvalidIdentity", "AdmissionTimeout", "SendFailed",
                "InvalidServerEnvelope", "InvalidServerReply", "InvalidAdmissionReply", "InvalidServerState",
                "ResponseOverflow", "LocalInputOverflow", "TableStopped", "ShutdownFailed", "MissingAppId",
                "ConnectionClosed", "AdmissionLimit"
            };
            foreach (var type in new[] { typeof(ShutdownReason), typeof(NetDisconnectReason),
                typeof(NetConnectFailedReason), typeof(HoldemRoomError) })
                foreach (string name in Enum.GetNames(type)) result.Add(name);
            return result;
        }

        public static string SafeReason(string reason) => reason != null && reason.Length <= 64
            && KnownReasons.Contains(reason) ? reason : "Unknown";

        public static string SafePath(string path) => path == "Direct" || path == "Relayed" || path == "None" ? path : "Unknown";

        public static bool IsLocalFailure(string reason) => reason == "MissingAppId" || reason == "AdmissionTimeout"
            || reason == "SendFailed" || reason == "InvalidServerEnvelope" || reason == "InvalidServerReply"
            || reason == "InvalidAdmissionReply" || reason == "InvalidServerState"
            || reason == "ResponseOverflow" || reason == "LocalInputOverflow";

        public static string Create(bool host, bool development, HoldemConnectionStage stage, string path, string reason)
        {
            string phase = stage == HoldemConnectionStage.Joining ? "joining"
                : stage == HoldemConnectionStage.Rejoining ? "rejoining"
                : stage == HoldemConnectionStage.Lobby ? "lobby"
                : stage == HoldemConnectionStage.Table ? "table" : "unknown";
            return "One More Card / connection-report-v1\n"
                + "protocol=" + HoldemRoomPacketMapper.ProtocolVersion + "\n"
                + "role=" + (host ? "host" : "guest") + "\n"
                + "mode=" + (development ? "dealer-test" : "poker") + "\n"
                + "stage=" + phase + "\n"
                + "path=" + SafePath(path) + "\n"
                + "reason=" + SafeReason(reason);
        }
    }
}
