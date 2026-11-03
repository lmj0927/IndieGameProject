using System;
using System.Text;
using NUnit.Framework;

namespace Poker.Online.Tests
{
    public sealed class HoldemFusionIngressTests
    {
        [TestCase("00112233445566778899aabbccddeeff")]
        [TestCase("00112233445566778899AABBCCDDEEFF")]
        public void CanonicalNoncePreservesItsOriginalGeneration(string value)
        {
            Assert.That(HoldemFusionIngress.TryReadToken(Encoding.ASCII.GetBytes(value), out var token), Is.True);
            Assert.That(token, Is.EqualTo(value));
        }

        [Test]
        public void InvalidRemoteBytesNeverEscapeAsDecoderExceptions()
        {
            foreach (var data in new[] { null, new byte[0], new byte[] { 0xc3, 0x28 },
                new byte[32], Encoding.ASCII.GetBytes(new string('0', 32)),
                Encoding.ASCII.GetBytes(new string('g', 32)), Encoding.ASCII.GetBytes(Guid.NewGuid().ToString("D")),
                Encoding.UTF8.GetBytes(new string('하', 32)), new byte[65536] })
            {
                Assert.That(HoldemFusionIngress.TryReadToken(data, out var token), Is.False);
                Assert.That(token, Is.Null);
            }
        }

        [Test]
        public void EveryCompletedMessageUsesTheSameBoundedBudget()
        {
            var ingress = new HoldemFusionIngress();
            for (int i = 0; i < 60; i++) Assert.That(ingress.AcceptMessage(5), Is.True);
            for (int i = 0; i < 100; i++) Assert.That(ingress.AcceptMessage(5.5), Is.False);
            Assert.That(ingress.AcceptMessage(6), Is.True);
            Assert.That(ingress.AcceptMessage(4), Is.False);
            Assert.That(ingress.AcceptMessage(double.NaN), Is.False);
            Assert.That(ingress.AcceptMessage(double.PositiveInfinity), Is.False);
        }
    }
}
