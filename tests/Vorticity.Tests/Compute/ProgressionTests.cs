// The progression kernel against the running sum it replaces, at every width it is given.
//
// The lengths straddle one vector and four of them, whatever a register holds of the type up to
// 64 bytes, and a sentinel past the span shows a store that ran over it. The steps wrap: a
// progression past the type's maximum must wrap exactly as the running sum does.
using System;
using System.Numerics;

using Vorticity.Compute;
using Xunit;

namespace Vorticity.Tests.Compute;

public sealed class ProgressionTests
{
    private static readonly int[] Lengths = [0, 1, 2, 3, 4, 5, 7, 8, 9, 15, 16, 17, 31, 32, 33, 63, 64, 65, 127, 128, 129, 255, 256, 257, 1_000];

    [Fact]
    public void FillsBytesAsTheRunningSumDoes() => Check<byte>(250, 3);

    [Fact]
    public void FillsShortsAsTheRunningSumDoes() => Check<short>(short.MaxValue - 40, -7);

    [Fact]
    public void FillsIntsAsTheRunningSumDoes() => Check<uint>(uint.MaxValue - 100, 1);

    [Fact]
    public void FillsLongsAsTheRunningSumDoes() => Check<long>(long.MaxValue - 1_000, 1_000_003);

    [Fact]
    public void FillsUnsignedLongsAsTheRunningSumDoes() => Check<ulong>(42, ulong.MaxValue);

    private static void Check<T>(T start, T step)
        where T : unmanaged, IBinaryInteger<T>
    {
        T sentinel = T.CreateTruncating(0x5A);
        foreach (int length in Lengths)
        {
            T[] values = new T[length + 1];
            values[length] = sentinel;
            Progression.Fill(values.AsSpan(0, length), start, step);

            T expected = start;
            for (int i = 0; i < length; i++)
            {
                Assert.True(values[i] == expected, $"slot {i} of {length}: {values[i]} where the running sum is {expected}");
                expected = unchecked(expected + step);
            }

            Assert.Equal(sentinel, values[length]);
        }
    }
}
