using System;
using Vorticity.Scanning;
using Xunit;

namespace Vorticity.Tests.Scan;

/// <summary>The rows of a batch cleared from its bits a word at a time, as a bit at a time clears them.</summary>
public sealed class RowBitsTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    public void RowsAreClearedAsABitAtATimeClearsThem(int seed)
    {
        Random random = new Random(seed);
        for (int trial = 0; trial < 5_000; trial++)
        {
            int count = random.Next(1, 400);
            long first = random.NextInt64(0, 1_000_000);
            ulong[] words = new ulong[(count + 63) >> 6];
            for (int w = 0; w < words.Length; w++)
            {
                words[w] = (ulong)random.NextInt64() ^ ((ulong)random.NextInt64() << 32);
            }

            ulong[] expected = (ulong[])words.Clone();
            long from = first + random.Next(0, count + 1);
            long to = random.Next(4) == 0 ? from : from + random.Next(0, (int)(first + count - from) + 1);
            for (long row = from; row < to; row++)
            {
                int at = (int)(row - first);
                expected[at >> 6] &= ~(1UL << (at & 63));
            }

            ScanBuilder.ClearRows(words, first, from, to);
            Assert.Equal(expected, words);
        }
    }
}
