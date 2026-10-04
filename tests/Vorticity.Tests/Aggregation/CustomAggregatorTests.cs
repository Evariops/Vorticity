using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// A caller's aggregator, folded per group over every form a block can take: a key in short runs,
/// folded row by row, and in long runs, folded a range at a time through a window of the selection;
/// values canonical, in a dictionary and in runs; with and without a filter; on one lane and several.
/// </summary>
public sealed class CustomAggregatorTests
{
    private const int Rows = 200_000;

    private const int Keys = 7;

    public static TheoryData<int, bool> Cases => new TheoryData<int, bool>
    {
        { 1, false },
        { 1, true },
        { 4, false },
        { 4, true },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryFormFoldsToWhatTheRowsHold(int degree, bool filtered)
    {
        (Folded[] rows, string path) = await WriteAsync();
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: TestContext.Current.CancellationToken);
            Scan<Folded> scan = filtered ? file.Scan<Folded>().Where(r => r.Runs >= 3.0) : file.Scan<Folded>();
            Dictionary<int, (SumCount Plain, SumCount Coded, SumCount Runs, SumCount Canonical)> folded = [];
            await foreach ((int key, SumCount plain, SumCount coded, SumCount runs, SumCount canonical) in scan
                .GroupBy(r => r.Key)
                .Select(g => (
                    g.Key,
                    g.Aggregate<double, EncodedSum, SumCount>(r => r.Plain),
                    g.Aggregate<double, EncodedSum, SumCount>(r => r.Coded),
                    g.Aggregate<double, EncodedSum, SumCount>(r => r.Runs),
                    g.Aggregate<double, PlainSum, SumCount>(r => r.Coded)))
                .WithCancellation(TestContext.Current.CancellationToken))
            {
                folded.Add(key, (plain, coded, runs, canonical));
            }

            IEnumerable<Folded> kept = filtered ? rows.Where(r => r.Runs >= 3.0) : rows;
            Dictionary<int, Folded[]> expected = kept.GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.ToArray());
            Assert.Equal(expected.Keys.Order(), folded.Keys.Order());
            foreach ((int key, Folded[] group) in expected)
            {
                (SumCount plain, SumCount coded, SumCount runs, SumCount canonical) = folded[key];
                AssertFolds(group.Select(r => r.Plain), plain);
                AssertFolds(group.Select(r => r.Coded), coded);
                AssertFolds(group.Select(r => (double?)r.Runs), runs);
                AssertFolds(group.Select(r => r.Coded), canonical);
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    // A window writes the range's words only, enumerates them only, and leaves the scratch as it found it.
    [Theory]
    [InlineData(0, 64)]
    [InlineData(3, 10)]
    [InlineData(60, 70)]
    [InlineData(100, 1_000)]
    [InlineData(0, 4_096)]
    public void AWindowHoldsTheRangeAndGivesItsWordsBack(int start, int end)
    {
        const int rows = 4_096;
        ulong[] mask = new ulong[rows / 64];
        for (int row = 0; row < rows; row += 3)
        {
            mask[row >> 6] |= 1UL << (row & 63);
        }

        ulong[] scratch = [];
        Selection window = RowMasks.Window(mask, rows, start, end, ref scratch);
        List<int> seen = [];
        foreach (int row in window)
        {
            seen.Add(row);
        }

        int[] expected = [.. Enumerable.Range(start, end - start).Where(row => row % 3 == 0)];
        Assert.Equal(expected, seen);
        Assert.Equal(expected.Length, window.Count);
        RowMasks.Unclip(scratch, start, end);
        Assert.All(scratch, word => Assert.Equal(0UL, word));
    }

    private static void AssertFolds(IEnumerable<double?> values, SumCount folded)
    {
        double[] present = [.. values.Where(v => v.HasValue).Select(v => v!.Value)];
        Assert.Equal(present.Length, folded.Count);
        double sum = present.Sum();
        Assert.True(Math.Abs(sum - folded.Sum) <= 1e-9 * Math.Max(1, Math.Abs(sum)), $"sum {folded.Sum}, expected {sum}");
    }

    private static async Task<(Folded[] Rows, string Path)> WriteAsync()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "custom-aggregators");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"folded-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        Folded[] rows = new Folded[Rows];
        int run = 0;
        for (int row = 0; row < Rows;)
        {
            // Runs of three rows for the first half, of three hundred for the second: the first folds
            // row by row, the second a range at a time.
            int length = row < Rows / 2 ? 3 : 300;
            for (int i = 0; i < length && row < Rows; i++, row++)
            {
                rows[row] = new Folded(
                    run % Keys,
                    row % 11 == 0 ? null : row * 0.5,
                    row % 17 == 0 ? null : (row % 13) * 1.5,
                    (row / 5) % 9);
            }

            run++;
        }

        VortexWriteOptions options = new VortexWriteOptions
        {
            Hints = ImmutableDictionary<string, EncodingHint>.Empty
                .Add(nameof(Folded.Key), EncodingHint.RunEnd)
                .Add(nameof(Folded.Plain), EncodingHint.Canonical)
                .Add(nameof(Folded.Coded), EncodingHint.Dictionary)
                .Add(nameof(Folded.Runs), EncodingHint.RunEnd),
        };
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Folded>(path, options))
        {
            await writer.WriteAsync<Folded>(rows, TestContext.Current.CancellationToken);
            await writer.CompleteAsync(TestContext.Current.CancellationToken);
        }

        return (rows, path);
    }
}

[VortexRecord]
public partial record struct Folded(int Key, double? Plain, double? Coded, double Runs);

public struct SumCount
{
    public double Sum;
    public long Count;
}

/// <summary>A sum and a count that read every encoded form, and take only the rows they are handed.</summary>
public readonly struct EncodedSum : IEncodedAggregator<double, SumCount>
{
    public static SumCount Seed() => default;

    public static void Step(ref SumCount state, ReadOnlySpan<double> values, ReadOnlySpan<ulong> validity, Selection rows)
    {
        foreach (int i in rows)
        {
            if (validity.IsEmpty || ((validity[i >> 6] >> (i & 63)) & 1) != 0)
            {
                state.Sum += values[i];
                state.Count++;
            }
        }
    }

    public static void StepDictionary(ref SumCount state, ReadOnlySpan<uint> codes, ReadOnlySpan<double> dictionary, Selection rows)
    {
        foreach (int i in rows)
        {
            state.Sum += dictionary[(int)codes[i]];
            state.Count++;
        }
    }

    public static void StepRunEnd(ref SumCount state, ReadOnlySpan<uint> runEnds, ReadOnlySpan<double> values, Selection rows)
    {
        int run = 0;
        foreach (int i in rows)
        {
            while (runEnds[run] <= (uint)i)
            {
                run++;
            }

            state.Sum += values[run];
            state.Count++;
        }
    }

    public static void StepConstant(ref SumCount state, double value, int count)
    {
        state.Sum += value * count;
        state.Count += count;
    }

    public static void Merge(ref SumCount into, in SumCount other)
    {
        into.Sum += other.Sum;
        into.Count += other.Count;
    }
}

/// <summary>The same fold, handed only the canonical form.</summary>
public readonly struct PlainSum : IAggregator<double, SumCount>
{
    public static SumCount Seed() => default;

    public static void Step(ref SumCount state, ReadOnlySpan<double> values, ReadOnlySpan<ulong> validity, Selection rows) =>
        EncodedSum.Step(ref state, values, validity, rows);

    public static void Merge(ref SumCount into, in SumCount other) => EncodedSum.Merge(ref into, in other);
}
