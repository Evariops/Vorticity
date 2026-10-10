using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// The row groups a scan's filter cannot select, proven by the footer's statistics, neither read
/// nor decoded; those whose count the statistics prove, counted without a read; and the same rows
/// whether the scan prunes or not.
/// </summary>
public sealed partial class PruningTests : IDisposable
{
    private const int Rows = 100_000;
    private const int GroupRows = 8_192;

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vorticity-pruning-{Guid.NewGuid():N}.parquet");

    [VortexRecord]
    public partial record struct Event(long Id, int Bucket, double? Value, string Name);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => System.IO.File.Delete(_path);

    [Fact]
    public async Task ReadsNoRowGroupItsStatisticsRuleOut()
    {
        await using ParquetFile file = await OpenAsync();
        Scan<Event> scan = file.Scan<Event>().Where(e => e.Id >= 90_000);
        List<Event> rows = await ReadAsync(scan);
        Assert.Equal(Rows - 90_000, rows.Count);
        Assert.All(rows, e => Assert.True(e.Id >= 90_000));

        // The ids climb: every row group below 90 000 is ruled out, ten of them.
        ScanPlan plan = await file.Scan<Event>().Where(e => e.Id >= 90_000).ExplainAsync(Ct);
        int blocks = (Rows + GroupRows - 1) / GroupRows;
        Assert.Equal(blocks, plan.Blocks);
        Assert.Equal(10, plan.Pruning.Sum(step => step.BlocksPruned));
        Assert.Equal(plan.Blocks, plan.LiveBlocks + plan.Pruning.Sum(step => step.BlocksPruned));
        Assert.Equal(plan.Blocks - plan.LiveBlocks, scan.Metrics.BlocksPruned);
        Assert.Equal(plan.LiveBlocks, scan.Metrics.BlocksDecoded);
        Assert.Equal(plan.Segments, scan.Metrics.Requests);
    }

    [Fact]
    public async Task CountsFromTheStatisticsTheRowGroupsTheyDecide()
    {
        await using ParquetFile file = await OpenAsync();

        // From 16 384 on, every row group is whole: pruned below, proven above, nothing read.
        Scan<Event> scan = file.Scan<Event>().Where(e => e.Id >= 16_384);
        Assert.Equal(Rows - 16_384, await scan.CountAsync(Ct));
        Assert.Equal(0, scan.Metrics.Requests);
        ScanPlan plan = await file.Scan<Event>().Where(e => e.Id >= 16_384).ExplainAsync(Ct);
        Assert.True(plan.Count.Exact);
        Assert.Equal(Rows - 16_384, plan.Count.Rows);
        Assert.Equal(0, plan.Count.Decoded);

        // A bound inside a row group: that one is read, the others are not.
        Scan<Event> partial = file.Scan<Event>().Where(e => e.Id >= 20_000);
        Assert.Equal(Rows - 20_000, await partial.CountAsync(Ct));
        Assert.Equal(1, partial.Metrics.BlocksDecoded);
        Assert.True(await file.Scan<Event>().Where(e => e.Id >= 20_000).AnyAsync(Ct));
        Assert.False(await file.Scan<Event>().Where(e => e.Id > Rows).AnyAsync(Ct));
    }

    [Fact]
    public async Task TakesTheRowsAskedForPastRowGroupsItPruned()
    {
        await using ParquetFile file = await OpenAsync();
        List<Event> rows = await ReadAsync(file.Scan<Event>().Rows(5, 16_390, 60_000, 99_999).Where(e => e.Id >= 50_000));
        Assert.Equal([60_000L, 99_999L], rows.Select(e => e.Id));
    }

    [Fact]
    public async Task SelectsTheSameRowsWhetherItPrunesOrNot()
    {
        await using ParquetFile file = await OpenAsync();
        Func<Probe<Event>, Predicate>[] filters =
        [
            e => e.Id < 1_000,
            e => e.Id >= 40_000 & e.Id < 41_000,
            e => e.Bucket == 3,
            e => e.Id > 99_999,
            e => e.Id.In(5, 50_000, 99_998),
            e => !(e.Id < 95_000),
            e => e.Value.IsNull & e.Id > 98_000,
            e => e.Id < 10 | e.Id > 99_990,
        ];
        foreach (Func<Probe<Event>, Predicate> filter in filters)
        {
            List<Event> pruned = await ReadAsync(file.Scan<Event>().Where(filter));
            List<Event> whole = await ReadAsync(file.Scan<Event>().Where(filter).With(new ScanOptions { UseStatistics = false }));
            Assert.Equal(whole, pruned);
            Assert.Equal(whole.Count, await file.Scan<Event>().Where(filter).CountAsync(Ct));
        }
    }

    [VortexRecord]
    public partial record struct Keyed(string Key, double Score);

