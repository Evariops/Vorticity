// The synopsis's table of contents: a sealed period's entry carries it, so that its synopsis tables read in
// one round trip and a slice of it in two, with no open of the object, no footer read and no data read
// past its synopsis part.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class SynopsisContentsTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Two sealed hours of calls on a store that answers in 100 ms: every object's synopsis tables, read at
    /// once through the tables of contents their entries carry, take one dependent round trip and a request
    /// an object, read none of the bytes past their synopsis parts, and are the tables the directory names.
    /// A slice of one takes two round trips, its chunk directory then its chunks, and holds the rows a whole
    /// open reads.
    /// </summary>
    [Fact]
    public async Task AWholePeriodReadsItsSynopsisInOneRoundTripAndASliceInTwo()
    {
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);

        // A session of its own: the default one gates the reads every test in flight makes, which would
        // queue the objects' requests behind others' and count them as dependent.
        await using VortexSession session = VortexSession.Create(static _ => { });
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Call.Schema, new DatasetOptions
        {
            Session = session,
            ClusteringKey = [nameof(Call.At)],
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour, SliceTargetRows = 500 },
            Write = new VortexWriteOptions { Indexes = IndexPolicy.None.WithBudgetPerMille(1_000) },
        }, Ct);
        Random random = new Random(5);
        for (int part = 0; part < 16; part++)
        {
            Call[] rows = [.. Enumerable.Range(0, 400).Select(i => new Call(
                Noon.AddSeconds((part * 400 + i) * 1.25), $"region-{random.Next(3)}", 200 + (random.Next(5) * 100), random.NextInt64(1_000_000)))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Call>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }, Ct) is not null)
        {
        }

        List<ObjectEntry> periods = [];
        await foreach (TreeEntry leaf in dataset.Levels[PeriodLevels.Periods].EnumerateAsync(dataset.Pages, Ct))
        {
            periods.Add(ObjectEntry.FromBytes(leaf.Value.Span));
        }

        Assert.Equal(2, periods.Count);
        SynopsisContents[] contents = [.. periods.Select(p => SynopsisContents.From(p.Synopsis.Span)!)];
        Assert.All(contents, c => Assert.NotEmpty(c.Tables));
        long partBytes = contents.Sum(c => c.PartEnd - c.PartStart);

        // Cold, every object at once: one round trip, a request an object, nothing past the parts. A latency
        // longer than what readying a read takes, which the tests running beside this one stretch: at 20 ms,
        // a thread held that long between the two objects' reads under a loaded full suite counted two steps.
        inner.Latency = TimeSpan.FromMilliseconds(100);
        store.Reset();
        SynopsisTableSet?[] sets = await Task.WhenAll(periods.Select(p => dataset.ReadSynopsisAsync(p, null, Ct).AsTask()));
        Assert.Equal(1, store.DependentSteps);
        Assert.Equal(periods.Count, store.Requests);
        Assert.InRange(store.BytesRead, 1, partBytes);
        Console.Out.Write(FormattableString.Invariant(
            $"SYNOPSIS CONTENTS: {periods.Count} objects, entries of {string.Join('/', periods.Select(p => p.ToBytes().Length))} bytes, the contents {string.Join('/', periods.Select(p => p.Synopsis.Length))}, {store.BytesRead} bytes read of {partBytes} in the parts.\n"));

        // The tables the directory names, row for row.
        for (int i = 0; i < periods.Count; i++)
        {
            using SynopsisTableSet read = sets[i]!;
            await using ObjectLease lease = await dataset.RentAsync(periods[i], Ct);
            using SynopsisTableSet named = await SynopsisTableInfo.ReadAsync(lease.File, Ct);
            Assert.Equal(named.Tables.Select(t => t.Table), read.Tables.Select(t => t.Table), Same);
            for (int t = 0; t < named.Tables.Count; t++)
            {
                Assert.Equal(Columns(named.Arena, named.Tables[t].Node), Columns(read.Arena, read.Tables[t].Node));
            }
        }

        // A slice: its columns' chunk directories, then its chunks.
        store.Reset();
        TimeSlice slice = contents[0].Slices.Slices[3];
        using (RecordBatch batch = (await dataset.ReadSliceAsync(periods[0], 3, null, Ct))!)
        {
            Assert.Equal(2, store.DependentSteps);
            Assert.Equal(slice.Rows, batch.RowCount);
            await using ObjectLease lease = await dataset.RentAsync(periods[0], Ct);
            List<Call> stored = await lease.File.Scan<Call>().ToRecordsAsync(Ct).ToListAsync(Ct);
            Columns<Call> columns = batch.As<Call>();
            DateTimeOffset[] at = new DateTimeOffset[batch.RowCount];
            long[] durations = new long[batch.RowCount];
            columns.Column<DateTimeOffset>(0).CopyTo(at);
            columns.Column<long>(3).CopyTo(durations);
            Assert.Equal(stored.GetRange((int)slice.FirstRow, (int)slice.Rows).Select(r => (r.At, r.Duration)), at.Zip(durations));
        }
    }

    /// <summary>
    /// An hour of calls whose attributes hold a route, and on about one row in a hundred a key no table
    /// counts: a question on that key reads, cold, the path directory beside the column's chunk directory,
    /// then the slices that may hold the key, two round trips, and warm none at all; it finds every row that
    /// holds the key.
    /// </summary>
    [Fact]
    public async Task APathNoTableNamesReadsInTwoRoundTripsColdAndNoneWarm()
    {
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);

        // A session of its own, with a cache that keeps what a read fetched for the next.
        await using VortexSession session = VortexSession.Create(static options => options.SegmentCache = new SegmentCache(64 << 20));
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Tagged.Schema, new DatasetOptions
        {
            Session = session,
            ClusteringKey = [nameof(Tagged.At)],
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour, SliceTargetRows = 500 },
        }, Ct);
        Random random = new Random(3);
        for (int part = 0; part < 16; part++)
        {
            Tagged[] rows = [.. Enumerable.Range(0, 400).Select(i =>
            {
                List<(string, Variant)> fields = [("route", Variant.From($"/r/{random.Next(9)}"))];
                if (random.Next(97) == 0)
                {
                    fields.Add(("rare", Variant.From($"v{random.Next(3)}")));
                }

                return new Tagged(Noon.AddSeconds((part * 400 + i) * 1.25), Variant.Object([.. fields]));
            })];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Tagged>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }, Ct) is not null)
        {
        }

        ObjectEntry hour = default!;
        await foreach (TreeEntry leaf in dataset.Levels[PeriodLevels.Periods].EnumerateAsync(dataset.Pages, Ct))
        {
            hour = ObjectEntry.FromBytes(leaf.Value.Span);
            break;
        }

        SynopsisContents contents = SynopsisContents.From(hour.Synopsis.Span)!;
        Assert.DoesNotContain(contents.Slices.Paths, p => p.Key == "rare");
        int field = Call.Schema.Count;
        await using (ObjectLease lease = await dataset.RentAsync(hour, Ct))
        {
            field = lease.File.DType.IndexOfField(nameof(Tagged.Attributes));
            List<Tagged> stored = await lease.File.Scan<Tagged>().ToRecordsAsync(Ct).ToListAsync(Ct);
            long expected = stored.Count(r => r.Attributes.Has("rare"));
            Assert.True(expected > 0);

            // Cold: the directories, then the slices.
            inner.Latency = TimeSpan.FromMilliseconds(20);
            store.Reset();
            List<RecordBatch> cold = (await dataset.ReadPathAsync(hour, field, "rare", Ct))!;
            Assert.Equal(2, store.DependentSteps);
            Assert.Equal(expected, Held(cold, "rare"));

            // Warm: the session's cache holds every region.
            store.Reset();
            List<RecordBatch> warm = (await dataset.ReadPathAsync(hour, field, "rare", Ct))!;
            Assert.Equal(0, store.Requests);
            Assert.Equal(expected, Held(warm, "rare"));
        }
    }

    /// <summary>The rows of <paramref name="batches"/>, each holding its variant column alone, whose object holds <paramref name="key"/>.</summary>
    private static long Held(List<RecordBatch> batches, string key)
    {
        long held = 0;
        foreach (RecordBatch batch in batches)
        {
            using (batch)
            {
                int column = batch.Arena.GetNode(batch.RootIndex).GetFieldIndex(0);
                int values = Vorticity.Writing.VariantShredder.KeyColumns(batch.Arena, new Vorticity.Types.DTypeArena(), column, [(key, Vorticity.Types.Variants.VariantClass.String)])[0];
                for (int row = 0; row < batch.RowCount; row++)
                {
                    held += ArenaWords.IsValid(batch.Arena, values, row) ? 1 : 0;
                }
            }
        }

        return held;
    }

    /// <summary>A table of contents reads back as it was written, and an entry carries it through its bytes.</summary>
    [Fact]
    public void TheContentsAndTheEntryRoundTrip()
    {
        SliceTable slices = new SliceTable(TimeUnit.Microseconds, [new TimeSlice(0, 60_000_000, 0, 10), new TimeSlice(60_000_000, 120_000_000, 10, 5)], [1]);
        SynopsisContents contents = new SynopsisContents(
            Vorticity.Types.Serialization.DTypeFlatBuffers.Serialize(Vorticity.VortexTypes.ToDType(Call.Schema, new Vorticity.Types.DTypeArena())),
            ["vortex.primitive", "vortex.runend"],
            slices,
            [new IndexSegment(100, 20, 0, 7), new IndexSegment(120, 30, 0, 8)],
            4_096,
            8_192,
            [(new SynopsisTableInfo(SynopsisTableKind.Coarse, SynopsisBucket.Hour, [1], [3], 4), [new TableChunk(new IndexSegment(4_096, 512, 6, 9), 0, 3_600_000_000, 3), new TableChunk(new IndexSegment(4_608, 128, 6, 10), 3_600_000_000, 3_600_000_000, 1)])],
            [(1, [new ChunkSpan(4_608, 300, 6, 0, 15)])]);
        SynopsisContents back = SynopsisContents.From(contents.ToBytes())!;
        Assert.Equal(contents.Schema, back.Schema);
        Assert.Equal(contents.Encodings, back.Encodings);
        Assert.Equal(contents.Slices.Slices, back.Slices.Slices);
        Assert.Equal(contents.ChunkDirectories, back.ChunkDirectories);
        Assert.Equal((contents.PartStart, contents.PartEnd), (back.PartStart, back.PartEnd));
        Assert.Equal(contents.Tables.SelectMany(t => t.Chunks), back.Tables.SelectMany(t => t.Chunks));
        Assert.Equal(contents.Tables.Select(t => t.Table), back.Tables.Select(t => t.Table), Same);
        Assert.Equal(contents.Columns.Select(c => (c.Field, c.Chunks.Single())), back.Columns.Select(c => (c.Field, c.Chunks.Single())));
        Assert.Null(SynopsisContents.From([1, 2, 3]));

        ObjectEntry entry = new ObjectEntry("data/a.vortex", 7, 15, 9_000, 11, (ObjectSummaries?)null).WithFooterBytes(80_000).WithSynopsis(contents.ToBytes());
        ObjectEntry read = ObjectEntry.FromBytes(entry.ToBytes());
        Assert.Equal(entry, read);
        Assert.Equal(80_000, read.FooterBytes);
        Assert.Equal(contents.ToBytes(), read.Synopsis.ToArray());
        Assert.Equal(read.Synopsis.ToArray(), read.WithMarkedAt(1_000).Synopsis.ToArray());
    }

    private static bool Same(SynopsisTableInfo left, SynopsisTableInfo right) =>
        left.Kind == right.Kind && left.Bucket == right.Bucket && left.Keys.SequenceEqual(right.Keys) && left.Measures.SequenceEqual(right.Measures) && left.Rows == right.Rows;

    /// <summary>Every 64-bit column of a table's rows, its keys aside, as one list.</summary>
    private static List<long> Columns(CanonicalArena arena, int node)
    {
        CanonicalNode table = arena.GetNode(node);
        List<long> values = [];
        for (int field = 0; field < table.DType.FieldCount; field++)
        {
            if (table.DType.GetField(field).Kind == Vorticity.Types.DTypeKind.Primitive && table.DType.GetField(field).PType == Vorticity.Types.PType.I64)
            {
                values.AddRange(ColumnData.Values<long>(arena, arena.GetNode(node).GetFieldIndex(field)).ToArray());
            }
        }

        return values;
    }

    [VortexRecord]
    public partial record struct Call(DateTimeOffset At, string Region, int Status, long Duration);

    [VortexRecord]
    public partial record struct Tagged(DateTimeOffset At, Variant Attributes);
}
