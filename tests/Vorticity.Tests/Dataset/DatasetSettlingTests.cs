// A dataset's blocks settled by their zone maps: a count by day, with its extremes, over an object with a few rows marked
// deleted decodes the blocks those rows lie in and those a day's end cuts, and folds every other block from its zones,
// answering what the rows answer, as a read that takes no statistics, which settles nothing, does.
//
// THE ORACLE IS THE ROWS: every answer is held to the days computed from the rows appended, less the rows deleted.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetSettlingTests
{
    private const int Days = 10;
    private const int PerDay = 100_000;

    private static readonly DateTimeOffset Midnight = new(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// One object of ten days, a row every 0.864 s, five rows deleted far apart, the object kept with their marks: the count
    /// by day decodes at most the five blocks the marks lie in and the nine a midnight cuts, against every block when the
    /// read takes no statistics, and both answer the rows.
    /// </summary>
    [Fact]
    public async Task ACountByDayDecodesOnlyTheBlocksADeleteOrAMidnightCuts()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Reading.Schema, new DatasetOptions { ClusteringKey = [nameof(Reading.At)] }, Ct);
        List<Reading> rows = await FillAsync(dataset);
        long[] deleted = [12_345, 234_567, 456_789, 678_901, 890_123];
        HashSet<DateTimeOffset> gone = [.. deleted.Select(i => rows[(int)i].At)];
        RowChangeResult result = await dataset.DeleteAsync<Reading>(r => r.At.In(gone.ToArray()), Ct);
        Assert.Equal(deleted.Length, result.Rows);
        Assert.True(result.ObjectsMarked > 0, "the delete rewrote the object rather than mark it");
        List<DayTotal> expected = Expected(rows.Where(r => !gone.Contains(r.At)));

        (List<DayTotal> settled, long settledDecoded) = await AskAsync(dataset, new ScanOptions());
        (List<DayTotal> read, long readDecoded) = await AskAsync(dataset, new ScanOptions { UseStatistics = false });
        Assert.Equal(expected, settled);
        Assert.Equal(expected, read);
        // Measured on 2026-10-25: 14 blocks decoded, the five marked and the nine a midnight cuts, against 123.
        Assert.InRange(settledDecoded, 1, deleted.Length + Days - 1);
        Assert.True(readDecoded > 8 * settledDecoded, $"{settledDecoded} blocks decoded settled, {readDecoded} read");
    }

    /// <summary>The same rows with no row deleted: only the blocks a midnight cuts are decoded.</summary>
    [Fact]
    public async Task AnObjectNoDeleteTouchedDecodesOnlyTheBlocksAMidnightCuts()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Reading.Schema, new DatasetOptions { ClusteringKey = [nameof(Reading.At)] }, Ct);
        List<Reading> rows = await FillAsync(dataset);
        (List<DayTotal> settled, long decoded) = await AskAsync(dataset, new ScanOptions());
        Assert.Equal(Expected(rows), settled);
        Assert.InRange(decoded, 0, Days);
    }

    /// <summary>Ten days of a row every 0.864 s, in the order of their instants, one object.</summary>
    private static async Task<List<Reading>> FillAsync(VortexDataset dataset)
    {
        Random random = new Random(0x5E77);
        List<Reading> rows = [.. Enumerable.Range(0, Days * PerDay).Select(i => new Reading(Midnight.AddTicks(i * 8_640_000L), random.Next(1_000_000)))];
        await using ObjectDraft draft = dataset.StartObject();
        await draft.Writer.WriteAsync<Reading>(rows.ToArray(), Ct);
        await dataset.AppendAsync(draft, Ct);
        return rows;
    }

    /// <summary>The count, the least and the greatest value of each day, and the blocks the read decoded.</summary>
    private static async Task<(List<DayTotal> Days, long Decoded)> AskAsync(VortexDataset dataset, ScanOptions options)
    {
        Vorticity.Aggregation days = dataset.Scan<Reading>().With(options)
            .GroupBy(r => r.At.Truncate(CalendarUnit.Day))
            .Select(g => (g.Key, g.Count(), g.Min(r => r.Value), g.Max(r => r.Value)));
        List<DayTotal> answer = await days.As<DayTotal>().ToRecordsAsync(Ct).ToListAsync(Ct);
        return ([.. answer.OrderBy(day => day.Day)], days.Metrics.BlocksDecoded);
    }

    private static List<DayTotal> Expected(IEnumerable<Reading> rows) =>
        [.. rows.GroupBy(r => new DateTimeOffset(r.At.UtcDateTime.Date, TimeSpan.Zero))
            .Select(g => new DayTotal(g.Key, g.Count(), g.Min(r => r.Value), g.Max(r => r.Value)))
            .OrderBy(day => day.Day)];

    [VortexRecord]
    public partial record struct Reading(DateTimeOffset At, long Value);

    [VortexRecord]
    public partial record struct DayTotal(DateTimeOffset Day, long Count, long Least, long Greatest);
}
