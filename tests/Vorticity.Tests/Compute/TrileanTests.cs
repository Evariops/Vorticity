using System;

using Vorticity.Compute;
using Xunit;

namespace Vorticity.Tests.Compute;

public sealed class TrileanTests
{
    /// <summary>
    /// A state per row as a bitmap, asked either way, at every length around a word and a vector:
    /// the bits are the rows', the last word's bits past the rows are clear, the words past the
    /// rows' are untouched, and the count is the bits set.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(200)]
    [InlineData(1_000)]
    public void ToWordsSetsTheBitOfEveryRowAskedFor(int rows)
    {
        Random random = new Random(rows);
        byte[] states = new byte[rows];
        for (int i = 0; i < rows; i++)
        {
            states[i] = (byte)random.Next(3);
        }

        const ulong Untouched = 0xDEAD_BEEF_DEAD_BEEF;
        int written = (rows + 63) >> 6;
        foreach (byte state in new[] { Trilean.False, Trilean.True, Trilean.Unknown })
        {
            foreach (bool equal in new[] { true, false })
            {
                ulong[] words = new ulong[written + 2];
                words.AsSpan().Fill(Untouched);

                int count = Trilean.ToWords(states, state, equal, words);

                int expected = 0;
                for (int i = 0; i < rows; i++)
                {
                    bool wanted = (states[i] == state) == equal;
                    expected += wanted ? 1 : 0;
                    Assert.Equal(wanted, ((words[i >> 6] >> (i & 63)) & 1) != 0);
                }

                if ((rows & 63) != 0)
                {
                    Assert.Equal(0UL, words[written - 1] >> (rows & 63));
                }

                Assert.Equal(expected, count);
                Assert.Equal(Untouched, words[written]);
                Assert.Equal(Untouched, words[written + 1]);
            }
        }
    }

    [Fact]
    public void ToWordsRefusesTooFewWords()
    {
        byte[] states = new byte[65];
        Assert.Throws<ArgumentException>(() => Trilean.ToWords(states, Trilean.True, equal: true, new ulong[1]));
    }
}
