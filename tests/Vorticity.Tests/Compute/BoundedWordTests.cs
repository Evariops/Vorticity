using System;
using Vorticity.Compute;
using Xunit;

namespace Vorticity.Tests.Compute;

/// <summary>
/// The bits of 64 integers that reach a bound, over the widest vectors there are, as a loop over each value answers them,
/// for bounds at the type's ends and inside; and a float, whose NaN the vectors and a comparison answer two ways, refused.
/// </summary>
public sealed class BoundedWordTests
{
    [Fact]
    public void SixtyFourIntegersReachABoundAsALoopSays()
    {
        Random random = new Random(64);
        long[] longs = new long[64];
        byte[] bytes = new byte[64];
        for (int i = 0; i < 64; i++)
        {
            longs[i] = i % 7 == 0 ? (i % 2 == 0 ? long.MinValue : long.MaxValue) : random.NextInt64(-1_000, 1_000);
            bytes[i] = (byte)random.Next(256);
        }

        foreach (long bound in (long[])[long.MinValue, -1, 0, 17, long.MaxValue])
        {
            foreach (bool atMost in (bool[])[true, false])
            {
                Assert.Equal(Loop(longs, bound, atMost), ComparisonKernels.Bounded<long>(longs, bound, atMost));
            }
        }

        foreach (byte bound in (byte[])[0, 1, 128, 255])
        {
            foreach (bool atMost in (bool[])[true, false])
            {
                Assert.Equal(Loop(bytes, bound, atMost), ComparisonKernels.Bounded<byte>(bytes, bound, atMost));
            }
        }
    }

    [Fact]
    public void AFloatIsRefused()
    {
        double[] values = new double[64];
        values[3] = double.NaN;
        Assert.Throws<NotSupportedException>(() => ComparisonKernels.Bounded<double>(values, 0.0, atMost: true));
        Assert.Throws<NotSupportedException>(() => ComparisonKernels.Bounded<float>(new float[64], 0f, atMost: false));
    }

    private static ulong Loop<T>(T[] values, T bound, bool atMost)
        where T : IComparable<T>
    {
        ulong held = 0;
        for (int i = 0; i < 64; i++)
        {
            int order = values[i].CompareTo(bound);
            held |= ((atMost ? order <= 0 : order >= 0) ? 1UL : 0UL) << i;
        }

        return held;
    }
}
