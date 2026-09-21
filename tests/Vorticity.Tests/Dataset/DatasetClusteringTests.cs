// The clustering key, the mandatory run on it, and the k-way merge across objects -- all three
// under one acceptance rule: every answer the dataset gives must equal the answer a single file
// would give.
//
// THE DATA IS BUILT TO BREAK A LAZY IMPLEMENTATION, and every choice here is one of those breaks.
// The objects' key ranges INTERLEAVE -- object i holds the keys congruent to i modulo four -- so a
// merge that walked objects one after another instead of merging them would produce a different
// order on the very first two keys. They are APPENDED OUT OF ORDER -- 3, 1, 0, 2 -- so a tree that
// kept insertion order rather than key order would be caught by the first assertion. And within an
// object the keys are SHUFFLED, so the column is not sorted and `file.Keys(...)` can only be served
// by the mandatory run: a test that wrote sorted keys would pass with no index at all.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Dataset;
using Vorticity.Diagnostics;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Keys;
using Vorticity.Scanning;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;
using System.Runtime.InteropServices;

namespace Vorticity.Tests.Dataset;

public sealed class DatasetClusteringTests
{
    private const int Objects = 4;
    private const int PerObject = 250;

    [Fact]
    public async Task ObjectsAreHeldInClusteringKeyOrderWhateverOrderTheyArrivedIn()
    {
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);

        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Clustered());
        Assert.Equal(["key"], dataset.ClusteringKeyPaths);
        Assert.NotNull(dataset.Key);

        foreach (int residue in (int[])[3, 1, 0, 2])
        {
            await dataset.AppendAsync(Batches(types, schema, residue));
        }

        // The tree walks them by key, not by arrival: object 0 holds the smallest key.
        List<long> firstKeys = [];
        await foreach (ObjectEntry entry in dataset.ObjectsAsync())
        {
            Assert.True(entry.Summaries.TryGet("key", out ColumnSummary key));
            firstKeys.Add(key.Min.SignedValue);
        }

        Assert.Equal([0L, 1L, 2L, 3L], firstKeys);
        Assert.Equal(Objects * PerObject, dataset.RowCount);
    }

    [Fact]
    public async Task EveryAppendedObjectCarriesTheMandatoryRun()
    {
        // The keys inside an object are shuffled, so nothing but the run can serve a cursor.
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);

        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Clustered());
        await dataset.AppendAsync(Batches(types, schema, 0));

        ObjectEntry entry = Assert.Single(await ObjectsAsync(dataset));
        await using ObjectSegmentSource source = new ObjectSegmentSource(store, entry.Key);
        await using VortexFile file = await VortexFile.OpenAsync(source, new VortexOpenOptions(), default);

        // The column is not sorted, and yet a key cursor opens and seeks: that is the run.
        Assert.False(IsSorted(file, "key"), "the keys were shuffled; a sorted column would void this test");
        await using KeyCursor cursor = await file.Keys("key").OpenAsync();
        Assert.True(await cursor.SeekAsync(FilterLiteral.From(4L * 7), SeekOp.Exact));
        Assert.Equal(4L * 7, cursor.Key.SignedValue);
    }

    [Fact]
    public async Task TheMergedCursorWalksEveryKeyInOrder()
    {
        // A k-way merge of the level-0 objects, through their runs, at no more than 8 + L cursors:
        // eight level-0 objects and one per level below.
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);

        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Clustered());
        foreach (int residue in (int[])[3, 1, 0, 2])
        {
            await dataset.AppendAsync(Batches(types, schema, residue));
        }

        await using DatasetKeyCursor cursor = await DatasetKeyCursor.OpenAsync(dataset);
        Assert.Equal(0, cursor.Cursors);

        List<long> walked = [];
        HashSet<string> objects = new HashSet<string>(StringComparer.Ordinal);
        bool any = await cursor.SeekFirstAsync();
        while (any)
        {
            walked.Add(cursor.Key.SignedValue);
            objects.Add(cursor.Object.Key);
            any = await cursor.NextAsync();
        }

        // Every key of every object, once, in order: 0, 1, 2, … and from all four objects.
        List<long> expected = [];
        for (long key = 0; key < Objects * PerObject; key++)
        {
            expected.Add(key);
        }

        Assert.Equal(expected, walked);
        Assert.Equal(Objects, objects.Count);

        // Four level-0 objects overlap, so every one of them is a candidate: four cursors.
        Assert.Equal(Objects, cursor.Cursors);

        // And a seek lands where a single sorted file would.
        Assert.True(await cursor.SeekAsync(FilterLiteral.From(517L)));
        Assert.Equal(517L, cursor.Key.SignedValue);
        Assert.True(await cursor.NextAsync());
        Assert.Equal(518L, cursor.Key.SignedValue);

        Console.Out.Write(FormattableString.Invariant(
            $"DATASET MERGE: {cursor.Cursors} cursors merged {walked.Count} keys from {objects.Count} interleaved objects.\n"));
    }

    [Fact]
    public async Task AnExactSeekLeavesTheMergeAbleToWalkOn()
    {
        // The trap this covers: seeking each cursor `Exact` invalidates the ones whose object does
        // not hold the key, and the walk after it is then missing their rows for EVERY key that
        // follows. Four interleaved objects and a key only one of them holds is exactly that shape.
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);

        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Clustered());
        foreach (int residue in (int[])[3, 1, 0, 2])
        {
            await dataset.AppendAsync(Batches(types, schema, residue));
        }

        await using DatasetKeyCursor cursor = await DatasetKeyCursor.OpenAsync(dataset);
        Assert.True(await cursor.SeekAsync(FilterLiteral.From(517L), SeekOp.Exact));

        List<long> rest = [cursor.Key.SignedValue];
        while (await cursor.NextAsync())
        {
            rest.Add(cursor.Key.SignedValue);
        }

        // Every key from 517 to the last, from all four objects, not just from 517's own.
        List<long> expected = [];
        for (long key = 517; key < Objects * PerObject; key++)
        {
            expected.Add(key);
        }

        Assert.Equal(expected, rest);

        // And a key no object holds is a refusal, not a position on the nearest one.
        Assert.False(await cursor.SeekAsync(FilterLiteral.From(-1L), SeekOp.Exact));
        Assert.False(cursor.IsValid);
        Assert.False(await cursor.NextAsync());
    }

    [Fact]
    public async Task AKeyWalkRefusesAnObjectThatCannotServeIt()
    {
        // An imported file with no run on the key: leaving its rows out would be a wrong answer, so
        // the walk refuses and names it.
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);

        await using MemoryObjectStore store = new MemoryObjectStore();
        byte[] unindexed = await OneFileAsync(types, schema, [0], indexes: false);
        string key = CommitKey.ForData("unindexed");
        await store.PutIfAbsentAsync(key, unindexed, default);

        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Clustered());
        await dataset.ImportAsync(key);

        // Refused when the walk reaches it -- the first seek, since it is the only object -- which is
        // the one moment its rows could have been left out.
        await using DatasetKeyCursor cursor = await DatasetKeyCursor.OpenAsync(dataset);
        VortexUnsupportedException refused = await Assert.ThrowsAsync<VortexUnsupportedException>(
            async () => await cursor.SeekFirstAsync());
        Assert.Contains(key, refused.Message, StringComparison.Ordinal);

        // The scan is unaffected: it reads objects, not keys.
        Assert.Equal(PerObject, await dataset.Scan().CountAsync());
    }

    [Fact]
    public async Task TheKeysColumnsAreSummarisedWhateverTheLimitSays()
    {
        // An entry's summaries are bounded to the first 32 columns. The column the dataset is
        // ORDERED by is not one a limit may drop: without it there is nothing to order a cursor-less
        // object by, and nothing to prune with on the column a reader filters by most.
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);

        await using MemoryObjectStore store = new MemoryObjectStore();
        DatasetOptions narrow = Clustered() with
        {
            ClusteringKey = ["measure"],
            SummaryColumnLimit = 1,
        };
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, narrow);
        await dataset.AppendAsync(Batches(types, schema, 0));

        ObjectEntry entry = Assert.Single(await ObjectsAsync(dataset));
        Assert.Equal(2, entry.Summaries.Count);
        Assert.True(entry.Summaries.TryGet("key", out _), "the limit of 1 keeps the first column");
        Assert.True(entry.Summaries.TryGet("measure", out ColumnSummary measure), "and the key's own");
        Assert.Equal(0.0, measure.Min.FloatValue);
    }

    [Fact]
    public async Task ADatasetOrderedByRowPositionHasNoKeyWalk()
    {
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);

        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Unclustered());
        Assert.Null(dataset.Key);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await DatasetKeyCursor.OpenAsync(dataset));
    }

    [Fact]
    public async Task TheTerminalsAcrossObjectsAnswerAsOneFileDoes()
    {
        // A single file's terminals, one level up. The summaries let an object that cannot beat
        // the best so far be skipped whole -- the pruning of an ordered LIMIT k, at k = 1.
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);

        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Clustered());
        foreach (int residue in (int[])[3, 1, 0, 2])
        {
            await dataset.AppendAsync(Batches(types, schema, residue));
        }

        byte[] single = await OneFileAsync(types, schema, [3, 1, 0, 2], indexes: true);
        await using MemorySegmentSource source = new MemorySegmentSource(single);
        await using VortexFile file = await VortexFile.OpenAsync(source, new VortexOpenOptions(), default);

        Assert.Equal(await file.Scan().MinAsync("key"), await dataset.Scan().MinAsync("key"));
        Assert.Equal(await file.Scan().MaxAsync("key"), await dataset.Scan().MaxAsync("key"));
        Assert.Equal(await file.Scan().CountAsync(), await dataset.Scan().CountAsync());
        Assert.True(await dataset.Scan().AnyAsync());

        VortexExpr window = Expr.And(
            Expr.Ge(Expr.Field("key"), Expr.Literal(FilterLiteral.From(500L))),
            Expr.Lt(Expr.Field("key"), Expr.Literal(FilterLiteral.From(600L))));
        Assert.Equal(
            await file.Scan().Where(window).MinAsync("key"),
            await dataset.Scan().Where(window).MinAsync("key"));
        Assert.Equal(
            await file.Scan().Where(window).MaxAsync("key"),
            await dataset.Scan().Where(window).MaxAsync("key"));

        // A value no object holds: `Any` refutes it from the summaries alone.
        DatasetScanMetrics metrics = new DatasetScanMetrics();
        VortexExpr absent = Expr.Eq(Expr.Field("key"), Expr.Literal(FilterLiteral.From(-5L)));
        Assert.False(await dataset.Scan().Where(absent).WithMetrics(metrics).AnyAsync());
        Assert.Equal(0, metrics.ObjectsOpened);
        Assert.Equal(Objects, metrics.ObjectsSkipped);
    }

    private static bool IsSorted(VortexFile file, string path)
    {
        int index = file.Schema.IndexOfField(path);
        return index >= 0
            && file.HasFileStatistics
            && file.Statistics.GetField(index).TryGetIsSorted(out bool sorted)
            && sorted;
    }

    private static async Task<List<ObjectEntry>> ObjectsAsync(VortexDataset dataset)
    {
        List<ObjectEntry> entries = [];
        await foreach (ObjectEntry entry in dataset.ObjectsAsync())
        {
            entries.Add(entry);
        }

        return entries;
    }

    private static DType Schema(DTypeArena types) => types.Struct(
        ["key", "measure"],
        [types.Primitive(PType.I64, Nullability.NonNullable), types.Primitive(PType.F64, Nullability.NonNullable)],
        Nullability.NonNullable);

    private static DatasetOptions Clustered() => Unclustered() with { ClusteringKey = ["key"] };

    private static DatasetOptions Unclustered() => new DatasetOptions
    {
        Seed = 0xC1057E_5EED,
        Write = new VortexWriteOptions { RowBlockSize = 64, DataBlockTargetBytes = 8 << 10 },
    };

    /// <summary>The whole dataset, written into one file in the same arrival order.</summary>
    private static async Task<byte[]> OneFileAsync(
        DTypeArena types, DType schema, IReadOnlyList<int> residues, bool indexes)
    {
        VortexWriteOptions options = Unclustered().Write;
        if (!indexes)
        {
            options = options.WithIndexes(Vorticity.Indexes.WritePolicy.None);
        }

        System.IO.MemoryStream stream = new System.IO.MemoryStream();
        await using (VortexFileWriter writer = VortexFileWriter.Create(
            new StreamSegmentSink(stream), schema, options))
        {
            foreach (int residue in residues)
            {
                await foreach (RecordBatch batch in Batches(types, schema, residue))
                {
                    await writer.WriteAsync(batch);
                }
            }

            await writer.CompleteAsync();
        }

        return stream.ToArray();
    }

    /// <summary>
    /// One object's rows: the keys congruent to <paramref name="residue"/> modulo four, shuffled.
    /// </summary>
    private static async IAsyncEnumerable<RecordBatch> Batches(DTypeArena types, DType schema, int residue)
    {
        const int size = 125;
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType f64 = types.Primitive(PType.F64, Nullability.NonNullable);
        long[] keys = new long[PerObject];
        for (int i = 0; i < PerObject; i++)
        {
            keys[i] = ((long)i * Objects) + residue;
        }

        // A fixed shuffle: the same every run, and nothing like the file order.
        Random shuffle = new Random(residue + 1);
        for (int i = keys.Length - 1; i > 0; i--)
        {
            int j = shuffle.Next(i + 1);
            (keys[i], keys[j]) = (keys[j], keys[i]);
        }

        for (int start = 0; start < PerObject; start += size)
        {
            int count = Math.Min(size, PerObject - start);
            CanonicalArena arena = new CanonicalArena();
            VortexBuffer keyBuffer = arena.Allocate(count * sizeof(long), sizeof(long), out Span<byte> keyBytes);
            VortexBuffer measures = arena.Allocate(count * sizeof(double), sizeof(double), out Span<byte> measureBytes);
            Span<long> keyValues = MemoryMarshal.Cast<byte, long>(keyBytes);
            Span<double> measureValues = MemoryMarshal.Cast<byte, double>(measureBytes);
            for (int row = 0; row < count; row++)
            {
                keyValues[row] = keys[start + row];
                measureValues[row] = keys[start + row] / 4.0;
            }

            int keyNode = arena.AddPrimitive(i64, count, Validity.NonNullable, PType.I64, keyBuffer);
            int measureNode = arena.AddPrimitive(f64, count, Validity.NonNullable, PType.F64, measures);
            int root = arena.AddStruct(schema, count, Validity.NonNullable, [keyNode, measureNode]);
            using RecordBatch batch = new RecordBatch(arena, root, start);
            yield return batch;
            await Task.CompletedTask;
        }
    }
}
