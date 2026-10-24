// The table of contents a sealed period's entry carries: a slice of the object reads in two round trips, and a key of its
// variant column in two, with no open of the object and no footer read.
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
    /// Two sealed hours of calls on a store that answers in 100 ms: a slice of one takes two round trips, its chunk
    /// directory then its chunks, and holds the rows a whole open reads.
    /// </summary>
    [Fact]
    public async Task ASliceReadsInTwoRoundTrips()
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
        SynopsisContents contents = SynopsisContents.From(periods[0].Synopsis.Span)!;

        // A slice: its columns' chunk directories, then its chunks. A latency longer than what readying a read takes,
        // which the tests running beside this one stretch, keeps the two round trips apart.
        inner.Latency = TimeSpan.FromMilliseconds(100);
        store.Reset();
        TimeSlice slice = contents.Slices.Slices[3];
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
    /// An hour of calls whose attributes hold a route, and on about one row in a hundred a rare key: a question on that key
    /// reads, cold, the path directory beside the column's chunk directory, then the slices that may hold the key, two round
    /// trips, and warm none at all, whether the session keeps the declared regions in a synopsis cache as well or not; it
    /// finds every row that holds the key.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APathReadsInTwoRoundTripsColdAndNoneWarm(bool synopsisCache)
    {
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vorticity-path-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            await PathReadsAsync(directory, synopsisCache);
        }
        finally
        {
            if (System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static async Task PathReadsAsync(string directory, bool synopsisCache)
    {
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);

        // A session of its own, with a cache that keeps what a read fetched for the next.
        await using VortexSession session = VortexSession.Create(options =>
        {
            options.SegmentCache = new SegmentCache(64 << 20);
            options.SynopsisCache = synopsisCache ? new SynopsisCache(directory, 64 << 20) : null;
        });
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

    /// <summary>A chunk without the bounds of its key's values, whose arrays compare by their references.</summary>
    private static TableChunk Unbounded(TableChunk chunk) => chunk with { Keys = null };

    /// <summary>A chunk's bounds as bytes, null for none: the bounds a chunk holds are a view of its table's store.</summary>
    private static (byte[]? Least, byte[]? Greatest) Bounds(TableChunk chunk) =>
        chunk.Keys is { } keys ? (keys.Least.ToArray(), keys.Greatest.ToArray()) : (null, null);

    /// <summary>
    /// A table of contents reads back as it was written, a chunk of a table of values with the least and the greatest of
    /// its values, and an entry carries it through its bytes.
    /// </summary>
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
            [
                (new SynopsisTableInfo(SynopsisTableKind.Coarse, SynopsisBucket.Hour, [1], [3], 4), [new TableChunk(new IndexSegment(4_096, 512, 6, 9), 0, 3_600_000_000, 3), new TableChunk(new IndexSegment(4_608, 128, 6, 10), 3_600_000_000, 3_600_000_000, 1)]),
                (new SynopsisTableInfo(SynopsisTableKind.Values, SynopsisBucket.Slice, [1], [3], 2), [new TableChunk(new IndexSegment(4_736, 64, 6, 11), 0, 60_000_000, 2) { Keys = new ValueBounds([1, 1, (byte)'a'], [1, 2, (byte)'e', (byte)'u']) }]),
            ]);
        SynopsisContents back = SynopsisContents.From(contents.ToBytes())!;
        Assert.Equal(contents.Schema, back.Schema);
        Assert.Equal(contents.Encodings, back.Encodings);
        Assert.Equal(contents.Slices.Slices, back.Slices.Slices);
        Assert.Equal(contents.ChunkDirectories, back.ChunkDirectories);
        Assert.Equal((contents.PartStart, contents.PartEnd), (back.PartStart, back.PartEnd));
        Assert.Equal(contents.Tables.SelectMany(t => t.Chunks).Select(Unbounded), back.Tables.SelectMany(t => t.Chunks).Select(Unbounded));
        Assert.Equal(contents.Tables.SelectMany(t => t.Chunks).Select(Bounds), back.Tables.SelectMany(t => t.Chunks).Select(Bounds));
        Assert.Equal(contents.Tables.Select(t => t.Table), back.Tables.Select(t => t.Table), Same);
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

    [VortexRecord]
    public partial record struct Call(DateTimeOffset At, string Region, int Status, long Duration);

    [VortexRecord]
    public partial record struct Tagged(DateTimeOffset At, Variant Attributes);
}
