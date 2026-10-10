// The counting matrix of the read budget, over a dataset instead of over one file.
//
// WHAT THE DESIGN CLAIMS is about CONSTANTS, not about speed: a clustering-key point
// lookup costs at most R requests and B bytes, constants of the design, asserted equal across data
// objects of 1 GiB, 10 GiB and 100 GiB (sparse, only the bytes read matter) and across datasets of
// 1, 10³ and 10⁶ objects (synthetic leaves). Two axes, and they fail differently: the SIZE axis
// catches anything on the read path that scales with an object's length, which ReadBudgetTests
// holds for one file and which a dataset must not reintroduce; the COUNT axis catches a descent
// that walks a level instead of descending it, which no single-file test can see at all.
//
// AND A THIRD INVARIANT A COUNT CANNOT PROVE. "The dependent requests are the critical path […]
// which no total of requests can prove, since parallel requests hide in a total." Ten requests
// issued together cost one round trip and ten issued in sequence cost ten, and both are ten. So the
// third test puts a latency on the store and reads a clock: it is the only assertion here that is
// about time, and it is about time because the thing it measures is a DEPTH.
//
// THE LEAVES ARE SYNTHETIC ON THE COUNT AXIS, and they must be: a million real data
// objects is a million files, and the thing under test is the tree above them, not the files below.
// An entry is the bytes an entry is; the tree cannot tell the difference and neither can the budget.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.Scanning;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class DatasetBudgetTests
{
    private const ulong Seed = 0xB0D9E7_5EED;

    /// <summary>
    /// The chunker at its own defaults — 64 / 128 / 256 KiB — because that is the parameterisation
    /// the design states its fan-out, depth and request counts for. A smaller cap makes a
    /// deeper tree out of fewer entries, which is convenient and measures something else: a
    /// fan-out the design does not have. The entries below are sized to the design's own worked
    /// assumption instead, entries of about 200 bytes and pages of about 128 KiB, which is what
    /// makes its fan-out of about 650 the thing under test.
    /// </summary>
    private static CommitOptions Options() => new CommitOptions
    {
        Seed = Seed,
        Template = new CommitHeader
        {
            Version = 1,
            ClusteringKey = ["k"],
            Chunker = new ChunkerSettings(
                ProllyBoundaryRule.DefaultMinBytes,
                ProllyBoundaryRule.DefaultTargetBytes,
                ProllyBoundaryRule.DefaultMaxBytes),
        },
    };

    private static ReadOnlyMemory<byte> Key(int i) => Encoding.UTF8.GetBytes($"k{i:D9}");

    /// <summary>Four summarised columns, enough to bring an entry to about 200 bytes.</summary>
    private static ObjectSummaries Summaries(int i) => ObjectSummaries.From(
    [
        new ColumnSummary("k", FilterLiteral.From((long)i), true, FilterLiteral.From(i + 999L), true, true, 0, true),
        new ColumnSummary("amount", FilterLiteral.From(i * 1.5), true, FilterLiteral.From(i * 2.5), true, true, 3, true),
        new ColumnSummary("tenant", FilterLiteral.From($"t{i:D6}"), true, FilterLiteral.From($"t{i + 40:D6}"), true, true, 0, true),
        new ColumnSummary("region", FilterLiteral.From($"r{i % 97:D3}"), true, FilterLiteral.From($"r{i % 97:D3}"), true, true, 0, true),
    ]);

    private static DatasetOperation Add(int i) => new DatasetOperation.AddObject(
        Key(i),
        new ObjectEntry(
            CommitKey.ForData($"{i:x8}"), (UInt128)(uint)i + 1, 1_000, 1 << 20, (UInt128)(uint)i, Summaries(i)));

    [Theory]
    [InlineData(1)]
    [InlineData(1_000)]
    [InlineData(100_000)]
    [InlineData(1_000_000)]
    public async Task APointLookupCostsTheSameWhateverTheObjectCount(int objects)
    {
        // The point-lookup constant, on the axis a single-file test cannot see. The numbers are
        // printed rather than pinned one by one: what the assertion holds is that they do not
        // GROW, which is the claim, and a ceiling nobody derived would be a number somebody chose.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);

        List<DatasetOperation> operations = new List<DatasetOperation>(objects);
        for (int i = 0; i < objects; i++)
        {
            operations.Add(Add(i));
        }

        CommitResult built = await DatasetCommitter.CommitAsync(store, operations, Options(), ct);
        Assert.Equal(objects, built.Tree.Entries);

        // Cold: nothing is known, so this is the list and the header read plus the descent.
        store.Reset();
        (ulong version, CommitObject? commit) = await DatasetCommitter.LatestAsync(store, ct);
        CommitObject found = Assert.IsType<CommitObject>(commit);
        CommitPageSource pages = new CommitPageSource(store);
        pages.Open(version, found);
        DatasetTree tree = DatasetCommitter.TreeOf(found.Header);

        TreeEntry? hit = await tree.FindAsync(Key(objects / 2), pages, ct);
        Assert.True(hit.HasValue, "the key was committed and should be found");
        Assert.Equal(Key(objects / 2).ToArray(), hit.Value.Key.ToArray());

        long requests = store.Requests;
        long steps = store.DependentSteps;
        long bytes = store.BytesRead;
        Console.Out.Write(FormattableString.Invariant(
            $"DATASET BUDGET: {objects} objects, depth {tree.Depth}: a cold point lookup cost {requests} requests in {steps} dependent steps and {bytes} bytes, {pages.Reads} page read(s).\n"));

        // A commit object the read that opens it holds whole inlines none of the pages it wrote:
        // they lie in that read already, and the descent asks the store for none of them.
        if (found.Trailer is not null)
        {
            Assert.DoesNotContain(found.Header.Levels, level => level.Inlined.Any(page => page.Reference.Version == version));
            Assert.Equal(0, pages.Reads);
        }

        // THE CONSTANT: the list, the header, and at most two pages below what the header inlined
        // (0 up to ~650 objects, 1 up to ~400 000, 2 up to ~280 million). Four is the
        // ceiling the design states; a descent that walked a level would blow past it at 100 000.
        Assert.InRange(requests, 2, 4);
        Assert.InRange(steps, 2, 4);
        Assert.Equal(1, store.CountOf(ObjectOperation.List));

        // Warm, the same key on the same handle: the pages are immutable, so nothing is re-read.
        // A DIFFERENT key is a different page and is not warm, which is the honest half of
        // pages being cacheable forever: the cache holds what was read, not what could be.
        store.Reset();
        Assert.True((await tree.FindAsync(Key(objects / 2), pages, ct)).HasValue);
        Assert.Equal(0, store.Requests);
    }

    [Fact]
    public async Task AWalkPrefetchesItsSiblingsAWindowAtATime()
    {
        // An in-order walk of a level's tree, children prefetched in parallel. A cold walk over
        // every leaf of a two-level tree: the leaves are read a window ahead, so the dependent
        // steps are about one per window, not one per leaf -- while the requests, which a total
        // counts, are still one per leaf.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);
        List<DatasetOperation> operations = new List<DatasetOperation>(40_000);
        for (int i = 0; i < 40_000; i++)
        {
            operations.Add(Add(i));
        }

        await DatasetCommitter.CommitAsync(store, operations, Options(), ct);

        // A latency, because a store that answers synchronously never has two requests in flight
        // and every request is its own step whatever the walk does -- the same reason the next test
        // injects one.
        inner.Latency = TimeSpan.FromMilliseconds(2);
        store.Reset();
        (ulong version, CommitObject? commit) = await DatasetCommitter.LatestAsync(store, ct);
        CommitObject found = Assert.IsType<CommitObject>(commit);
        CommitPageSource pages = new CommitPageSource(store) { Reading = version };
        pages.Know(version, found.HeaderEnd);
        DatasetTree tree = DatasetCommitter.TreeOf(found.Header);
        Assert.Equal(2, tree.Depth);

        long entries = 0;
        await foreach (TreeEntry entry in tree.EnumerateAsync(pages, ct))
        {
            entries++;
        }

        Assert.Equal(40_000, entries);
        long leaves = pages.Reads - 1;
        long steps = store.DependentSteps;
        Console.Out.Write(FormattableString.Invariant(
            $"DATASET WALK: {leaves} leaves read in {store.Requests} requests and {steps} dependent steps, a window of {DatasetTree.PrefetchWindow}.\n"));
        Assert.True(leaves > 3 * DatasetTree.PrefetchWindow, $"only {leaves} leaves: the tree is too small to show a window");
        Assert.True(steps <= 4 + (leaves / DatasetTree.PrefetchWindow * 2), $"{steps} dependent steps for {leaves} leaves");
        Assert.True(steps < leaves / 2, $"{steps} dependent steps for {leaves} leaves is a sequential walk");
    }

    [Fact]
    public async Task TheDependentStepsAreTheCriticalPathAndNotTheRequestCount()
    {
        // The third invariant: the in-memory store injects a latency λ and no CPU cost, and a
        // cold clustering-key lookup completes within D × λ, D its count of dependent requests,
        // which no total of requests can prove, since parallel requests hide in a total. So this
        // one reads a clock.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);

        List<DatasetOperation> operations = new List<DatasetOperation>(2_000);
        for (int i = 0; i < 2_000; i++)
        {
            operations.Add(Add(i));
        }

        await DatasetCommitter.CommitAsync(store, operations, Options(), ct);

        TimeSpan latency = TimeSpan.FromMilliseconds(20);
        inner.Latency = latency;
        store.Reset();

        Stopwatch clock = Stopwatch.StartNew();
        (ulong version, CommitObject? commit) = await DatasetCommitter.LatestAsync(store, ct);
        CommitObject found = Assert.IsType<CommitObject>(commit);
        CommitPageSource pages = new CommitPageSource(store);
        pages.Open(version, found);
        DatasetTree tree = DatasetCommitter.TreeOf(found.Header);
        Assert.NotNull(await tree.FindAsync(Key(1_000), pages, ct));
        clock.Stop();

        long steps = store.DependentSteps;
        Console.Out.Write(FormattableString.Invariant(
            $"DATASET LATENCY: a cold lookup over 2 000 objects took {clock.ElapsedMilliseconds} ms of {latency.TotalMilliseconds} ms per request, in {steps} dependent steps for {store.Requests} requests.\n"));

        // A LOWER BOUND AND NO UPPER ONE, deliberately. The lower bound is the claim: the lookup
        // waited for its round trips, one after another, so the clock saw the DEPTH and not the
        // count. An upper bound would be a wall-clock ceiling in the test suite, which this
        // repository keeps out of CI on purpose — the suite runs its classes in parallel, and a
        // ceiling that a busy machine breaks is a flake, not a measurement. What an upper bound
        // would buy is proof that parallel requests do not add up, and a descent of three
        // sequential requests has none to hide.
        Assert.True(
            clock.Elapsed.TotalMilliseconds >= (steps - 1) * latency.TotalMilliseconds,
            $"{steps} dependent steps at {latency.TotalMilliseconds} ms each cannot take {clock.ElapsedMilliseconds} ms");

        // And warm costs no round trip at all: every page the cold lookup read stays cached.
        store.Reset();
        Assert.True((await tree.FindAsync(Key(1_000), pages, ct)).HasValue);
        Assert.Equal(0, store.Requests);
    }

    [Fact]
    public async Task ACommitCostsThreeRequestsAndOneMoreWhenItOutgrowsTheInlining()
    {
        // A commit costs three dependent requests: the List, the read of N's header, the
        // creation. An iteration costs depth + 2: the header of N+1, the touched leaves, the
        // creation. BOTH ARE TRUE, and what decides which is the header's inlining: while the
        // header carries the pages the commit will touch, the touched leaves cost nothing and the
        // total is three. Once the tree outgrows the 192 KiB the header inlines, the touched leaf
        // is a read of its own and the total is four. That is the whole story of this number, and
        // it is why the assertion below is a range with an explanation rather than a constant.
        CancellationToken ct = TestContext.Current.CancellationToken;
        int[] sizes = [1, 1_000, 100_000];
        long[] steps = new long[sizes.Length];
        long[] written = new long[sizes.Length];
        for (int at = 0; at < sizes.Length; at++)
        {
            await using MemoryObjectStore inner = new MemoryObjectStore();
            await using CountingObjectStore store = new CountingObjectStore(inner);
            List<DatasetOperation> operations = new List<DatasetOperation>(sizes[at]);
            for (int i = 0; i < sizes[at]; i++)
            {
                operations.Add(Add(i));
            }

            await DatasetCommitter.CommitAsync(store, operations, Options(), ct);

            store.Reset();
            CommitResult one = await DatasetCommitter.CommitAsync(store, [Add(sizes[at] + 1)], Options(), ct);
            steps[at] = store.DependentSteps;
            written[at] = store.BytesWritten;
            Assert.Equal(sizes[at] + 1, one.Tree.Entries);
            Assert.Equal(1, one.Attempts);
        }

        Console.Out.Write(FormattableString.Invariant(
            $"DATASET COMMIT COST: one object added to {sizes[0]}, {sizes[1]} and {sizes[2]} objects cost {steps[0]}, {steps[1]} and {steps[2]} dependent steps and wrote {written[0]}, {written[1]} and {written[2]} bytes.\n"));

        // Three while the inlining covers the path, four when it does not, and never a function of
        // the object count: a commit that walked the objects would be off by orders of magnitude
        // here, not by one.
        Assert.Equal(3, steps[0]);
        Assert.Equal(3, steps[1]);
        Assert.InRange(steps[2], 3, 4);
    }

    [Fact]
    public async Task AnEqualityOnANonClusteringColumnCostsAConstantPlusOneTermPerUnrefutedObject()
    {
        // Second invariant: an equality lookup through a Bloom or sorted run on a non-clustering
        // column costs at most R′ requests plus a term proportional to the objects the summaries
        // could not refute, with the constant part asserted across object counts. REAL objects
        // here, because the term is what opening an object and asking its Bloom costs, which no
        // synthetic leaf has. Two layouts of the same values: DISJOINT, where every object's
        // `measure` range is its own and the summaries refute all but the one that holds the key;
        // and INTERLEAVED, where every range spans the whole column, the summaries refute nothing,
        // and each object's Bloom does. The difference between the two, per refuted object, is the
        // term; it must be one number whatever the count.
        //
        // WHAT THIS DOES NOT SHOW: these objects are smaller than an open's first read, so the term
        // is the open's cost and says nothing about the bytes a Bloom saves inside an object. That
        // is the single-file half of the matrix, ReadBudgetTests, on sparse files of 1.31 and
        // 13.1 GiB.
        Decoders.EnsureRegistered();
        int[] counts = [4, 16, 48];
        long[] constant = new long[counts.Length];
        double[] term = new double[counts.Length];
        for (int at = 0; at < counts.Length; at++)
        {
            (long disjoint, long opened) = await EqualityLookupAsync(counts[at], interleaved: false);
            (long interleaved, long all) = await EqualityLookupAsync(counts[at], interleaved: true);
            Assert.Equal(1, opened);
            Assert.Equal(counts[at], all);
            constant[at] = disjoint;
            term[at] = (double)(interleaved - disjoint) / (counts[at] - 1);
        }

        Console.Out.Write(FormattableString.Invariant(
            $"DATASET BUDGET, INVARIANT 2: over {counts[0]}, {counts[1]} and {counts[2]} objects an equality on a Bloom-indexed column cost {constant[0]}, {constant[1]} and {constant[2]} requests when the summaries refute the rest, and {term[0]:F2}, {term[1]:F2} and {term[2]:F2} requests per object they cannot refute.\n"));

        Assert.Equal(constant[0], constant[1]);
        Assert.Equal(constant[0], constant[2]);
        Assert.True(term[0] >= 1, "an object the summaries cannot refute costs at least its open");
        Assert.Equal(term[0], term[1]);
        Assert.Equal(term[0], term[2]);
    }

    /// <summary>
    /// A cold equality on <c>measure</c> over <paramref name="objects"/> real objects, and what it
    /// cost: the requests after the dataset is open, and the objects the summaries left to open.
    /// </summary>
    private static async Task<(long Requests, long Opened)> EqualityLookupAsync(int objects, bool interleaved)
    {
        const int rows = 256;
        DTypeArena types = new DTypeArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType schema = types.Struct(["key", "measure"], [i64, i64], Nullability.NonNullable);
        DatasetOptions options = new DatasetOptions
        {
            Seed = Seed,
            ClusteringKey = ["key"],
            Write = new VortexWriteOptions
            {
                RowBlockSize = 128,
                WritePolicy = WritePolicy.None
                    .For("key", IndexSpec.SortedRuns.AsRequired())
                    .For("measure", IndexSpec.Bloom(falsePositivePpm: 100)),
            },
        };

        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);
        await using (VortexDataset writer = await VortexDataset.CreateAsync(store, schema, options))
        {
            for (int o = 0; o < objects; o++)
            {
                long[] keys = new long[rows];
                long[] measures = new long[rows];
                for (int r = 0; r < rows; r++)
                {
                    keys[r] = ((long)o * rows) + r;
                    measures[r] = 2L * (interleaved ? ((long)r * objects) + o : ((long)o * rows) + r);
                }

                await writer.AppendAsync(OneBatch(types, schema, keys, measures));
            }
        }

        // The key is in object `objects / 2`, row 100, in both layouts; nothing else holds it.
        long sought = 2L * (interleaved ? (100L * objects) + (objects / 2) : ((long)(objects / 2) * rows) + 100);
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, options);
        store.Reset();
        DatasetScanCounters metrics = new DatasetScanCounters();
        long found = await dataset.ScanBuilder()
            .Where(Expr.Eq(Expr.Field("measure"), Expr.Literal(FilterLiteral.From(sought))))
            .WithMetrics(metrics)
            .CountAsync();
        Assert.Equal(1, found);
        return (store.Requests, metrics.ObjectsOpened);
    }

    private static async IAsyncEnumerable<RecordBatch> OneBatch(
        DTypeArena types, DType schema, long[] keys, long[] measures)
    {
        await Task.Yield();
        CanonicalArena arena = new CanonicalArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        int key = arena.AddPrimitive(i64, keys.Length, Validity.NonNullable, PType.I64, Buffer(arena, keys));
        int measure = arena.AddPrimitive(i64, measures.Length, Validity.NonNullable, PType.I64, Buffer(arena, measures));
        int root = arena.AddStruct(schema, keys.Length, Validity.NonNullable, [key, measure]);
        yield return new RecordBatch(arena, root, 0);
    }

    private static VortexBuffer Buffer(CanonicalArena arena, long[] values)
    {
        VortexBuffer buffer = arena.Allocate(values.Length * sizeof(long), sizeof(long), out Span<byte> bytes);
        MemoryMarshal.Cast<long, byte>(values).CopyTo(bytes);
        return buffer;
    }
}
