// The runs of equal buckets of a chunk of a synopsis table, found a vector at a time.
//
// WHAT IS HELD: the vector path finds the ends of the runs its scalar twin finds, one past each run's last row and the
// last the values' length, over lengths on and around a vector's, runs of one row to thousands, every value apart and
// one value throughout; an empty span has no run, and a span of ends too short for the values is refused. The rows a
// bucket keeps are those the scalar twin keeps, from anywhere to anywhere, with states or without.
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

    /// <summary>
    /// The vector path keeps, of rows from anywhere to anywhere, the rows its twin keeps: counts zero, negative, positive
    /// and at the ends of their type, states false, true and unknown or none, into words some of whose bits are set
    /// already, which it keeps; and it says as many.
    /// </summary>
    [Fact]
    public void TheVectorPathKeepsTheRowsItsTwinKeeps()
    {
        Random random = new Random(41);
        const int Rows = 1_000;
        long[] stood = new long[Rows];
        byte[] states = new byte[Rows];
        for (int row = 0; row < Rows; row++)
        {
            stood[row] = random.Next(8) switch
            {
                0 => 0,
                1 => -random.Next(1, 1_000),
                2 => long.MinValue,
                3 => long.MaxValue,
                _ => random.Next(1, 1_000),
            };
            states[row] = (byte)random.Next(3);
        }

        for (int trial = 0; trial < 2_000; trial++)
        {
            int start = random.Next(Rows);
            int end = Math.Min(Rows, start + random.Next(0, trial % 2 == 0 ? 300 : Rows));
            ulong[] vector = new ulong[(Rows + 63) / 64];
            for (int word = 0; word < vector.Length; word++)
            {
                vector[word] = random.Next(4) == 0 ? (ulong)random.NextInt64() : 0;
            }

            ulong[] scalar = [.. vector];
            ReadOnlySpan<byte> asked = trial % 3 == 0 ? ReadOnlySpan<byte>.Empty : states;
            int kept = BucketRuns.Keep(stood, asked, start, end, vector);
            Assert.Equal(BucketRuns.KeepScalar(stood, asked, start, end, scalar), kept);
            Assert.Equal(scalar, vector);
        }
    }
}
