using NUnit.Framework;

namespace Poker.Online.Tests
{
    public sealed class HoldemReliableInboxTests
    {
        [Test] public void ReordersCompletedStreamsWithoutApplyingLaterCommandEarly()
        {
            var inbox = new HoldemReliableInbox<string>();
            Assert.That(inbox.Accept(2, "state", 200), Is.True);
            Assert.That(inbox.TryRead(out _), Is.False);
            Assert.That(inbox.Accept(1, "admission", 100), Is.True);
            Assert.That(inbox.TryRead(out var first), Is.True); Assert.That(first, Is.EqualTo("admission"));
            Assert.That(inbox.TryRead(out var second), Is.True); Assert.That(second, Is.EqualTo("state"));
            Assert.That(inbox.PendingCount, Is.Zero);
        }
        [Test] public void RejectsRepeatedSequencesAndUnboundedGaps()
        {
            var inbox = new HoldemReliableInbox<string>();
            Assert.That(inbox.Accept(0, "", 1), Is.False);
            Assert.That(inbox.Accept(33, "", 1), Is.False);
            Assert.That(inbox.Accept(2, "", 1), Is.True);
            Assert.That(inbox.Accept(2, "", 1), Is.False);
            Assert.That(inbox.Accept(1, "", 1), Is.True);
            Assert.That(inbox.TryRead(out _), Is.True);
            Assert.That(inbox.Accept(1, "", 1), Is.False);
        }
        [Test] public void BoundsIndividualAndAggregateBytes()
        {
            var inbox = new HoldemReliableInbox<string>();
            Assert.That(inbox.Accept(1, "", 65537), Is.False);
            for (int i = 2; i <= 5; i++) Assert.That(inbox.Accept(i, "", 65536), Is.True);
            Assert.That(inbox.Accept(1, "", 1), Is.False);
        }
    }
}
