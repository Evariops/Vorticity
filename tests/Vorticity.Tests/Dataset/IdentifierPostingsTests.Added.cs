// Identifiers added to a dataset that exists (VortexDataset.AddIdentifiersAsync): the objects written from the version
// that adds them carry their postings, those written before are read as they lie and answer all the same, and two
// writers that add one each keep both.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class IdentifierPostingsTests
{
    /// <summary>
    /// The hour of noon compacted before the id is declared an identifier, the rest after: the objects of before carry no
    /// postings and are read as rows, every object written since carries them and is looked up; a scan for an id of either
    /// finds its row, and a count that leaves an id out counts every other row, from the synopses and from the rows.
    /// </summary>
    [Fact]
    public async Task AnIdentifierAddedLaterIndexesTheObjectsWrittenSince()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Request.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Request.At)],
            Clustering = new ClusteringOptions { Value = ClusteringValue.Truncate(CalendarUnit.Hour) }, ContentsObjectBytes = 0, SliceTargetRows = 500,
            LookupFooterBytes = 0,
        }, Ct);
        Random random = new Random(37);
        List<Request> appended = [];

        // Nine parts to a quarter past one, compacted, then seven to past two, which rewrite the hour past one.
        await AppendRequestsAsync(dataset, random, appended, 0, 9);
        HashSet<string> before = [];
        for (int level = 0; level < dataset.Levels.Count; level++)
        {
            await foreach (TreeEntry leaf in dataset.Levels[level].EnumerateAsync(dataset.Pages, Ct))
            {
                before.Add(ObjectEntry.FromBytes(leaf.Value.Span).Key);
            }
        }

        ulong version = dataset.Version;
        ulong added = await dataset.AddIdentifiersAsync([nameof(Request.RequestId)], Ct);
        Assert.Equal(version + 1, added);
        Assert.Equal([nameof(Request.RequestId)], dataset.Snapshot.Header.Identifiers);
        Assert.Equal(DatasetFeatures.Identifiers, dataset.Snapshot.Header.Features & DatasetFeatures.Identifiers);
        Assert.Equal(added, await dataset.AddIdentifiersAsync([nameof(Request.RequestId)], Ct));
        await AppendRequestsAsync(dataset, random, appended, 9, 16);

        // The objects of before, the hour of noon among them, have no postings and no lookup; every object written since
        // has postings.
        ObjectEntry? noon = null;
        int indexed = 0;
        for (int level = 0; level < dataset.Levels.Count; level++)
        {
            await foreach (TreeEntry leaf in dataset.Levels[level].EnumerateAsync(dataset.Pages, Ct))
            {
                ObjectEntry entry = ObjectEntry.FromBytes(leaf.Value.Span);
                bool old = before.Contains(entry.Key);
                noon ??= old ? entry : null;
                Assert.True(old != await PostingsAsync(dataset, entry, nameof(Request.RequestId)), $"an object of level {level} {(old ? "with" : "without")} postings");
                indexed += !old && entry.Contents is { DirectoryRegion: not null } && !entry.HasDeletions ? 1 : 0;
            }
        }

        Assert.NotNull(noon);
        Assert.True(indexed >= 2, $"{indexed} objects indexed since");
        Request early = appended[1_234];
        Request late = appended[5_678];
        Assert.Null(await dataset.LookupAsync(noon, VortexExpr.Parse($"RequestId = '{early.RequestId}'"), null, Ct));

        // Each id found, that of before among the rows of its hour, read whole, that of after by the postings.
        foreach (Request needle in new[] { early, late })
        {
            DatasetScanCounters metrics = new DatasetScanCounters();
            Assert.Equal([needle.RequestId], await IdsAsync(dataset.ScanBuilder().Where(VortexExpr.Parse($"RequestId = '{needle.RequestId}'")).WithMetrics(metrics)));
            Assert.Equal(indexed, metrics.ObjectsLookedUp);
            Assert.Equal([needle], await dataset.Scan<Request>().Where(r => r.RequestId == needle.RequestId).ToRecordsAsync(Ct).ToListAsync(Ct));
            foreach (bool synopses in new[] { true, false })
            {
                Assert.Equal(appended.Count - 1, await dataset.Scan<Request>().With(new ScanOptions { UseSynopses = synopses })
                    .Where(r => r.RequestId != needle.RequestId).CountAsync(Ct));
            }
        }
    }

    /// <summary>
    /// Two handles that each add an identifier, the second from the version before the first's: both are kept, the
    /// second joined to the first. One that adds an identifier to a schema another writer changed first is refused, and so
    /// is a name that is no column of the schema nor a key read as a string or an integer.
    /// </summary>
    [Fact]
    public async Task IdentifiersAddedByTwoWritersAreBothKept()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset first = await VortexDataset.CreateAsync(store, Request.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Request.At)],
        }, Ct);
        await using VortexDataset second = await VortexDataset.OpenAsync(store, new DatasetOptions(), Ct);
        await first.AddIdentifiersAsync([nameof(Request.RequestId)], Ct);
        ulong both = await second.AddIdentifiersAsync([nameof(Request.Status), nameof(Request.RequestId)], Ct);
        Assert.Equal([nameof(Request.RequestId), nameof(Request.Status)], second.Snapshot.Header.Identifiers);
        Assert.Equal(both, await first.RefreshAsync(Ct));
        Assert.Equal([nameof(Request.RequestId), nameof(Request.Status)], first.Snapshot.Schema.Identifiers);

        // An object appended by the handle that added none carries the postings of both.
        Request[] rows = [.. Enumerable.Range(0, 100).Select(i => new Request(Noon.AddSeconds(i), $"r{i:D3}", 200 + (i % 5)))];
        await using (ObjectDraft draft = first.StartObject())
        {
            await draft.Writer.WriteAsync<Request>(rows, Ct);
            await first.AppendAsync(draft, Ct);
        }

        await foreach (TreeEntry leaf in first.Levels[0].EnumerateAsync(first.Pages, Ct))
        {
            await using ObjectLease lease = await first.RentAsync(ObjectEntry.FromBytes(leaf.Value.Span), Ct);
            IndexDirectory directory = (await lease.File.ReadIndexDirectoryAsync(Ct))!;
            foreach (string column in new[] { nameof(Request.RequestId), nameof(Request.Status) })
            {
                int field = lease.File.DType.IndexOfField(column);
                Assert.Contains(directory.Entries, e => e.Kind == IndexKinds.PostingsHashed && e.ColumnPath.SequenceEqual([(uint)field]));
            }
        }

        // A schema another writer changed first.
        await second.EvolveSchemaAsync(VortexSchema.Create([.. Request.Schema.FieldArray, new VortexField("Note", VortexType.Utf8.Nullable)]), cancellationToken: Ct);
        await using VortexDataset stale = await VortexDataset.OpenAsync(store, new DatasetOptions(), Ct);
        await second.EvolveSchemaAsync(
            VortexSchema.Create([.. Request.Schema.FieldArray, new VortexField("Note", VortexType.Utf8.Nullable), new VortexField("Route", VortexType.Utf8.Nullable)]),
            cancellationToken: Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await stale.AddIdentifiersAsync(["Note"], Ct));
        ArgumentException refused = await Assert.ThrowsAsync<ArgumentException>(async () => await second.AddIdentifiersAsync(["Nope"], Ct));
        Assert.Equal("identifiers", refused.ParamName);
    }

    /// <summary>
    /// Parts <paramref name="from"/> to <paramref name="to"/> of 400 requests a second and a quarter apart from noon, each
    /// with an id of its own, appended to <paramref name="dataset"/> and compacted until nothing is due.
    /// </summary>
    private static async Task AppendRequestsAsync(VortexDataset dataset, Random random, List<Request> appended, int from, int to)
    {
        for (int part = from; part < to; part++)
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
    }

    /// <summary>Whether the object <paramref name="entry"/> names carries hashed postings on <paramref name="column"/>.</summary>
    private static async Task<bool> PostingsAsync(VortexDataset dataset, ObjectEntry entry, string column)
    {
        await using ObjectLease lease = await dataset.RentAsync(entry, Ct);
        IndexDirectory? directory = await lease.File.ReadIndexDirectoryAsync(Ct);
        int field = lease.File.DType.IndexOfField(column);
        return directory is not null && directory.Entries.Any(e => e.Kind == IndexKinds.PostingsHashed && e.ColumnPath.SequenceEqual([(uint)field]));
    }
}
