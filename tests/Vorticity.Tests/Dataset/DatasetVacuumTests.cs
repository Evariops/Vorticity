// Vacuum: an orphan younger than the window is kept, older is deleted, and a reader on a swept
// version reports the missing object with its version.
//
// THE CLOCK IS THE STORE'S, moved by hand. Every age vacuum compares is a store timestamp, so the
// store and the vacuum share one manual clock, and "two hours later" is one line rather than a wait.
//
// THE ORACLE IS THE LATEST VERSION READ BACK WHOLE after the sweep: every key it held before, from a
// fresh handle. Deleting a live object is the one failure vacuum must not have, and a count of what
// it deleted would not catch it; the rows do.
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
using Vorticity.Scanning;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class DatasetVacuumTests
{
    private const int Objects = 4;
    private const int PerObject = 200;
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    [Fact]
    public async Task WhatACompactionLeftBehindIsKeptInsideTheWindowAndDeletedAfterIt()
    {
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);
        ManualClock clock = new ManualClock(new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero));
        await using MemoryObjectStore store = new MemoryObjectStore { Clock = clock };
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Options());
        for (int i = 0; i < Objects; i++)
        {
            await dataset.AppendAsync(Of(types, schema, i * PerObject, PerObject));
        }

        List<string> inputs = [.. (await EntriesAsync(dataset)).Select(entry => entry.Key)];
        await using VortexDataset stale = await VortexDataset.OpenAsync(store);
        ulong staleVersion = stale.Version;
        Assert.NotNull(await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2, TargetBytesAtLevelOne = 1 << 20 }));
        List<string> outputs = [.. (await EntriesAsync(dataset)).Select(entry => entry.Key)];
        Assert.Empty(inputs.Intersect(outputs));

        // Inside the window: every version is retained, so nothing is garbage yet, and the inputs
        // are marked by the versions that still name them.
        VacuumResult early = await dataset.VacuumAsync(new VacuumOptions { Clock = clock });
        Assert.Equal(dataset.Version, early.Latest);
        Assert.Equal(Window, early.Window);
        Assert.Equal((int)dataset.Version, early.Retained.Count);
        Assert.Empty(early.Deleted);
        Assert.Empty(early.Young);

        // Two hours on, only the latest is inside the window. A dry run deletes nothing.
        clock.Advance(TimeSpan.FromHours(2));
        int count = store.Count;
        VacuumResult dry = await dataset.VacuumAsync(new VacuumOptions { Clock = clock, DryRun = true });
        Assert.Equal(count, store.Count);
        Assert.Equal([dataset.Version], dry.Retained);

        VacuumResult swept = await dataset.VacuumAsync(new VacuumOptions { Clock = clock });
        Assert.Equal(dry.Deleted, swept.Deleted);
        Assert.Equal(inputs.Order(StringComparer.Ordinal), swept.Deleted.Where(key => key.StartsWith(CommitKey.DataPrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(CommitKey.For(dataset.Version), swept.Deleted);
        Assert.Equal(count - swept.Deleted.Count, store.Count);

        // The oracle: the latest version, from a fresh handle, whole.
        await using (VortexDataset fresh = await VortexDataset.OpenAsync(store))
        {
            Assert.Equal(Enumerable.Range(0, Objects * PerObject).Select(i => (long)i), (await KeysAsync(fresh.Scan())).Order());
        }

        // A reader on a swept version reports the object it lost, and its version.
        ObjectNotFoundException lost = await Assert.ThrowsAsync<ObjectNotFoundException>(
            async () => await KeysAsync(stale.Scan()));
        Assert.Equal(staleVersion, lost.Version);
        Assert.Contains(lost.Key, swept.Deleted);
        Assert.Contains("retention window", lost.Message, StringComparison.Ordinal);

        // And a second vacuum finds nothing left to do.
        VacuumResult again = await dataset.VacuumAsync(new VacuumOptions { Clock = clock });
        Assert.Empty(again.Deleted);
    }

    [Fact]
    public async Task AnOrphanYoungerThanTheWindowIsKeptAndAnOlderOneDeleted()
    {
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);
        ManualClock clock = new ManualClock(new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero));
        await using MemoryObjectStore store = new MemoryObjectStore { Clock = clock };
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Options());
        await dataset.AppendAsync(Of(types, schema, 0, PerObject));

        // A writer that uploaded and never committed, and an import's object outside `data/`.
        string old = CommitKey.ForData("orphan-old");
        await store.PutIfAbsentAsync(old, new byte[] { 1, 2, 3 }, default);
        await store.PutIfAbsentAsync("imports/theirs.vortex", new byte[] { 4, 5, 6 }, default);
        clock.Advance(TimeSpan.FromHours(2));
        string inFlight = CommitKey.ForData("orphan-in-flight");
        await store.PutIfAbsentAsync(inFlight, new byte[] { 7, 8, 9 }, default);

        VacuumResult result = await dataset.VacuumAsync(new VacuumOptions { Clock = clock });
        Assert.Contains(old, result.Deleted);
        Assert.Contains(inFlight, result.Young);
        Assert.Null(await store.HeadAsync(old, default));
        Assert.NotNull(await store.HeadAsync(inFlight, default));
        Assert.NotNull(await store.HeadAsync("imports/theirs.vortex", default));
        Assert.Equal(PerObject, (await KeysAsync(dataset.Scan())).Count);
    }

    [Fact]
    public async Task TheVersionsSettingKeepsThatManyWhateverTheirAge()
    {
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);
        ManualClock clock = new ManualClock(new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero));
        await using MemoryObjectStore store = new MemoryObjectStore { Clock = clock };
        await using VortexDataset dataset = await VortexDataset.CreateAsync(
            store, schema, Options() with { Retention = new RetentionSettings(2, (long)Window.TotalSeconds) });
        for (int i = 0; i < Objects; i++)
        {
            await dataset.AppendAsync(Of(types, schema, i * PerObject, PerObject));
        }

        clock.Advance(TimeSpan.FromDays(30));
        ulong latest = dataset.Version;
        await using VortexDataset previous = await VortexDataset.OpenAsync(store);
        VacuumResult result = await dataset.VacuumAsync(new VacuumOptions { Clock = clock });
        Assert.Equal([latest, latest - 1, latest - 2], result.Retained);
        Assert.Contains(CommitKey.For(1), result.Deleted);

        // Every retained version still reads whole: its objects and pages were marked.
        foreach (ulong version in result.Retained)
        {
            Assert.NotNull(await store.HeadAsync(CommitKey.For(version), default));
        }

        Assert.Equal(Objects * PerObject, (await KeysAsync(previous.Scan())).Count);
    }

    [Fact]
    public async Task ACommitHeldOnlyByAFragmentIsKept()
    {
        // An indexing commit writes its fragment into its own commit object. Every later
        // append rewrites the leaf, so that object's PAGES die, and only the entry's fragment
        // reference keeps it: a vacuum that marked pages alone would delete the index under a
        // live object.
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);
        ManualClock clock = new ManualClock(new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero));
        await using MemoryObjectStore store = new MemoryObjectStore { Clock = clock };
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Options());
        await dataset.AppendAsync(Of(types, schema, 0, PerObject));
        PositionedObject target = await SingleObjectAsync(dataset);
        IndexingResult indexed = await DatasetIndexer.IndexAsync(
            dataset, target, WritePolicy.None.For("measure", IndexSpec.Bloom(falsePositivePpm: 1_000)),
            options: new VortexWriteOptions { IndexBudgetPerMille = 1_000_000 });
        Assert.Equal(OperationOutcome.Applied, indexed.Outcome);
        ulong indexing = dataset.Version;
        for (int i = 1; i < Objects; i++)
        {
            await dataset.AppendAsync(Of(types, schema, i * PerObject, PerObject));
        }

        clock.Advance(TimeSpan.FromHours(2));
        VacuumResult result = await dataset.VacuumAsync(new VacuumOptions { Clock = clock });
        Assert.Equal([dataset.Version], result.Retained);
        Assert.Contains(CommitKey.For(indexing - 1), result.Deleted);
        Assert.DoesNotContain(CommitKey.For(indexing), result.Deleted);

        // The object opens with its fragment, from a fresh handle.
        await using VortexDataset fresh = await VortexDataset.OpenAsync(store);
        Assert.Equal(Objects * PerObject, (await KeysAsync(fresh.Scan())).Count);
    }

    [Fact]
    public async Task ACommitWhosePagesTheLatestStillReferencesIsKept()
    {
        // A commit references the pages it did not change where they already are, in older
        // commit objects. On a tree of small pages, appends in key order leave the first leaves
        // where the first commits wrote them, and those commits outlive the window through them.
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);
        ManualClock clock = new ManualClock(new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero));
        await using MemoryObjectStore store = new MemoryObjectStore { Clock = clock };
        DatasetOptions options = Options() with { Rule = new FillBoundaryRule(400) };
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, options);
        const int appends = 8;
        for (int i = 0; i < appends; i++)
        {
            await dataset.AppendAsync(Of(types, schema, i * 50, 50));
        }

        Assert.True(dataset.Depth > 1, $"depth {dataset.Depth}");
        clock.Advance(TimeSpan.FromHours(2));
        VacuumResult result = await dataset.VacuumAsync(new VacuumOptions { Clock = clock });
        Assert.Equal([dataset.Version], result.Retained);

        List<string> commits = [];
        for (ulong version = 1; version < dataset.Version; version++)
        {
            if (await store.HeadAsync(CommitKey.For(version), default) is not null)
            {
                commits.Add(CommitKey.For(version));
            }
        }

        Assert.NotEmpty(commits);
        Assert.NotEmpty(result.Deleted);

        await using VortexDataset fresh = await VortexDataset.OpenAsync(store, options);
        Assert.Equal(Enumerable.Range(0, appends * 50).Select(i => (long)i), (await KeysAsync(fresh.Scan())).Order());
    }

    [Fact]
    public async Task ARepackEmptiesTheSparseCommitsSoTheNextVacuumCanTakeThem()
    {
        // When vacuum finds a commit object kept alive by a few live pages among many dead ones, a
        // metadata-only commit rewrites those pages into itself, so the old object can go at the
        // next pass. Old leaves and a fragment keep early commits alive after the window.
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);
        ManualClock clock = new ManualClock(new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero));
        await using MemoryObjectStore store = new MemoryObjectStore { Clock = clock };
        DatasetOptions options = Options() with { Rule = new FillBoundaryRule(400) };
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, options);
        const int appends = 8;
        for (int i = 0; i < appends; i++)
        {
            await dataset.AppendAsync(Of(types, schema, i * 50, 50));
            if (i == 0)
            {
                IndexingResult indexed = await DatasetIndexer.IndexAsync(
                    dataset, await SingleObjectAsync(dataset), WritePolicy.None.For("measure", IndexSpec.Bloom(falsePositivePpm: 1_000)),
                    options: new VortexWriteOptions { IndexBudgetPerMille = 1_000_000 });
                Assert.Equal(OperationOutcome.Applied, indexed.Outcome);
            }
        }

        List<string> content = Described(await EntriesAsync(dataset));
        clock.Advance(TimeSpan.FromHours(2));

        // Measured: an append here writes a leaf and the internal pages above it, and the leaf stays
        // live, so about a third of each body is live -- over the default 0.25. A threshold of 1
        // names every commit kept by references alone.
        Assert.Empty((await dataset.VacuumAsync(new VacuumOptions { Clock = clock, DryRun = true })).Sparse);
        VacuumResult first = await dataset.VacuumAsync(new VacuumOptions { Clock = clock, RepackBelow = 1.0 });
        Assert.NotEmpty(first.Sparse);
        Assert.DoesNotContain(dataset.Version, first.Sparse);

        (ulong repacked, OperationOutcome outcome) = await dataset.RepackAsync(first.Sparse);
        Assert.Equal(OperationOutcome.Applied, outcome);
        Assert.Equal(repacked, dataset.Version);

        // Placement moved, content did not: every entry as it was, its fragments compared by content
        // -- a fragment reference names where it lies, so the entry holding a moved one is a changed
        // entry and the tree hash moves with it -- a clean verify, and nothing of the version left in
        // the objects it emptied.
        Assert.Equal(content, Described(await EntriesAsync(dataset)));
        Assert.True((await dataset.VerifyAsync()).Holds);
        foreach (ObjectEntry entry in await EntriesAsync(dataset))
        {
            Assert.All(entry.Fragments, fragment => Assert.DoesNotContain(fragment.Version, first.Sparse));
        }

        // A second repack of the same objects finds nothing to move.
        Assert.Equal(OperationOutcome.AlreadyThere, (await dataset.RepackAsync(first.Sparse)).Outcome);

        // Past the window again, the next vacuum takes every one of them.
        clock.Advance(TimeSpan.FromHours(2));
        VacuumResult second = await dataset.VacuumAsync(new VacuumOptions { Clock = clock });
        foreach (ulong version in first.Sparse)
        {
            Assert.Contains(CommitKey.For(version), second.Deleted);
        }

        // The repack's own commit is all live past its header but for its table, so nothing is due
        // again at the default threshold: the ratio is over the body, or every repack would make the
        // next one due.
        Assert.Empty(second.Sparse);
        await using VortexDataset fresh = await VortexDataset.OpenAsync(store, options);
        Assert.Equal(Enumerable.Range(0, appends * 50).Select(i => (long)i), (await KeysAsync(fresh.Scan())).Order());
    }

    [Fact]
    public async Task AnEmptyStoreHasNothingToVacuum()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        VacuumResult result = await DatasetVacuum.RunAsync(store);
        Assert.Equal(0UL, result.Latest);
        Assert.Empty(result.Deleted);
    }

    // ------------------------------------------------------------------------------ helpers

    private static DatasetOptions Options() => new DatasetOptions
    {
        Seed = 0x7AC_0017,
        ClusteringKey = ["key"],
        Retention = new RetentionSettings(0, (long)Window.TotalSeconds),
        Write = new VortexWriteOptions { RowBlockSize = 64, DataBlockTargetBytes = 512 },
    };

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

    /// <summary>Each entry by what it is, its fragments by their content rather than their placement.</summary>
    private static List<string> Described(List<ObjectEntry> entries) =>
    [
        .. entries.Select(entry =>
            $"{entry.Key} {entry.Uid:x32} {entry.Rows} {entry.Bytes} {entry.Hash:x32} "
            + string.Join(",", entry.Fragments.Select(fragment => $"{fragment.Length}:{fragment.Hash:x32}"))),
    ];

    private static async Task<PositionedObject> SingleObjectAsync(VortexDataset dataset)
    {
        List<PositionedObject> objects = [];
        await foreach (PositionedObject held in dataset.Scan().ObjectsAsync())
        {
            objects.Add(held);
        }

        return Assert.Single(objects);
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
