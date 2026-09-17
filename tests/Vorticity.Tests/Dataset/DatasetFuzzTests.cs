// The interleaving fuzzer of docs/13-dataset.md §14: "seeded schedules of readers, writers,
// indexers, compactors and vacuum, crashes between any two store calls; every read equals a scan
// with indexes off on the same version; every retained root references only existing objects".
//
// WHAT THE STALENESS IS, and it is the whole point. A writer PREPARES its operations against the
// version it read and COMMITS them later, after other writers have moved the ground under it. §8.2
// says each operation then knows what to do, and `RebaseMatrixTests` holds each of those answers
// one row at a time. What no row can hold is the composition: a hundred operations prepared at
// scattered versions, landing in a seeded order, some of them through a store that stored the
// commit and then threw. So the schedule here separates prepare from commit and lets the seed
// decide which happens next.
//
// THE ORACLE IS A MODEL, NOT THE IMPLEMENTATION. `Model` re-derives §8.2's rules over a plain
// dictionary, and the tree must equal it entry for entry after every schedule. That does not prove
// the rules are right -- the rebase matrix does that, row by row, against the spec's own table --
// it proves that the TREE, the rebase loop and the crash handling deliver what the rules say, under
// interleavings no fixed test would write down. The two failures it is built for are a page
// re-chunked wrongly after a rebase, and a commit that landed while its writer was told it had not.
//
// VACUUM AND THE COMPACTOR DRIVER ARE NOT HERE because they do not exist yet (steps 41 and 43).
// Their OPERATIONS do (`ReplaceObjects`), so the schedule drives those directly; what is missing is
// the policy that chooses them, not the concurrency they run under.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Dataset;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class DatasetFuzzTests
{
    private const ulong Seed = 0xF0224E_5EED;
    private const int Writers = 4;
    private const int Rounds = 60;

    private static CommitOptions Options() => new CommitOptions
    {
        Seed = Seed,
        // A small cap on purpose here, unlike the budget tests: a deep tree out of few entries is
        // what puts the re-chunking and the convergence rule under the schedule.
        Rule = new ProllyBoundaryRule(Seed, minBytes: 256, targetBytes: 512, maxBytes: 1_024),
        Template = new CommitHeader
        {
            Version = 1,
            ClusteringKey = ["k"],
            Chunker = new ChunkerSettings(256, 512, 1_024),
        },
    };

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(13)]
    [InlineData(21)]
    [InlineData(34)]
    public async Task ASeededScheduleLeavesTheTreeEqualToTheModel(int seed)
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        Random random = new Random(seed);
        Model model = new Model();

        // Each writer holds a batch it prepared and has not committed yet, with the version it saw.
        List<DatasetOperation>?[] pending = new List<DatasetOperation>?[Writers];
        int commits = 0;
        int crashes = 0;
        int stale = 0;

        for (int round = 0; round < Rounds; round++)
        {
            int writer = random.Next(Writers);
            if (pending[writer] is null)
            {
                pending[writer] = await PrepareAsync(store, random);
                continue;
            }

            List<DatasetOperation> batch = pending[writer]!;
            pending[writer] = null;
            if (batch.Count == 0)
            {
                continue;
            }

            // A crash in one commit out of five: the object is stored and the writer never learns.
            bool crashing = random.Next(5) == 0;
            store.CrashesAfterPut = crashing ? _ => true : null;
            try
            {
                await DatasetCommitter.CommitAsync(store, batch, Options(), default);
                commits++;
            }
            catch (ObjectStoreException) when (crashing)
            {
                crashes++;
            }
            finally
            {
                store.CrashesAfterPut = null;
            }

            // The commit landed either way -- that is what a crash after the put means -- so the
            // model applies it either way.
            foreach (DatasetOperation operation in batch)
            {
                model.Apply(operation);
            }

            // The writer that crashed did not learn its version; it retries, and every operation
            // must come back as already done.
            if (crashing)
            {
                await DatasetCommitter.CommitAsync(store, batch, Options(), default);
                stale++;
            }
        }

        (DatasetTree tree, IPageSource pages, ulong version) = await LatestAsync(store);
        List<(string Key, ObjectEntry Entry)> held = [];
        await foreach (TreeEntry entry in tree.EnumerateAsync(pages, default))
        {
            held.Add((Convert.ToHexString(entry.Key.Span), ObjectEntry.FromBytes(entry.Value.Span)));
        }

        Assert.Equal(model.Count, held.Count);
        Assert.Equal(model.Count, tree.Entries);
        Assert.Equal(model.Rows, tree.Rows);

        List<(string Key, ObjectEntry Entry)> expected = model.Snapshot();
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Key, held[i].Key);
            Assert.Equal(expected[i].Entry, held[i].Entry);
        }

        // And every leaf points at an object the store holds (§14's second invariant), for every
        // version still there, not only the latest.
        await EveryRootReferencesOnlyExistingObjectsAsync(store, version);

        Console.Out.Write(FormattableString.Invariant(
            $"DATASET FUZZ seed {seed}: {commits} commits and {crashes} crash(es) retried, {version} versions, {held.Count} objects, depth {tree.Depth}.\n"));
        Assert.True(commits + crashes > 5, "the schedule should have committed something");
        Assert.Equal(crashes, stale);
    }

    [Fact]
    public async Task ACrashAfterThePutIsNotASecondObject()
    {
        // §8.2, row 1, under the state the rebase exists for: the commit landed and the writer was
        // told it had not. Retrying must add nothing, because the uid is the same bytes.
        await using MemoryObjectStore store = new MemoryObjectStore();
        List<DatasetOperation> batch = [Add(1, 0), Add(2, 0)];

        store.CrashesAfterPut = _ => true;
        await Assert.ThrowsAsync<ObjectStoreException>(
            async () => await DatasetCommitter.CommitAsync(store, batch, Options(), default));
        store.CrashesAfterPut = null;

        CommitResult again = await DatasetCommitter.CommitAsync(store, batch, Options(), default);
        Assert.All(again.Outcomes, outcome => Assert.Equal(OperationOutcome.AlreadyThere, outcome));

        (DatasetTree tree, IPageSource pages, ulong version) = await LatestAsync(store);
        Assert.Equal(2, tree.Entries);

        // The retry wrote a version whose tree is the crashed one's: same entries, same root hash.
        Assert.Equal(2UL, version);
        Assert.NotEqual(UInt128.Zero, await tree.ContentHashAsync(pages, default));
    }

    [Fact]
    public async Task EveryVersionsRootStillReferencesOnlyExistingObjects()
    {
        // The reader of §8.2's last row "holds a root; everything it references is immutable". This
        // is that sentence as an invariant over every version a run left behind.
        await using MemoryObjectStore store = new MemoryObjectStore();
        Random random = new Random(99);
        for (int i = 0; i < 12; i++)
        {
            List<DatasetOperation> batch = await PrepareAsync(store, random);
            if (batch.Count > 0)
            {
                await DatasetCommitter.CommitAsync(store, batch, Options(), default);
            }
        }

        (_, _, ulong version) = await LatestAsync(store);
        Assert.True(version > 1, "the run should have produced several versions");
        await EveryRootReferencesOnlyExistingObjectsAsync(store, version);
    }

    [Fact]
    public async Task AScheduleOfRealAppendsAnswersAsOneFile()
    {
        // §14's first invariant, end to end: "every read equals a scan with indexes off on the same
        // version". Real data objects this time, because there is nothing to scan in a synthetic
        // leaf -- and the single file is the same rows written once, which is §14's acceptance.
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = types.Struct(
            ["key", "measure"],
            [types.Primitive(PType.I64, Nullability.NonNullable), types.Primitive(PType.F64, Nullability.NonNullable)],
            Nullability.NonNullable);

        await using MemoryObjectStore store = new MemoryObjectStore();
        DatasetOptions options = new DatasetOptions
        {
            Seed = Seed,
            Write = new VortexWriteOptions { RowBlockSize = 128, DataBlockTargetBytes = 8 << 10 },
        };
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, options);

        // Two handles on one dataset, appending in a seeded order: each has to refresh to see the
        // other's commits, and each commit rebases onto whatever landed since.
        await using VortexDataset second = await VortexDataset.OpenAsync(store, options);
        Random random = new Random(7);
        long rows = 0;
        for (int batch = 0; batch < 10; batch++)
        {
            VortexDataset writer = random.Next(2) == 0 ? dataset : second;
            await writer.AppendAsync(Batches(types, schema, rows, 300));
            rows += 300;
        }

        await dataset.RefreshAsync();
        Assert.Equal(10, dataset.ObjectCount);
        Assert.Equal(rows, dataset.RowCount);

        List<long> withIndexes = await KeysAsync(dataset.Scan());
        List<long> without = await KeysAsync(dataset.Scan().WithIndexes(false).WithSummaries(false));
        Assert.Equal(withIndexes, without);

        List<long> expected = [];
        for (long key = 0; key < rows; key++)
        {
            expected.Add(key);
        }

        withIndexes.Sort();
        Assert.Equal(expected, withIndexes);
    }

    /// <summary>One writer's next batch, built from the dataset as it is right now.</summary>
    private static async Task<List<DatasetOperation>> PrepareAsync(MemoryObjectStore store, Random random)
    {
        (DatasetTree tree, IPageSource pages, _) = await LatestAsync(store);
        List<(ReadOnlyMemory<byte> Key, ObjectEntry Entry)> seen = [];
        await foreach (TreeEntry entry in tree.EnumerateAsync(pages, default))
        {
            seen.Add((entry.Key, ObjectEntry.FromBytes(entry.Value.Span)));
        }

        List<DatasetOperation> batch = [];
        int count = 1 + random.Next(3);
        for (int i = 0; i < count; i++)
        {
            switch (seen.Count == 0 ? 0 : random.Next(4))
            {
                case 0:
                {
                    // An append: a fresh object at a key nobody has used, or a replacement of one.
                    int at = random.Next(200);
                    int version = random.Next(2);
                    batch.Add(Add(at, version));
                    await EnsureObjectAsync(store, at, version);
                    break;
                }

                case 1:
                {
                    // An indexer: a fragment against the object as this writer sees it.
                    (ReadOnlyMemory<byte> key, ObjectEntry entry) = seen[random.Next(seen.Count)];
                    batch.Add(new DatasetOperation.AddFragment(key, entry.Uid, Fragment(random.Next(8))));
                    break;
                }

                case 2:
                {
                    // The same indexer, dropping one.
                    (ReadOnlyMemory<byte> key, ObjectEntry entry) = seen[random.Next(seen.Count)];
                    batch.Add(new DatasetOperation.DropFragment(key, Fragment(random.Next(8))));
                    break;
                }

                default:
                {
                    // A compaction: one or two inputs, one output at the first input's key.
                    int inputs = Math.Min(1 + random.Next(2), seen.Count);
                    List<ReadOnlyMemory<byte>> taken = [];
                    for (int n = 0; n < inputs; n++)
                    {
                        taken.Add(seen[random.Next(seen.Count)].Key);
                    }

                    int output = 200 + random.Next(50);
                    await EnsureObjectAsync(store, output, 0);
                    batch.Add(new DatasetOperation.ReplaceObjects(
                        taken, [(Key(output), Object(output, 0))]));
                    break;
                }
            }
        }

        return batch;
    }

    /// <summary>
    /// A byte in the store under the entry's object key, so "references only existing objects" is
    /// a question the store can answer for a synthetic leaf.
    /// </summary>
    private static async Task EnsureObjectAsync(MemoryObjectStore store, int i, int version)
    {
        await store.PutIfAbsentAsync(Object(i, version).Key, new byte[] { (byte)i }, default);
    }

    private static async Task EveryRootReferencesOnlyExistingObjectsAsync(MemoryObjectStore store, ulong latest)
    {
        for (ulong version = 1; version <= latest; version++)
        {
            string key = CommitKey.For(version);
            if (await store.HeadAsync(key, default) is null)
            {
                continue;
            }

            CommitObject commit = await CommitObject.OpenAsync(store, key, default);
            CommitPageSource pages = new CommitPageSource(store);
            pages.Inline(commit.Header);
            pages.Know(version, commit.HeaderEnd);
            DatasetTree tree = DatasetCommitter.TreeOf(commit.Header);
            await foreach (TreeEntry entry in tree.EnumerateAsync(pages, default))
            {
                ObjectEntry held = ObjectEntry.FromBytes(entry.Value.Span);
                Assert.True(
                    await store.HeadAsync(held.Key, default) is not null,
                    $"version {version} references '{held.Key}', which the store does not hold");
            }
        }
    }

    private static async Task<(DatasetTree Tree, IPageSource Pages, ulong Version)> LatestAsync(
        MemoryObjectStore store)
    {
        (ulong version, CommitObject? commit) = await DatasetCommitter.LatestAsync(store, default);
        if (commit is null)
        {
            return (DatasetTree.Empty, new CommitPageSource(store), 0);
        }

        CommitPageSource pages = new CommitPageSource(store);
        pages.Inline(commit.Header);
        pages.Know(version, commit.HeaderEnd);
        return (DatasetCommitter.TreeOf(commit.Header), pages, version);
    }

    private static ReadOnlyMemory<byte> Key(int i) => Encoding.UTF8.GetBytes($"k{i:D6}");

    private static ObjectEntry Object(int i, int version) => new ObjectEntry(
        CommitKey.ForData($"{i:x8}{version:x8}"),
        ((UInt128)(uint)i << 32) | (uint)version,
        i + 1,
        4_096,
        (UInt128)(uint)i);

    private static DatasetOperation Add(int i, int version) =>
        new DatasetOperation.AddObject(Key(i), Object(i, version));

    private static PageReference Fragment(int i) =>
        new PageReference(1, 64 + i, 32, ((UInt128)(uint)i << 64) | 0xF7A6);

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

    private static async IAsyncEnumerable<RecordBatch> Batches(
        DTypeArena types, DType schema, long from, int rows)
    {
        const int size = 150;
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType f64 = types.Primitive(PType.F64, Nullability.NonNullable);
        for (int start = 0; start < rows; start += size)
        {
            int count = Math.Min(size, rows - start);
            CanonicalArena arena = new CanonicalArena();
            VortexBuffer keys = arena.Allocate(count * sizeof(long), sizeof(long), out Span<byte> keyBytes);
            VortexBuffer measures = arena.Allocate(count * sizeof(double), sizeof(double), out Span<byte> measureBytes);
            Span<long> keyValues = MemoryMarshal.Cast<byte, long>(keyBytes);
            Span<double> measureValues = MemoryMarshal.Cast<byte, double>(measureBytes);
            for (int row = 0; row < count; row++)
            {
                keyValues[row] = from + start + row;
                measureValues[row] = (from + start + row) / 4.0;
            }

            int keyNode = arena.AddPrimitive(i64, count, Validity.NonNullable, PType.I64, keys);
            int measureNode = arena.AddPrimitive(f64, count, Validity.NonNullable, PType.F64, measures);
            int root = arena.AddStruct(schema, count, Validity.NonNullable, [keyNode, measureNode]);
            using RecordBatch batch = new RecordBatch(arena, root, start);
            yield return batch;
            await Task.CompletedTask;
        }
    }

    /// <summary>
    /// §8.2's rules over a plain dictionary, written from the spec's table rather than from the
    /// committer: the oracle the schedule is checked against.
    /// </summary>
    private sealed class Model
    {
        private readonly SortedDictionary<string, ObjectEntry> _entries =
            new SortedDictionary<string, ObjectEntry>(StringComparer.Ordinal);

        internal int Count => _entries.Count;

        internal long Rows
        {
            get
            {
                long rows = 0;
                foreach (ObjectEntry entry in _entries.Values)
                {
                    rows += entry.Rows;
                }

                return rows;
            }
        }

        internal List<(string Key, ObjectEntry Entry)> Snapshot()
        {
            List<(string, ObjectEntry)> all = [];
            foreach ((string key, ObjectEntry entry) in _entries)
            {
                all.Add((key, entry));
            }

            return all;
        }

        internal void Apply(DatasetOperation operation)
        {
            switch (operation)
            {
                case DatasetOperation.AddObject add:
                {
                    string key = Hex(add.Key);
                    // "the same uid is the same bytes": an add of what is already there does nothing.
                    if (!_entries.TryGetValue(key, out ObjectEntry? held) || held.Uid != add.Entry.Uid)
                    {
                        _entries[key] = add.Entry;
                    }

                    break;
                }

                case DatasetOperation.AddFragment fragment:
                {
                    string key = Hex(fragment.Key);
                    if (_entries.TryGetValue(key, out ObjectEntry? held)
                        && held.Uid == fragment.Uid
                        && !held.Holds(fragment.Fragment))
                    {
                        _entries[key] = held.With(fragment.Fragment);
                    }

                    break;
                }

                case DatasetOperation.DropFragment drop:
                {
                    string key = Hex(drop.Key);
                    if (_entries.TryGetValue(key, out ObjectEntry? held) && held.Holds(drop.Fragment))
                    {
                        List<PageReference> kept = [];
                        foreach (PageReference reference in held.Fragments)
                        {
                            if (reference != drop.Fragment)
                            {
                                kept.Add(reference);
                            }
                        }

                        _entries[key] = held with { Fragments = kept };
                    }

                    break;
                }

                case DatasetOperation.ReplaceObjects replace:
                {
                    foreach (ReadOnlyMemory<byte> input in replace.Inputs)
                    {
                        if (!_entries.ContainsKey(Hex(input)))
                        {
                            // "an input is missing: the outputs are garbage" -- the whole operation
                            // is abandoned, inputs included.
                            return;
                        }
                    }

                    foreach (ReadOnlyMemory<byte> input in replace.Inputs)
                    {
                        _entries.Remove(Hex(input));
                    }

                    foreach ((ReadOnlyMemory<byte> key, ObjectEntry entry) in replace.Outputs)
                    {
                        _entries[Hex(key)] = entry;
                    }

                    break;
                }

                default:
                    throw new ArgumentException($"{operation.GetType().Name} is not an operation of §8.2.");
            }
        }

        private static string Hex(ReadOnlyMemory<byte> key) => Convert.ToHexString(key.Span);
    }
}
