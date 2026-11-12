// A caller's aggregator over columns without nulls, read with no filter and with one: a key in runs of one to four rows,
// whose rows of one group one after the other are one step, and in runs of three hundred, folded a range at a time from
// the range's own values or codes; values canonical and in a dictionary, by a plain aggregator and an encoded one.
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Vorticity.Tests.Aggregation;

public sealed class CustomDenseStepTests
{
    private const int Rows = 120_000;

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
    public async Task EveryRowIsFoldedOnceIntoItsGroup(int degree, bool filtered)
    {
        (DenseRow[] rows, string path) = await WriteAsync();
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: TestContext.Current.CancellationToken);
            Scan<DenseRow> scan = filtered ? file.Scan<DenseRow>().Where(r => r.Coded >= 3.0) : file.Scan<DenseRow>();
            Dictionary<int, (SumCount Plain, SumCount Coded, SumCount Canonical)> folded = [];
            await foreach ((int key, SumCount plain, SumCount coded, SumCount canonical) in scan
                .GroupBy(r => r.Key)
                .Select(g => (
                    g.Key,
                    g.Aggregate<double, PlainSum, SumCount>(r => r.Plain),
                    g.Aggregate<double, EncodedSum, SumCount>(r => r.Coded),
                    g.Aggregate<double, EncodedSum, SumCount>(r => r.Plain)))
                .As<DenseFolds>()
                .ToRecordsAsync(TestContext.Current.CancellationToken))
            {
                folded.Add(key, (plain, coded, canonical));
            }

            IEnumerable<DenseRow> kept = filtered ? rows.Where(r => r.Coded >= 3.0) : rows;
            Dictionary<int, DenseRow[]> expected = kept.GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.ToArray());
            Assert.Equal(expected.Keys.Order(), folded.Keys.Order());
            foreach ((int key, DenseRow[] group) in expected)
            {
                (SumCount plain, SumCount coded, SumCount canonical) = folded[key];
                AssertFolds(group.Select(r => r.Plain), plain);
                AssertFolds(group.Select(r => r.Coded), coded);
                AssertFolds(group.Select(r => r.Plain), canonical);
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static void AssertFolds(IEnumerable<double> values, SumCount folded)
    {
        double[] present = [.. values];
        Assert.Equal(present.Length, folded.Count);
        double sum = present.Sum();
        Assert.True(Math.Abs(sum - folded.Sum) <= 1e-9 * Math.Max(1, Math.Abs(sum)), $"sum {folded.Sum}, expected {sum}");
    }

    private static async Task<(DenseRow[] Rows, string Path)> WriteAsync()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "custom-aggregators");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"dense-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        DenseRow[] rows = new DenseRow[Rows];
        Random random = new Random(1018);
        int run = 0;
        for (int row = 0; row < Rows;)
        {
            // Short runs for the first half, which go row by row, then long ones, which go a range at a time; a run's
            // key drawn, so that two runs in a row may share it.
            int length = row < Rows / 2 ? random.Next(1, 5) : 300;
            int key = random.Next(Keys);
            for (int i = 0; i < length && row < Rows; i++, row++)
            {
                rows[row] = new DenseRow(key, row * 0.5, (row % 13) * 1.5);
            }

            run++;
        }

        VortexWriteOptions options = new VortexWriteOptions
        {
            Hints = ImmutableDictionary<string, EncodingHint>.Empty
                .Add(nameof(DenseRow.Key), EncodingHint.RunEnd)
                .Add(nameof(DenseRow.Plain), EncodingHint.Canonical)
                .Add(nameof(DenseRow.Coded), EncodingHint.Dictionary),
        };
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<DenseRow>(path, options))
        {
            await writer.WriteAsync<DenseRow>(rows, TestContext.Current.CancellationToken);
            await writer.CompleteAsync(TestContext.Current.CancellationToken);
        }

        return (rows, path);
    }
}

[VortexRecord]
public partial record struct DenseRow(int Key, double Plain, double Coded);

/// <summary>A key and three folds of its rows.</summary>
[VortexRecord]
public partial record struct DenseFolds(int Key, SumCount Plain, SumCount Coded, SumCount Canonical);
