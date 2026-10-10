using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// Two aggregates of one kind over two columns fold in one pass (<c>AggregateSlot.StepRowsPaired</c>), and
/// apart when a batch has nulls on either side, a column comes as a dictionary, or one is filtered: sums
/// and means of integers against LINQ over the same rows, at every degree.
/// </summary>
public sealed partial class PairedSlotsTests
{
    private const int Rows = 60_000;

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task TwoAggregatesOfOneKindFoldAsTheRowsDo(int degree)
    {
        Reading[] rows = Readings();
        string path = await WriteAsync(rows);
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            List<Totals> totals = [];
            Scan<Totals> grouped = file.Scan<Reading>().GroupBy(r => r.Key)
                .Select(g => (
                    g.Key,
                    g.Count(),
                    g.Sum(r => r.A),
                    g.Sum(r => r.B),
                    g.Where(r => r.A > 500).Sum(r => r.B),
                    g.Average(r => r.Maybe),
                    g.Average(r => r.A),
                    g.Average(r => r.Coded),
                    g.Average(r => r.B)))
                .As<Totals>();
            await foreach (Totals total in grouped.ToRecordsAsync(Ct))
            {
                totals.Add(total);
            }

            Assert.Equal(
                rows.GroupBy(r => r.Key).Select(g => new Totals(
                    g.Key,
                    g.Count(),
                    g.Sum(r => (long)r.A),
                    g.Sum(r => (long)r.B),
                    g.Where(r => r.A > 500).Sum(r => (long)r.B),
                    g.Average(r => r.Maybe),
                    g.Average(r => r.A),
                    g.Average(r => r.Coded),
                    g.Average(r => r.B))).OrderBy(t => t.Key),
                totals.OrderBy(t => t.Key));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A key of 128 values; nulls in the second half of the rows alone; a column of five values, which the writer codes.</summary>
    private static Reading[] Readings()
    {
        Reading[] rows = new Reading[Rows];
        for (int row = 0; row < Rows; row++)
        {
            int key = (int)((uint)(row * 2_654_435_761u) >> 25);
            rows[row] = new Reading(key, row * 7 % 1_000, (row * 13 % 1_000) - 500, row >= Rows / 2 && row % 11 == 0 ? null : row * 3 % 700, row % 5 * 10);
        }

        return rows;
    }

    private static async Task<string> WriteAsync(Reading[] rows)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "paired-slots");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"readings-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Reading>(path, new VortexWriteOptions { RowBlockSize = 8_192, ChunkTargetBytes = 1 << 16 }))
        {
            await writer.WriteAsync<Reading>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        return path;
    }

    [VortexRecord]
    public partial record struct Reading(int Key, int A, int B, int? Maybe, int Coded);

    [VortexRecord]
    public partial record struct Totals(int Key, long Count, long SumA, long SumB, long BigB, double? MeanMaybe, double? MeanA, double? MeanCoded, double? MeanB);
}
