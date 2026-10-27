// Reads at the granule, end to end (spec C; C5, C6): a dataset's object read by the granules its key's sparse index, its
// zone maps, its pages or its identifier's postings keep, the bytes of those granules alone; a needle found through the
// index of its partition, then its granules.
//
// THE ORACLE IS LINQ AND THE OBJECT'S BYTES: every read returns LINQ's rows; what it reads of the store is a part of the
// object near the part of its granules it keeps, and a filter asked again reads no more than the first time.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetGranuleReadsTests
{
    private static readonly DateTime Start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// One day of 600 000 rows sorted by the service, then the instant, two appends merged and consolidated into one object:
    /// a filter on one service reads its granules alone, under a tenth of the object; an equality on the sequence, outside
    /// the key, reads past its first pass the granule that holds its row; a range of the doubles asked twice reads no more
    /// the second time, from the pages the first left; every read returns LINQ's rows.
    /// </summary>
    [Fact]
    public async Task AReadTakesTheGranulesItKeepsAlone()
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        (VortexDataset dataset, Row[] rows) = await DayAsync(store, identifiers: null, location: IdentifierLocation.Granule);
        await using (dataset)
        {
            long objectBytes = 0;
            await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(Ct))
            {
                objectBytes += held.Entry.Bytes;
            }

            foreach (string service in (string[])["svc-07", "svc-31"])
            {
                store.Reset();
                List<Row> read = await dataset.Scan<Row>().Where(r => r.Service == service).ToRecordsAsync(Ct).ToListAsync(Ct);
                Console.WriteLine($"GRANULES {service}: {read.Count} rows, {store.BytesRead} bytes in {store.Requests} requests of an object of {objectBytes}");
                Assert.Equal(rows.Where(r => r.Service == service).OrderBy(static r => r.Sequence), read.OrderBy(static r => r.Sequence));
                Assert.True(store.BytesRead < objectBytes / 10, $"{service}: {store.BytesRead} bytes of {objectBytes}");
            }

            foreach (long[] sought in (long[][])[[123_457], [77, 78, 401_002]])
            {
                HashSet<long> asked = [.. sought];
                store.Reset();
                List<Row> read = await dataset.Scan<Row>().Where(r => r.Sequence.In(sought)).ToRecordsAsync(Ct).ToListAsync(Ct);
                long both = store.BytesRead;
                store.Reset();
                Assert.Equal(sought.Length, await dataset.Scan<Row>().Where(r => r.Sequence.In(sought) & (r.Sequence >= 0)).Select(r => r.Sequence).ToValuesAsync(Ct).CountAsync(Ct));
                long first = store.BytesRead;
                Console.WriteLine($"TWO PASSES {sought.Length}: {both} bytes, the first pass alone {first}");
                Assert.Equal(rows.Where(r => asked.Contains(r.Sequence)), read.OrderBy(static r => r.Sequence));
                Assert.True(both - first < objectBytes / 10, $"the second pass read {both - first} bytes of {objectBytes}");
            }

            long before = long.MaxValue;
            for (int time = 0; time < 2; time++)
            {
                store.Reset();
                List<Row> read = await dataset.Scan<Row>().Where(r => r.Value > 0.9999).ToRecordsAsync(Ct).ToListAsync(Ct);
                Console.WriteLine($"PAGES time {time + 1}: {read.Count} rows, {store.BytesRead} bytes");
                Assert.Equal(rows.Where(static r => r.Value > 0.9999), read.OrderBy(static r => r.Sequence));
                Assert.True(store.BytesRead <= before, $"asked again, {store.BytesRead} bytes after {before}");
                before = store.BytesRead;
            }
        }
    }

    /// <summary>
    /// The same day, its sequence an identifier, its postings placed at units of granules (the default) or at granules, in
    /// the index of its partition once consolidated: each needle finds its row in six dependent steps at most, the partition's
    /// index, its page, the object's directories, the identifier over the place, then a granule of each other column, under
    /// 1.3 MB a needle at units and 1 MiB at granules; the units' index weighs no more than the granules' (C6).
    /// </summary>
    [Fact]
    public async Task ANeedleReadsItsPartitionsIndexThenItsGranules()
    {
        long[] sought = [17, 299_999, 300_000, 512_345, 599_999];
        Dictionary<IdentifierLocation, long> indexBytes = [];
        foreach (IdentifierLocation location in (IdentifierLocation[])[IdentifierLocation.Chunk, IdentifierLocation.Granule])
        {
            await using MemoryObjectStore memory = new MemoryObjectStore();
            await using CountingObjectStore store = new CountingObjectStore(memory);
            (VortexDataset dataset, Row[] rows) = await DayAsync(store, identifiers: [nameof(Row.Sequence)], location);
            await using (dataset)
            {
                PositionedObject held = await dataset.ScanBuilder().ObjectsAsync(Ct).SingleAsync(Ct);
                PartitionIndexEntry? index = await dataset.Snapshot.PartitionIndexAsync(PartitionLookup.PartitionOf(dataset.Snapshot, held), Ct);
                Assert.NotNull(index);
                indexBytes[location] = index.Bytes;

                // A store that answers in 5 ms, so that the reads in flight together count one step.
                memory.Latency = TimeSpan.FromMilliseconds(5);

                foreach (long seq in sought)
                {
                    store.Reset();
                    List<Row> found = await dataset.Scan<Row>().Where(r => r.Sequence == seq).ToRecordsAsync(Ct).ToListAsync(Ct);
                    Console.WriteLine($"NEEDLE {location} {seq}: {store.CountOf(ObjectOperation.Read)} reads, {store.BytesRead} bytes, {store.DependentSteps} dependent steps");
                    Assert.Equal([rows[seq]], found);
                    Assert.InRange(store.DependentSteps, 1, 6);
                    Assert.True(store.BytesRead < (location == IdentifierLocation.Granule ? 1 << 20 : 5 << 19), $"{location} {seq}: {store.BytesRead} bytes");
                }
            }
        }

        Console.WriteLine($"INDEX: {indexBytes[IdentifierLocation.Chunk]} bytes at units of granules, {indexBytes[IdentifierLocation.Granule]} at granules, {indexBytes[IdentifierLocation.Chunk] / 600_000.0:F2} a row");
        Assert.True(indexBytes[IdentifierLocation.Chunk] <= indexBytes[IdentifierLocation.Granule]);
    }

    /// <summary>
    /// A day of 600 000 rows, a row every 37 ms, sorted by the service then the instant, appended in two halves, merged and
    /// consolidated: one object, and the index of its partition when <paramref name="identifiers"/> names one.
    /// </summary>
    private static async Task<(VortexDataset Dataset, Row[] Rows)> DayAsync(IObjectStore store, string[]? identifiers, IdentifierLocation location)
    {
        Random random = new Random(103);
        Row[] rows = [.. Enumerable.Range(0, 600_000).Select(i => new Row(random.Next(0, 7), $"svc-{random.Next(0, 50):D2}", Start.AddMilliseconds(i * 37L), i, random.NextDouble()))];
        VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(Row.At)).Truncate(CalendarUnit.Day),
                SortKey = [LayoutValue.Column(nameof(Row.Service)), LayoutValue.Column(nameof(Row.At))],
                ObjectBytes = 256 << 20,
                MergeFanout = 4,
                CalmAfter = null,
            },
            ContentsObjectBytes = 0,
            SheetObjectBytes = 0,
            Identifiers = identifiers ?? [],
            IdentifierLocation = location,
        }, Ct);
        try
        {
            foreach ((int from, int to) in (ValueTuple<int, int>[])[(0, rows.Length / 2), (rows.Length / 2, rows.Length)])
            {
                await using ObjectDraft draft = dataset.StartObject();
                await draft.Writer.WriteAsync<Row>(rows[from..to], Ct);
                await dataset.AppendAsync(draft, Ct);
            }

            while (await dataset.CompactAsync(cancellationToken: Ct) is not null)
            {
            }

            await dataset.ConsolidateAsync(Ct);
            return (dataset, rows);
        }
        catch
        {
            await dataset.DisposeAsync();
            throw;
        }
    }

    [VortexRecord]
    public partial record struct Row(int Day, string Service, DateTime At, long Sequence, double Value);
}
