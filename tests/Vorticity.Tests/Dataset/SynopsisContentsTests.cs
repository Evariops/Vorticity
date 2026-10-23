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
    /// once through the tables of contents their entries carry, take a dependent round trip and a request an
    /// object, the entries holding the chunks of tables this small, read none of the bytes past their synopsis
    /// parts, and are the tables the directory names, which names each table by its list. Listed apart, as a
    /// day's large tables are, the lists come first, a round trip and a request an object more. A slice of one
    /// takes two round trips, its chunk directory then its chunks, and holds the rows a whole open reads.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AWholePeriodReadsItsSynopsisInARoundTripAndASliceInTwo(bool listed)
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
            Write = new VortexWriteOptions
            {
                Indexes = IndexPolicy.None.WithBudgetPerMille(1_000),
                InlineListBytes = listed ? 0 : SynopsisContents.InlineListBytes,
            },
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

        // Cold, every object at once: the lists in one round trip, a request an object, when the tables list their
        // chunks apart, then the chunks in one more, a request an object, nothing past the parts. The store counts
        // a step when a read starts with none in flight, so each round trip is counted on its own: an object's
        // chunks, asked the instant its lists arrive, would count with the other's lists still in flight. A
        // latency longer than what readying a read takes, which the tests running beside this one stretch: at
        // 20 ms, a thread held that long between the two objects' reads under a loaded full suite counted two steps.
        inner.Latency = TimeSpan.FromMilliseconds(100);
        store.Reset();
        await dataset.LoadSynopsisListsAsync([.. periods.Select(p => (p, p.Contents!, (ISynopsisReads?)null))], Ct);
        Assert.Equal(listed ? 1 : 0, store.DependentSteps);
        Assert.Equal(listed ? periods.Count : 0, store.Requests);
        long listBytes = store.BytesRead;
        store.Reset();
        SynopsisTableSet?[] sets = await Task.WhenAll(periods.Select(p => dataset.ReadSynopsisAsync(p, null, Ct).AsTask()));
        Assert.Equal(1, store.DependentSteps);
        Assert.Equal(periods.Count, store.Requests);
        Assert.InRange(listBytes + store.BytesRead, 1, partBytes);
        Console.Out.Write(FormattableString.Invariant(
            $"SYNOPSIS CONTENTS: {periods.Count} objects, entries of {string.Join('/', periods.Select(p => p.ToBytes().Length))} bytes, the contents {string.Join('/', periods.Select(p => p.Synopsis.Length))}, {listBytes} bytes of lists and {store.BytesRead} of chunks read of {partBytes} in the parts.\n"));

        // The tables the directory names, row for row, those of sketches whose chunks lay their distinct sketches apart among
        // them, which the directory names after the chunks.
        for (int i = 0; i < periods.Count; i++)
        {
            using SynopsisTableSet read = sets[i]!;
            await using ObjectLease lease = await dataset.RentAsync(periods[i], Ct);
            using SynopsisTableSet named = await SynopsisTableReader.ReadAsync(lease.File, Ct);
            Assert.Equal(named.Tables.Select(t => t.Table), read.Tables.Select(t => t.Table), Same);
            Assert.Contains(read.Tables, t => t.Chunk.Distinct is not null);
            Assert.Equal(read.Tables.Select(t => t.Chunk.Distinct), named.Tables.Select(t => t.Chunk.Distinct));

            // The directory names each table by its list alone, the one the contents name when they list it apart, and
            // none of the part's columns.
            IndexDirectory directory = (await lease.File.ReadIndexDirectoryAsync(Ct))!;
            Assert.DoesNotContain(directory.Entries, e => e.Kind == IndexKinds.SynopsisColumns);
            IndexSegment[] lists = [.. directory.Entries.Where(e => e.Kind == IndexKinds.SynopsisTable).Select(e => Assert.Single(e.Runs[0].Payload))];
            Assert.Equal(periods[i].Contents!.Tables.Count, lists.Length);
            Assert.All(periods[i].Contents!.Tables, t => Assert.Equal(listed, t.Chunks is SynopsisContents.TableChunks { List: not null }));
            if (listed)
            {
                Assert.Equal(periods[i].Contents!.Tables.Select(t => ((SynopsisContents.TableChunks)t.Chunks).List!.Value), lists);
            }
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
    /// then the slices that may hold the key, two round trips, and warm none at all, whether the session
    /// keeps the declared regions in a synopsis cache as well or not; it finds every row that holds the key.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APathNoTableNamesReadsInTwoRoundTripsColdAndNoneWarm(bool synopsisCache)
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

    /// <summary>A chunk without the bounds of its key's values, whose arrays compare by their references.</summary>
    private static TableChunk Unbounded(TableChunk chunk) => chunk with { Keys = null };

    /// <summary>A chunk's bounds as bytes, null for none: the bounds a chunk holds are a view of its table's store.</summary>
    private static (byte[]? Least, byte[]? Greatest) Bounds(TableChunk chunk) =>
        chunk.Keys is { } keys ? (keys.Least.ToArray(), keys.Greatest.ToArray()) : (null, null);

    /// <summary>
    /// A table of contents reads back as it was written, a chunk of a table of values with the least and the greatest of
    /// its values, and an entry carries it through its bytes.
    /// </summary>
    /// <summary>
    /// A table whose chunks a writer listed apart: its entry names the list and its chunks' count, and reads back with the
    /// list not loaded, its chunks refused until it is; the list's bytes load it, the same chunks, keys and all; bytes that
    /// fail the list's checksum, or a list of another count, are refused, the table left unloaded.
    /// </summary>
    [Fact]
    public void ATableListedApartLoadsItsChunksFromItsList()
    {
        TableChunk[] chunks =
        [
            new TableChunk(new IndexSegment(4_096, 512, 6, 9), 0, 3_600_000_000, 3) { Keys = new ValueBounds([1, 1, (byte)'a'], [1, 2, (byte)'e']) },
            new TableChunk(new IndexSegment(4_608, 128, 6, 10), 3_600_000_000, 3_600_000_000, 1) { Keys = new ValueBounds([1, 1, (byte)'f'], [1, 3, (byte)'u']) },
        ];
        byte[] list = SynopsisContents.ListBytes(chunks);
        IndexSegment region = IndexSegment.Of(8_192, list, 6);
        SliceTable slices = new SliceTable(TimeUnit.Microseconds, [new TimeSlice(0, 60_000_000, 0, 4)], [1]);
        SynopsisContents contents = new SynopsisContents(
            Vorticity.Types.Serialization.DTypeFlatBuffers.Serialize(Vorticity.VortexTypes.ToDType(Call.Schema, new Vorticity.Types.DTypeArena())),
            ["vortex.primitive"],
            slices,
            [new IndexSegment(100, 20, 0, 7)],
            4_096,
            8_192 + list.Length,
            [(new SynopsisTableInfo(SynopsisTableKind.Values, SynopsisBucket.Hour, [1], [3], 4), new SynopsisContents.TableChunks(region, chunks))]);

        SynopsisContents back = SynopsisContents.From(contents.ToBytes())!;
        SynopsisContents.TableChunks listed = Assert.IsType<SynopsisContents.TableChunks>(back.Tables[0].Chunks);
        Assert.Equal(region, listed.List);
        Assert.Equal(2, listed.Count);
        Assert.False(listed.Loaded);
        Assert.Throws<System.InvalidOperationException>(() => listed[0]);

        byte[] torn = [.. list];
        torn[^1] ^= 1;
        Assert.False(listed.Load(torn));
        Assert.False(listed.Loaded);

        // A list that holds its checksum and other chunks than the entry counts is refused too.
        byte[] one = SynopsisContents.ListBytes(chunks[..1]);
        SynopsisContents.TableChunks miscounted = new SynopsisContents.TableChunks(IndexSegment.Of(8_192, one, 6), 2, listed.KeyBytes, listed.Declared);
        Assert.False(miscounted.Load(one));

        Assert.True(listed.Load(list));
        Assert.True(listed.Loaded);
        Assert.Equal(chunks.Select(Unbounded), listed.Select(Unbounded));
        Assert.Equal(chunks.Select(Bounds), listed.Select(Bounds));
    }

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

    /// <summary>
    /// A table a later writer made, of a kind this reader does not know, is skipped and counted, the contents' other tables
    /// read as before; a table of a known kind whose options are torn still refuses the contents, as it did.
    /// </summary>
    [Fact]
    public void ATableOfALaterKindIsSkippedAndTheOthersRead()
    {
        SliceTable slices = new SliceTable(TimeUnit.Microseconds, [new TimeSlice(0, 60_000_000, 0, 10)], [1]);
        SynopsisContents contents = new SynopsisContents(
            Vorticity.Types.Serialization.DTypeFlatBuffers.Serialize(Vorticity.VortexTypes.ToDType(Call.Schema, new Vorticity.Types.DTypeArena())),
            ["vortex.primitive"],
            slices,
            [new IndexSegment(100, 20, 0, 7)],
            4_096,
            8_192,
            [(new SynopsisTableInfo(SynopsisTableKind.Coarse, SynopsisBucket.Hour, [1], [3], 4), [new TableChunk(new IndexSegment(4_096, 512, 6, 9), 0, 3_600_000_000, 3)])]);
        byte[] whole = contents.ToBytes();
        byte[] later = TableMessage(new SynopsisTableInfo((SynopsisTableKind)9, SynopsisBucket.Hour, [1], [3], 4).OptionBytes());
        byte[] torn = TableMessage(new SynopsisTableInfo(SynopsisTableKind.Coarse, SynopsisBucket.Hour, [1], [3], 4).OptionBytes()[..^1]);

        long skipped = 0;
        using System.Diagnostics.Metrics.MeterListener listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, listening) =>
        {
            if (instrument.Meter.Name == VortexDiagnostics.MeterName && instrument.Name == "vortex.synopsis_tables.unknown")
            {
                listening.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref skipped, value));
        listener.Start();

        SynopsisContents back = SynopsisContents.From([.. whole, .. later])!;
        Assert.NotNull(back);
        Assert.Equal(contents.Tables.Select(t => t.Table), back.Tables.Select(t => t.Table), Same);
        Assert.Equal(contents.Tables.SelectMany(t => t.Chunks).Select(Unbounded), back.Tables.SelectMany(t => t.Chunks).Select(Unbounded));
        Assert.Equal(1, Interlocked.Read(ref skipped));

        Assert.Null(SynopsisContents.From([.. whole, .. torn]));
    }

    /// <summary>A table's message as the contents lay it out, holding its options alone.</summary>
    private static byte[] TableMessage(ReadOnlySpan<byte> options)
    {
        Vorticity.Serialization.Protobuf.ProtoWriter table = new Vorticity.Serialization.Protobuf.ProtoWriter();
        Vorticity.Serialization.Protobuf.ProtoWriter message = new Vorticity.Serialization.Protobuf.ProtoWriter();
        try
        {
            table.WriteBytes(1, options);
            message.WriteBytes(7, table.WrittenSpan);
            return message.WrittenSpan.ToArray();
        }
        finally
        {
            table.Dispose();
            message.Dispose();
        }
    }

    /// <summary>
    /// A read of an object's tables that names some fields of their rows decodes those alone, in their order and under
    /// their names, their values a whole read's: a count by no key reads a table's bucket and count, of its eight
    /// columns and more.
    /// </summary>
    [Fact]
    public async Task AReadDecodesTheFieldsItNamesAlone()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Call.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Call.At)],
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour, SliceTargetRows = 500 },
            Write = new VortexWriteOptions { Indexes = IndexPolicy.None.WithBudgetPerMille(1_000) },
        }, Ct);
        Random random = new Random(11);
        for (int part = 0; part < 8; part++)
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

        ObjectEntry? period = null;
        await foreach (TreeEntry leaf in dataset.Levels[PeriodLevels.Periods].EnumerateAsync(dataset.Pages, Ct))
        {
            period ??= ObjectEntry.FromBytes(leaf.Value.Span);
        }

        SynopsisContents contents = SynopsisContents.From(period!.Synopsis.Span)!;
        using SynopsisTableSet whole = await dataset.ReadSynopsisAsync(period, contents, null, Ct);
        using SynopsisTableSet projected = await dataset.ReadSynopsisAsync(period, contents, new FieldsRead(table => [0, table.Keys.Length + 1]), Ct);
        Assert.NotEmpty(whole.Tables);
        Assert.Equal(whole.Tables.Select(t => t.Table), projected.Tables.Select(t => t.Table));
        for (int t = 0; t < whole.Tables.Count; t++)
        {
            ReadTable full = whole.Tables[t];
            ReadTable part = projected.Tables[t];
            int count = full.Table.Keys.Length + 1;
            CanonicalNode all = whole.Arena.GetNode(full.Node);
            CanonicalNode narrowed = projected.Arena.GetNode(part.Node);
            Assert.True(full.Fields.IsAll);
            Assert.False(part.Fields.IsAll);
            Assert.True(all.DType.FieldCount > 2);
            Assert.Equal(["bucket", "count"], [narrowed.DType.GetFieldName(0), narrowed.DType.GetFieldName(1)]);
            Assert.Equal(2, narrowed.DType.FieldCount);
            Assert.Equal(
                ColumnData.Values<long>(whole.Arena, all.GetFieldIndex(0)).ToArray(), ColumnData.Values<long>(projected.Arena, narrowed.GetFieldIndex(0)).ToArray());
            Assert.Equal(
                ColumnData.Values<long>(whole.Arena, all.GetFieldIndex(count)).ToArray(), ColumnData.Values<long>(projected.Arena, narrowed.GetFieldIndex(1)).ToArray());
        }
    }

    /// <summary>A read of every chunk, of the fields <paramref name="fields"/> names of each table's rows.</summary>
    private sealed class FieldsRead(Func<SynopsisTableInfo, int[]> fields) : ISynopsisReads
    {
        public bool Reads(SynopsisTableInfo table, TableChunk chunk) => true;

        public bool ReadsTable(SynopsisTableInfo table) => true;

        public void Loaded(SynopsisContents contents)
        {
        }

        public Vorticity.Layouts.FieldMask Fields(SynopsisTableInfo table) =>
            Vorticity.Layouts.FieldMask.Subset(new Vorticity.Layouts.FieldMaskNode(fields(table), null));

        public ulong Encoded(SynopsisTableInfo table) => 0;
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
