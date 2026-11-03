using System;
using System.Security.Cryptography;
using Poker.Foundation;

namespace Poker.Application
{
    /// <summary>Generates bounded random values from OS entropy using rejection sampling.</summary>
    public sealed class CryptographicRandomSource : IRandomSource, IDisposable
    {
        private readonly RandomNumberGenerator generator = RandomNumberGenerator.Create();
        private readonly byte[] bytes = new byte[4];
        public int NextInt(int exclusiveUpperBound)
        {
            if (exclusiveUpperBound <= 0) throw new ArgumentOutOfRangeException(nameof(exclusiveUpperBound));
            uint bound = (uint)exclusiveUpperBound;
            uint threshold = unchecked(0u - bound) % bound;
            uint value;
            do { generator.GetBytes(bytes); value = BitConverter.ToUInt32(bytes, 0); } while (value < threshold);
            return (int)(value % bound);
        }
        public void Dispose() => generator.Dispose();
    }
}
