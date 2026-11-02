// The table of contents a sealed partition's entry carries: a granule of the object reads in two round trips, and a key of
// its variant column in two, with no open of the object and no footer read.
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

public sealed partial class ObjectContentsTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Two sealed hours of calls on a store that answers in 100 ms, in granules of 256 rows: a granule of one takes two round
    /// trips, its columns' chunk directories then its chunks, and holds the rows a whole open reads.
    /// </summary>
    [Fact]
    public async Task AGranuleReadsInTwoRoundTrips()
    {
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);

        // A session of its own: the default one gates the reads every test in flight makes, which would
        // queue the objects' requests behind others' and count them as dependent.
        await using VortexSession session = VortexSession.Create(static _ => { });
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Call.Schema, new DatasetOptions
        {
            Session = session,
            Layout = new DatasetLayout { Partition = LayoutValue.Column(nameof(Call.At)).Truncate(CalendarUnit.Hour), SortKey = [LayoutValue.Column(nameof(Call.At))], Organization = LayoutOrganization.Leveled }, ContentsObjectBytes = 0,
            Write = new VortexWriteOptions { Indexes = IndexPolicy.None.WithBudgetPerMille(1_000), BlockRows = GranuleRows },
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
        await foreach (TreeEntry leaf in dataset.AboveLevelZeroAsync(Ct))
        {
            periods.Add(ObjectEntry.FromBytes(leaf.Value.Span));
        }

        Assert.Equal(3, periods.Count);
        Assert.True(periods[0].Rows > 4 * GranuleRows, $"an hour of {periods[0].Rows} rows");

        // A granule: its columns' chunk directories, then its chunks. A latency longer than what readying a read takes,
        // which the tests running beside this one stretch, keeps the two round trips apart.
        inner.Latency = TimeSpan.FromMilliseconds(100);
        store.Reset();
        const int Granule = 3;
        List<RecordBatch> read = (await dataset.ReadBlocksAsync(periods[0], [Granule], GranuleRows, VortexExpr.Parse("Duration >= 0"), null, Ct))!;
        try
        {
            Assert.Equal(2, store.DependentSteps);
            List<(DateTimeOffset At, long Duration)> held = [];
            foreach (RecordBatch batch in read)
            {
                Columns<Call> columns = batch.As<Call>();
                DateTimeOffset[] at = new DateTimeOffset[batch.RowCount];
                long[] durations = new long[batch.RowCount];
                columns.Column<DateTimeOffset>(0).CopyTo(at);
                columns.Column<long>(3).CopyTo(durations);
                held.AddRange(at.Zip(durations));
            }

            await using ObjectLease lease = await dataset.RentAsync(periods[0], Ct);
            List<Call> stored = await lease.File.Scan<Call>().ToRecordsAsync(Ct).ToListAsync(Ct);
            Assert.Equal(stored.GetRange(Granule * GranuleRows, GranuleRows).Select(r => (r.At, r.Duration)), held);
        }
        finally
        {
            foreach (RecordBatch batch in read)
            {
                batch.Dispose();
            }
        }
    }

    /// <summary>The rows of a granule of the objects these tests write.</summary>
    private const int GranuleRows = 256;

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
            Layout = new DatasetLayout { Partition = LayoutValue.Column(nameof(Tagged.At)).Truncate(CalendarUnit.Hour), SortKey = [LayoutValue.Column(nameof(Tagged.At))], Organization = LayoutOrganization.Leveled }, ContentsObjectBytes = 0,
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
        await foreach (TreeEntry leaf in dataset.AboveLevelZeroAsync(Ct))
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

    /// <summary>The rows of <paramref name="batches"/>, each holding its variant column alone, whose object holds <paramref name="key"/> as a text.</summary>
    private static long Held(List<RecordBatch> batches, string key)
    {
        long held = 0;
        foreach (RecordBatch batch in batches)
        {
            using (batch)
            {
                CanonicalArena arena = batch.Arena;
                int column = arena.GetNode(batch.RootIndex).GetFieldIndex(0);
                CanonicalNode metadata = arena.GetNode(Vorticity.Writing.ArrayBlobWriter.Materialize(arena, arena.GetNode(column).GetFieldIndex(0)));
                int value = Vorticity.Writing.ArrayBlobWriter.Materialize(arena, arena.GetNode(column).GetFieldIndex(1));
                for (int row = 0; row < batch.RowCount; row++)
                {
                    if (ArenaWords.IsValid(arena, column, row) && ArenaWords.IsValid(arena, value, row)
                        && Variant.FromBytes(Vorticity.Compute.LiteralReader.ViewAt(metadata, row), Vorticity.Compute.LiteralReader.ViewAt(arena.GetNode(value), row))
                            is { Kind: VariantKind.Object } document
                        && document[key] is { } text
                        && Vorticity.Types.Variants.VariantClasses.Of(text.Kind) == Vorticity.Types.Variants.VariantClass.String)
                    {
                        held++;
                    }
                }
            }
        }

        return held;
    }

    /// <summary>
    /// A table of contents reads back as it was written, an entry carries it through its bytes, and contents of the version
    /// that held the synopsis tables are no contents.
    /// </summary>
    [Fact]
    public void TheContentsAndTheEntryRoundTrip()
    {
        ObjectContents contents = new ObjectContents(
            Vorticity.Types.Serialization.DTypeFlatBuffers.Serialize(Vorticity.VortexTypes.ToDType(Call.Schema, new Vorticity.Types.DTypeArena())),
            ["vortex.primitive", "vortex.runend"],
            15,
            [new IndexSegment(100, 20, 0, 7), new IndexSegment(120, 30, 0, 8)],
            [(2, new IndexSegment(150, 40, 0, 9))]);
        ObjectContents back = ObjectContents.From(contents.ToBytes())!;
        Assert.Equal(contents.Schema, back.Schema);
        Assert.Equal(contents.Encodings, back.Encodings);
        Assert.Equal(contents.Rows, back.Rows);
        Assert.Equal(contents.ChunkDirectories, back.ChunkDirectories);
        Assert.Equal(contents.PathDirectories, back.PathDirectories);
        Assert.Null(ObjectContents.From([1, 2, 3]));

        // The first version's contents, the same fields behind version 1, are none this library reads.
        byte[] first = contents.ToBytes();
        Assert.Equal(0x08, first[0]);
        Assert.Equal(2, first[1]);
        first[1] = 1;
        Assert.Null(ObjectContents.From(first));

        ObjectEntry entry = new ObjectEntry("data/a.vortex", 7, 15, 9_000, 11, (ObjectSummaries?)null).WithFooterBytes(80_000).WithTableOfContents(contents.ToBytes());
        ObjectEntry read = ObjectEntry.FromBytes(entry.ToBytes());
        Assert.Equal(entry, read);
        Assert.Equal(80_000, read.FooterBytes);
        Assert.Equal(contents.ToBytes(), read.TableOfContents.ToArray());
        Assert.Equal(read.TableOfContents.ToArray(), read.WithMarkedAt(1_000).TableOfContents.ToArray());
    }

    [VortexRecord]
    public partial record struct Call(DateTimeOffset At, string Region, int Status, long Duration);

    [VortexRecord]
    public partial record struct Tagged(DateTimeOffset At, Variant Attributes);
}
