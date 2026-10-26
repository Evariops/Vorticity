// The dataset's interleaving fuzzer: seeded schedules of readers, writers, indexers, compactors
// and vacuum, crashes between any two store calls; every read equals a scan with indexes off on
// the same version; every retained root references only existing objects.
//
// WHAT THE STALENESS IS, and it is the whole point. A writer PREPARES its operations against the
// version it read and COMMITS them later, after other writers have moved the ground under it.
// Each operation then knows what to do, and `RebaseMatrixTests` holds each of those answers
// one row at a time. What no row can hold is the composition: a hundred operations prepared at
// scattered versions, landing in a seeded order, some of them through a store that stored the
// commit and then threw. So the schedule here separates prepare from commit and lets the seed
// decide which happens next.
//
// THE ORACLE IS A MODEL, NOT THE IMPLEMENTATION. `Model` re-derives the rebase rules over a plain
// dictionary, and the tree must equal it entry for entry after every schedule. That does not prove
// the rules are right -- the rebase matrix does that, row by row, against the spec's own table --
// it proves that the TREE, the rebase loop and the crash handling deliver what the rules say, under
// interleavings no fixed test would write down. The two failures it is built for are a page
// re-chunked wrongly after a rebase, and a commit that landed while its writer was told it had not.
//
// THE MODEL SCHEDULE DRIVES `ReplaceObjects` DIRECTLY, since a model of synthetic leaves has no rows
// to merge. The real compactor and vacuum run in their own schedule, over real
// objects, where the invariant is the store's: every version a vacuum retained still verifies. And
// compactions planned on one version and committed after others run in a third, where the invariant
// is the levels': each one above 0 key-disjoint, which a walk of the key relies on.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.Scanning;
using Vorticity.Tests.Scan;
using Vorticity.Types;
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
        CancellationToken ct = TestContext.Current.CancellationToken;
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
            // A batch that applies nothing puts nothing, so it has nothing to crash after.
            bool crashing = random.Next(5) == 0;
            bool crashed = false;
            store.CrashesAfterPut = crashing ? _ => true : null;
            try
            {
                await DatasetCommitter.CommitAsync(store, batch, Options(), ct);
                commits++;
            }
            catch (ObjectStoreException) when (crashing)
            {
                crashes++;
                crashed = true;
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
            if (crashed)
            {
                await DatasetCommitter.CommitAsync(store, batch, Options(), ct);
                stale++;
            }
        }

        (DatasetTree tree, IPageSource pages, ulong version) = await LatestAsync(store);
        List<(string Key, ObjectEntry Entry)> held = [];
        await foreach (TreeEntry entry in tree.EnumerateAsync(pages, ct))
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
            Assert.Equal(expected[i].Entry, ByContent(held[i].Entry));
        }

        // And every leaf points at an object the store holds, for every version still there, not
        // only the latest.
        await EveryRootReferencesOnlyExistingObjectsAsync(store, version);

        Console.Out.Write(FormattableString.Invariant(
            $"DATASET FUZZ seed {seed}: {commits} commits and {crashes} crash(es) retried, {version} versions, {held.Count} objects, depth {tree.Depth}.\n"));
        Assert.True(commits + crashes > 5, "the schedule should have committed something");
        Assert.Equal(crashes, stale);
    }

    [Fact]
    public async Task ACrashAfterThePutIsNotASecondObject()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // A rebased add of an object already there, under the state the rebase exists for: the
        // commit landed and the writer was told it had not. Retrying must add nothing, because
        // the uid is the same bytes.
        await using MemoryObjectStore store = new MemoryObjectStore();
        List<DatasetOperation> batch = [Add(1, 0), Add(2, 0)];

        store.CrashesAfterPut = _ => true;
        await Assert.ThrowsAsync<ObjectStoreException>(
            async () => await DatasetCommitter.CommitAsync(store, batch, Options(), ct));
        store.CrashesAfterPut = null;

        CommitResult again = await DatasetCommitter.CommitAsync(store, batch, Options(), ct);
        Assert.All(again.Outcomes, outcome => Assert.Equal(OperationOutcome.AlreadyThere, outcome));

        (DatasetTree tree, IPageSource pages, ulong version) = await LatestAsync(store);
        Assert.Equal(2, tree.Entries);

        // The retry, having nothing to add, wrote no version: the latest is the crashed commit.
        Assert.Equal(1UL, version);
        Assert.Equal(1UL, again.Version);
        Assert.NotEqual(UInt128.Zero, await tree.ContentHashAsync(pages, ct));
    }

    [Fact]
    public async Task EveryVersionsRootStillReferencesOnlyExistingObjects()
    {
        // A reader holds a root, and everything it references is immutable. This is that sentence
        // as an invariant over every version a run left behind.
        await using MemoryObjectStore store = new MemoryObjectStore();
        Random random = new Random(99);
        for (int i = 0; i < 12; i++)
        {
            List<DatasetOperation> batch = await PrepareAsync(store, random);
            if (batch.Count > 0)
            {
                await DatasetCommitter.CommitAsync(store, batch, Options(), TestContext.Current.CancellationToken);
            }
        }

        (_, _, ulong version) = await LatestAsync(store);
        Assert.True(version > 1, "the run should have produced several versions");
        await EveryRootReferencesOnlyExistingObjectsAsync(store, version);
    }

    [Fact]
    public async Task AScheduleOfRealAppendsAnswersAsOneFile()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // The first invariant, end to end: every read equals a scan with indexes off on the same
        // version. Real data objects this time, because there is nothing to scan in a synthetic
        // leaf -- and the single file is the same rows written once.
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
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, options, ct);

        // Two handles on one dataset, appending in a seeded order: each has to refresh to see the
        // other's commits, and each commit rebases onto whatever landed since.
        await using VortexDataset second = await VortexDataset.OpenAsync(store, options, ct);
        Random random = new Random(7);
        long rows = 0;
        for (int batch = 0; batch < 10; batch++)
        {
            VortexDataset writer = random.Next(2) == 0 ? dataset : second;
            await writer.AppendAsync(Batches(types, schema, rows, 300), ct);
            rows += 300;
        }

        await dataset.RefreshAsync(ct);
        Assert.Equal(10, dataset.ObjectCount);
        Assert.Equal(rows, dataset.RowCount);

        List<long> withIndexes = await KeysAsync(dataset.ScanBuilder());
        List<long> without = await KeysAsync(dataset.ScanBuilder().WithIndexes(false).WithSummaries(false));
        Assert.Equal(withIndexes, without);

        List<long> expected = [];
        for (long key = 0; key < rows; key++)
        {
            expected.Add(key);
        }

        withIndexes.Sort();
        Assert.Equal(expected, withIndexes);
    }

    [Fact]
    public async Task AScheduleOfRealAppendsAndIndexersAnswersAsWithoutIndexes()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // Indexers are among what the schedule interleaves, and here they are the real one: two
        // handles, each appending or indexing a block range of an object AS IT LAST SAW THE
        // DATASET, in a seeded order. A stale indexer rebases like any writer; every read after
        // every step must equal the scan with indexes and summaries off.
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
            Write = new VortexWriteOptions
            {
                RowBlockSize = 128,
                DataBlockTargetBytes = 8 << 10,
                WritePolicy = WritePolicy.None,
            },
        };
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, options, ct);
        await using VortexDataset second = await VortexDataset.OpenAsync(store, options, ct);
        VortexDataset[] handles = [dataset, second];
        WritePolicy policy = WritePolicy.None
            .For("key", IndexSpec.SortedRuns.AsRequired())
            .For("measure", IndexSpec.Bloom(falsePositivePpm: 100));
        VortexWriteOptions build = new VortexWriteOptions { IndexBudgetPerMille = 1_000 };
        RowRange[] ranges = [new RowRange(0, 300), new RowRange(0, 128), new RowRange(128, 300)];

        Random random = new Random(11);
        long rows = 0;
        Dictionary<OperationOutcome, int> outcomes = [];
        for (int step = 0; step < 24; step++)
        {
            VortexDataset handle = handles[random.Next(2)];
            List<PositionedObject> seen = [];
            await foreach (PositionedObject held in handle.ScanBuilder().ObjectsAsync(ct))
            {
                seen.Add(held);
            }

            if (seen.Count == 0 || random.Next(3) == 0)
            {
                await handle.AppendAsync(Batches(types, schema, rows, 300), ct);
                rows += 300;
            }
            else
            {
                PositionedObject target = seen[random.Next(seen.Count)];
                FragmentResult result = await DatasetIndexer.IndexAsync(
                    handle, target, policy, ranges[random.Next(ranges.Length)], build, ct);
                outcomes[result.Outcome] = outcomes.GetValueOrDefault(result.Outcome) + 1;
            }

            // The other handle stays where it was half of the time: its next step is stale.
            if (random.Next(2) == 0)
            {
                await handles[0].RefreshAsync(ct);
                await handles[1].RefreshAsync(ct);
            }

            await dataset.RefreshAsync(ct);
            foreach (VortexExpr question in (VortexExpr[])[
                Expr.Eq(Expr.Field("key"), Expr.Literal(FilterLiteral.From(rows / 2))),
                Expr.Eq(Expr.Field("measure"), Expr.Literal(FilterLiteral.From(0.3))),
                Expr.Lt(Expr.Field("key"), Expr.Literal(FilterLiteral.From(rows / 3)))])
            {
                Assert.Equal(
                    await KeysAsync(dataset.ScanBuilder().Where(question).WithIndexes(false).WithSummaries(false)),
                    await KeysAsync(dataset.ScanBuilder().Where(question)));
            }
        }

        Assert.Equal(rows, dataset.RowCount);
        Assert.True(outcomes.GetValueOrDefault(OperationOutcome.Applied) > 3, "the schedule must index something");
        Console.Out.Write(FormattableString.Invariant(
            $"DATASET FUZZ INDEXERS: {dataset.ObjectCount} objects, {outcomes.GetValueOrDefault(OperationOutcome.Applied)} fragments applied, {outcomes.GetValueOrDefault(OperationOutcome.AlreadyThere)} already there, {outcomes.GetValueOrDefault(OperationOutcome.Dropped)} dropped.\n"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(19)]
    [InlineData(42)]
    public async Task WritersThatTakeTheSameSourcesCountEachEntryOnce(int seed)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // Four writers take entries of three sources in any order, each through a handle that may
        // be many versions behind, which is what a pause or a rebalance makes of a writer: it read an
        // offset long ago and commits now. One commit in five takes two sources at once, one writer
        // in eight replays the commit it made last as if its answer was lost, and a compaction
        // runs between them now and then. Entry n of source s is the row of key s * 1 000 000 + n.
        Decoders.EnsureRegistered();
        await using MemoryObjectStore store = new MemoryObjectStore();
        DatasetOptions options = new DatasetOptions { Seed = Seed, ClusteringKey = ["Key"] };
        await using VortexDataset first = await VortexDataset.CreateAsync(store, ChangeRow.Schema, options, ct);
        VortexDataset[] writers = [first, .. await Task.WhenAll(Enumerable.Range(1, Writers - 1).Select(_ => VortexDataset.OpenAsync(store, options, ct).AsTask()))];
        string[] sources = ["s0", "s1", "s2"];
        const ulong Entries = 400;
        Random random = new Random(seed);
        Dictionary<string, List<(ulong From, ulong To)>> applied = sources.ToDictionary(source => source, _ => new List<(ulong, ulong)>());
        (VortexDataset Writer, SourceRange[] Ranges)? last = null;
        int conflicts = 0;
        int counted = 0;
        int replays = 0;
        try
        {
            for (int step = 0; step < 160 && sources.Any(source => first.SourceOffset(source) < Entries); step++)
            {
                VortexDataset writer = writers[random.Next(writers.Length)];
                if (random.Next(3) == 0)
                {
                    await writer.RefreshAsync(ct);
                }

                if (random.Next(10) == 0)
                {
                    await writer.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }, ct);
                    continue;
                }

                // A replay of the last commit: its ranges are counted, so it adds nothing.
                if (last is { } previous && random.Next(8) == 0)
                {
                    SourceCommit replayed = await CommitRangesAsync(previous.Writer, previous.Ranges, ct);
                    Assert.Equal(OperationOutcome.AlreadyThere, replayed.Outcome);
                    replays++;
                    continue;
                }

                List<SourceRange> ranges = [];
                foreach (string source in sources.OrderBy(_ => random.Next()).Take(random.Next(5) == 0 ? 2 : 1))
                {
                    ulong from = writer.SourceOffset(source);
                    if (from < Entries)
                    {
                        ranges.Add(new SourceRange(source, from, Math.Min(Entries, from + (ulong)random.Next(1, 40))));
                    }
                }

                if (ranges.Count == 0)
                {
                    continue;
                }

                try
                {
                    SourceCommit commit = await CommitRangesAsync(writer, ranges, ct);
                    if (commit.Outcome == OperationOutcome.Applied)
                    {
                        foreach (SourceRange range in ranges)
                        {
                            applied[range.Source].Add((range.From, range.To));
                        }

                        last = (writer, [.. ranges]);
                    }
                    else
                    {
                        counted++;
                    }
                }
                catch (SourceOffsetConflictException conflict)
                {
                    // The handle reads the version that decided it, so it starts from there next time.
                    Assert.Equal(conflict.Version, writer.Version);
                    conflicts++;
                }
            }

            // For each source, the ranges applied tile (0, r] without a gap or an overlap, and the
            // rows are the entries of that span once each: what a single writer would have written.
            await first.RefreshAsync(ct);
            List<long> keys = [.. (await first.Scan<ChangeRow>().ToRecordsAsync(ct).ToListAsync(ct)).Select(row => row.Key)];
            keys.Sort();
            List<long> expected = [];
            for (int s = 0; s < sources.Length; s++)
            {
                ulong reached = 0;
                foreach ((ulong from, ulong to) in applied[sources[s]].OrderBy(range => range.From))
                {
                    Assert.Equal(reached, from);
                    reached = to;
                }

                Assert.Equal(reached, first.SourceOffset(sources[s]));
                for (ulong entry = 1; entry <= reached; entry++)
                {
                    expected.Add((s * 1_000_000L) + (long)entry);
                }
            }

            expected.Sort();
            Assert.Equal(expected, keys);
            Console.Out.Write(FormattableString.Invariant(
                $"DATASET FUZZ SOURCES seed {seed}: version {first.Version}, {keys.Count} entries counted, {conflicts} conflict(s), {counted} counted already, {replays} replay(s).\n"));
            Assert.True(conflicts + counted > 0, "the schedule should have had two writers meet on a range");
        }
        finally
        {
            foreach (VortexDataset writer in writers.Skip(1))
            {
                await writer.DisposeAsync();
            }
        }
    }

    /// <summary>The rows of the ranges' entries, as one commit that counts them.</summary>
    private static async Task<SourceCommit> CommitRangesAsync(VortexDataset writer, IReadOnlyList<SourceRange> ranges, CancellationToken ct)
    {
        List<ChangeRow> rows = [];
        foreach (SourceRange range in ranges)
        {
            long source = range.Source[^1] - '0';
            for (ulong entry = range.From + 1; entry <= range.To; entry++)
            {
                rows.Add(new ChangeRow((source * 1_000_000L) + (long)entry, entry * 0.5, "Paris"));
            }
        }

        await using ObjectDraft draft = writer.StartObject();
        await draft.Writer.WriteAsync<ChangeRow>([.. rows], ct);
        return await writer.AppendAsync(draft, ranges, ct);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(17)]
    [InlineData(29)]
    public async Task AScheduleWithTheCompactorAndVacuumLosesNothingARetainedVersionNeeds(int seed)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // The rest of the schedule's cast: compactors and vacuum, with repack after it, under
        // one clock the schedule moves. Two handles, each acting on the version it last saw half of
        // the time: an append, an indexing, a compaction, a vacuum. Two invariants after every
        // step: the answers are the scan's with indexes and summaries off, and after a vacuum every
        // version it retained still verifies whole -- pages, objects and fragments all there.
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = types.Struct(
            ["key", "measure"],
            [types.Primitive(PType.I64, Nullability.NonNullable), types.Primitive(PType.F64, Nullability.NonNullable)],
            Nullability.NonNullable);

        ManualClock clock = new ManualClock(new DateTimeOffset(2026, 9, 18, 0, 0, 0, TimeSpan.Zero));
        await using MemoryObjectStore store = new MemoryObjectStore { TimeProvider = clock };
        DatasetOptions options = new DatasetOptions
        {
            Seed = Seed,
            ClusteringKey = ["key"],
            RetentionWindow = TimeSpan.FromSeconds(3_600),
            Write = new VortexWriteOptions { RowBlockSize = 128, DataBlockTargetBytes = 8 << 10 },
            TimeProvider = clock,
        };
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, options, ct);
        await using VortexDataset second = await VortexDataset.OpenAsync(store, options, ct);
        VortexDataset[] handles = [dataset, second];
        CompactionOptions compaction = new CompactionOptions { LevelZeroCeiling = 2, TargetBytesAtLevelOne = 64 << 10 };
        WritePolicy policy = WritePolicy.None.For("measure", IndexSpec.Bloom(falsePositivePpm: 100));
        VortexWriteOptions build = new VortexWriteOptions { IndexBudgetPerMille = 1_000_000 };

        Random random = new Random(seed);
        long rows = 0;
        int compactions = 0;
        int deleted = 0;
        int repacked = 0;

        // Every version the schedule made, with the rows it holds, and its own draws for the version
        // it opens by number after each step: one vacuum took answers ObjectNotFoundException, at the
        // open or at the read, and one it left answers every row it had.
        Dictionary<ulong, long> rowsAt = new Dictionary<ulong, long> { [dataset.Version] = 0 };

        // And the last version that changed rows, which only an append moves here: the creation's
        // until the first one, then each append's, whatever moves the rows in between.
        Dictionary<ulong, ulong> changeAt = new Dictionary<ulong, ulong> { [dataset.Version] = dataset.Version };
        ulong recorded = dataset.Version;
        Random picks = new Random(seed + 1);
        int gone = 0;
        for (int step = 0; step < 30; step++)
        {
            clock.Advance(TimeSpan.FromMinutes(random.Next(90)));
            VortexDataset handle = handles[random.Next(2)];
            bool appended = false;
            switch (rows == 0 ? 0 : random.Next(4))
            {
                case 0:
                    await handle.AppendAsync(Batches(types, schema, rows, 300), ct);
                    rows += 300;
                    appended = true;
                    break;

                case 1:
                {
                    List<PositionedObject> seen = [];
                    await foreach (PositionedObject held in handle.ScanBuilder().ObjectsAsync(ct))
                    {
                        seen.Add(held);
                    }

                    _ = await DatasetIndexer.IndexAsync(handle, seen[random.Next(seen.Count)], policy, options: build, cancellationToken: ct);
                    break;
                }

                case 2:
                    if (await handle.CompactAsync(compaction, ct) is { Outcome: OperationOutcome.Applied })
                    {
                        compactions++;
                    }

                    break;

                default:
                {
                    // A threshold of 1: every commit kept by references alone is repacked, so the
                    // schedule puts repack commits between the others rather than almost never.
                    VacuumResult vacuum = await handle.VacuumAsync(new VacuumOptions { TimeProvider = clock, RepackBelow = 1.0 }, ct);
                    deleted += vacuum.Deleted.Length;
                    foreach (ulong version in vacuum.Retained)
                    {
                        DatasetVerification verified = await DatasetVerifier.VerifyAsync(store, new VerifyOptions { Version = version }, ct);
                        Assert.True(verified.Holds, $"step {step}, version {version}: {string.Join("; ", verified.Problems)}");
                    }

                    if (vacuum.Sparse.Count > 0 && (await handle.RepackAsync(vacuum.Sparse, ct)).Outcome == OperationOutcome.Applied)
                    {
                        repacked++;
                    }

                    break;
                }
            }

            if (random.Next(2) == 0)
            {
                await handles[0].RefreshAsync(ct);
                await handles[1].RefreshAsync(ct);
            }

            await dataset.RefreshAsync(ct);
            Assert.Equal(rows, dataset.RowCount);
            // A step makes one version at most, so every version since the last step holds its rows.
            for (; recorded < dataset.Version; recorded++)
            {
                rowsAt[recorded + 1] = rows;
                changeAt[recorded + 1] = appended ? recorded + 1 : changeAt[recorded];
            }

            Assert.Equal(changeAt[dataset.Version], dataset.LastDataChange);
            ulong opened = (ulong)picks.Next(1, (int)dataset.Version + 1);
            try
            {
                await using VortexDataset past = await VortexDataset.OpenAsync(store, opened, options, ct);
                List<long> held = await KeysAsync(past.ScanBuilder());
                held.Sort();
                Assert.Equal(rowsAt[opened], held.Count);
                Assert.Equal(rowsAt[opened] == 0 ? 0 : rowsAt[opened] - 1, held.Count == 0 ? 0 : held[^1]);
                Assert.Equal(changeAt[opened], past.LastDataChange);
            }
            catch (ObjectNotFoundException)
            {
                gone++;
            }

            VortexExpr question = Expr.Eq(Expr.Field("measure"), Expr.Literal(FilterLiteral.From(rows / 8.0)));
            Assert.Equal(
                await KeysAsync(dataset.ScanBuilder().Where(question).WithIndexes(false).WithSummaries(false)),
                await KeysAsync(dataset.ScanBuilder().Where(question)));
            List<long> keys = await KeysAsync(dataset.ScanBuilder());
            keys.Sort();
            Assert.Equal(rows, keys.Count);
            Assert.Equal(rows == 0 ? 0 : rows - 1, keys.Count == 0 ? 0 : keys[^1]);
        }

        Assert.True((await dataset.VerifyAsync(cancellationToken: ct)).Holds);
        Console.Out.Write(FormattableString.Invariant(
            $"DATASET FUZZ LIFECYCLE seed {seed}: version {dataset.Version}, {dataset.ObjectCount} objects, {compactions} compaction(s), {deleted} object(s) vacuumed, {repacked} repack(s), {gone} of 30 versions opened by number gone.\n"));
        Assert.True(deleted > 0, "the schedule should have vacuumed something");
        Assert.True(repacked > 0, "the schedule should have repacked something");
        Assert.True(gone > 0, "the schedule should have opened a version vacuum took");
    }

    [Theory]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(12)]
    [InlineData(16)]
    public async Task CompactionsPlannedOnOneVersionAndCommittedLaterKeepEveryLevelKeyDisjoint(int seed)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // A compaction plans on the version its handle saw and commits later, after other compactions
        // have written into the levels it writes: three handles, each holding the job it planned, and
        // appends whose keys land anywhere in a range a few times what they hold, so that the levels
        // meet and keep holes between their objects. Each step is an append, a plan kept for later,
        // the job held run and committed, or a refresh. After every step each level above 0 is
        // key-disjoint, and a walk of the key cursor meets every row. Without the commit's look at the
        // level a job writes, seeds 7 and 16 leave two objects of level 2 overlapping.
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
            ClusteringKey = ["key"],
            Write = new VortexWriteOptions { RowBlockSize = 64, DataBlockTargetBytes = 512 },
        };
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, options, ct);
        await using VortexDataset second = await VortexDataset.OpenAsync(store, options, ct);
        await using VortexDataset third = await VortexDataset.OpenAsync(store, options, ct);
        VortexDataset[] handles = [dataset, second, third];
        CompactionJob?[] held = new CompactionJob?[handles.Length];

        // An object here takes three or four kibibytes, most of them its footer, whatever its rows. A
        // level 1 that holds three of them, and outputs rolled at half a kibibyte there and at a
        // kibibyte above, so that a merge into level 1 leaves it over its size and level 2 keeps
        // narrow objects; and three levels: level 0 empties into level 1 or straight into level 2,
        // depending on what it holds, while level 1 empties into level 2. The third handle plans as a
        // background loop beside inline compactions might, with a ceiling on level 0 it never
        // reaches and a level 1 of two objects, so that it moves level 1 up while the others empty
        // level 0.
        CompactionOptions compaction = new CompactionOptions
        {
            LevelZeroCeiling = 2,
            TargetBytesAtLevelOne = 512,
            Fanout = 24,
            MaxObjectBytes = 1 << 10,
            MaxLevels = 3,
        };
        CompactionOptions[] plans = [compaction, compaction, compaction with { LevelZeroCeiling = 1_000, TargetBytesAtLevelOne = 64 }];

        // Three appends, three plans and three runs for a refresh: a held job then often commits after
        // another handle's.
        Random random = new Random(seed);
        List<long> appended = [];
        Dictionary<OperationOutcome, int> outcomes = [];
        for (int step = 0; step < 150; step++)
        {
            int at = random.Next(handles.Length);
            VortexDataset handle = handles[at];
            switch (appended.Count == 0 ? 0 : random.Next(10) / 3)
            {
                case 0:
                {
                    long from = random.Next(3_000);
                    int count = 10 + random.Next(60);
                    await handle.AppendAsync(Batches(types, schema, from, count), ct);
                    for (int i = 0; i < count; i++)
                    {
                        appended.Add(from + i);
                    }

                    break;
                }

                case 1:
                    held[at] = (await handle.PlanCompactionAsync(plans[at], ct)).Job;
                    break;

                case 2:
                    if (held[at] is { } job)
                    {
                        held[at] = null;
                        CompactionResult result = await DatasetCompactor.RunAsync(handle, job, ct);
                        outcomes[result.Outcome] = outcomes.GetValueOrDefault(result.Outcome) + 1;
                    }

                    break;

                default:
                    await handle.RefreshAsync(ct);
                    break;
            }

            await dataset.RefreshAsync(ct);
            for (int level = 1; level < dataset.Levels.Count; level++)
            {
                long last = long.MinValue;
                await foreach (TreeEntry entry in dataset.Levels[level].EnumerateAsync(dataset.Pages, ct))
                {
                    Assert.True(ObjectEntry.FromBytes(entry.Value.Span).Summaries.TryGet("key", out ColumnSummary key));
                    Assert.True(
                        last < key.Min.SignedValue,
                        $"step {step}: level {level} holds an object from {key.Min.SignedValue} after one that ends at {last}");
                    last = key.Max.SignedValue;
                }
            }

            List<long> walked = [];
            await using (DatasetKeyCursor cursor = await DatasetKeyCursor.OpenAsync(dataset, ct))
            {
                for (bool on = await cursor.SeekFirstAsync(ct); on; on = await cursor.NextAsync(ct))
                {
                    walked.Add(cursor.Key.SignedValue);
                }
            }

            List<long> sorted = [.. appended];
            sorted.Sort();
            Assert.Equal(sorted, walked);
        }

        Console.Out.Write(FormattableString.Invariant(
            $"DATASET FUZZ LATE COMPACTIONS seed {seed}: version {dataset.Version}, {dataset.ObjectCount} objects over {dataset.Levels.Count} levels, {outcomes.GetValueOrDefault(OperationOutcome.Applied)} applied, {outcomes.GetValueOrDefault(OperationOutcome.Abandoned)} abandoned.\n"));
        Assert.True(outcomes.GetValueOrDefault(OperationOutcome.Applied) > 3, "the schedule must compact something");
        Assert.True(outcomes.GetValueOrDefault(OperationOutcome.Abandoned) > 0, "the schedule must commit a job the ground moved under");
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
                    batch.Add(new DatasetOperation.AddFragment(key, entry.Uid, FragmentBytes(random.Next(8))));
                    break;
                }

                case 2:
                {
                    // The same indexer, dropping one: by its content, wherever a commit wrote it.
                    (ReadOnlyMemory<byte> key, _) = seen[random.Next(seen.Count)];
                    batch.Add(new DatasetOperation.DropFragment(key, Content(FragmentBytes(random.Next(8)))));
                    break;
                }

                default:
                {
                    // A compaction: one or two inputs, one output at the first input's key.
                    int inputs = Math.Min(1 + random.Next(2), seen.Count);
                    List<(int Level, ReadOnlyMemory<byte> Key)> taken = [];
                    for (int n = 0; n < inputs; n++)
                    {
                        taken.Add((0, seen[random.Next(seen.Count)].Key));
                    }

                    int output = 200 + random.Next(50);
                    await EnsureObjectAsync(store, output, 0);
                    batch.Add(new DatasetOperation.ReplaceObjects(
                        taken, [(0, Key(output), Object(output, 0))]));
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
            pages.Open(version, commit);
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
        pages.Open(version, commit);
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

    /// <summary>The bytes of one of eight fragments an indexer may write.</summary>
    private static ReadOnlyMemory<byte> FragmentBytes(int i)
    {
        byte[] bytes = new byte[32 + i];
        bytes.AsSpan().Fill((byte)(0x40 + i));
        return bytes;
    }

    /// <summary>
    /// A fragment as its content: the length and hash a reference carries, with no place. What the
    /// model can know of a fragment, since only the commit that lands decides where it is written.
    /// </summary>
    private static PageReference Content(ReadOnlyMemory<byte> fragment) =>
        new PageReference(0, 0, fragment.Length, System.IO.Hashing.XxHash128.HashToUInt128(fragment.Span));

    /// <summary>An entry with its fragments reduced to their content, as the model holds them.</summary>
    private static ObjectEntry ByContent(ObjectEntry entry)
    {
        List<PageReference> contents = new List<PageReference>(entry.Fragments.Count);
        foreach (PageReference fragment in entry.Fragments)
        {
            contents.Add(new PageReference(0, 0, fragment.Length, fragment.Hash));
        }

        return entry.WithFragments(contents);
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
    /// The rebase rules over a plain dictionary, written from the rules as stated rather than
    /// from the committer: the oracle the schedule is checked against.
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
                    // "the second finds the fragment present in the winner's leaf, drops its own";
                    // where the first one was written, the model neither knows nor needs.
                    string key = Hex(fragment.Key);
                    if (_entries.TryGetValue(key, out ObjectEntry? held)
                        && held.Uid == fragment.Uid
                        && !held.Holds(Content(fragment.Fragment)))
                    {
                        _entries[key] = held.With(Content(fragment.Fragment));
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
                            if (reference.Length != drop.Fragment.Length || reference.Hash != drop.Fragment.Hash)
                            {
                                kept.Add(reference);
                            }
                        }

                        _entries[key] = held.WithFragments(kept);
                    }

                    break;
                }

                case DatasetOperation.ReplaceObjects replace:
                {
                    foreach ((int _, ReadOnlyMemory<byte> input) in replace.Inputs)
                    {
                        if (!_entries.ContainsKey(Hex(input)))
                        {
                            // "an input is missing: the outputs are garbage" -- the whole operation
                            // is abandoned, inputs included.
                            return;
                        }
                    }

                    foreach ((int _, ReadOnlyMemory<byte> input) in replace.Inputs)
                    {
                        _entries.Remove(Hex(input));
                    }

                    foreach ((int _, ReadOnlyMemory<byte> key, ObjectEntry entry) in replace.Outputs)
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