    [Fact]
    public async Task PrunesOnTextAndFloatsAsOnIntegers()
    {
        Keyed[] rows = new Keyed[Rows];
        for (int i = 0; i < Rows; i++)
        {
            rows[i] = new Keyed($"key-{i:D6}", i == 50_000 ? double.NaN : i * 0.5);
        }

        await using (ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter<Keyed>(
            _path, new ParquetWriteOptions { RowGroupRows = GroupRows, BlockRows = GroupRows }))
        {
            await writer.WriteAsync<Keyed>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        await using ParquetFile file = await ParquetFile.OpenAsync(_path, Ct);
        Scan<Keyed> text = file.Scan<Keyed>().Where(k => k.Key >= "key-090000");
        Assert.Equal(Rows - 90_000, await text.CountAsync(Ct));
        Assert.Equal(10, (await file.Scan<Keyed>().Where(k => k.Key >= "key-090000").ExplainAsync(Ct)).Pruning.Sum(step => step.BlocksPruned));

        // The scores climb but for one NaN, which neither bound takes in and a comparison never selects.
        Scan<Keyed> low = file.Scan<Keyed>().Where(k => k.Score < 100.0);
        Assert.Equal(200, await low.CountAsync(Ct));
        Assert.Equal(1, low.Metrics.BlocksDecoded);
        Assert.Equal(49_999, await file.Scan<Keyed>().Where(k => k.Score >= 25_000.0).CountAsync(Ct));
        Assert.Equal(49_999, await file.Scan<Keyed>().Where(k => k.Score >= 25_000.0).With(new ScanOptions { UseStatistics = false }).CountAsync(Ct));
        Assert.Equal(
            await file.Scan<Keyed>().Where(k => !(k.Score < 30_000.0)).With(new ScanOptions { UseStatistics = false }).CountAsync(Ct),
            await file.Scan<Keyed>().Where(k => !(k.Score < 30_000.0)).CountAsync(Ct));
    }

    [VortexRecord]
    public partial record struct Tagged(long Id, ReadOnlyMemory<long> Tags, string? Note);

    [Theory]
    [InlineData(0)]
    [InlineData(3_000)]
    public async Task SkipsThePagesItsIndexRulesOutInsideARowGroup(int batchRows)
    {
        // One row group of a hundred thousand rows, a page every 8 192: the index bounds each page.
        Tagged[] rows = new Tagged[Rows];
        for (int i = 0; i < Rows; i++)
        {
            long[] tags = new long[i % 4];
            for (int k = 0; k < tags.Length; k++)
            {
                tags[k] = i * 10L + k;
            }

            rows[i] = new Tagged(i, tags, i % 5 == 0 ? null : $"note {i}");
        }

        await using (ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter<Tagged>(
            _path, new ParquetWriteOptions { RowGroupRows = GroupRows * 16, BlockRows = GroupRows }))
        {
            await writer.WriteAsync<Tagged>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        await using ParquetFile file = await ParquetFile.OpenAsync(_path, Ct);
        Assert.Equal(1, file.RowGroupCount);
        ScanOptions options = batchRows > 0 ? new ScanOptions { BatchRows = batchRows } : new ScanOptions();
        Func<Probe<Tagged>, Predicate>[] filters =
        [
            t => t.Id >= 50_000 & t.Id < 50_100,
            t => t.Id < 100 | t.Id > 99_900,
            t => t.Note >= "note 7",
            t => t.Note.IsNull & t.Id > 99_000,
        ];
        foreach (Func<Probe<Tagged>, Predicate> filter in filters)
        {
            Scan<Tagged> pruned = file.Scan<Tagged>().Where(filter).With(options);
            List<string> kept = await RenderAsync(pruned);
            List<string> whole = await RenderAsync(file.Scan<Tagged>().Where(filter).With(options with { UseStatistics = false }));
            Assert.Equal(whole, kept);
        }

        // Ids in one page: the index rules out every other one, and each is stepped over unread.
        Scan<Tagged> narrow = file.Scan<Tagged>().Where(t => t.Id >= 50_000 & t.Id < 50_100).With(options);
        Assert.Equal(100, (await RenderAsync(narrow)).Count);
        int blocks = (Rows + (batchRows > 0 ? batchRows : GroupRows) - 1) / (batchRows > 0 ? batchRows : GroupRows);
        // The page that holds them, of 8 192 rows, overlaps one batch of as many, four of 3 000.
        Assert.Equal(batchRows > 0 ? 4 : 1, narrow.Metrics.BlocksDecoded);
        Assert.Equal(blocks - narrow.Metrics.BlocksDecoded, narrow.Metrics.BlocksPruned);
        ScanPlan plan = await file.Scan<Tagged>().Where(t => t.Id >= 50_000 & t.Id < 50_100).With(options).ExplainAsync(Ct);
        Assert.Equal(narrow.Metrics.BlocksDecoded, plan.LiveBlocks);
        Assert.Equal(blocks - plan.LiveBlocks, plan.Pruning.Single(step => step.Structure == "page index").BlocksPruned);
        Assert.Equal(plan.Segments, narrow.Metrics.Requests);
    }

    /// <summary>Every row a scan keeps, each column written out.</summary>
    private static async Task<List<string>> RenderAsync(Scan<Tagged> scan)
    {
        List<string> rows = [];
        await foreach (Tagged row in scan.ToRecordsAsync(Ct))
        {
            rows.Add($"{row.Id} [{string.Join(", ", row.Tags.ToArray())}] {row.Note}");
        }

        return rows;
    }

    private async Task<ParquetFile> OpenAsync()
    {
        Event[] rows = new Event[Rows];
        for (int i = 0; i < Rows; i++)
        {
            rows[i] = new Event(i, i % 10, i % 7 == 0 ? null : i * 0.5, $"name {i % 100}");
        }

        await using (ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter<Event>(
            _path, new ParquetWriteOptions { RowGroupRows = GroupRows, BlockRows = GroupRows }))
        {
            await writer.WriteAsync<Event>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        return await ParquetFile.OpenAsync(_path, Ct);
    }

    private static async Task<List<Event>> ReadAsync(Scan<Event> scan)
    {
        List<Event> rows = [];
        await foreach (Event row in scan.ToRecordsAsync(Ct))
        {
            rows.Add(row);
        }

        return rows;
    }
}
