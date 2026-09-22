// The dataset's summaries and its positional access, under one acceptance: every answer the
// dataset gives must equal the answer a single file would give.
//
// SO EVERY PRUNING TEST HERE IS TWO ASSERTIONS, never one. The first is the acceptance: the rows the
// scan returns with the summaries consulted are exactly the rows it returns with `WithSummaries
// (false)`, and exactly the rows one file returns. The second is the point: it got there having
// opened fewer objects, or having skipped a whole subtree without reading its pages. A pruning test
// that only checks the first proves nothing was broken; one that only checks the second proves
// nothing was answered. The defect this pair catches -- a bound folded the wrong way up the tree,
// which silently drops rows -- passes either assertion alone.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Scanning;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class DatasetSummaryTests
{
    private const int Rows = 1_000;

    [Fact]
    public async Task AFilterInsideOneObjectOpensOnlyThatObject()
    {
        // A predicate that a node's summaries refute skips the whole subtree -- here, at the leaf,
        // the object itself.
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);

        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Options());
        const int objects = 8;
        for (int i = 0; i < objects; i++)
        {
            await dataset.AppendAsync(Batches(types, schema, i * Rows, Rows));
        }

        // Wholly inside object 3's keys, which are [3000, 4000).
        VortexExpr filter = Expr.And(
            Expr.Ge(Expr.Field("key"), Expr.Literal(FilterLiteral.From(3_100L))),
            Expr.Lt(Expr.Field("key"), Expr.Literal(FilterLiteral.From(3_200L))));

        DatasetScanMetrics pruned = new DatasetScanMetrics();
        DatasetScanMetrics whole = new DatasetScanMetrics();
        List<long> withSummaries = await KeysAsync(dataset.ScanBuilder().Where(filter).WithMetrics(pruned));
        List<long> without = await KeysAsync(
            dataset.ScanBuilder().Where(filter).WithSummaries(false).WithMetrics(whole));

        // The acceptance: the same rows as one file, whichever way the scan got to them.
        byte[] single = await OneFileAsync(types, schema, objects * Rows);
        await using MemorySegmentSource source = new MemorySegmentSource(single);
        await using VortexFile file = await VortexFile.OpenAsync(source, new VortexOpenOptions(), default);
        List<long> expected = await KeysAsync(file.ScanBuilder().Where(filter));
        Assert.Equal(100, expected.Count);
        Assert.Equal(expected, withSummaries);
        Assert.Equal(expected, without);

        // And the point: one object opened instead of eight.
        Assert.Equal(1, pruned.ObjectsOpened);
        Assert.Equal(objects - 1, pruned.ObjectsSkipped);
        Assert.Equal(objects, whole.ObjectsOpened);
        Assert.Equal(0, whole.ObjectsSkipped);
        Console.Out.Write(FormattableString.Invariant(
            $"DATASET SUMMARIES: a filter over 1 of {objects} objects opened {pruned.ObjectsOpened} with the summaries and {whole.ObjectsOpened} without, for the same {expected.Count} rows.\n"));
    }

    [Fact]
    public async Task AValueNoObjectHoldsOpensNothingAtAll()
    {
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);

        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Options());
        for (int i = 0; i < 4; i++)
        {
            await dataset.AppendAsync(Batches(types, schema, i * Rows, Rows));
        }

        VortexExpr absent = Expr.Eq(Expr.Field("key"), Expr.Literal(FilterLiteral.From(-1L)));
        DatasetScanMetrics metrics = new DatasetScanMetrics();
        Assert.Empty(await KeysAsync(dataset.ScanBuilder().Where(absent).WithMetrics(metrics)));
        Assert.Equal(0, metrics.ObjectsOpened);
        Assert.Equal(4, metrics.ObjectsSkipped);
        Assert.Equal(0, await dataset.ScanBuilder().Where(absent).CountAsync());
    }

    [Fact]
    public async Task ANodeThatRefutesThePredicateCostsItsSubtreeNoRead()
    {
        // The summaries' claim that only a deep tree can show: the walk stops at an INTERNAL entry,
        // so the leaf pages under it are never read and their objects are never even considered.
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);

        await using MemoryObjectStore store = new MemoryObjectStore();
        // Pages of a few entries, so that sixteen objects make a tree of three levels rather than
        // the one page 128 KiB would hold them all in.
        await using VortexDataset dataset = await VortexDataset.CreateAsync(
            store, schema, Options() with { Rule = new FillBoundaryRule(400) });
        const int objects = 16;
        for (int i = 0; i < objects; i++)
        {
            await dataset.AppendAsync(Batches(types, schema, i * Rows, Rows));
        }

        Assert.True(dataset.Depth >= 3, $"sixteen objects in pages of 400 bytes should nest; depth is {dataset.Depth}");

        VortexExpr absent = Expr.Gt(Expr.Field("key"), Expr.Literal(FilterLiteral.From(1_000_000L)));
        DatasetScanMetrics metrics = new DatasetScanMetrics();
        Assert.Empty(await KeysAsync(dataset.ScanBuilder().Where(absent).WithMetrics(metrics)));
        Assert.True(metrics.SubtreesSkipped > 0, "a node's summaries should have refuted the predicate");
        Assert.Equal(0, metrics.ObjectsConsidered);
        Assert.Equal(0, metrics.ObjectsOpened);

        // And the acceptance again: with the summaries off, the same empty answer the long way.
        DatasetScanMetrics whole = new DatasetScanMetrics();
        Assert.Empty(await KeysAsync(dataset.ScanBuilder().Where(absent).WithSummaries(false).WithMetrics(whole)));
        Assert.Equal(objects, whole.ObjectsConsidered);
        Assert.Equal(0, whole.SubtreesSkipped);
        Console.Out.Write(FormattableString.Invariant(
            $"DATASET SUBTREES: a tree of depth {dataset.Depth} over {objects} objects answered an impossible filter by skipping {metrics.SubtreesSkipped} subtree(s) and considering {metrics.ObjectsConsidered} objects.\n"));
    }

    [Fact]
    public async Task RowsAcrossObjectsAreTheSameRowsOfOneFile()
    {
        // Access by position, `Rows(a, b)`, walks the insertion-order tree, whose nodes carry row
        // sums. The ranges below are chosen to fall inside one object, to straddle two, and to run
        // past the end -- the three cases an off-by-one lives in.
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);

        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Options());
        const int objects = 5;
        for (int i = 0; i < objects; i++)
        {
            await dataset.AppendAsync(Batches(types, schema, i * Rows, Rows));
        }

        byte[] single = await OneFileAsync(types, schema, objects * Rows);
        await using MemorySegmentSource source = new MemorySegmentSource(single);
        await using VortexFile file = await VortexFile.OpenAsync(source, new VortexOpenOptions(), default);

        (long From, long To)[] ranges =
        [
            (0, 1),
            (100, 900),
            (900, 1_100),
            (0, objects * Rows),
            (4_500, 10_000),
            (objects * Rows, objects * Rows + 10),
        ];

        foreach ((long from, long to) in ranges)
        {
            Assert.Equal(
                await KeysAsync(file.ScanBuilder().Rows(new RowRange(from, Math.Min(to, file.RowCount)))),
                await KeysAsync(dataset.ScanBuilder().Rows(from, to)));
        }

        // And it does not open what it does not need: rows 2 000..2 500 are object 2's alone.
        DatasetScanMetrics metrics = new DatasetScanMetrics();
        Assert.Equal(500, (await KeysAsync(dataset.ScanBuilder().Rows(2_000, 2_500).WithMetrics(metrics))).Count);
        Assert.Equal(1, metrics.ObjectsOpened);
        Assert.Equal(1, metrics.ObjectsConsidered);
    }

    [Fact]
    public async Task AnObjectOpenedOnceStaysOpen()
    {
        // A data object is immutable, so the second scan's opens are pure waste and the cache
        // is what removes them. The counters are the claim; the rows are the acceptance.
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);

        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Options());
        for (int i = 0; i < 4; i++)
        {
            await dataset.AppendAsync(Batches(types, schema, i * Rows, Rows));
        }

        DatasetScanMetrics first = new DatasetScanMetrics();
        DatasetScanMetrics second = new DatasetScanMetrics();
        List<long> once = await KeysAsync(dataset.ScanBuilder().WithMetrics(first));
        List<long> twice = await KeysAsync(dataset.ScanBuilder().WithMetrics(second));

        Assert.Equal(once, twice);
        Assert.Equal(0, first.CacheHits);
        Assert.Equal(4, second.CacheHits);
        Assert.Equal(4, second.ObjectsOpened);
    }

    [Fact]
    public void SummariesRoundTripThroughTheirCanonicalBytes()
    {
        ObjectSummaries summaries = ObjectSummaries.From(
        [
            new ColumnSummary("measure", FilterLiteral.From(1.5), true, FilterLiteral.From(9.5), true, true, 0, true),
            new ColumnSummary("key", FilterLiteral.From(7L), true, FilterLiteral.From(11L), true, true, 3, true),
            new ColumnSummary("name", FilterLiteral.From("a"), true, FilterLiteral.From("z"), true, false, 0, false),
        ]);

        // Sorted by path, so that one set of bounds has one encoding and a key set's pages stay
        // byte-identical.
        Assert.Equal(["key", "measure", "name"], [.. Paths(summaries)]);

        byte[] bytes = summaries.ToBytes();
        ObjectSummaries read = ObjectSummaries.FromBytes(bytes);
        Assert.Equal(summaries, read);
        Assert.Equal(bytes, read.ToBytes());

        Assert.True(read.TryGet("key", out ColumnSummary key));
        Assert.Equal(7L, key.Min.SignedValue);
        Assert.Equal(11L, key.Max.SignedValue);
        Assert.Equal(3, key.NullCount);
        Assert.False(read.TryGet("absent", out _));
        Assert.Empty(ObjectSummaries.Empty.ToBytes());
        Assert.Equal(0, ObjectSummaries.FromBytes([]).Count);
    }

    [Fact]
    public void OneSetOfBoundsHasOneEncoding()
    {
        // A key set has byte-identical pages and one root hash, so a summary that says nothing
        // must not be writable: two encodings of one meaning are two trees.
        ColumnSummary silent = new ColumnSummary("x", default, false, default, false, true, 0, false);
        Assert.True(silent.IsEmpty);
        Assert.Equal(0, ObjectSummaries.From([silent]).Count);

        // One column, path "x", flags 0: the encoding `From` refuses to produce.
        byte[] handWritten = [0x01, 0x01, (byte)'x', 0x00];
        Assert.Throws<CommitFormatException>(() => ObjectSummaries.FromBytes(handWritten));

        // And out of order, which a binary search over the paths would read as an absence.
        ObjectSummaries pair = ObjectSummaries.From(
        [
            new ColumnSummary("a", FilterLiteral.From(1L), true, FilterLiteral.From(2L), true, true, 0, false),
            new ColumnSummary("b", FilterLiteral.From(3L), true, FilterLiteral.From(4L), true, true, 0, false),
        ]);
        byte[] bytes = pair.ToBytes();
        byte[] swapped = Swapped(bytes);
        Assert.Throws<CommitFormatException>(() => ObjectSummaries.FromBytes(swapped));
    }

    /// <summary>The same two columns, written in the other order.</summary>
    private static byte[] Swapped(byte[] bytes)
    {
        // Both columns encode to the same length here -- one-byte paths, two signed bounds -- so
        // swapping them is a swap of two equal halves after the count.
        int half = (bytes.Length - 1) / 2;
        byte[] swapped = new byte[bytes.Length];
        swapped[0] = bytes[0];
        Array.Copy(bytes, 1 + half, swapped, 1, half);
        Array.Copy(bytes, 1, swapped, 1 + half, half);
        return swapped;
    }

    [Fact]
    public void AUnionIsTheLoosestBoundAndSilenceWinsOverIt()
    {
        ObjectSummaries left = ObjectSummaries.From(
        [
            new ColumnSummary("key", FilterLiteral.From(0L), true, FilterLiteral.From(10L), true, true, 1, true),
            new ColumnSummary("only", FilterLiteral.From(0L), true, FilterLiteral.From(1L), true, true, 0, true),
        ]);
        ObjectSummaries right = ObjectSummaries.From(
        [
            new ColumnSummary("key", FilterLiteral.From(5L), true, FilterLiteral.From(40L), true, false, 2, true),
        ]);

        ObjectSummaries union = ObjectSummaries.Union([left, right]);

        // The loosest bound over both, and the precision is lost the moment one side lost it.
        Assert.Equal(1, union.Count);
        Assert.True(union.TryGet("key", out ColumnSummary key));
        Assert.Equal(0L, key.Min.SignedValue);
        Assert.Equal(40L, key.Max.SignedValue);
        Assert.Equal(3, key.NullCount);
        Assert.False(key.IsExact);

        // A column only one side knows about is a column the union knows nothing about: the other
        // side's rows could hold anything, and a bound that excluded them would lose them.
        Assert.False(union.TryGet("only", out _));

        // And a part that says nothing at all makes the whole union say nothing.
        Assert.Equal(0, ObjectSummaries.Union([left, ObjectSummaries.Empty]).Count);
        Assert.Equal(0, ObjectSummaries.Union([]).Count);
        Assert.Equal(left, ObjectSummaries.Union([left]));
    }

    [Fact]
    public void ASummaryRefutesOnlyWhatItProves()
    {
        ObjectSummaries summaries = ObjectSummaries.From(
            [new ColumnSummary("key", FilterLiteral.From(10L), true, FilterLiteral.From(20L), true, true, 0, true)]);

        Assert.False(summaries.MayMatch(Pruner(Expr.Lt(Expr.Field("key"), Expr.Literal(FilterLiteral.From(10L)))), 100));
        Assert.False(summaries.MayMatch(Pruner(Expr.Gt(Expr.Field("key"), Expr.Literal(FilterLiteral.From(20L)))), 100));
        Assert.True(summaries.MayMatch(Pruner(Expr.Eq(Expr.Field("key"), Expr.Literal(FilterLiteral.From(15L)))), 100));

        // A column it says nothing about licenses nothing, so the answer is "may match".
        Assert.True(summaries.MayMatch(
            Pruner(Expr.Gt(Expr.Field("other"), Expr.Literal(FilterLiteral.From(99L)))), 100));

        // And an extent of no rows is not an extent the bounds describe.
        Assert.True(summaries.MayMatch(Pruner(Expr.Lt(Expr.Field("key"), Expr.Literal(FilterLiteral.From(0L)))), 0));
    }

    private static SummaryPruner Pruner(VortexExpr filter) => new SummaryPruner(filter);

    private static IEnumerable<string> Paths(ObjectSummaries summaries)
    {
        foreach (ColumnSummary column in summaries.Columns)
        {
            yield return column.Path;
        }
    }

    private static DType Schema(DTypeArena types) => types.Struct(
        ["key", "measure"],
        [types.Primitive(PType.I64, Nullability.NonNullable), types.Primitive(PType.F64, Nullability.NonNullable)],
        Nullability.NonNullable);

    private static DatasetOptions Options() => new DatasetOptions
    {
        Seed = 0x5ADDED_5EED,
        Write = new VortexWriteOptions { RowBlockSize = 256, DataBlockTargetBytes = 16 << 10 },
    };

    private static async Task<List<long>> KeysAsync(ScanBuilder scan)
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

    private static async Task<byte[]> OneFileAsync(DTypeArena types, DType schema, int rows)
    {
        System.IO.MemoryStream stream = new System.IO.MemoryStream();
        await using (VortexFileWriter writer = VortexFileWriter.Create(
            new StreamSegmentSink(stream), schema, Options().Write))
        {
            await foreach (RecordBatch batch in Batches(types, schema, 0, rows))
            {
                await writer.WriteAsync(batch);
            }

            await writer.CompleteAsync();
        }

        return stream.ToArray();
    }

    private static async IAsyncEnumerable<RecordBatch> Batches(
        DTypeArena types, DType schema, long from, int rows)
    {
        const int size = 500;
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
}
