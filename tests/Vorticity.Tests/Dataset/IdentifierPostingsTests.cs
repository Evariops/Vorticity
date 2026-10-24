// Declared identifiers: every object of a dataset, appended or written by a job, carries hashed postings
// on each identifier, required and outside the index budget, so that a lookup of one value reads the
// objects that hold it and almost no other.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class IdentifierPostingsTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Two hours and a quarter of requests, each with an id of its own, the dataset declaring the id an
    /// identifier: every object at every level, those of level 0 and the sealed hours alike, carries
    /// hashed postings on the id, under no index policy at all; a lookup of one id finds its row; and the
    /// postings of a sealed hour weigh what the ratchet holds.
    /// </summary>
    [Fact]
    public async Task EveryObjectCarriesHashedPostingsOnEachIdentifier()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Request.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Request.At)],
            Identifiers = [nameof(Request.RequestId)],
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour, SliceTargetRows = 500 },
        }, Ct);
        Random random = new Random(19);
        List<Request> appended = [];
        for (int part = 0; part < 16; part++)
        {
            Request[] rows = [.. Enumerable.Range(0, 400).Select(i => new Request(
                Noon.AddSeconds((part * 400 + i) * 1.25), $"{random.NextInt64():x16}{random.NextInt64():x16}", 200 + random.Next(5)))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Request>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
            appended.AddRange(rows);
            if (part == 0)
            {
                Assert.Equal(1, await WithPostingsAsync(dataset, 0));
            }
        }

        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }, Ct) is not null)
        {
        }

        int objects = 0;
        for (int level = 0; level < dataset.Levels.Count; level++)
        {
            objects += await WithPostingsAsync(dataset, level);
        }

        Assert.True(objects >= 3, $"only {objects} objects");

        // A lookup of one id finds its row.
        Request needle = appended[4_321];
        Assert.Equal([needle], await dataset.Scan<Request>().Where(r => r.RequestId == needle.RequestId).ToRecordsAsync(Ct).ToListAsync(Ct));

        // What the postings of a sealed hour weigh a row, the id 32 hexadecimal digits: a gap of about 12
        // bits and a block number, here always the hour's one block. A sorted run took 19.3 bytes.
        await foreach (TreeEntry leaf in dataset.Levels[PeriodLevels.Periods].EnumerateAsync(dataset.Pages, Ct))
        {
            ObjectEntry entry = ObjectEntry.FromBytes(leaf.Value.Span);
            await using ObjectLease lease = await dataset.RentAsync(entry, Ct);
            IndexDirectory directory = (await lease.File.ReadIndexDirectoryAsync(Ct))!;
            int field = lease.File.DType.IndexOfField(nameof(Request.RequestId));
            long bytes = directory.Entries
                .Where(e => e.Kind == IndexKinds.PostingsHashed && e.ColumnPath.SequenceEqual([(uint)field]))
                .Sum(e => e.Runs.Sum(r => r.Payload.Sum(p => (long)p.Length)));
            double perRow = (double)bytes / entry.Rows;
            Console.Out.Write(FormattableString.Invariant($"IDENTIFIER POSTINGS: {entry.Rows} rows, {bytes} bytes, {perRow:F2} a row, the object {entry.Bytes} bytes.\n"));
            Assert.InRange(perRow, 1, 4);
        }
    }

    /// <summary>
    /// A lookup of one id in two sealed hours at once, on a store that answers in 20 ms, with no open of
    /// either: cold, three dependent round trips, the index directories beside the chunk directories, the
    /// postings' segments that may hold the id, then the chunks of the blocks left; warm, none, the
    /// session's cache holding them all. The id's row is among those read, and no other object's block.
    /// </summary>
    [Fact]
    public async Task ALookupReadsInThreeRoundTripsColdAndTwoWarm()
    {
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);
        await using VortexSession session = VortexSession.Create(static options => options.SegmentCache = new SegmentCache(64 << 20));
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Request.Schema, new DatasetOptions
        {
            Session = session,
            ClusteringKey = [nameof(Request.At)],
            Identifiers = [nameof(Request.RequestId)],
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour, SliceTargetRows = 500 },
        }, Ct);
        Random random = new Random(23);
        List<Request> appended = [];
        for (int part = 0; part < 16; part++)
        {
            Request[] rows = [.. Enumerable.Range(0, 400).Select(i => new Request(
                Noon.AddSeconds((part * 400 + i) * 1.25), $"{random.NextInt64():x16}{random.NextInt64():x16}", 200 + random.Next(5)))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Request>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
            appended.AddRange(rows);
        }

        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }, Ct) is not null)
        {
        }

        List<ObjectEntry> hours = [];
        await foreach (TreeEntry leaf in dataset.Levels[PeriodLevels.Periods].EnumerateAsync(dataset.Pages, Ct))
        {
            hours.Add(ObjectEntry.FromBytes(leaf.Value.Span));
        }

        Assert.Equal(2, hours.Count);
        Request needle = appended[1_234];
        VortexExpr filter = VortexExpr.Parse($"RequestId = '{needle.RequestId}'");
        inner.Latency = TimeSpan.FromMilliseconds(20);

        // Each object alone, so that the steps counted are its own: a step is a request that starts with
        // the store idle, which another object's requests in flight would hide.
        List<RecordBatch>?[] cold = new List<RecordBatch>?[hours.Count];
        List<RecordBatch>?[] warm = new List<RecordBatch>?[hours.Count];
        for (int h = 0; h < hours.Count; h++)
        {
            store.Reset();
            cold[h] = await dataset.LookupAsync(hours[h], filter, null, Ct);
            long coldSteps = store.DependentSteps;
            long coldRequests = store.Requests;
            store.Reset();
            warm[h] = await dataset.LookupAsync(hours[h], filter, null, Ct);
            Console.Out.Write(FormattableString.Invariant(
                $"IDENTIFIER LOOKUP: {coldSteps} dependent steps and {coldRequests} requests cold, {store.DependentSteps} steps and {store.Requests} requests warm.\n"));

            // The hour that holds the id reads its block; the other's postings rule every block out, and
            // it reads no chunk at all.
            bool holds = cold[h]!.Count > 0;
            Assert.Equal(holds ? 3 : 2, coldSteps);
            Assert.InRange(store.DependentSteps, 0, 2);
        }

        Assert.Equal(1, Holding(cold, needle.RequestId));
        Assert.Equal(1, Holding(warm, needle.RequestId));
        Assert.Single(cold, batches => batches!.Count > 0);
    }

    /// <summary>
    /// The same lookup through the session's synopsis cache, with no cache of segments, the postings' fence tables
    /// inline or in pages: warm, the store is asked for the chunks of the blocks left alone, in one round trip in
    /// the hour that holds the id and none in the other, the postings' pages and segments coming from the disk as
    /// the directories do; and every file the cache keeps is a region of an index, none of the rows'.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALookupWarmThroughTheSynopsisCacheAsksTheStoreForItsBlocksAlone(bool paged)
    {
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vorticity-postings-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using MemoryObjectStore inner = new MemoryObjectStore();
            await using CountingObjectStore store = new CountingObjectStore(inner);
            await using VortexSession session = VortexSession.Create(options => options.SynopsisCache = new SynopsisCache(directory, 64 << 20));
            await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Request.Schema, new DatasetOptions
            {
                Session = session,
                ClusteringKey = [nameof(Request.At)],
                Identifiers = [nameof(Request.RequestId)],
                Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour, SliceTargetRows = 500 },
                Write = paged ? new VortexWriteOptions { Fences = new global::Vorticity.Writing.FenceShape(InlineFences: 1, PageBytes: 512) } : VortexWriteOptions.Default,
            }, Ct);
            // The same two hours and a quarter; in pages, enough rows that a run of an hour's postings takes more
            // segments, 16 384 entries each, than its one fence inline holds.
            Random random = new Random(29);
            List<Request> appended = [];
            int perPart = paged ? 5_000 : 400;
            double apart = 8_000.0 / (16 * perPart);
            for (int part = 0; part < 16; part++)
            {
                Request[] rows = [.. Enumerable.Range(0, perPart).Select(i => new Request(
                    Noon.AddSeconds((part * perPart + i) * apart), $"{random.NextInt64():x16}{random.NextInt64():x16}", 200 + random.Next(5)))];
                await using ObjectDraft draft = dataset.StartObject();
                await draft.Writer.WriteAsync<Request>(rows, Ct);
                await dataset.AppendAsync(draft, Ct);
                appended.AddRange(rows);
            }

            while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }, Ct) is not null)
            {
            }

            List<ObjectEntry> hours = [];
            await foreach (TreeEntry leaf in dataset.Levels[PeriodLevels.Periods].EnumerateAsync(dataset.Pages, Ct))
            {
                hours.Add(ObjectEntry.FromBytes(leaf.Value.Span));
            }

            Assert.Equal(2, hours.Count);
            Request needle = appended[2_345];
            VortexExpr filter = VortexExpr.Parse($"RequestId = '{needle.RequestId}'");
            inner.Latency = TimeSpan.FromMilliseconds(20);
            List<RecordBatch>?[] warm = new List<RecordBatch>?[hours.Count];
            for (int h = 0; h < hours.Count; h++)
            {
                store.Reset();
                List<RecordBatch>? cold = await dataset.LookupAsync(hours[h], filter, null, Ct);
                long coldSteps = store.DependentSteps;
                long coldRequests = store.Requests;
                store.Reset();
                warm[h] = await dataset.LookupAsync(hours[h], filter, null, Ct);
                Console.Out.Write(FormattableString.Invariant(
                    $"IDENTIFIER LOOKUP CACHED{(paged ? ", PAGED" : string.Empty)}: {coldSteps} dependent steps and {coldRequests} requests cold, {store.DependentSteps} steps and {store.Requests} requests warm.\n"));

                bool holds = cold!.Count > 0;
                foreach (RecordBatch batch in cold)
                {
                    batch.Dispose();
                }

                Assert.Equal(holds ? 1 : 0, store.DependentSteps);
                Assert.Equal(holds, store.Requests > 0);
                Assert.True(coldSteps > store.DependentSteps);
            }

            Assert.Equal(1, Holding(warm, needle.RequestId));

            // The files kept: the regions the contents declare, and the pages and segments of the postings, which
            // are in pages when asked; none of the rows' segments.
            HashSet<string> declared = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> data = new HashSet<string>(StringComparer.Ordinal);
            foreach (ObjectEntry hour in hours)
            {
                foreach (IndexSegment region in ObjectContents.From(hour.TableOfContents.Span)!.Declared())
                {
                    declared.Add(SynopsisCache.NameOf(hour.Uid, region));
                }

                await using ObjectLease lease = await dataset.RentAsync(hour, Ct);
                foreach (global::Vorticity.Serialization.Schemas.SegmentSpec segment in lease.File.SegmentSpecs)
                {
                    data.Add(FormattableString.Invariant($"{hour.Uid:x32}-{segment.Offset:x}-{segment.Length:x}-"));
                }

                IndexDirectory index = (await lease.File.ReadIndexDirectoryAsync(Ct))!;
                IndexEntry postings = index.Entries.Single(e => e.Kind == IndexKinds.PostingsHashed);
                Assert.Equal(paged, postings.Runs.Any(run => KeyRunOptions.IsPaged(run.OptionBytes)));
            }

            session.Options.SynopsisCache!.Flush();
            string[] files = [.. System.IO.Directory.GetFiles(directory, "*", System.IO.SearchOption.AllDirectories)
                .Select(System.IO.Path.GetFileName)
                .Where(file => file != ".lock")!];
            Assert.Contains(files, file => !declared.Contains(file!));
            Assert.DoesNotContain(files, file => data.Contains(file![..(file.LastIndexOf('-') + 1)]));
        }
        finally
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A user's id among the attributes, declared as <c>Attributes['user'].AsString()</c>, unique to its
    /// row: every sealed hour shreds the key and carries hashed postings on it in its class; a lookup of
    /// one user, with no open, takes three dependent round trips cold in the hour that holds it and two in
    /// the other, as a declared column does; and a scan finds its row.
    /// </summary>
    [Fact]
    public async Task AnIdentifierAmongTheAttributesIsFoundAsAColumnIs()
    {
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);
        await using VortexSession session = VortexSession.Create(static options => options.SegmentCache = new SegmentCache(64 << 20));
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Visit.Schema, new DatasetOptions
        {
            Session = session,
            ClusteringKey = [nameof(Visit.At)],
            Identifiers = ["Attributes['user'].AsString()"],
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour, SliceTargetRows = 500 },
        }, Ct);
        Random random = new Random(31);
        List<string> users = [];
        for (int part = 0; part < 16; part++)
        {
            Visit[] rows = [.. Enumerable.Range(0, 400).Select(i =>
            {
                string user = $"{random.NextInt64():x16}{random.NextInt64():x16}";
                users.Add(user);
                return new Visit(
                    Noon.AddSeconds((part * 400 + i) * 1.25),
                    Variant.Object(("route", Variant.From($"/r/{i % 7}")), ("status", Variant.From(200 + random.Next(4))), ("user", Variant.From(user))));
            })];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Visit>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }, Ct) is not null)
        {
        }

        List<ObjectEntry> hours = [];
        await foreach (TreeEntry leaf in dataset.Levels[PeriodLevels.Periods].EnumerateAsync(dataset.Pages, Ct))
        {
            ObjectEntry hour = ObjectEntry.FromBytes(leaf.Value.Span);
            hours.Add(hour);
            await using ObjectLease lease = await dataset.RentAsync(hour, Ct);
            IndexDirectory directory = (await lease.File.ReadIndexDirectoryAsync(Ct))!;
            Assert.Contains(directory.Entries, e => e.Kind == IndexKinds.PostingsHashed && e.VariantPath is ["user"] && e.VariantClass == Vorticity.Types.Variants.PathClass.String);
        }

        Assert.Equal(2, hours.Count);
        string needle = users[1_234];
        VortexExpr filter = VortexExpr.Parse($"Attributes['user'].AsString() = '{needle}'");
        inner.Latency = TimeSpan.FromMilliseconds(20);
        int holding = 0;
        foreach (ObjectEntry hour in hours)
        {
            store.Reset();
            List<RecordBatch> cold = (await dataset.LookupAsync(hour, filter, null, Ct))!;
            long coldSteps = store.DependentSteps;
            Console.Out.Write(FormattableString.Invariant($"PATH IDENTIFIER LOOKUP: {coldSteps} dependent steps and {store.Requests} requests cold, {cold.Count} batches.\n"));
            bool holds = cold.Count > 0;
            holding += holds ? 1 : 0;
            Assert.Equal(holds ? 3 : 2, coldSteps);
            foreach (RecordBatch batch in cold)
            {
                batch.Dispose();
            }
        }

        Assert.Equal(1, holding);
        inner.Latency = TimeSpan.Zero;
        Assert.Equal(1, await dataset.Scan<Visit>().Where(r => r.Attributes["user"].AsString() == needle).CountAsync(Ct));
    }

    [Theory]
    [InlineData("Status")]
    [InlineData("Attributes['user']")]
    [InlineData("Attributes['user']['id'].AsString()")]
    [InlineData("Attributes['user'].AsDouble()")]
    [InlineData("Nope['user'].AsString()")]
    public async Task AnIdentifierNamesAColumnOrAKeyReadAsAStringOrAnInteger(string identifier)
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        ArgumentException refused = await Assert.ThrowsAsync<ArgumentException>(async () => await VortexDataset.CreateAsync(store, Visit.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Visit.At)],
            Identifiers = [identifier],
        }, Ct));
        Assert.Contains("Attributes['user'].AsString()", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>The rows of <paramref name="lookups"/> whose id is <paramref name="id"/>, the batches let go.</summary>
    private static int Holding(List<RecordBatch>?[] lookups, string id)
    {
        int found = 0;
        foreach (List<RecordBatch>? batches in lookups)
        {
            foreach (RecordBatch batch in batches!)
            {
                using (batch)
                {
                    int ids = batch.Arena.GetNode(batch.RootIndex).GetFieldIndex(1);
                    for (int row = 0; row < batch.RowCount; row++)
                    {
                        found += System.Text.Encoding.UTF8.GetString(ColumnData.Bytes(batch.Arena, ids, row)) == id ? 1 : 0;
                    }
                }
            }
        }

        return found;
    }

    /// <summary>
    /// A declared identifier that admits nulls, as a trace id untraced records lack: appends whose every row lacks it are
    /// written, and so are the jobs that merge them with traced rows. Before and after compaction, the rows of one trace
    /// come back, and a count that leaves the trace out is the rows', from the synopses and from the rows alike.
    /// </summary>
    [Fact]
    public async Task AnAppendWhoseIdentifierIsNullInEveryRowIsWritten()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Logged.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Logged.At)],
            Identifiers = [nameof(Logged.TraceId)],
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour, SliceTargetRows = 500 },
        }, Ct);
        List<Logged> appended = [];
        for (int part = 0; part < 6; part++)
        {
            // Every other append untraced, every row of it.
            Logged[] rows = [.. Enumerable.Range(0, 400).Select(i => new Logged(
                Noon.AddSeconds((part * 400 + i) * 1.25), 200 + (i % 5), part % 2 == 0 ? null : $"t{part}-{i / 10:D3}"))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Logged>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
            appended.AddRange(rows);
        }

        const string Trace = "t3-007";
        await AssertTraceAsync();
        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }, Ct) is not null)
        {
        }

        await AssertTraceAsync();

        async Task AssertTraceAsync()
        {
            List<Logged> found = await dataset.Scan<Logged>().Where(r => r.TraceId == Trace).ToRecordsAsync(Ct).ToListAsync(Ct);
            Assert.Equal(appended.Where(r => r.TraceId == Trace).OrderBy(r => r.At), found.OrderBy(r => r.At));
            long left = appended.Count(r => r.TraceId != Trace);
            foreach (bool synopses in new[] { true, false })
            {
                Assert.Equal(left, await dataset.Scan<Logged>().With(new ScanOptions { UseSynopses = synopses })
                    .Where(r => r.TraceId != Trace || r.TraceId == null).CountAsync(Ct));
            }
        }
    }

    /// <summary>
    /// An identifier whose column a change of the schema renames, then drops: the appends that follow are written, the
    /// column carries no postings under its new name until that name is added as an identifier, and every id is found in
    /// the objects of before and after the rename; once the column is dropped, appends are written still.
    /// </summary>
    [Fact]
    public async Task AnIdentifierWhoseColumnIsRenamedOrDroppedLeavesTheWritesAlone()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Request.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Request.At)],
            Identifiers = [nameof(Request.RequestId)],
            LookupFooterBytes = 0,
        }, Ct);
        await using (ObjectDraft draft = dataset.StartObject())
        {
            await draft.Writer.WriteAsync<Request>([.. Enumerable.Range(0, 100).Select(i => new Request(Noon.AddSeconds(i), $"r{i:D3}", 200))], Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        await dataset.EvolveSchemaAsync(Renamed.Schema, new Dictionary<string, string> { [nameof(Renamed.Id)] = nameof(Request.RequestId) }, Ct);
        Renamed[] after = [.. Enumerable.Range(100, 100).Select(i => new Renamed(Noon.AddSeconds(i), $"r{i:D3}", 200))];
        await using (ObjectDraft draft = dataset.StartObject())
        {
            await draft.Writer.WriteAsync<Renamed>(after[..50], Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        await dataset.AddIdentifiersAsync([nameof(Renamed.Id)], Ct);
        await using (ObjectDraft draft = dataset.StartObject())
        {
            await draft.Writer.WriteAsync<Renamed>(after[50..], Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        List<(bool OnRequestId, bool OnId)> postings = [];
        await foreach (TreeEntry leaf in dataset.Levels[0].EnumerateAsync(dataset.Pages, Ct))
        {
            ObjectEntry entry = ObjectEntry.FromBytes(leaf.Value.Span);
            postings.Add((await PostingsAsync(dataset, entry, nameof(Request.RequestId)), await PostingsAsync(dataset, entry, nameof(Renamed.Id))));
        }

        Assert.Equal([(true, false), (false, false), (false, true)], postings);
        foreach (string id in new[] { "r007", "r120", "r170" })
        {
            Assert.Equal([id], (await dataset.Scan<Renamed>().Where(r => r.Id == id).ToRecordsAsync(Ct).ToListAsync(Ct)).Select(r => r.Id));
        }

        await dataset.EvolveSchemaAsync(Slim.Schema, cancellationToken: Ct);
        await using (ObjectDraft draft = dataset.StartObject())
        {
            await draft.Writer.WriteAsync<Slim>([new Slim(Noon.AddSeconds(300), 404)], Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        Assert.Equal(201, await dataset.Scan<Slim>().CountAsync(Ct));
    }

    /// <summary>The objects of <paramref name="level"/>, each checked to carry hashed postings on the request id; how many.</summary>
    private static async Task<int> WithPostingsAsync(VortexDataset dataset, int level)
    {
        int objects = 0;
        await foreach (TreeEntry leaf in dataset.Levels[level].EnumerateAsync(dataset.Pages, Ct))
        {
            await using ObjectLease lease = await dataset.RentAsync(ObjectEntry.FromBytes(leaf.Value.Span), Ct);
            IndexDirectory directory = (await lease.File.ReadIndexDirectoryAsync(Ct))!;
            int field = lease.File.DType.IndexOfField(nameof(Request.RequestId));
            Assert.Contains(directory.Entries, e => e.Kind == IndexKinds.PostingsHashed && e.ColumnPath.SequenceEqual([(uint)field]));
            objects++;
        }

        return objects;
    }

    [VortexRecord]
    public partial record struct Request(DateTimeOffset At, string RequestId, long Status);

    [VortexRecord]
    public partial record struct Logged(DateTimeOffset At, long Status, string? TraceId);

    [VortexRecord]
    public partial record struct Visit(DateTimeOffset At, Variant Attributes);

    [VortexRecord]
    public partial record struct Renamed(DateTimeOffset At, string Id, long Status);

    [VortexRecord]
    public partial record struct Slim(DateTimeOffset At, long Status);
}
