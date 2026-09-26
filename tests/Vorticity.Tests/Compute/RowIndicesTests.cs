// The selected rows as indices, from verdicts and from bitmaps, against a row-at-a-time oracle.
//
// With AVX-512 the kernels store sixteen slots per compress whatever they keep, so every case runs
// with the indices sized exactly to the rows and a sentinel past them: a store that ran past the
// rows would show there. The densities go from nothing selected through mixed words to every row,
// which a bitmap takes as whole words of its own.
using System;

using Vorticity.Compute;
using Xunit;

namespace Vorticity.Tests.Compute;

public sealed class RowIndicesTests
{
    private static readonly int[] Lengths = [0, 1, 15, 16, 17, 63, 64, 65, 128, 200, 1_000];

    private static readonly int[] Densities = [0, 1, 50, 99, 100];

    [Fact]
    public void TheTrueStatesAreSelectedInOrder()
    {
        Random random = new Random(1);
        foreach (int rows in Lengths)
        {
            foreach (int density in Densities)
            {
                byte[] states = new byte[rows];
                for (int i = 0; i < rows; i++)
                {
                    states[i] = random.Next(100) < density ? Trilean.True : (byte)(random.Next(2) == 0 ? Trilean.False : Trilean.Unknown);
                }

                int[] indices = new int[rows + 16];
                indices.AsSpan(rows).Fill(-7);
                int count = RowIndices.FromStates(states, indices.AsSpan(0, rows));

                int expected = 0;
                for (int i = 0; i < rows; i++)
                {
                    if (states[i] == Trilean.True)
                    {
                        Assert.Equal(i, indices[expected++]);
                    }
                }

                Assert.Equal(expected, count);
                Assert.All(indices[rows..], sentinel => Assert.Equal(-7, sentinel));
            }
        }
    }

    [Fact]
    public void TheSetAndValidBitsAreSelectedInOrder()
    {
        Random random = new Random(2);
        foreach (int rows in Lengths)
        {
            foreach (int density in Densities)
            {
                foreach (int offset in new[] { 0, 3 })
                {
                    foreach (bool allValid in new[] { true, false })
                    {
                        bool[] set = new bool[rows];
                        bool[] valid = new bool[rows];
                        for (int i = 0; i < rows; i++)
                        {
                            set[i] = random.Next(100) < density;
                            valid[i] = allValid || random.Next(100) < Math.Max(density, 50);
                        }

                        int validityOffset = offset ^ 5;
                        int[] indices = new int[rows + 16];
                        indices.AsSpan(rows).Fill(-7);
                        int count = RowIndices.FromBits(
                            Bitmap(set, offset), offset, allValid ? default : Bitmap(valid, validityOffset), validityOffset,
                            allValid, rows, indices.AsSpan(0, rows));

                        int expected = 0;
                        for (int i = 0; i < rows; i++)
                        {
                            if (set[i] && valid[i])
                            {
                                Assert.Equal(i, indices[expected++]);
                            }
                        }

                        Assert.Equal(expected, count);
                        Assert.All(indices[rows..], sentinel => Assert.Equal(-7, sentinel));
                    }
                }
            }
        }
    }

    /// <summary>A bitmap exactly as long as the rows need, its bits outside the rows set.</summary>
    private static byte[] Bitmap(bool[] set, int offset)
    {
        byte[] bits = new byte[(set.Length + offset + 7) / 8];
        bits.AsSpan().Fill(0xFF);
        for (int i = 0; i < set.Length; i++)
        {
            if (!set[i])
            {
                int at = offset + i;
                bits[at >> 3] &= (byte)~(1 << (at & 7));
            }
        }

        return bits;
    }
}
