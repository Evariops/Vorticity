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
    /// The same rows partitioned by the hour, seven partitions each consolidated with its index, the sequences drawn at random
    /// as a request's ids are, so that no object's bounds pass over a needle: on one lane, it asks the index of every
    /// partition before it awaits any, and waits on six round trips at most, as over one, where each partition's index read
    /// in turn cost two, its directory then its page, sixteen in all; the rows of a request over a month waited on 63 (T4 of
    /// S2), on 4 once read side by side.
    /// </summary>
    [Fact]
    public async Task ANeedleReadsThePartitionsIndexesSideBySide()
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        (VortexDataset dataset, Row[] rows) = await DayAsync(store, identifiers: [nameof(Row.Sequence)], IdentifierLocation.Chunk, CalendarUnit.Hour, shuffled: true);
        await dataset.DisposeAsync();

        // One lane, which walks every partition: lanes would each read their own side by side.
        await using VortexSession session = VortexSession.Create(static options => options.MaxDegreeOfParallelism = 1);
        await using VortexDataset cold = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session }, Ct);
        HashSet<string> partitions = [];
        int indexed = 0;
        await foreach (PositionedObject held in cold.ScanBuilder().ObjectsAsync(Ct))
        {
            byte[] partition = PartitionLookup.PartitionOf(cold.Snapshot, held);
            if (partitions.Add(Convert.ToHexString(partition)) && await cold.Snapshot.PartitionIndexAsync(partition, Ct) is not null)
            {
                indexed++;
            }
        }

        // A store that answers in 20 ms, so that the reads in flight together count one step, and those one after another one each.
        memory.Latency = TimeSpan.FromMilliseconds(20);
        List<Row> found;
        Row sought = rows[512_345];
        try
        {
            store.Reset();
            found = await cold.Scan<Row>().Where(r => r.Sequence == sought.Sequence).ToRecordsAsync(Ct).ToListAsync(Ct);
        }
        finally
        {
            memory.Latency = TimeSpan.Zero;
        }

        Console.WriteLine($"NEEDLE OVER {indexed} INDEXED PARTITIONS OF {partitions.Count}: {store.CountOf(ObjectOperation.Read)} reads, {store.DependentSteps} dependent steps");
        Assert.Equal(7, partitions.Count);
        Assert.Equal(7, indexed);
        Assert.Equal([sought], found);
        Assert.InRange(store.DependentSteps, 1, 6);
    }

    /// <summary>
    /// Ids that lie in different units of one object, looked up together: their spans are read in one set of requests, and a
    /// buffer each of them reads whole, the ends of a sorted column's runs, is one request both share; each span's other
    /// ranges keep their own. The rows are LINQ's, at units of granules and at granules.
    /// </summary>
    [Fact]
    public async Task IdsInSeveralUnitsOfAnObjectAreReadTogether()
    {
        foreach (IdentifierLocation location in (IdentifierLocation[])[IdentifierLocation.Chunk, IdentifierLocation.Granule])
        {
            await using MemoryObjectStore store = new MemoryObjectStore();
            (VortexDataset dataset, Row[] rows) = await DayAsync(store, identifiers: [nameof(Row.Sequence)], location);
            await using (dataset)
            {
                // Rows far apart in the object's order, the service then the instant: each in a unit of its own.
                Row[] sorted = [.. rows.OrderBy(static r => r.Service, StringComparer.Ordinal).ThenBy(static r => r.At)];
                long[] sought = [.. ((int[])[1_000, 140_000, 280_000, 420_000, 560_000]).Select(at => sorted[at].Sequence)];
                List<Row> found = await dataset.Scan<Row>().Where(r => r.Sequence.In(sought)).ToRecordsAsync(Ct).ToListAsync(Ct);
                Assert.Equal(rows.Where(r => sought.Contains(r.Sequence)).OrderBy(static r => r.Sequence), found.OrderBy(static r => r.Sequence));
            }
        }
    }

    /// <summary>
    /// A day of calls sorted by the service then the instant, merged and consolidated into one object whose instant is one
    /// chunk: a count of one service over twelve hours, by a cold handle, reads the granules its key's sparse index keeps,
    /// under a tenth of the object, its open's tail the most of it, where it read the instant's column whole, three quarters
    /// of it; the plan, analyzed, prices the part of the object a read of those rows takes, within half and twice what a cold
    /// read takes; every answer is LINQ's.
    /// </summary>
    [Fact]
    public async Task ACountReadsTheGranulesItsKeyKeeps()
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        (VortexDataset dataset, Call[] calls) = await CallsAsync(store);
        DateTime from = Start.AddHours(6);
        DateTime to = Start.AddHours(18);
        await using (dataset)
        {
            PositionedObject held = await dataset.ScanBuilder().ObjectsAsync(Ct).SingleAsync(Ct);
            Assert.Equal(1, held.Entry.Contents!.ColumnChunks[0]);
            Call[] expected = [.. calls.Where(c => c.Service == "svc-07" && c.At >= from && c.At < to).OrderBy(static c => c.At)];

            await using (VortexDataset cold = await VortexDataset.OpenAsync(store, cancellationToken: Ct))
            {
                store.Reset();
                Assert.Equal(expected.Length, await Asked(cold).CountAsync(Ct));
                Console.WriteLine($"COUNT: {store.CountOf(ObjectOperation.Read)} reads, {store.BytesRead} bytes of an object of {held.Entry.Bytes}");
                Assert.True(store.BytesRead < held.Entry.Bytes / 10, $"{store.BytesRead} bytes of {held.Entry.Bytes}");
            }

            ScanPlan plan = await Asked(dataset).ExplainAsync(analyze: true, Ct);
            await using (VortexDataset cold = await VortexDataset.OpenAsync(store, cancellationToken: Ct))
            {
                store.Reset();
                List<Call> read = await Asked(cold).ToRecordsAsync(Ct).ToListAsync(Ct);
                Console.WriteLine($"ROWS: {plan.Units.Single().Detail}; {store.CountOf(ObjectOperation.Read)} reads, {store.BytesRead} bytes");
                Assert.Equal(expected, read);
                Assert.InRange(plan.Cost!.Bytes, store.BytesRead / 2, store.BytesRead * 2);
            }
        }

        Scan<Call> Asked(VortexDataset handle) => handle.Scan<Call>().Where(c => c.Service == "svc-07" && c.At >= from && c.At < to);
    }

    /// <summary>
    /// A day of 384 000 calls, one every 225 ms, of forty services, sorted by the service then the instant, its sparse index on
    /// the service alone, appended in eight parts, merged and consolidated: one object.
    /// </summary>
    private static async Task<(VortexDataset Dataset, Call[] Calls)> CallsAsync(IObjectStore store)
    {
        Random random = new Random(11);
        Call[] calls = [.. Enumerable.Range(0, 384_000).Select(i => new Call(
            Start.AddMilliseconds(i * 225L), $"svc-{random.Next(40):D2}", random.Next(50) == 0 ? (short)500 : (short)200, Math.Round(random.NextDouble() * 200, 1)))];
        VortexDataset dataset = await VortexDataset.CreateAsync(store, Call.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(Call.At)).Truncate(CalendarUnit.Day),
                SortKey = [LayoutValue.Column(nameof(Call.Service)), LayoutValue.Column(nameof(Call.At))],
                IndexedPrefix = 1,
                MergeFanout = 4,
                CalmAfter = null,
            },
        }, Ct);
        try
        {
            for (int part = 0; part < 8; part++)
            {
                await using ObjectDraft draft = dataset.StartObject();
                await draft.Writer.WriteAsync<Call>(calls[(part * 48_000)..((part + 1) * 48_000)], Ct);
                await dataset.AppendAsync(draft, Ct);
            }

            while (await dataset.CompactAsync(cancellationToken: Ct) is not null)
            {
            }

            await dataset.ConsolidateAsync(Ct);
            return (dataset, calls);
        }
        catch
        {
            await dataset.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// A day of 600 000 rows, a row every 37 ms, sorted by the service then the instant, appended in two halves, merged and
    /// consolidated: one object, and the index of its partition when <paramref name="identifiers"/> names one; or, partitioned
    /// by a finer <paramref name="partition"/>, an object and an index of each. A row's sequence is its rank, or, when
    /// <paramref name="shuffled"/>, a rank drawn at random.
    /// </summary>
    private static async Task<(VortexDataset Dataset, Row[] Rows)> DayAsync(
        IObjectStore store, string[]? identifiers, IdentifierLocation location, CalendarUnit partition = CalendarUnit.Day, bool shuffled = false)
    {
        Random random = new Random(103);
        long[] sequences = [.. Enumerable.Range(0, 600_000).Select(static i => (long)i)];
        if (shuffled)
        {
            new Random(5).Shuffle(sequences);
        }

        Row[] rows = [.. Enumerable.Range(0, 600_000).Select(i => new Row(random.Next(0, 7), $"svc-{random.Next(0, 50):D2}", Start.AddMilliseconds(i * 37L), sequences[i], random.NextDouble()))];
        VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(Row.At)).Truncate(partition),
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
            // Partitioned by the day, the rows are appended in two halves and merged; finer, a row of two each time, so that
            // every partition merges two objects as it is consolidated, where one alone would be moved up unindexed.
            Row[][] parts = partition == CalendarUnit.Day
                ? [rows[..(rows.Length / 2)], rows[(rows.Length / 2)..]]
                : [[.. rows.Where(static (_, i) => i % 2 == 0)], [.. rows.Where(static (_, i) => i % 2 == 1)]];
            foreach (Row[] part in parts)
            {
                await using ObjectDraft draft = dataset.StartObject();
                await draft.Writer.WriteAsync<Row>(part, Ct);
                await dataset.AppendAsync(draft, Ct);
            }

            while (partition == CalendarUnit.Day && await dataset.CompactAsync(cancellationToken: Ct) is not null)
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

    [VortexRecord]
    public partial record struct Call(DateTime At, string Service, short Status, double LatencyMs);
}
