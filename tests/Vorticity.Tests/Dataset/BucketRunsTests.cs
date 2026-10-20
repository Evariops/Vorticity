// The runs of equal buckets of a chunk of a synopsis table, found a vector at a time.
//
// WHAT IS HELD: the vector path finds the ends of the runs its scalar twin finds, one past each run's last row and the
// last the values' length, over lengths on and around a vector's, runs of one row to thousands, every value apart and
// one value throughout; an empty span has no run, and a span of ends too short for the values is refused.
using System;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class BucketRunsTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(31)]
    [InlineData(64)]
    [InlineData(1_000)]
    [InlineData(65_537)]
    public void TheVectorPathFindsTheRunsItsTwinFinds(int length)
    {
        Random random = new Random(length);
        foreach (int longest in new[] { 1, 2, 3, 7, 64, 5_000 })
        {
            long[] values = new long[length];
            long value = random.NextInt64(-1_000, 1_000);
            for (int row = 0; row < length;)
            {
                int run = Math.Min(length - row, random.Next(1, longest + 1));
                values.AsSpan(row, run).Fill(value);
                row += run;
                value += random.Next(1, 3);
            }

            int[] vector = new int[length];
            int[] scalar = new int[length];
            int runs = BucketRuns.Ends(values, vector);
            Assert.Equal(BucketRuns.EndsScalar(values, scalar), runs);
            Assert.Equal(scalar.AsSpan(0, runs).ToArray(), vector.AsSpan(0, runs).ToArray());
            Assert.Equal(length, vector[runs - 1]);
        }
    }

    [Fact]
    public void ValuesAllApartOrAllTheSameMakeARunEachOrOne()
    {
        long[] apart = new long[100];
        for (int i = 0; i < apart.Length; i++)
        {
            apart[i] = i * 3;
        }

        int[] ends = new int[100];
        Assert.Equal(100, BucketRuns.Ends(apart, ends));
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(i + 1, ends[i]);
        }

        long[] same = new long[100];
        same.AsSpan().Fill(7);
        Assert.Equal(1, BucketRuns.Ends(same, ends));
        Assert.Equal(100, ends[0]);
    }

    [Fact]
    public void NoValueNoRunAndTooFewEndsAreRefused()
    {
        Assert.Equal(0, BucketRuns.Ends(ReadOnlySpan<long>.Empty, []));
        Assert.Throws<ArgumentException>(() => BucketRuns.Ends(new long[10], new int[9]));
    }
}
