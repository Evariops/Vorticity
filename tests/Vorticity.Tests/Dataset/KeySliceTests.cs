// The slices of the objects a job writes, bounded by their keys: the table that holds them, the objects that keep them,
// the reads they serve without an open, for keys of every type.
//
// THE ORACLE IS THE OBJECT'S OWN ROWS: each slice's rows, read whole from the object in its order, hold keys between the
// slice's bounds, row-encoded as the dataset's key encodes them; and every question answered through the slices equals the
// rows appended.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.Scanning;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class KeySliceTests
{
    private static readonly DateTimeOffset Midnight = new(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void AKeyTableIsWrittenAndReadBackTheSame()
    {
        byte[][] lows = [[1, 2, 3], [1, 2, 3, 4, 5], [1, 9], [2, .. Enumerable.Repeat((byte)7, 80)]];
        byte[] high = [2, .. Enumerable.Repeat((byte)7, 80), 1];
        SliceTable table = SliceTable.ByKeys([10, 20, 5, 1], lows, high);
        SliceTable read = SliceTable.FromBytes(table.ToBytes())!;

        Assert.True(read.ByKey);
        Assert.Equal(36, read.Rows);
        Assert.Equal(table.Slices, read.Slices);
        for (int i = 0; i < lows.Length; i++)
        {
            Assert.Equal(lows[i], read.LowOf(i).ToArray());
            Assert.Equal(i + 1 < lows.Length ? lows[i + 1] : high, read.CeilingOf(i).ToArray());
        }

        // A slice states no time.
        Assert.All(read.Slices, slice => Assert.Equal((long.MinValue, long.MaxValue), (slice.Start, slice.End)));

        // Keys out of order, or a slice of no row, are no table.
        Assert.Throws<ArgumentException>(() => SliceTable.ByKeys([1, 1], [[2], [1]], [3]));
        Assert.Throws<ArgumentException>(() => SliceTable.ByKeys([1, 0], [[1], [2]], [3]));
        Assert.Throws<ArgumentException>(() => SliceTable.ByKeys([1], [[2]], [1]));
        byte[] bytes = table.ToBytes();
        Assert.Null(SliceTable.FromBytes(bytes.AsSpan(0, bytes.Length - 3)));

        // A time table reads as before.
        SliceTable timed = new SliceTable(TimeUnit.Microseconds, [new TimeSlice(0, 10, 0, 4), new TimeSlice(10, 20, 4, 6)]);
        Assert.False(SliceTable.FromBytes(timed.ToBytes())!.ByKey);
    }

    [Fact]
    public async Task AJobWritesNumberKeysInSlicesBoundedByThem()
    {
        Random random = new Random(0x51CE);
        List<Numbered> appended = [];
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Numbered.Schema, Options([nameof(Numbered.Id)]), Ct);
        for (int append = 0; append < 6; append++)
        {
            Numbered[] rows = [.. Enumerable.Range(0, 300).Select(i => new Numbered(random.NextInt64(1_000_000), $"n{append}-{i}"))];
            appended.AddRange(rows);
            await AppendAsync(dataset, rows);
        }

        await DrainAsync(dataset);
        await HoldsTheirKeysAsync<Numbered>(dataset, rows => rows.Select(row => (FilterLiteral[])[FilterLiteral.From(row.Id)]).ToList(), appended.Count);
        await HoldsTheirRowsAsync<Numbered>(dataset, appended, row => row.Id);
    }

    [Fact]
    public async Task AJobWritesTextAndCompositeKeysInSlicesBoundedByThem()
    {
        string[] tenants = ["acme", "globex", "initech", new string('u', 45), new string('u', 45) + "-labs"];
        List<Tenanted> appended = [];
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(
            store, Tenanted.Schema, Options([nameof(Tenanted.Tenant), nameof(Tenanted.Id)]), Ct);
        for (int append = 0; append < 6; append++)
        {
            Tenanted[] rows = [.. Enumerable.Range(0, 300).Select(i => new Tenanted(tenants[(i * 7 + append) % tenants.Length], (append * 1_000L) + i, i))];
            appended.AddRange(rows);
            await AppendAsync(dataset, rows);
        }

        await DrainAsync(dataset);
        await HoldsTheirKeysAsync<Tenanted>(
            dataset,
            rows => rows.Select(row => (FilterLiteral[])[FilterLiteral.From(row.Tenant), FilterLiteral.From(row.Id)]).ToList(),
            appended.Count);

        // A question on the first column, of each tenant and of a range of them, reads the slices it reaches and finds its rows.
        foreach (string tenant in tenants)
        {
            Assert.Equal(
                appended.Where(row => row.Tenant == tenant).Select(row => row.Id).Order(),
                (await dataset.Scan<Tenanted>().Where(r => r.Tenant == tenant).ToRecordsAsync(Ct).ToListAsync(Ct)).Select(row => row.Id).Order());
        }

        Assert.Equal(
            appended.Count(row => string.CompareOrdinal(row.Tenant, "b") >= 0 && string.CompareOrdinal(row.Tenant, "j") < 0),
            await dataset.Scan<Tenanted>().Where(r => r.Tenant >= "b" & r.Tenant < "j").CountAsync(Ct));
    }

    [Fact]
    public async Task AJobWritesInstantKeysInSlicesBoundedByThem()
    {
        Random random = new Random(0x7135);
        List<Event> appended = [];
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Event.Schema, Options([nameof(Event.At)]), Ct);
        for (int append = 0; append < 6; append++)
        {
            Event[] rows = [.. Enumerable.Range(0, 300).Select(i => new Event(Midnight.AddSeconds(random.Next(86_400)), i))];
            appended.AddRange(rows);
            await AppendAsync(dataset, rows);
        }

        await DrainAsync(dataset);
        await HoldsTheirKeysAsync<Event>(dataset, rows => rows.Select(row => (FilterLiteral[])[Micros(row.At)]).ToList(), appended.Count);

        // A window of an hour reads the slices it reaches, and finds its rows.
        DateTimeOffset from = Midnight.AddHours(7);
        DateTimeOffset to = Midnight.AddHours(8);
        Assert.Equal(
            appended.Count(row => row.At >= from && row.At < to),
            await dataset.Scan<Event>().Where(r => r.At >= from & r.At < to).CountAsync(Ct));
    }

    [Fact]
    public void AFilterOnTheFirstColumnReachesTheSlicesItsBoundsMeet()
    {
        ClusteringKey key = ClusteringKey.For([nameof(Numbered.Id)], VortexTypes.ToDType(Numbered.Schema, new Vorticity.Types.DTypeArena()))!;
        byte[] Of(long id) => key.Encode([FilterLiteral.From(id)]);
        SliceTable table = SliceTable.ByKeys([10, 10, 10, 10], [Of(0), Of(100), Of(200), Of(300)], Of(399));

        (byte[] low, byte[] high) = DatasetScanBuilder.KeyBounds(VortexExpr.Parse("Id >= 150 and Id < 250"), key)!.Value;
        Assert.Equal(new RowRange(10, 30), DatasetScanBuilder.KeyRows(table, low, high, key.LeadingType));
        (low, high) = DatasetScanBuilder.KeyBounds(VortexExpr.Parse("Id = 100"), key)!.Value;
        Assert.Equal(new RowRange(0, 20), DatasetScanBuilder.KeyRows(table, low, high, key.LeadingType));
        (low, high) = DatasetScanBuilder.KeyBounds(VortexExpr.Parse("Id > 399"), key)!.Value;
        Assert.Equal(new RowRange(30, 40), DatasetScanBuilder.KeyRows(table, low, high, key.LeadingType));
        (low, high) = DatasetScanBuilder.KeyBounds(VortexExpr.Parse("Id > 500"), key)!.Value;
        Assert.Equal(RowRange.Empty, DatasetScanBuilder.KeyRows(table, low, high, key.LeadingType));

        // No bound on the first column, or a value of another type, bounds nothing.
        Assert.Null(DatasetScanBuilder.KeyBounds(VortexExpr.Parse("Name = 'x'"), key));
        Assert.Null(DatasetScanBuilder.KeyBounds(VortexExpr.Parse("Id = 'x'"), key));
    }

    [Fact]
    public async Task AnIdentifierOfADatasetWithoutPeriodsIsLookedUpWithoutAnOpen()
    {
        Random random = new Random(0x1D);
        List<Numbered> appended = [];
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);
        await using VortexDataset dataset = await VortexDataset.CreateAsync(
            store, Numbered.Schema, Options([nameof(Numbered.Id)]) with { Identifiers = [nameof(Numbered.Name)], LookupFooterBytes = 0 }, Ct);
        for (int append = 0; append < 6; append++)
        {
            Numbered[] rows = [.. Enumerable.Range(0, 300).Select(i => new Numbered(random.NextInt64(1_000_000), $"{random.NextInt64():x16}"))];
            appended.AddRange(rows);
            await AppendAsync(dataset, rows);
        }

        await DrainAsync(dataset);
        Numbered needle = appended[777];
        VortexExpr filter = VortexExpr.Parse($"Name = '{needle.Name}'");

        // Cold both times: a handle of its own for each.
        DatasetScanCounters metrics = new DatasetScanCounters();
        await using VortexDataset looking = await VortexDataset.OpenAsync(store, new DatasetOptions { LookupFooterBytes = 0 }, Ct);
        store.Reset();
        Assert.Equal([needle.Id], await IdsAsync(looking.ScanBuilder().Where(filter).WithMetrics(metrics)));
        (long lookedUpRequests, long lookedUpBytes) = (store.Requests, store.BytesRead);
        await using VortexDataset opening = await VortexDataset.OpenAsync(store, new DatasetOptions { LookupFooterBytes = 0 }, Ct);
        store.Reset();
        Assert.Equal([needle.Id], await IdsAsync(opening.ScanBuilder().Where(filter).WithIndexes(false)));
        (long openedRequests, long openedBytes) = (store.Requests, store.BytesRead);

        Console.Out.Write(FormattableString.Invariant(
            $"KEY SLICES LOOKUP: {metrics.ObjectsLookedUp} objects looked up, {metrics.ObjectsOpened} opened; {lookedUpRequests} requests and {lookedUpBytes} bytes, against {openedRequests} and {openedBytes} opening them.\n"));
        Assert.True(metrics.ObjectsLookedUp > 0, $"{metrics.ObjectsLookedUp} objects looked up, {metrics.ObjectsOpened} opened");
        Assert.Equal(0, metrics.ObjectsOpened);
        Assert.True(lookedUpBytes < openedBytes, $"{lookedUpBytes} bytes looked up against {openedBytes} opening");
    }

    private static async Task<List<long>> IdsAsync(DatasetScanBuilder scan)
    {
        List<long> ids = [];
        await foreach (RecordBatch batch in scan.ExecuteAsync(Ct))
        {
            Columns<Numbered> columns = batch.As<Numbered>();
            Numbered[] rows = new Numbered[columns.RowCount];
            Numbered.ReadRows(columns, rows);
            foreach (int row in columns.Selection)
            {
                ids.Add(rows[row].Id);
            }
        }

        return ids;
    }

    /// <summary>
    /// Holds every object above level 0 to its slices bounded by keys: they cover its rows; each slice's rows, read whole in
    /// the object's order, hold keys between its first key and the key above it; a slice read without an open holds as
    /// many rows. Every row has left level 0.
    /// </summary>
    private static async Task HoldsTheirKeysAsync<T>(VortexDataset dataset, Func<List<T>, List<FilterLiteral[]>> keysOf, long appended)
        where T : IVortexRecord<T>
    {
        ClusteringKey key = dataset.Snapshot.Schema.Key!;
        long rows = 0;
        int sliced = 0;
        await foreach (PositionedObject held in dataset.WalkAsync(null, 0, long.MaxValue, null, default))
        {
            Assert.True(held.Level > 0, "every row has left level 0");
            ObjectEntry entry = held.Entry;
            rows += entry.Rows;
            SliceTable table = Assert.IsType<ObjectContents>(entry.Contents).Slices;
            Assert.True(table.ByKey);
            Assert.Equal(entry.Rows, table.Rows);
            sliced += table.Slices.Count > 1 ? 1 : 0;

            await using ObjectLease lease = await dataset.RentAsync(entry, Ct);
            List<FilterLiteral[]> keys = keysOf(await lease.File.Scan<T>().ToRecordsAsync(Ct).ToListAsync(Ct));
            for (int slice = 0; slice < table.Slices.Count; slice++)
            {
                TimeSlice span = table.Slices[slice];
                for (long row = span.FirstRow; row < span.FirstRow + span.Rows; row++)
                {
                    byte[] encoded = key.Encode(keys[(int)row]);
                    Assert.True(table.LowOf(slice).SequenceCompareTo(encoded) <= 0 && table.CeilingOf(slice).SequenceCompareTo(encoded) >= 0, $"row {row} lies outside slice {slice}");
                }

                using RecordBatch read = (await dataset.ReadSliceAsync(entry, slice, null, Ct))!;
                Assert.Equal(span.Rows, read.RowCount);
            }
        }

        Assert.Equal(appended, rows);
        Assert.True(sliced > 0, "no object holds two slices or more");
    }

    private static async Task HoldsTheirRowsAsync<T>(VortexDataset dataset, List<T> appended, Func<T, long> id)
        where T : IVortexRecord<T>
    {
        List<T> read = await dataset.Scan<T>().ToRecordsAsync(Ct).ToListAsync(Ct);
        Assert.Equal(appended.Select(id).Order(), read.Select(id).Order());
    }

    private static async Task DrainAsync(VortexDataset dataset)
    {
        CompactionOptions options = new CompactionOptions { LevelZeroCeiling = 1, TargetBytesAtLevelOne = 16L << 10, Fanout = 4, MaxObjectBytes = 1L << 30 };
        for (int step = 0; await dataset.CompactAsync(options, Ct) is { } done; step++)
        {
            Assert.True(step < 1_000, "compaction never runs out of jobs");
            Assert.Equal(OperationOutcome.Applied, done.Outcome);
        }
    }

    private static async Task AppendAsync<T>(VortexDataset dataset, T[] rows)
        where T : IVortexRecord<T>
    {
        await using ObjectDraft draft = dataset.StartObject();
        await draft.Writer.WriteAsync<T>(rows, Ct);
        await dataset.AppendAsync(draft, Ct);
    }

    private static FilterLiteral Micros(DateTimeOffset at) => FilterLiteral.From((at - DateTimeOffset.UnixEpoch).Ticks / 10);

    private static DatasetOptions Options(string[] key) => new DatasetOptions
    {
        Seed = 0x5_11CE,
        ClusteringKey = key,
        ContentsObjectBytes = 0,
        SliceTargetRows = 100,
        Write = new VortexWriteOptions { RowBlockSize = 64, DataBlockTargetBytes = 512 },
    };

    [VortexRecord]
    public partial record struct Numbered(long Id, string Name);

    [VortexRecord]
    public partial record struct Tenanted(string Tenant, long Id, double Amount);

    [VortexRecord]
    public partial record struct Event(DateTimeOffset At, long Size);
}
