// Verify and tampering - docs/13-dataset.md §10 and the rows §14 asks of it: "an object replaced out
// of band at equal size -- nothing sees it but verify, which says so; a page, a root, a fragment
// torn at every byte; a fragment of another object, refused".
//
// TWO QUESTIONS PER TEAR, ALWAYS BOTH. Does a reader give a wrong answer -- it may fail, it may
// answer right, it may not answer wrong -- and does verify name the tear. A tear readers cannot see
// at all (a stored page whose inlined twin they read instead, a fragment they leave out) is exactly
// the case where the second question is the only one that finds it.
//
// THE TAMPERING IS OUT OF BAND: the object is deleted and created again under its key with the
// bytes changed, which is the only way bytes change in a store this library writes (§11).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Vorticity.Scan;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class DatasetVerifyTests
{
    private const int Appends = 8;
    private const int PerAppend = 50;

    [Fact]
    public async Task ADatasetAsWrittenVerifies()
    {
        (MemoryObjectStore store, VortexDataset dataset) = await BuildAsync(indexed: true);
        await using (store)
        await using (dataset)
        {
            DatasetVerification verified = await dataset.VerifyAsync();
            Assert.True(verified.Holds, string.Join("\n", verified.Problems));
            Assert.Equal(Appends, verified.Objects);
            Assert.Equal(1, verified.Fragments);
            Assert.True(verified.Pages > 1);
            Assert.True(verified.Commits > 1);
            Assert.Equal(0, verified.Unhashed);
        }
    }

    [Fact]
    public async Task AnObjectReplacedAtEqualSizeIsSeenByVerifyAlone()
    {
        (MemoryObjectStore store, VortexDataset dataset) = await BuildAsync(indexed: false);
        await using (store)
        await using (dataset)
        {
            ObjectEntry victim = (await EntriesAsync(dataset))[3];
            byte[] bytes = await ReadAllAsync(store, victim.Key);

            // A byte of the data region, well away from the tail a reader parses: the length, the
            // identity and the footer are all as they were.
            bytes[bytes.Length / 4] ^= 0x01;
            await ReplaceAsync(store, victim.Key, bytes);

            // Everything a reader checks still holds: the length, and the identity in the tail it
            // parses. No reader computes the content hash (§7), so no reader can tell.
            Assert.Equal(victim.Bytes, (await store.HeadAsync(victim.Key, default))!.Value.Length);
            await using (Vorticity.File.VortexFile reopened = await Vorticity.File.VortexFile.OpenAsync(
                new ObjectSegmentSource(store, victim.Key), new Vorticity.File.VortexOpenOptions(), default))
            {
                Assert.Equal(victim.Uid, VortexDataset.Identity(reopened));
                Assert.Equal(victim.Rows, reopened.RowCount);
            }

            DatasetVerification verified = await dataset.VerifyAsync();
            Assert.False(verified.Holds);
            string problem = Assert.Single(verified.Problems);
            Assert.Contains(victim.Key, problem, StringComparison.Ordinal);
            Assert.Contains("hashes to", problem, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task APageTornAtEveryByteNeverAnswersWrongAndVerifyNamesIt()
    {
        (MemoryObjectStore store, VortexDataset dataset) = await BuildAsync(indexed: false);
        await using (store)
        await using (dataset)
        {
            // The first leaf: written by an early commit and referenced from there ever since (§3).
            // Every header since inlines it too -- a tree this small fits the header's 192 KiB -- so
            // a reader takes the inlined copy and the stored one is verify's alone, which disabling
            // verify's store-only reads proves: this test fails with the root's. A reader that does
            // read a stored page checks it against its reference (CommitPageSource), and may refuse.
            IReadOnlyList<InternalEntry> root = TreePage.ReadInternal(
                await dataset.Pages.ReadPageAsync(dataset.Levels[0].Root, default));
            PageReference leaf = root[0].Child;
            Assert.NotEqual(dataset.Version, leaf.Version);
            _ = await TearEveryByteAsync(store, dataset, leaf, "the page of version");
        }
    }

    [Fact]
    public async Task ARootTornAtEveryByteIsInvisibleToReadersAndNamedByVerify()
    {
        // The root lies in the latest commit object AND in its header, inlined; a reader takes the
        // inlined copy, which the header's checksum covers, and never meets the stored one.
        (MemoryObjectStore store, VortexDataset dataset) = await BuildAsync(indexed: false);
        await using (store)
        await using (dataset)
        {
            PageReference root = dataset.Levels[0].Root;
            Assert.Equal(dataset.Version, root.Version);
            Assert.Equal(0, await TearEveryByteAsync(store, dataset, root, "the page of version"));
        }
    }

    [Fact]
    public async Task AFragmentTornAtEveryByteIsLeftOutAndNamedByVerify()
    {
        (MemoryObjectStore store, VortexDataset dataset) = await BuildAsync(indexed: true);
        await using (store)
        await using (dataset)
        {
            PageReference fragment = (await EntriesAsync(dataset)).Single(entry => entry.Fragments.Count > 0).Fragments[0];
            // Left out, not refused: the object answers without its index (§7's rule for a region
            // that fails), so no tear stops a reader.
            Assert.Equal(0, await TearEveryByteAsync(store, dataset, fragment, "the fragment in version"));
        }
    }

    [Fact]
    public async Task AFragmentOfAnotherObjectIsRefusedAndNamedByVerify()
    {
        (MemoryObjectStore store, VortexDataset dataset) = await BuildAsync(indexed: true);
        await using (store)
        await using (dataset)
        {
            List<PositionedObject> objects = await ObjectsAsync(dataset);
            PositionedObject indexed = objects.Single(held => held.Entry.Fragments.Count > 0);
            PositionedObject other = objects.First(held => held.Entry.Fragments.Count == 0);
            ReadOnlyMemory<byte> theirs = await dataset.ReadFragmentAsync(indexed.Entry.Fragments[0], default);

            // A caller that lies: the operation names the other object, its uid included, and carries
            // bytes built against the indexed one. The commit cannot tell; the binding can (§7).
            await dataset.ApplyAsync([new DatasetOperation.AddFragment(other.TreeKey, other.Entry.Uid, theirs)]);

            await using (VortexDataset reader = await VortexDataset.OpenAsync(store, Options()))
            {
                Assert.Equal(Expected(), (await KeysAsync(reader.Scan())).Order());
            }

            DatasetVerification verified = await dataset.VerifyAsync();
            string problem = Assert.Single(verified.Problems);
            Assert.Contains(other.Entry.Key, problem, StringComparison.Ordinal);
            Assert.Contains("refused", problem, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AVerifySinceAnotherVersionChecksOnlyWhatChanged()
    {
        (MemoryObjectStore store, VortexDataset dataset) = await BuildAsync(indexed: false);
        await using (store)
        await using (dataset)
        {
            ulong before = dataset.Version;
            DatasetVerification whole = await dataset.VerifyAsync();
            Assert.True(whole.Holds);

            DTypeArena types = new DTypeArena();
            await dataset.AppendAsync(Of(types, Schema(types), Appends * PerAppend, PerAppend));
            DatasetVerification since = await dataset.VerifyAsync(since: before);
            Assert.True(since.Holds, string.Join("\n", since.Problems));
            Assert.Equal(1, since.Objects);
            Assert.True(since.Pages < whole.Pages, $"{since.Pages} pages against {whole.Pages}");

            // What the older version vouched for is trusted, and what is new is not: a tear in an
            // old object is the full verify's to find, a tear in the new one the incremental's.
            List<ObjectEntry> entries = await EntriesAsync(dataset);
            ObjectEntry old = entries[0];
            ObjectEntry fresh = entries[^1];
            await FlipAsync(store, old.Key, 0.25);
            await FlipAsync(store, fresh.Key, 0.25);
            DatasetVerification incremental = await dataset.VerifyAsync(since: before);
            Assert.Contains(fresh.Key, Assert.Single(incremental.Problems), StringComparison.Ordinal);
            Assert.Equal(2, (await dataset.VerifyAsync()).Problems.Count);
        }
    }

    // ------------------------------------------------------------------------------ the tear

    /// <summary>
    /// Flips each byte of a region of a commit object in turn: a fresh reader must fail or answer
    /// right, and verify must name the region.
    /// </summary>
    /// <returns>How many of the tears a reader refused rather than answered.</returns>
    private static async Task<int> TearEveryByteAsync(
        MemoryObjectStore store, VortexDataset dataset, PageReference region, string named)
    {
        int refused = 0;
        string key = CommitKey.For(region.Version);
        byte[] original = await ReadAllAsync(store, key);
        long start = await CommitObject.PagesStartAsync(store, key, default) + region.Offset;
        List<long> expected = Expected();
        for (int at = 0; at < region.Length; at++)
        {
            byte[] torn = (byte[])original.Clone();
            torn[start + at] ^= 0x5A;
            await ReplaceAsync(store, key, torn);

            try
            {
                await using VortexDataset reader = await VortexDataset.OpenAsync(store, Options());
                Assert.Equal(expected, (await KeysAsync(reader.Scan())).Order());
            }
            catch (CommitFormatException)
            {
                // A reader that refuses is a reader that did not answer wrong.
                refused++;
            }

            DatasetVerification verified = await dataset.VerifyAsync();
            Assert.False(verified.Holds, $"byte {at} of {region.Length}");
            Assert.Contains(verified.Problems, problem => problem.Contains(named, StringComparison.Ordinal));
        }

        await ReplaceAsync(store, key, original);
        Assert.True((await dataset.VerifyAsync()).Holds);
        return refused;
    }

    // ------------------------------------------------------------------------------ helpers

    /// <summary>
    /// Eight appends in key order on a tree of small pages, so the first leaves stay in the commits
    /// that wrote them; one object indexed by a fragment when asked.
    /// </summary>
    private static async Task<(MemoryObjectStore Store, VortexDataset Dataset)> BuildAsync(bool indexed)
    {
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);
        MemoryObjectStore store = new MemoryObjectStore();
        VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Options());
        for (int i = 0; i < Appends; i++)
        {
            await dataset.AppendAsync(Of(types, schema, i * PerAppend, PerAppend));
            if (indexed && i == 1)
            {
                PositionedObject target = (await ObjectsAsync(dataset))[1];
                IndexingResult result = await DatasetIndexer.IndexAsync(
                    dataset, target, WritePolicy.None.For("measure", IndexPolicy.Bloom(falsePositivePpm: 1_000)),
                    options: new VortexWriteOptions { IndexBudgetPerMille = 1_000_000 });
                Assert.Equal(OperationOutcome.Applied, result.Outcome);
            }
        }

        Assert.True(dataset.Depth > 1);
        return (store, dataset);
    }

    private static DatasetOptions Options() => new DatasetOptions
    {
        Seed = 0x7E81_F1ED,
        ClusteringKey = ["key"],
        Rule = new FillBoundaryRule(400),
        Write = new VortexWriteOptions { RowBlockSize = 64, DataBlockTargetBytes = 512 },
    };

    private static List<long> Expected() => [.. Enumerable.Range(0, Appends * PerAppend).Select(i => (long)i)];

    private static async Task<byte[]> ReadAllAsync(MemoryObjectStore store, string key)
    {
        long length = (await store.HeadAsync(key, default))!.Value.Length;
        using ObjectRange range = await store.GetRangeAsync(key, 0, (int)length, default);
        return range.Bytes.ToArray();
    }

    private static async Task ReplaceAsync(MemoryObjectStore store, string key, byte[] bytes)
    {
        await store.DeleteAsync(key, default);
        Assert.Equal(PutOutcome.Created, await store.PutIfAbsentAsync(key, bytes, default));
    }

    private static async Task FlipAsync(MemoryObjectStore store, string key, double where)
    {
        byte[] bytes = await ReadAllAsync(store, key);
        bytes[(int)(bytes.Length * where)] ^= 0x01;
        await ReplaceAsync(store, key, bytes);
    }

    private static DType Schema(DTypeArena types) => types.Struct(
        ["key", "measure"],
        [types.Primitive(PType.I64, Nullability.NonNullable), types.Primitive(PType.F64, Nullability.NonNullable)],
        Nullability.NonNullable);

    private static async Task<List<ObjectEntry>> EntriesAsync(VortexDataset dataset)
    {
        List<ObjectEntry> entries = [];
        await foreach (ObjectEntry entry in dataset.ObjectsAsync())
        {
            entries.Add(entry);
        }

        return entries;
    }

    private static async Task<List<PositionedObject>> ObjectsAsync(VortexDataset dataset)
    {
        List<PositionedObject> objects = [];
        await foreach (PositionedObject held in dataset.Scan().ObjectsAsync())
        {
            objects.Add(held);
        }

        return objects;
    }

    private static async Task<List<long>> KeysAsync(DatasetScanBuilder scan)
    {
        List<long> keys = [];
        await foreach (RecordBatch batch in scan.ExecuteAsync())
        {
            ReadOnlySpan<long> values = batch.Column(0).AsPrimitive<long>().Values;
            for (int row = 0; row < batch.RowCount; row++)
            {
                keys.Add(values[row]);
            }
        }

        return keys;
    }

    private static async IAsyncEnumerable<RecordBatch> Of(DTypeArena types, DType schema, long from, int count)
    {
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType f64 = types.Primitive(PType.F64, Nullability.NonNullable);
        CanonicalArena arena = new CanonicalArena();
        VortexBuffer keyBuffer = arena.Allocate(count * sizeof(long), sizeof(long), out Span<byte> keyBytes);
        VortexBuffer measures = arena.Allocate(count * sizeof(double), sizeof(double), out Span<byte> measureBytes);
        Span<long> keyValues = MemoryMarshal.Cast<byte, long>(keyBytes);
        Span<double> measureValues = MemoryMarshal.Cast<byte, double>(measureBytes);
        for (int row = 0; row < count; row++)
        {
            keyValues[row] = from + row;
            measureValues[row] = (from + row) / 4.0;
        }

        int keyNode = arena.AddPrimitive(i64, count, Validity.NonNullable, PType.I64, keyBuffer);
        int measureNode = arena.AddPrimitive(f64, count, Validity.NonNullable, PType.F64, measures);
        int root = arena.AddStruct(schema, count, Validity.NonNullable, [keyNode, measureNode]);
        using RecordBatch batch = new RecordBatch(arena, root, 0);
        yield return batch;
        await Task.CompletedTask;
    }
}
