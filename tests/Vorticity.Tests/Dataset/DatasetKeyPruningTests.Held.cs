// The key's pruning over the sparse indexes a question reads: an index is searched only where it may refute a granule, and
// none is kept past the search that read it, its bytes staying in the session's caches.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetKeyPruningTests
{
    /// <summary>
    /// A window on the hour, past the indexed prefix, searches no object's sparse index, for no granule it could refute; the
    /// service searches each object's once, its ranges asked again found without a read. Both return the rows LINQ keeps.
    /// </summary>
    [Fact]
    public async Task AnIndexIsSearchedOnlyWhereItMayRefuteAGranule()
    {
        Random random = new Random(63);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                SortKey = [LayoutValue.Column(nameof(Row.Service)), LayoutValue.Column(nameof(Row.At)).Truncate(CalendarUnit.Hour)],
                IndexedPrefix = 1,
            },
            ContentsObjectBytes = 0,

            // Priced as a remote store, over columns of many chunks, so that a sparse index spares more than its read.
            CostProfile = CostProfile.Remote,
            ChunkBytes = 64 << 10,
            Write = new VortexWriteOptions { BlockRows = 1_024 },
        }, Ct);
        Row[] all = [.. Enumerable.Range(0, 200_000).Select(i =>
            new Row(0, $"svc-{random.Next(0, 3):D2}", Start.AddTicks((long)(random.NextDouble() * TimeSpan.TicksPerDay) / 10 * 10), i, random.NextDouble()))];
        await using (ObjectDraft draft = dataset.StartObject())
        {
            await draft.Writer.WriteAsync<Row>(all, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        DateTime ten = Start.AddHours(10);
        DateTime eleven = Start.AddHours(11);
        VortexExpr window = Expr.And(Ge(nameof(Row.At), Micros(ten)), Lt(nameof(Row.At), Micros(eleven)));
        VortexExpr service = Eq(nameof(Row.Service), FilterLiteral.From("svc-01"));
        DatasetSchema schema = dataset.Snapshot.Schema;
        KeyPruning byWindow = KeyPruning.Of(schema, window)!;
        KeyPruning byService = KeyPruning.Of(schema, service)!;
        DatasetScanBuilder builder = dataset.ScanBuilder();
        int objects = 0;
        await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(Ct))
        {
            Assert.True(builder.ReadsKeyIndex(held), $"'{held.Entry.Key}': its index is not worth reading");
            ReadOnlyMemory<byte> first = VortexDataset.OrderOf(held.TreeKey);
            Assert.Null(await builder.KeptGranulesAsync(held, byWindow, first, read: true, Ct));
            (List<(int First, int End)> Ranges, GranuleGrid Grid)? kept = await builder.KeptGranulesAsync(held, byService, first, read: true, Ct);
            Assert.NotNull(kept);
            Assert.Equal(kept.Value.Ranges, (await builder.KeptGranulesAsync(held, byService, first, read: false, Ct))!.Value.Ranges);
            objects++;
        }

        Assert.True(objects > 0);
        Assert.Equal(0, byWindow.Searches);
        Assert.Equal(objects, byService.Searches);
        Assert.Equal(all.Count(row => row.At >= ten && row.At < eleven), await dataset.ScanBuilder().Where(window).CountAsync(Ct));
        Assert.Equal(all.Count(static row => row.Service == "svc-01"), await dataset.ScanBuilder().Where(service).CountAsync(Ct));
    }

    /// <summary>
    /// Objects whose sparse index lies past the tail an open reads, in a session with a disk cache and no segment cache: the
    /// first read of each index asks the store; a second read on the handle, and one on a handle opened afterwards, ask it
    /// nothing, the disk serving the table and the pages, and give the same keys. Nothing of an index stays in memory past
    /// its read: a new question's pruning has kept nothing of any object.
    /// </summary>
    [Fact]
    public async Task AnIndexIsReadAgainFromTheDiskCacheNotKeptInMemory()
    {
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vorticity-key-index-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            await IndexesFromTheDiskAsync(directory);
        }
        finally
        {
            if (System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static async Task IndexesFromTheDiskAsync(string directory)
    {
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);
        await using VortexSession session = VortexSession.Create(options =>
        {
            options.SegmentCache = new SegmentCache(0);
            options.SynopsisCache = new SynopsisCache(directory, 64 << 20);
        });
        DatasetOptions options = new DatasetOptions
        {
            Session = session,
            Layout = new DatasetLayout
            {
                SortKey = [LayoutValue.Column(nameof(Row.Service)), LayoutValue.Column(nameof(Row.Sequence))],
            },
            ContentsObjectBytes = 0,
            Write = new VortexWriteOptions { BlockRows = 64 },
        };

        // Granules of 64 rows, each its own service of a long name: an index of some 3 000 keys of 50 bytes, past the tail.
        Row[] all = [.. Enumerable.Range(0, 3_000 * 64).Select(static i =>
            new Row(0, $"service-with-a-rather-long-name-{i / 64:D8}", Start, i, 0))];
        await using (VortexDataset writing = await VortexDataset.CreateAsync(store, Row.Schema, options, Ct))
        {
            await using ObjectDraft draft = writing.StartObject();
            await draft.Writer.WriteAsync<Row>(all, Ct);
            await writing.AppendAsync(draft, Ct);
        }

        byte[]? keysRead = null;
        for (int handle = 0; handle < 2; handle++)
        {
            await using VortexDataset dataset = await VortexDataset.OpenAsync(store, options, Ct);
            List<PositionedObject> objects = [.. await dataset.ScanBuilder().ObjectsAsync(Ct).ToListAsync(Ct)];
            PositionedObject held = Assert.Single(objects);
            ObjectContents contents = held.Entry.Contents!;
            Assert.False(PathCosts.InOpen(held.Entry, contents.KeyIndex!.Value), "the index lies in the tail an open reads");
            for (int read = 0; read < 2; read++)
            {
                store.Reset();
                using GranuleKeys keys = (await dataset.KeyIndexAsync(held.Entry, contents, Ct))!;
                byte[] decoded = [.. Enumerable.Range(0, keys.Granules).SelectMany(granule =>
                {
                    GranuleKeys.KeyReader reader = default;
                    byte[] key = reader.Key(keys, granule).ToArray();
                    reader.Dispose();
                    return key;
                })];
                if (keysRead is null)
                {
                    Assert.True(store.Requests > 0, "the first read asked the store nothing");
                    keysRead = decoded;
                }
                else
                {
                    Assert.True(store.Requests == 0, $"read {read} of handle {handle} asked the store {store.Requests} times");
                    Assert.Equal(keysRead, decoded);
                }
            }

            // A question's pruning keeps the ranges it found for that question alone.
            KeyPruning pruning = KeyPruning.Of(dataset.Snapshot.Schema, Eq(nameof(Row.Service), FilterLiteral.From("service-with-a-rather-long-name-00000042")))!;
            Assert.False(pruning.TryKept(held.Entry.Uid, default, out _, out _));
            Assert.Equal(64, await dataset.ScanBuilder().Where(Eq(nameof(Row.Service), FilterLiteral.From("service-with-a-rather-long-name-00000042"))).CountAsync(Ct));
        }
    }
}
