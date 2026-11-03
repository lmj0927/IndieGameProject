using NUnit.Framework;

namespace Poker.Online.Tests
{
    public sealed class HoldemOnlineTextTests
    {
        [TestCase("GameIsFull", "꽉 찼어요")]
        [TestCase("Full", "꽉 찼어요")]
        [TestCase("GameNotFound", "방을 찾지")]
        [TestCase("SessionGone", "새로 참가")]
        [TestCase("HostEnded", "방장이 나가")]
        [TestCase("InvalidIdentity", "새로 참가")]
        [TestCase("IncompatibleConfiguration", "버전")]
        [TestCase("AdmissionTimeout", "입장 확인")]
        [TestCase("SendFailed", "전송")]
        [TestCase("InvalidServerEnvelope", "잘못된")]
        [TestCase("MissingAppId", "온라인 설정")]
        [TestCase("AdmissionLimit", "새 방")]
        public void FailureExplainsTheNextAction(string error, string expected)
        { Assert.That(HoldemOnlineText.ConnectionFailure(error), Does.Contain(expected)); }

        [TestCase(null)]
        [TestCase("opaque internal details")]
        public void UnknownErrorsDoNotExposeInternalStrings(string error)
        {
            string text = HoldemOnlineText.ConnectionFailure(error);
            Assert.That(text, Does.Contain("다시 시도"));
            if (error != null) Assert.That(text, Does.Not.Contain(error));
        }

        [TestCase("SessionGone", true)]
        [TestCase("HostEnded", true)]
        [TestCase("LobbyLeft", true)]
        [TestCase("InvalidIdentity", true)]
        [TestCase("ClientUpgradeRequired", true)]
        [TestCase("IncompatibleConfiguration", true)]
        [TestCase("GameNotFound", false)]
        [TestCase("NoMatchFound", false)]
        [TestCase("ServerLogic", false)]
        [TestCase("SendFailed", false)]
        [TestCase("MemberStillConnected", false)]
        [TestCase(null, false)]
        public void RetryDoesNotReuseTerminatedMembership(string error, bool fresh)
        { Assert.That(HoldemOnlineText.RequiresFreshParticipation(error), Is.EqualTo(fresh)); }
    }
}
