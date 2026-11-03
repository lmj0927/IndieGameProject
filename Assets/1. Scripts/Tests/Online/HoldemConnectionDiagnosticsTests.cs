using NUnit.Framework;

namespace Poker.Online.Tests
{
    public sealed class HoldemConnectionDiagnosticsTests
    {
        [TestCase("GameNotFound")]
        [TestCase("HostEnded")]
        [TestCase("MissingAppId")]
        [TestCase("AdmissionTimeout")]
        [TestCase("InvalidServerState")]
        [TestCase("ResponseOverflow")]
        [TestCase("MemberStillConnected")]
        public void KnownCodesRemainUseful(string code)
        { Assert.That(HoldemConnectionDiagnostics.SafeReason(code), Is.EqualTo(code)); }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("0")]
        [TestCase("HostEnded\nroom=SECRET")]
        [TestCase("/Users/private/name/token")]
        [TestCase("K♥ nickname=PRIVATE speech=SECRET")]
        public void RawErrorsNeverBecomeShareableText(string value)
        {
            string text = HoldemConnectionDiagnostics.Create(false, false, HoldemConnectionStage.Table, value, value);
            Assert.That(text, Does.Contain("path=Unknown\nreason=Unknown"));
            Assert.That(text.Split('\n'), Has.Length.EqualTo(7));
            Assert.That(text.Length, Is.LessThan(256));
        }

        [Test]
        public void ReportContainsOnlyBoundedContext()
        {
            string text = HoldemConnectionDiagnostics.Create(true, true, HoldemConnectionStage.Rejoining, "Relayed", "HostEnded");
            Assert.That(text, Does.Contain("role=host\nmode=dealer-test\nstage=rejoining\npath=Relayed\nreason=HostEnded"));
            Assert.That(HoldemConnectionDiagnostics.Create(false, false, (HoldemConnectionStage)999, "Direct", new string('x', 100000)),
                Does.Contain("stage=unknown\npath=Direct\nreason=Unknown"));
        }
    }
}
