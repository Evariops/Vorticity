using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// An integer key the statistics bound to a span past 2^16, within four values a row of the source,
/// is numbered by its value less the least, in pages of a table of groups allocated as values meet
/// them (PLAN-HIGH-CARDINALITY.md, H11): it groups as a hashed key would, with nulls, on one lane and
/// four, under a filter; a span past the budget is hashed.
/// </summary>
public sealed partial class DirectPagesTests
{
    private const int Rows = 150_000;

    public static TheoryData<int, bool> Cases => new TheoryData<int, bool>
    {
        { 1, false },
        { 1, true },
        { 4, false },
        { 4, true },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task AKeyOfAWideSpanGroupsByValue(int degree, bool filtered)
    {
        Row[] rows = MakeRows(span: 300_000);
        string path = await WriteAsync(rows);
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            IEnumerable<Row> kept = filtered ? rows.Where(r => r.Value > 20L) : rows;
            Scan<Row> scan = filtered ? file.Scan<Row>().Where(r => r.Value > 20L) : file.Scan<Row>();
            (List<Total> totals, bool byValue) = await RunAsync(scan);
            Assert.True(byValue, "the key was hashed, not numbered by value");
            Assert.Equal(
                [.. kept.GroupBy(r => r.Key).Select(g => new Total(g.Key, g.Count(), g.Sum(r => r.Value))).OrderBy(t => t.Key ?? int.MinValue)],
                totals.OrderBy(t => t.Key ?? int.MinValue));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    // Keys in the order of the rows, each met first in a run of new values, then three times more: a
    // chunk of new values sends the next through the lookup alone, and the repeats back to two passes
    // (PLAN-HIGH-CARDINALITY, H14).
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task KeysMetInTheOrderOfTheRowsGroupByValue(int degree)
    {
        Row[] rows = new Row[Rows];
        for (int row = 0; row < Rows; row++)
        {
            rows[row] = new Row(row % 23 == 0 ? null : (row % 37_500) - 1_000, row % 100);
        }

        string path = await WriteAsync(rows);
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            (List<Total> totals, bool byValue) = await RunAsync(file.Scan<Row>());
            Assert.True(byValue, "the key was hashed, not numbered by value");
            Assert.Equal(
                [.. rows.GroupBy(r => r.Key).Select(g => new Total(g.Key, g.Count(), g.Sum(r => r.Value))).OrderBy(t => t.Key ?? int.MinValue)],
                totals.OrderBy(t => t.Key ?? int.MinValue));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    // A span past four values a row is hashed: a table of groups that wide would hold more than the
    // rows could fill.
    [Fact]
    public async Task AKeyPastTheBudgetIsHashed()
    {
        Row[] rows = MakeRows(span: 4 * Rows + 1_000_000);
        string path = await WriteAsync(rows);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            (List<Total> totals, bool byValue) = await RunAsync(file.Scan<Row>());
            Assert.False(byValue);
            Assert.Equal(rows.Select(r => r.Key).Distinct().Count(), totals.Count);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>The groups, and whether every lane numbered its key by value.</summary>
    private static async Task<(List<Total> Totals, bool ByValue)> RunAsync(Scan<Row> scan)
    {
        Vorticity.Aggregation grouped = scan.GroupBy(r => r.Key).Select(g => (g.Key, g.Count(), g.Sum(r => r.Value)));
        GroupKeys?[] lanes = [];
        grouped.Plan.Watch = partitions => lanes = [.. partitions.Select(partition => partition.Keys)];
        List<Total> totals = [];
        await foreach (Total total in grouped.As<Total>().ToRecordsAsync(Ct))
        {
            totals.Add(total);
        }

        return (totals, lanes.Length > 0 && lanes.All(keys => keys is FixedKeys<int> { ByValue: true }));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Some fifty thousand keys spread in no order over <paramref name="span"/> values from a negative least, a null every seventeenth row.</summary>
    private static Row[] MakeRows(int span)
    {
        Row[] rows = new Row[Rows];
        for (int row = 0; row < Rows; row++)
        {
            ulong mix = (ulong)row * 0x9E37_79B9_7F4A_7C15UL;
            int key = (int)((((mix >> 20) % 50_000) * 0x2545_F491UL) % (ulong)span) - 1_000;
            rows[row] = new Row(row % 17 == 0 ? null : key, (long)((mix >> 8) % 100));
        }

        // Both ends of the span met, so that the statistics bound the key to all of it.
        rows[0] = rows[0] with { Key = -1_000 };
        rows[1] = rows[1] with { Key = span - 1_001 };
        return rows;
    }

    private static async Task<string> WriteAsync(Row[] rows)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "direct-pages");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"rows-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Row>(path, new VortexWriteOptions { RowBlockSize = 8_192, ChunkTargetBytes = 1 << 16 }))
        {
            await writer.WriteAsync<Row>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        return path;
    }

    [VortexRecord]
    public partial record struct Row(int? Key, long Value);

    [VortexRecord]
    public partial record struct Total(int? Key, long Count, long Sum);
}
