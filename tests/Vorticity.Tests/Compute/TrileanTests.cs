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

    /// <summary>
    /// The connectives against Kleene's tables, written here as a truth table rather than taken
    /// from the kernels: every pair of states at every position of a vector, at every length
    /// around a 16-byte lane and a 64-byte vector, so each width and the scalar tail answer.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(80)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(1_000)]
    public void TheConnectivesFollowKleenesTables(int rows)
    {
        byte[] states = [Trilean.False, Trilean.True, Trilean.Unknown];
        byte[] left = new byte[rows];
        byte[] right = new byte[rows + 5];
        for (int i = 0; i < rows; i++)
        {
            // The nine pairs, shifted every 64 rows so each meets every lane position.
            int pair = (i + (i / 64)) % 9;
            left[i] = states[pair / 3];
            right[i] = states[pair % 3];
        }

        byte[] and = (byte[])left.Clone();
        byte[] or = (byte[])left.Clone();
        byte[] not = (byte[])left.Clone();
        Trilean.And(and, right);
        Trilean.Or(or, right);
        Trilean.Not(not);

        for (int i = 0; i < rows; i++)
        {
            Assert.Equal(Kleene(left[i], right[i], conjunction: true), and[i]);
            Assert.Equal(Kleene(left[i], right[i], conjunction: false), or[i]);
            Assert.Equal(left[i] == Trilean.Unknown ? Trilean.Unknown : left[i] == Trilean.True ? Trilean.False : Trilean.True, not[i]);
        }

        Assert.Throws<ArgumentException>(() => Trilean.And(new byte[rows + 1], right.AsSpan(0, rows)));
    }

    /// <summary>
    /// Kleene's AND and OR over (false, unknown, true) ordered as 0 &lt; 1 &lt; 2: the minimum and the
    /// maximum, which is the table the kernels must reproduce in their own encoding.
    /// </summary>
    private static byte Kleene(byte left, byte right, bool conjunction)
    {
        static int Rank(byte state) => state == Trilean.False ? 0 : state == Trilean.Unknown ? 1 : 2;
        byte[] byRank = [Trilean.False, Trilean.Unknown, Trilean.True];
        int a = Rank(left);
        int b = Rank(right);
        return byRank[conjunction ? Math.Min(a, b) : Math.Max(a, b)];
    }
}
