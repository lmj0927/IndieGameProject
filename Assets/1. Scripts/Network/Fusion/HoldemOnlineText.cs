namespace Poker.Online
{
    public static class HoldemOnlineText
    {
        public static bool RoomIsMissing(string error) => error == "GameNotFound" || error == "NoMatchFound";

        public static bool RequiresFreshParticipation(string error) => error == "SessionGone" || error == "HostEnded"
            || error == "LobbyLeft" || error == "InvalidIdentity" || error == "ClientUpgradeRequired"
            || error == "IncompatibleConfiguration";

        public static string ConnectionFailure(string reason)
        {
            switch (reason)
            {
                case "GameIsFull": case "Full":
                    return "방이 꽉 찼어요. 빈자리가 생긴 뒤 참가하거나 다른 코드를 입력해 주세요.";
                case "GameNotFound": case "NoMatchFound":
                    return "방을 찾지 못했어요. 방 코드와 방장이 접속 중인지 확인해 주세요.";
                case "SessionGone":
                    return "이전 방은 종료됐어요. 같은 코드라도 새 방이므로 메뉴로 돌아가 새로 참가해 주세요.";
                case "InvalidIdentity": case "LobbyLeft":
                    return "이전 참가 정보로 돌아갈 수 없어요. 메뉴로 돌아가 새로 참가해 주세요.";
                case "HostEnded":
                    return "방장이 나가 방이 종료됐어요. 메뉴로 돌아가 주세요.";
                case "MemberStillConnected":
                    return "이전 연결이 정리되기를 기다리고 있어요. 잠시 뒤 다시 연결해 주세요.";
                case "ClientUpgradeRequired": case "IncompatibleConfiguration":
                    return "게임 버전이 맞지 않아요. 모두 같은 실행파일로 접속해 주세요.";
                case "AdmissionTimeout":
                    return "입장 확인이 오래 걸려 연결을 멈췄어요. 인터넷과 방장 접속 상태를 확인하고 다시 연결해 주세요.";
                case "SendFailed":
                    return "입력을 전송하지 못했어요. 다시 연결한 뒤 현재 차례와 칩을 확인해 주세요.";
                case "InvalidServerEnvelope": case "InvalidServerReply": case "InvalidAdmissionReply": case "InvalidServerState":
                    return "잘못된 게임 정보를 받아 연결을 멈췄어요. 같은 실행파일인지 확인하고 다시 연결해 주세요.";
                case "ResponseOverflow": case "LocalInputOverflow":
                    return "처리할 입력이 너무 많이 쌓여 연결을 멈췄어요. 오류 정보를 복사한 뒤 메뉴로 돌아가 주세요.";
                case "MissingAppId":
                    return "이 실행파일의 온라인 설정이 빠져 있어요. 설정된 실행파일로 다시 실행해 주세요.";
                case "ShutdownFailed":
                    return "이전 연결을 정리하지 못했어요. 오류 정보를 복사한 뒤 앱을 다시 실행해 주세요.";
                case "AdmissionLimit":
                    return "이 방의 참가 기록 한도에 도달했어요. 방장에게 새 방을 만들어 달라고 요청해 주세요.";
                default:
                    return "연결하지 못했어요. 인터넷 연결과 방장 접속 상태를 확인한 뒤 다시 시도해 주세요.";
            }
        }
    }
}
