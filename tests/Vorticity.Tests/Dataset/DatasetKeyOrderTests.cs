// The dataset's key-order guarantees — `InKeyOrder` across levels equals the sorted scan, and
// `ORDER BY x LIMIT k` through the summaries equals the first `k` of the full sort — and the two
// claims they rest on: the merge holds at most 8 + L cursors, and an object whose minimum lies
// past the k-th key is never opened.
//
// THE DATA IS THE CLUSTERING TESTS' SHAPE, FOR THE SAME REASON. Objects whose keys INTERLEAVE,
// appended OUT OF ORDER, shuffled INSIDE each object: a merge that chained objects instead of
// merging them is caught on the second key, one that read file order instead of the runs on the
// first. Where the claim is about what is NOT opened, the objects' ranges are DISJOINT instead, and
// appended out of order, so that the first k rows live in one object and the tree — not the arrival
// order — has to find it.
//
// THE ORACLE IS THE SAME ROWS SORTED BEFORE A BYTE OF THEM WAS WRITTEN, compared row by row, key and
// measure, never a count typed into the test. And the reads are made with the summaries and without:
// without them no object has a bound and every one is opened before a row goes out — the eager merge,
// which a lazy one must answer exactly as.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Dataset;
using Vorticity.Diagnostics;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.Layouts;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class DatasetKeyOrderTests
{
    private const int Objects = 4;
    private const int PerObject = 250;
    private const int Rows = Objects * PerObject;

    [Fact]
    public async Task InKeyOrderAcrossInterleavedObjectsEqualsTheSortedRows()
    {
        // At level 0: four objects whose keys interleave modulo four.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);

        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Clustered(), ct);
        foreach (int residue in (int[])[3, 1, 0, 2])
        {
            await dataset.AppendAsync(Shuffled(types, schema, Stride(residue, Objects), residue + 1), ct);
        }

        List<(long Key, double Measure)> sorted = Sorted(Range(0, Rows));
        List<(long Key, double Measure)> reversed = [.. sorted];
        reversed.Reverse();
        foreach (bool summaries in (bool[])[true, false])
        {
            DatasetScanCounters metrics = new DatasetScanCounters();
            Assert.Equal(
                sorted,
                await RowsAsync(dataset.ScanBuilder().WithSummaries(summaries).WithMetrics(metrics).InKeyOrder("key")));

            // Every object reaches down to the smallest keys, so all four are held at once: level 0
            // is the part of the merge's bound that the level-0 ceiling is there to keep small.
            Assert.Equal(Objects, metrics.Cursors);
            Assert.Equal(Objects, metrics.ObjectsOpened);

            Assert.Equal(
                reversed,
                await RowsAsync(dataset.ScanBuilder().WithSummaries(summaries).InKeyOrder("key", descending: true)));
        }
    }

    [Fact]
    public async Task InKeyOrderAcrossLevelsEqualsTheSortedRowsBeforeAndAfterCompaction()
    {
        // Across levels: level 1 after a compaction, then level 0 on top of it again — and the
        // merge's bound of at most 8 + L cursors, stated by Explain before the read and counted
        // during it.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);

        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Clustered(), ct);

        // Even keys, interleaved modulo eight across the four objects.
        List<long> held = [];
        foreach (int residue in (int[])[3, 1, 0, 2])
        {
            long[] keys = Stride(2 * residue, 2 * Objects);
            held.AddRange(keys);
            await dataset.AppendAsync(Shuffled(types, schema, keys, residue + 1), ct);
        }

        await AssertOrderedAsync(dataset, held, cursors: Objects);

        // A target small enough that level 1 is several key-disjoint objects: the merge reads them
        // one after another, so it holds one cursor for the whole level.
        CompactionOptions options = new CompactionOptions
        {
            LevelZeroCeiling = 2,
            TargetBytesAtLevelOne = 2 << 10,
            MaxObjectBytes = 1L << 30,
            Fanout = 1_000,
        };
        CompactionResult compaction = Assert.IsType<CompactionResult>(await dataset.CompactAsync(options, ct));
        Assert.True(compaction.ObjectsOut > 2, $"level 1 must hold several objects; it holds {compaction.ObjectsOut}");
        Assert.Equal(0, dataset.Levels[0].Entries);
        await AssertOrderedAsync(dataset, held, cursors: 1);

        // Odd keys over the lower half, in two new objects of level 0: two cursors for level 0, and
        // still one for level 1 — 2 + L.
        foreach (int residue in (int[])[1, 0])
        {
            long[] keys = Stride((2 * residue) + 1, 4);
            held.AddRange(keys);
            await dataset.AppendAsync(Shuffled(types, schema, keys, residue + 7), ct);
        }

        Assert.Equal(2, dataset.Levels[0].Entries);
        await AssertOrderedAsync(dataset, held, cursors: 3);

        Console.Out.Write(FormattableString.Invariant(
            $"DATASET KEY ORDER: 4 cursors over 4 interleaved objects of level 0; 1 over the {compaction.ObjectsOut} key-disjoint objects of level 1 after a compaction; 3 with 2 objects of level 0 on top, for {held.Count} rows.\n"));
    }

    [Fact]
    public async Task OrderByKeyLimitKOpensOnlyTheObjectsThatCouldHoldTheFirstKRows()
    {
        // `ORDER BY key LIMIT k` equals the first k of the full sort, and an object whose min
        // exceeds the current k-th best is skipped. Four DISJOINT quarters appended out of order:
        // the first ten keys live in one object, and it is the tree that has to find it, not the
        // arrival order.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);

        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Clustered(), ct);
        foreach (int quarter in (int[])[2, 0, 3, 1])
        {
            await dataset.AppendAsync(Shuffled(types, schema, Range(quarter * PerObject, PerObject), quarter + 1), ct);
        }

        List<(long Key, double Measure)> sorted = Sorted(Range(0, Rows));
        List<(long Key, double Measure)> reversed = [.. sorted];
        reversed.Reverse();

        // Upward on the clustering key, by the tree's exact minima: one object for the first ten,
        // and two for the first three hundred, which cross one boundary and not a second.
        DatasetScanCounters metrics = new DatasetScanCounters();
        Assert.Equal(sorted[..10], await FirstAsync(dataset.ScanBuilder().WithMetrics(metrics).InKeyOrder("key"), 10));
        Assert.Equal(1, metrics.ObjectsOpened);

        metrics = new DatasetScanCounters();
        Assert.Equal(sorted[..300], await FirstAsync(dataset.ScanBuilder().WithMetrics(metrics).InKeyOrder("key"), 300));
        Assert.Equal(2, metrics.ObjectsOpened);

        // Downward, by the summaries' maxima.
        metrics = new DatasetScanCounters();
        Assert.Equal(
            reversed[..10],
            await FirstAsync(dataset.ScanBuilder().WithMetrics(metrics).InKeyOrder("key", descending: true), 10));
        Assert.Equal(1, metrics.ObjectsOpened);

        // Without the summaries, the same rows and every object opened: they are what bought the skip.
        foreach (bool descending in (bool[])[false, true])
        {
            metrics = new DatasetScanCounters();
            Assert.Equal(
                descending ? reversed[..10] : sorted[..10],
                await FirstAsync(
                    dataset.ScanBuilder().WithSummaries(false).WithMetrics(metrics).InKeyOrder("key", descending), 10));
            Assert.Equal(Objects, metrics.ObjectsOpened);
        }

        Console.Out.Write(
            "DATASET LIMIT: ORDER BY key LIMIT 10 opened 1 object of 4 either way, LIMIT 300 opened 2, and 4 without the summaries.\n");
    }

    [Fact]
    public async Task AFilterOnTheKeyNarrowsEveryObjectsOwnWalk()
    {
        // A conjunct on the key bounds each object's walk as it bounds a file's; one on
        // another column prunes as it always does. The summaries skip what the range refutes.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);

        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Clustered(), ct);
        foreach (int residue in (int[])[3, 1, 0, 2])
        {
            await dataset.AppendAsync(Shuffled(types, schema, Stride(residue, Objects), residue + 1), ct);
        }

        VortexExpr band = Expr.And(
            Expr.And(
                Expr.Ge(Expr.Field("key"), Expr.Literal(FilterLiteral.From(500L))),
                Expr.Lt(Expr.Field("key"), Expr.Literal(FilterLiteral.From(600L)))),
            Expr.Ge(Expr.Field("measure"), Expr.Literal(FilterLiteral.From(0.0))));
        List<(long Key, double Measure)> expected = Sorted(Range(500, 100));
        List<(long Key, double Measure)> reversed = [.. expected];
        reversed.Reverse();
        foreach (bool summaries in (bool[])[true, false])
        {
            Assert.Equal(
                expected,
                await RowsAsync(dataset.ScanBuilder().Where(band).WithSummaries(summaries).InKeyOrder("key")));
            Assert.Equal(
                reversed,
                await RowsAsync(dataset.ScanBuilder().Where(band).WithSummaries(summaries).InKeyOrder("key", descending: true)));
        }

        // A key no object holds: the summaries refute every object, and nothing is opened.
        DatasetScanCounters metrics = new DatasetScanCounters();
        VortexExpr absent = Expr.Lt(Expr.Field("key"), Expr.Literal(FilterLiteral.From(-1L)));
        Assert.Empty(await RowsAsync(dataset.ScanBuilder().Where(absent).WithMetrics(metrics).InKeyOrder("key")));
        Assert.Equal(0, metrics.ObjectsOpened);
        Assert.Equal(Objects, metrics.ObjectsSkipped);
    }

    [Fact]
    public async Task ASelectionWithoutTheKeyIsStillMergedByIt()
    {
        // The merge compares rows by the key whatever `Select` says: the key is read on top of the
        // selection and dropped before a batch goes out, as a filter's columns are in a file.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);

        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Clustered(), ct);
        foreach (int residue in (int[])[3, 1, 0, 2])
        {
            await dataset.AppendAsync(Shuffled(types, schema, Stride(residue, Objects), residue + 1), ct);
        }

        List<double> expected = [];
        foreach ((long _, double measure) in Sorted(Range(0, Rows)))
        {
            expected.Add(measure);
        }

        List<double> measures = [];
        await foreach (RecordBatch batch in dataset.ScanBuilder().Project(Columns(dataset, "measure")).InKeyOrder("key").ExecuteAsync(ct))
        {
            Assert.Equal(1, batch.FieldCount);
            Assert.Equal("measure", batch.GetFieldName(0));
            measures.AddRange(batch.Column(0).AsPrimitive<double>().Values.ToArray());
        }

        Assert.Equal(expected, measures);

        // Selected, the key stays, and the order is the same.
        Assert.Equal(Sorted(Range(0, Rows)), await RowsAsync(dataset.ScanBuilder().Project(Columns(dataset, "key", "measure")).InKeyOrder("key")));
    }

    /// <summary>The mask of <paramref name="columns"/>, resolved against the dataset's schema.</summary>
    private static FieldMask Columns(VortexDataset dataset, params string[] columns)
    {
        FieldMaskBuilder mask = new FieldMaskBuilder();
        foreach (string column in columns)
        {
            mask.Include(ToolPaths.Resolve(dataset.Schema, column));
        }

        return mask.Build();
    }

    [Fact]
    public async Task AnotherColumnIsMergedToo_WithEveryKeptObjectAsABound()
    {
        // An order on another column costs, at best, one cursor per object the summaries cannot
        // refute, with a sorted run per object: proportional to the output, not bounded.
        // A dataset with no clustering key, whose objects carry a run on `key` by their own policy.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);

        await using MemoryObjectStore store = new MemoryObjectStore();
        DatasetOptions indexed = Unclustered() with
        {
            Write = Unclustered().Write.WithIndexes(
                WritePolicy.Auto.For("key", IndexSpec.SortedRuns.AsRequired())),
        };
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, indexed, ct);
        foreach (int quarter in (int[])[2, 0, 3, 1])
        {
            await dataset.AppendAsync(Shuffled(types, schema, Range(quarter * PerObject, PerObject), quarter + 1), ct);
        }

        List<(long Key, double Measure)> sorted = Sorted(Range(0, Rows));
        List<(long Key, double Measure)> reversed = [.. sorted];
        reversed.Reverse();

        DatasetPlan plan = await dataset.ScanBuilder().InKeyOrder("key").ExplainAsync(ct);
        Assert.Equal("key", plan.Order);
        Assert.Equal(Objects, plan.Cursors);

        foreach (bool summaries in (bool[])[true, false])
        {
            Assert.Equal(sorted, await RowsAsync(dataset.ScanBuilder().WithSummaries(summaries).InKeyOrder("key")));
            Assert.Equal(
                reversed,
                await RowsAsync(dataset.ScanBuilder().WithSummaries(summaries).InKeyOrder("key", descending: true)));
        }

        // Output-sensitive, and still pruned by the summaries: their minima say which quarter
        // holds the first ten rows, and the other three are not opened.
        DatasetScanCounters metrics = new DatasetScanCounters();
        Assert.Equal(sorted[..10], await FirstAsync(dataset.ScanBuilder().WithMetrics(metrics).InKeyOrder("key"), 10));
        Assert.Equal(1, metrics.ObjectsOpened);
    }

    [Fact]
    public async Task AFloatKeyIsMergedInTheKeyOrderNaNsIncluded()
    {
        // A float key is ordered negative NaN first and positive NaN last, while a zone's min and
        // max exclude NaN: a float's summary is no bound in the key order. A merge that used
        // one downward would deliver the positive NaN after the largest number, and upward the tree's
        // own minimum — the run's first key, NaN included — is the bound, not the summary.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = types.Struct(
            ["f", "id"],
            [types.Primitive(PType.F64, Nullability.NonNullable), types.Primitive(PType.I64, Nullability.NonNullable)],
            Nullability.NonNullable);

        await using MemoryObjectStore store = new MemoryObjectStore();
        DatasetOptions clustered = Unclustered() with { ClusteringKey = ["f"] };
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, clustered, ct);

        double negativeNaN = BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8_0000_0000_0000UL));
        double positiveNaN = BitConverter.Int64BitsToDouble(0x7FF8_0000_0000_0000L);
        double[][] objects =
        [
            [3.5, negativeNaN, 1.5, -2.0, 5.5],
            [2.5, 0.5, positiveNaN, -1.0, 4.5],
            [double.NegativeInfinity, 6.5, -0.5],
        ];

        List<(double Value, long Id)> all = [];
        long id = 0;
        foreach (double[] values in objects)
        {
            long[] ids = new long[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                ids[i] = id++;
                all.Add((values[i], ids[i]));
            }

            await dataset.AppendAsync(Floats(types, schema, values, ids), ct);
        }

        all.Sort((left, right) => TotalOrder(left.Value).CompareTo(TotalOrder(right.Value)));
        List<long> upward = [];
        foreach ((double _, long held) in all)
        {
            upward.Add(held);
        }

        List<long> downward = [.. upward];
        downward.Reverse();
        foreach (bool summaries in (bool[])[true, false])
        {
            Assert.Equal(upward, await IdsAsync(dataset.ScanBuilder().WithSummaries(summaries).InKeyOrder("f")));
            Assert.Equal(
                downward,
                await IdsAsync(dataset.ScanBuilder().WithSummaries(summaries).InKeyOrder("f", descending: true)));
        }
    }

    [Fact]
    public async Task AConsumerThatDisposesItsBatchesStillGetsEveryRow()
    {
        // A run that is a whole batch is handed out as that very batch, and a consumer may dispose
        // what it is given. Disjoint objects make every run a whole batch: stepping past one
        // must not read it again.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);

        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Clustered(), ct);
        foreach (int quarter in (int[])[2, 0, 3, 1])
        {
            await dataset.AppendAsync(Shuffled(types, schema, Range(quarter * PerObject, PerObject), quarter + 1), ct);
        }

        List<long> keys = [];
        await foreach (RecordBatch batch in dataset.ScanBuilder().InKeyOrder("key").ExecuteAsync(ct))
        {
            using (batch)
            {
                keys.AddRange(batch.Column("key"u8).AsPrimitive<long>().Values.ToArray());
            }
        }

        Assert.Equal(Range(0, Rows), keys);
    }

    [Fact]
    public async Task InKeyOrderAndRowsExcludeEachOther()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);

        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Clustered(), ct);

        Assert.Throws<InvalidOperationException>(() => dataset.ScanBuilder().Rows(0, 10).InKeyOrder("key"));
        Assert.Throws<InvalidOperationException>(() => dataset.ScanBuilder().InKeyOrder("key").Rows(0, 10));
        Assert.Throws<ArgumentException>(() => dataset.ScanBuilder().InKeyOrder("nope"));

        // An empty dataset has nothing to merge, and says so without opening anything.
        Assert.Empty(await RowsAsync(dataset.ScanBuilder().InKeyOrder("key")));
        DatasetPlan plan = await dataset.ScanBuilder().InKeyOrder("key").ExplainAsync(ct);
        Assert.Equal(0, plan.Cursors);
    }

    [Fact]
    public async Task AnObjectWithNoSourceForTheKeyIsRefusedWhenTheMergeReachesIt()
    {
        // An imported file with shuffled keys and no run: leaving its rows out would be a wrong
        // answer, so the key-ordered read refuses, as the core refuses such a file.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);

        await using MemoryObjectStore store = new MemoryObjectStore();
        System.IO.MemoryStream stream = new System.IO.MemoryStream();
        await using (VortexFileWriter writer = VortexFileWriter.Create(
            new StreamSegmentSink(stream), schema, Unclustered().Write.WithIndexes(WritePolicy.None)))
        {
            await foreach (RecordBatch batch in Shuffled(types, schema, Range(0, PerObject), 5))
            {
                await writer.WriteAsync(batch, ct);
            }

            await writer.CompleteAsync(ct);
        }

        string key = CommitKey.ForData("unindexed");
        await store.PutIfAbsentAsync(key, stream.ToArray(), ct);
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Clustered(), ct);
        await dataset.ImportAsync(key, ct);

        await Assert.ThrowsAsync<VortexUnsupportedException>(async () => await RowsAsync(dataset.ScanBuilder().InKeyOrder("key")));

        // The scan in the tree's order is unaffected: it reads objects, not keys.
        Assert.Equal(PerObject, await dataset.Scan().CountAsync(ct));
    }

    /// <summary>
    /// The key-ordered read equals the sorted rows both ways, with and without the summaries, and
    /// holds the cursors Explain says it will.
    /// </summary>
    private static async Task AssertOrderedAsync(VortexDataset dataset, List<long> held, int cursors)
    {
        List<(long Key, double Measure)> sorted = Sorted(held);
        List<(long Key, double Measure)> reversed = [.. sorted];
        reversed.Reverse();

        DatasetPlan plan = await dataset.ScanBuilder().InKeyOrder("key").ExplainAsync();
        Assert.Equal("key", plan.Order);
        Assert.Equal(cursors, plan.Cursors);

        DatasetScanCounters metrics = new DatasetScanCounters();
        Assert.Equal(sorted, await RowsAsync(dataset.ScanBuilder().WithMetrics(metrics).InKeyOrder("key")));
        Assert.Equal(cursors, metrics.Cursors);

        metrics = new DatasetScanCounters();
        Assert.Equal(reversed, await RowsAsync(dataset.ScanBuilder().WithMetrics(metrics).InKeyOrder("key", descending: true)));
        Assert.Equal(cursors, metrics.Cursors);

        // Without the summaries every object is opened up front, and the answer does not move.
        Assert.Equal(sorted, await RowsAsync(dataset.ScanBuilder().WithSummaries(false).InKeyOrder("key")));
        Assert.Equal(reversed, await RowsAsync(dataset.ScanBuilder().WithSummaries(false).InKeyOrder("key", descending: true)));
        Assert.Equal(
            dataset.ObjectCount,
            (await dataset.ScanBuilder().WithSummaries(false).InKeyOrder("key").ExplainAsync()).Cursors);
    }

    private static async Task<List<(long Key, double Measure)>> RowsAsync(DatasetScanBuilder scan)
    {
        List<(long Key, double Measure)> rows = [];
        await foreach (RecordBatch batch in scan.ExecuteAsync())
        {
            ReadOnlySpan<long> keys = batch.Column("key"u8).AsPrimitive<long>().Values;
            ReadOnlySpan<double> measures = batch.Column("measure"u8).AsPrimitive<double>().Values;
            for (int row = 0; row < batch.RowCount; row++)
            {
                rows.Add((keys[row], measures[row]));
            }
        }

        return rows;
    }

    /// <summary>The first <paramref name="count"/> rows, and not one enumeration more: a LIMIT.</summary>
    private static async Task<List<(long Key, double Measure)>> FirstAsync(DatasetScanBuilder scan, int count)
    {
        List<(long Key, double Measure)> rows = [];
        await foreach (RecordBatch batch in scan.ExecuteAsync())
        {
            ReadOnlySpan<long> keys = batch.Column("key"u8).AsPrimitive<long>().Values;
            ReadOnlySpan<double> measures = batch.Column("measure"u8).AsPrimitive<double>().Values;
            for (int row = 0; row < batch.RowCount && rows.Count < count; row++)
            {
                rows.Add((keys[row], measures[row]));
            }

            if (rows.Count == count)
            {
                break;
            }
        }

        return rows;
    }

    private static async Task<List<long>> IdsAsync(DatasetScanBuilder scan)
    {
        List<long> ids = [];
        await foreach (RecordBatch batch in scan.ExecuteAsync())
        {
            ids.AddRange(batch.Column("id"u8).AsPrimitive<long>().Values.ToArray());
        }

        return ids;
    }

    /// <summary>The IEEE 754 total order as a signed integer: negative NaN first, positive NaN last.</summary>
    private static long TotalOrder(double value)
    {
        long bits = BitConverter.DoubleToInt64Bits(value);
        return bits < 0 ? bits ^ long.MaxValue : bits;
    }

    private static List<(long Key, double Measure)> Sorted(IEnumerable<long> keys)
    {
        List<long> ordered = [.. keys];
        ordered.Sort();
        List<(long Key, double Measure)> rows = new List<(long Key, double Measure)>(ordered.Count);
        foreach (long key in ordered)
        {
            rows.Add((key, key / 4.0));
        }

        return rows;
    }

    /// <summary><paramref name="count"/> consecutive keys from <paramref name="from"/>.</summary>
    private static long[] Range(long from, int count)
    {
        long[] keys = new long[count];
        for (int i = 0; i < count; i++)
        {
            keys[i] = from + i;
        }

        return keys;
    }

    /// <summary>One object's keys: <paramref name="residue"/>, then every <paramref name="step"/>-th after it.</summary>
    private static long[] Stride(int residue, int step)
    {
        long[] keys = new long[PerObject];
        for (int i = 0; i < PerObject; i++)
        {
            keys[i] = ((long)i * step) + residue;
        }

        return keys;
    }

    private static DType Schema(DTypeArena types) => types.Struct(
        ["key", "measure"],
        [types.Primitive(PType.I64, Nullability.NonNullable), types.Primitive(PType.F64, Nullability.NonNullable)],
        Nullability.NonNullable);

    private static DatasetOptions Clustered() => Unclustered() with { ClusteringKey = ["key"] };

    private static DatasetOptions Unclustered() => new DatasetOptions
    {
        Seed = 0x0DE2_5EED,

        // Small blocks, so that an object is several batches and a compaction's target is reached.
        Write = new VortexWriteOptions { RowBlockSize = 64, DataBlockTargetBytes = 512 },
    };

    /// <summary>The keys, in a fixed shuffle that is nothing like their order, with measure = key / 4.</summary>
    private static async IAsyncEnumerable<RecordBatch> Shuffled(DTypeArena types, DType schema, long[] keys, int seed)
    {
        long[] shuffled = [.. keys];
        Random random = new Random(seed);
        for (int i = shuffled.Length - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }

        const int size = 125;
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType f64 = types.Primitive(PType.F64, Nullability.NonNullable);
        for (int start = 0; start < shuffled.Length; start += size)
        {
            int count = Math.Min(size, shuffled.Length - start);
            CanonicalArena arena = new CanonicalArena();
            VortexBuffer keyBuffer = arena.Allocate(count * sizeof(long), sizeof(long), out Span<byte> keyBytes);
            VortexBuffer measures = arena.Allocate(count * sizeof(double), sizeof(double), out Span<byte> measureBytes);
            Span<long> keyValues = MemoryMarshal.Cast<byte, long>(keyBytes);
            Span<double> measureValues = MemoryMarshal.Cast<byte, double>(measureBytes);
            for (int row = 0; row < count; row++)
            {
                keyValues[row] = shuffled[start + row];
                measureValues[row] = shuffled[start + row] / 4.0;
            }

            int keyNode = arena.AddPrimitive(i64, count, Validity.NonNullable, PType.I64, keyBuffer);
            int measureNode = arena.AddPrimitive(f64, count, Validity.NonNullable, PType.F64, measures);
            int root = arena.AddStruct(schema, count, Validity.NonNullable, [keyNode, measureNode]);
            using RecordBatch batch = new RecordBatch(arena, root, start);
            yield return batch;
            await Task.CompletedTask;
        }
    }

    /// <summary>One batch of a float key and an id, in the order given.</summary>
    private static async IAsyncEnumerable<RecordBatch> Floats(DTypeArena types, DType schema, double[] values, long[] ids)
    {
        DType f64 = types.Primitive(PType.F64, Nullability.NonNullable);
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        CanonicalArena arena = new CanonicalArena();
        VortexBuffer valueBuffer = arena.Allocate(values.Length * sizeof(double), sizeof(double), out Span<byte> valueBytes);
        VortexBuffer idBuffer = arena.Allocate(ids.Length * sizeof(long), sizeof(long), out Span<byte> idBytes);
        values.CopyTo(MemoryMarshal.Cast<byte, double>(valueBytes));
        ids.CopyTo(MemoryMarshal.Cast<byte, long>(idBytes));
        int valueNode = arena.AddPrimitive(f64, values.Length, Validity.NonNullable, PType.F64, valueBuffer);
        int idNode = arena.AddPrimitive(i64, ids.Length, Validity.NonNullable, PType.I64, idBuffer);
        int root = arena.AddStruct(schema, values.Length, Validity.NonNullable, [valueNode, idNode]);
        using RecordBatch batch = new RecordBatch(arena, root, 0);
        yield return batch;
        await Task.CompletedTask;
    }
}
