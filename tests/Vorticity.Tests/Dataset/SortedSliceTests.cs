// Sorted slices: the objects a dataset of periods writes hold each slice's rows sorted by the object's
// synopsis columns, then by the instant, as their slice table says; and the dataset answers as before,
// in key order and by key.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class SortedSliceTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Two hours of spans, three regions and five statuses: the sealed hour's object is sorted by region,
    /// then status, then instant inside each of its slices, the host, near unique, left in that order;
    /// and a key-ordered read, a range and a key cursor give the rows appended.
    /// </summary>
    [Fact]
    public async Task ASealedPeriodIsSortedSliceBySlice()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Span.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Span.At)],
            // Slices of a minute, 48 rows each.
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour, SliceTargetRows = 500 },
        }, Ct);
        Random random = new Random(9);
        List<Span> appended = [];
        for (int part = 0; part < 16; part++)
        {
            Span[] rows = [.. Enumerable.Range(0, 400).Select(i => new Span(
                Noon.AddSeconds((part * 400 + i) * 1.25),
                200 + (random.Next(5) * 100),
                $"region-{random.Next(3)}",
                $"host-{random.Next(100_000)}"))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Span>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
            appended.AddRange(rows);
        }

        CompactionOptions compaction = new CompactionOptions { LevelZeroCeiling = 2 };
        while (await dataset.CompactAsync(compaction, Ct) is not null)
        {
        }

        TreeEntry period = await FirstAsync(dataset, PeriodLevels.Periods);
        ObjectEntry entry = ObjectEntry.FromBytes(period.Value.Span);
        await using (ObjectLease lease = await dataset.RentAsync(entry, Ct))
        {
            SliceTable table = (await SliceTable.ReadAsync(lease.File, Ct))!;
            Assert.Equal([lease.File.DType.IndexOfField(nameof(Span.Region)), lease.File.DType.IndexOfField(nameof(Span.Status))], table.Order);

            List<Span> stored = [];
            await foreach (Span row in lease.File.Scan<Span>().ToRecordsAsync(Ct))
            {
                stored.Add(row);
            }

            Assert.True(table.Slices.Count > 1, "the hour is cut in several slices");
            foreach (TimeSlice slice in table.Slices)
            {
                List<Span> rows = stored.GetRange((int)slice.FirstRow, (int)slice.Rows);
                Assert.Equal(rows.OrderBy(r => r.Region, StringComparer.Ordinal).ThenBy(r => r.Status).ThenBy(r => r.At), rows);
                Assert.All(rows, r => Assert.InRange(r.At.ToUnixTimeMilliseconds() * 1000, slice.Start, slice.End - 1));
            }
        }

        // The dataset answers as before: in key order, by range, and through its key cursor.
        Assert.Equal(appended.Select(r => r.At).Order(), await dataset.Scan<Span>().OrderBy(r => r.At).Select(r => r.At).ToListAsync(Ct));
        DateTimeOffset from = Noon.AddMinutes(20);
        DateTimeOffset to = Noon.AddMinutes(70);
        Assert.Equal(
            appended.Count(r => r.At >= from && r.At < to),
            (await dataset.Scan<Span>().Where(r => r.At >= from & r.At < to).Select(r => r.At).ToListAsync(Ct)).Count);
        List<long> walked = [];
        await using (DatasetKeyCursor cursor = await DatasetKeyCursor.OpenAsync(dataset, Ct))
        {
            for (bool on = await cursor.SeekFirstAsync(Ct); on; on = await cursor.NextAsync(Ct))
            {
                walked.Add(cursor.Key.SignedValue);
            }
        }

        Assert.Equal(appended.Select(r => r.At.ToUnixTimeMilliseconds() * 1000).Order(), walked);
    }

    private static async Task<TreeEntry> FirstAsync(VortexDataset dataset, int level)
    {
        await foreach (TreeEntry entry in dataset.Levels[level].EnumerateAsync(dataset.Pages, Ct))
        {
            return entry;
        }

        throw new InvalidOperationException($"Level {level} holds no object.");
    }

    [VortexRecord]
    public partial record struct Span(DateTimeOffset At, int Status, string Region, string Host);
}
