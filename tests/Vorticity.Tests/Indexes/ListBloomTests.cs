// A list's elements, indexed and asked - docs/10-indexes.md §5.1's "list elements", step 28b.
//
// The Bloom builder hashes each element of a valid row into the row's block, `ListContains` asks
// whether a row's list holds a value, and the scan answers it the same with the filters as without:
// 10 §6.6's acceptance test, over values present in one block, absent everywhere, held only by a
// null element, or held only by the list of a null row. The predicate's own semantics come first,
// because every equivalence below is measured against it.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Indexes;
using Vorticity.Layouts;
using Vorticity.Scan;
using Vorticity.Tests.Scan;
using Vorticity.Tests.Writing;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Indexes;

public sealed class ListBloomTests
{
    private const int Rows = 20_000;
    private const int Block = 512;
    private static readonly Guid Pinned = new Guid("28b28b28-28b2-48b2-8b28-b28b28b28b28");

    // ------------------------------------------------------------------------------ the predicate

    [Fact]
    public void ListContainsIsThreeValuedAndANullElementMatchesNothing()
    {
        // [1, 2] · [] · null · [null, 3] · [3, 3] · [null]
        Batch batch = LongLists(
            [[1, 2], [], null, [null, 3], [3, 3], [null]]);
        FieldExpr items = Expr.Field("items");

        Assert.Equal("FFUTTF", batch.Evaluate(Expr.ListContains(items, FilterLiteral.From(3L))));
        Assert.Equal("TTUFFT", batch.Evaluate(Expr.Not(Expr.ListContains(items, FilterLiteral.From(3L)))));
        Assert.Equal("FFUFFF", batch.Evaluate(Expr.ListContains(items, FilterLiteral.From(7L))));
        Assert.Equal("UUUUUU", batch.Evaluate(Expr.ListContains(items, FilterLiteral.Null)));
        Assert.Equal("TFUFFF", batch.Evaluate(Expr.ListContains(items, FilterLiteral.From(1))));
    }

    [Fact]
    public void AFixedSizeListStringsAndTheTwoZeros()
    {
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();

        // A nullable triple of i32: (1, 2, 3) · (4, 5, 6) · null.
        DType i32 = types.Primitive(PType.I32, Nullability.NonNullable);
        DType triple = types.FixedSizeList(i32, 3, Nullability.Nullable);
        int ints = Ints(arena, i32, [1, 2, 3, 4, 5, 6, 7, 8, 9]);
        int triples = arena.AddFixedSizeList(triple, 3, Mask(arena, types, [true, true, false]), ints, 3);

        // Strings, one past the twelve inline bytes: [a, bb] · [a long string, surely] · [].
        DType utf8 = types.Utf8(Nullability.NonNullable);
        DType strings = types.List(utf8, Nullability.NonNullable);
        int words = Strings(arena, utf8, ["a", "bb", "a long string, surely"]);
        int texts = View(arena, strings, words, [0, 2, 0], [2, 1, 0], Validity.NonNullable);

        // Floats: [-0.0] · [NaN] · [1.5].
        DType f64 = types.Primitive(PType.F64, Nullability.NonNullable);
        DType floats = types.List(f64, Nullability.NonNullable);
        int doubles = Doubles(arena, f64, [-0.0, double.NaN, 1.5]);
        int numbers = View(arena, floats, doubles, [0, 1, 2], [1, 1, 1], Validity.NonNullable);

        DType schema = types.Struct(["triple", "texts", "numbers"], [triple, strings, floats], Nullability.NonNullable);
        Batch batch = new Batch(arena, arena.AddStruct(schema, 3, Validity.NonNullable, [triples, texts, numbers]), 3);

        Assert.Equal("FTU", batch.Evaluate(Expr.ListContains(Expr.Field("triple"), FilterLiteral.From(5))));
        Assert.Equal("TFF", batch.Evaluate(Expr.ListContains(Expr.Field("texts"), FilterLiteral.From("bb"))));
        Assert.Equal("FTF", batch.Evaluate(Expr.ListContains(Expr.Field("texts"), FilterLiteral.From("a long string, surely"))));
        Assert.Equal("TFF", batch.Evaluate(Expr.ListContains(Expr.Field("numbers"), FilterLiteral.From(0.0))));
        Assert.Equal("FFF", batch.Evaluate(Expr.ListContains(Expr.Field("numbers"), FilterLiteral.From(double.NaN))));
    }

    [Fact]
    public void OnlyAListOfComparableElementsIsAsked()
    {
        Batch batch = LongLists([[1]]);
        Assert.Throws<NotSupportedException>(() => batch.Evaluate(Expr.ListContains(Expr.Field("id"), FilterLiteral.From(1L))));
        Assert.Throws<NotSupportedException>(() => batch.Evaluate(Expr.ListContains(Expr.Field("items"), FilterLiteral.From("1"))));
        Assert.Throws<ArgumentNullException>(() => Expr.ListContains(null!, FilterLiteral.From(1L)));
    }

    [Fact]
    public void ABatchCutFromALargerListAnswersForItsOwnRows()
    {
        ListShape shape = ListShapes.Build("list_i64", 2_000);
        Batch whole = new Batch(shape.Arena, shape.Root, shape.Rows);
        int slice = CanonicalSlice.SliceAcross(shape.Arena, shape.Arena, shape.Root, 700, 300);
        Batch part = new Batch(shape.Arena, slice, 300);
        foreach (long value in new[] { Element(1_000), Element(1_500), Element(5), 42L })
        {
            VortexExpr filter = Expr.ListContains(Expr.Field("items"), FilterLiteral.From(value));
            Assert.Equal(whole.Evaluate(filter).Substring(700, 300), part.Evaluate(filter));
        }
    }

    // ------------------------------------------------------------------------------ the index

    [Fact]
    public async Task TheWriterBuildsAFilterOverTheElements()
    {
        Decoders.EnsureRegistered();
        ListShape shape = ListShapes.Build("list_i64", Rows);
        string path = await WriteAsync(shape, Policy);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path);
            VortexIndexInfo info = Assert.Single(await file.ReadIndexesAsync());
            Assert.Equal(("items", IndexKinds.BloomSbbf, VortexIndexLayout.FilterTree), (info.Column, info.Kind, info.Layout));
            Assert.Equal((long)Block, info.BlockLength);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    public static TheoryData<string, long> Values() => new()
    {
        { "in one row", Element(ElementOfRow(6_001)) },
        { "in another", Element(ElementOfRow(15_002)) },
        { "absent", 42 },
        { "only a null element", Element(11) },
        { "only a null row's list", Element(ElementOfNullRow()) },
    };

    [Theory]
    [MemberData(nameof(Values))]
    public async Task AScanAnswersTheSameWithTheFiltersAsWithout(string what, long value)
    {
        Decoders.EnsureRegistered();
        ListShape shape = ListShapes.Build("list_i64", Rows);
        string indexed = await WriteAsync(shape, Policy);
        string plain = await WriteAsync(shape, WritePolicy.None);
        try
        {
            VortexExpr filter = Expr.ListContains(Expr.Field("items"), FilterLiteral.From(value));
            List<string> expected = Matching(shape, filter);
            Assert.Equal(what.StartsWith("in ", StringComparison.Ordinal), expected.Count > 0);
            await using VortexFile withFilters = await VortexFile.OpenAsync(indexed);
            await using VortexFile without = await VortexFile.OpenAsync(plain);
            Assert.Equal(expected, await RowsAsync(withFilters, filter));
            Assert.Equal(expected, await RowsAsync(without, filter));
            Assert.Equal(expected.Count, await withFilters.Scan().Where(filter).CountAsync());

            // What the filters proved: every block but those that hold the value.
            ScanPlan plan = await withFilters.Scan().Where(filter).ExplainAsync();
            int holding = BlocksHolding(shape, value);
            Assert.True(plan.LiveBlocks <= Math.Max(holding, 0) + FalsePositives(plan.Blocks), $"{what}: {plan.LiveBlocks} live of {plan.Blocks}, {holding} holding");
            PruningStep bloom = Assert.Single(plan.Pruning, step => step.Structure == "bloom filter");
            Assert.True(bloom.BlocksPruned > 0, what);

            // An equality on the list column is not the same question, and the filter claims nothing.
            ScanPlan equality = await withFilters.Scan().Where(Expr.Eq(Expr.Field("items"), Expr.Literal(FilterLiteral.From(value)))).ExplainAsync();
            Assert.DoesNotContain(equality.Pruning, step => step.Structure == "bloom filter" && step.BlocksPruned > 0);

            // The file-level filter answers for the whole file.
            Assert.Equal(expected.Count > 0 || holding > 0, await withFilters.MayMatchAsync(filter));
        }
        finally
        {
            System.IO.File.Delete(indexed);
            System.IO.File.Delete(plain);
        }
    }

    [Fact]
    public async Task AListUnderANullStructNamesNothing()
    {
        // `person.tags`: "ghost" is only ever in the tags of a null person.
        Decoders.EnsureRegistered();
        const int rows = 4 * Block;
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        DType utf8 = types.Utf8(Nullability.NonNullable);
        DType tags = types.List(utf8, Nullability.NonNullable);
        DType person = types.Struct(["tags"], [tags], Nullability.Nullable);
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType schema = types.Struct(["id", "person"], [i64, person], Nullability.NonNullable);

        string[] values = new string[rows * 2];
        long[] offsets = new long[rows];
        long[] sizes = new long[rows];
        bool[] valid = new bool[rows];
        for (int row = 0; row < rows; row++)
        {
            valid[row] = row % 7 != 3;
            values[2 * row] = valid[row] ? "tag-" + (row % 97).ToString(System.Globalization.CultureInfo.InvariantCulture) : "ghost";
            values[(2 * row) + 1] = "shared-" + (row % 13).ToString(System.Globalization.CultureInfo.InvariantCulture);
            offsets[row] = 2L * row;
            sizes[row] = 2;
        }

        int elements = Strings(arena, utf8, values);
        int list = View(arena, tags, elements, offsets, sizes, Validity.NonNullable);
        int people = arena.AddStruct(person, rows, Mask(arena, types, valid), [list]);
        int ids = Longs(arena, i64, [.. Enumerable.Range(0, rows).Select(row => (long)row)]);
        int root = arena.AddStruct(schema, rows, Validity.NonNullable, [ids, people]);

        string path = Path.Combine(Path.GetTempPath(), $"vorticity-listbloom-{Guid.NewGuid():N}.vortex");
        try
        {
            VortexWriteOptions options = new VortexWriteOptions
            {
                RowBlockSize = Block,
                Identity = Pinned,
                Indexes = WritePolicy.None.For("person.tags", IndexPolicy.Bloom(minDistinct: 1)),
                IndexBudgetPerMille = Unbounded,
            };

            await using (VortexFileWriter writer = VortexFileWriter.Create(path, schema, options))
            {
                using RecordBatch batch = new RecordBatch(arena, root, 0);
                await writer.WriteAsync(batch);
                WriteReport report = await writer.CompleteAsync();
                IndexWriteReport built = Assert.IsType<IndexWriteReport>(report.Index("person.tags", IndexKinds.BloomSbbf));
                Assert.True(built.Outcome == IndexOutcome.Built, built.Reason);
            }

            await using VortexFile file = await VortexFile.OpenAsync(path);
            VortexExpr ghost = Expr.ListContains(Expr.Field("person.tags"), FilterLiteral.From("ghost"));
            Assert.Equal(0, await file.Scan().Where(ghost).CountAsync());
            ScanPlan plan = await file.Scan().Where(ghost).ExplainAsync();
            Assert.Equal(0, plan.LiveBlocks);

            VortexExpr tag = Expr.ListContains(Expr.Field("person.tags"), FilterLiteral.From("tag-5"));
            Assert.Equal(
                Enumerable.Range(0, rows).Count(row => valid[row] && row % 97 == 5),
                await file.Scan().Where(tag).CountAsync());
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task TheWriterSaysWhatItCannotIndex()
    {
        Decoders.EnsureRegistered();
        ListShape lists = ListShapes.Build("list_list_i32", 1_000);
        ListShape map = ListShapes.Build("map_utf8_i64", 1_000);
        ListShape texts = ListShapes.Build("list_utf8", 1_000);

        Assert.Contains("holds List", await ReasonAsync(lists, IndexPolicy.Bloom()), StringComparison.Ordinal);
        Assert.Contains("Map", await ReasonAsync(map, IndexPolicy.Bloom()), StringComparison.Ordinal);
        Assert.Contains("trigram", await ReasonAsync(texts, IndexPolicy.NgramBloom()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutoWeighsAListLikeAnyOtherColumn()
    {
        Decoders.EnsureRegistered();
        ListShape shape = ListShapes.Build("list_utf8", Rows);
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-listbloom-{Guid.NewGuid():N}.vortex");
        try
        {
            await using VortexFileWriter writer = VortexFileWriter.Create(
                path, shape.Schema, new VortexWriteOptions { RowBlockSize = Block, Identity = Pinned });
            using (RecordBatch batch = new RecordBatch(shape.Arena, shape.Root, 0))
            {
                await writer.WriteAsync(batch);
            }

            WriteReport report = await writer.CompleteAsync();
            IndexWriteReport items = Assert.IsType<IndexWriteReport>(report.Index("items", IndexKinds.BloomSbbf));
            Assert.True(
                items.Outcome == IndexOutcome.Built || items.Reason!.StartsWith("Auto gave it up", StringComparison.Ordinal),
                items.Reason);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AnIndexAddedLaterAnswersAsTheOneWrittenWithTheData()
    {
        Decoders.EnsureRegistered();
        ListShape shape = ListShapes.Build("list_i64", Rows);
        string later = await WriteAsync(shape, WritePolicy.None);
        string written = await WriteAsync(shape, Policy);
        try
        {
            IReadOnlyList<IndexWriteReport> reports = await VortexFileIndexer.AppendIndexesAsync(
                later, Policy, new VortexWriteOptions { IndexBudgetPerMille = Unbounded });
            Assert.Contains(reports, r => r.Path == "items" && r.Outcome == IndexOutcome.Built);

            VortexExpr filter = Expr.ListContains(Expr.Field("items"), FilterLiteral.From(Element(12_345)));
            await using VortexFile a = await VortexFile.OpenAsync(later);
            await using VortexFile b = await VortexFile.OpenAsync(written);
            ScanPlan planA = await a.Scan().Where(filter).ExplainAsync();
            ScanPlan planB = await b.Scan().Where(filter).ExplainAsync();
            Assert.Equal(planB.LiveBlocks, planA.LiveBlocks);
            Assert.Equal(await RowsAsync(b, filter), await RowsAsync(a, filter));
        }
        finally
        {
            System.IO.File.Delete(later);
            System.IO.File.Delete(written);
        }
    }

    [Fact]
    public async Task AZoneOfNullListsHoldsNoMatchEitherWay()
    {
        // The first block's lists are all null; the zone map's null count proves it holds no row
        // for the predicate and none for its negation.
        Decoders.EnsureRegistered();
        List<long?[]?> rows = [];
        for (int row = 0; row < 3 * Block; row++)
        {
            rows.Add(row < Block ? null : [row, row + 1]);
        }

        Batch batch = LongLists([.. rows]);
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-listbloom-{Guid.NewGuid():N}.vortex");
        try
        {
            await using (VortexFileWriter writer = VortexFileWriter.Create(
                path, batch.Schema, new VortexWriteOptions { RowBlockSize = Block, Identity = Pinned, Indexes = WritePolicy.None }))
            {
                using RecordBatch record = new RecordBatch(batch.Arena, batch.Root, 0);
                await writer.WriteAsync(record);
                await writer.CompleteAsync();
            }

            await using VortexFile file = await VortexFile.OpenAsync(path);
            VortexExpr contains = Expr.ListContains(Expr.Field("items"), FilterLiteral.From(Block + 5L));
            foreach (VortexExpr filter in new[] { contains, Expr.Not(contains) })
            {
                ScanPlan plan = await file.Scan().Where(filter).ExplainAsync();
                PruningStep zones = Assert.Single(plan.Pruning, step => step.Structure == "zone map");
                Assert.Equal(1, zones.BlocksPruned);
            }

            // Rows 516 and 517 hold 517; the null lists are neither.
            Assert.Equal(2, await file.Scan().Where(contains).CountAsync());
            Assert.Equal((2 * Block) - 2, await file.Scan().Where(Expr.Not(contains)).CountAsync());
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    // ------------------------------------------------------------------------------ plumbing

    private static readonly WritePolicy Policy = WritePolicy.None.For("items", IndexPolicy.Bloom(resolutions: 3));

    /// <summary>An index budget no filter here reaches.</summary>
    private const int Unbounded = 1_000_000;

    /// <summary>The value of element <paramref name="e"/> of `list_i64` (`ListShapes`).</summary>
    private static long Element(int e) => 1_000_000 + ((e * 7919L) % 100_003);

    /// <summary>The size of row <paramref name="row"/> of `list_i64` (`ListShapes`).</summary>
    private static int SizeOf(int row) => row % 13 == 0 ? (row % 26 == 0 ? 2 : 0) : row % 5;

    /// <summary>
    /// A valid element of the first valid, non-empty row of `list_i64` from <paramref name="row"/>
    /// on: a value some row holds.
    /// </summary>
    private static int ElementOfRow(int row)
    {
        int named = 0;
        for (int before = 0; before < row; before++)
        {
            named += SizeOf(before);
        }

        for (; ; row++)
        {
            int size = SizeOf(row);
            for (int e = named; row % 13 != 0 && e < named + size; e++)
            {
                if (e % 11 != 0)
                {
                    return e;
                }
            }

            named += size;
        }
    }

    /// <summary>An element named only by a null row of `list_i64`: row 26 names two, and its list is null.</summary>
    private static int ElementOfNullRow()
    {
        // Rows 0..25 name these elements before row 26; row 0 is itself null and names two.
        int named = 0;
        for (int row = 0; row < 26; row++)
        {
            named += SizeOf(row);
        }

        // Row 26's first element, which is valid (its index is not a multiple of eleven).
        return named % 11 == 0 ? named + 1 : named;
    }

    /// <summary>A generous bound on the blocks a 1 % filter lets through by mistake.</summary>
    private static int FalsePositives(int blocks) => 2 + (blocks / 10);

    private static async Task<string> WriteAsync(ListShape shape, WritePolicy policy)
    {
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-listbloom-{Guid.NewGuid():N}.vortex");
        // A block of these lists holds about a thousand distinct values, so its filters outweigh the
        // column, which compresses to a few bytes a row: the budget is lifted for the question asked.
        VortexWriteOptions options = new VortexWriteOptions
        {
            RowBlockSize = Block,
            DataBlockTargetBytes = 1 << 15,
            Identity = Pinned,
            Indexes = policy,
            IndexBudgetPerMille = Unbounded,
        };

        await using VortexFileWriter writer = VortexFileWriter.Create(path, shape.Schema, options);
        for (int start = 0; start < shape.Rows; start += 1_000)
        {
            int count = Math.Min(1_000, shape.Rows - start);
            int slice = CanonicalSlice.SliceAcross(shape.Arena, shape.Arena, shape.Root, start, count);
            using RecordBatch batch = new RecordBatch(shape.Arena, slice, start);
            await writer.WriteAsync(batch);
        }

        await writer.CompleteAsync();
        return path;
    }

    private static async Task<string> ReasonAsync(ListShape shape, IndexPolicy policy)
    {
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-listbloom-{Guid.NewGuid():N}.vortex");
        try
        {
            await using VortexFileWriter writer = VortexFileWriter.Create(
                path, shape.Schema, new VortexWriteOptions { Indexes = WritePolicy.None.For("items", policy) });
            using (RecordBatch batch = new RecordBatch(shape.Arena, shape.Root, 0))
            {
                await writer.WriteAsync(batch);
            }

            WriteReport report = await writer.CompleteAsync();
            IndexWriteReport index = Assert.Single(report.Indexes, r => r.Path == "items");
            Assert.Equal(IndexOutcome.Abandoned, index.Outcome);
            return index.Reason!;
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>The source rows the filter selects, rendered.</summary>
    private static List<string> Matching(ListShape shape, VortexExpr filter)
    {
        using RecordBatch batch = new RecordBatch(shape.Arena, shape.Root, 0);
        byte[] truth = new byte[shape.Rows];
        FilterEvaluator.Evaluate(filter, shape.Arena, shape.Root, shape.Rows, truth);
        List<string> all = [];
        ListShapes.Describe(batch, all);
        return [.. all.Where((_, row) => truth[row] == Trilean.True)];
    }

    /// <summary>The blocks whose rows' valid lists hold <paramref name="value"/> in a valid element.</summary>
    private static int BlocksHolding(ListShape shape, long value)
    {
        byte[] truth = new byte[shape.Rows];
        FilterEvaluator.Evaluate(
            Expr.ListContains(Expr.Field("items"), FilterLiteral.From(value)), shape.Arena, shape.Root, shape.Rows, truth);
        return Enumerable.Range(0, (shape.Rows + Block - 1) / Block)
            .Count(block => truth.AsSpan(block * Block, Math.Min(Block, shape.Rows - (block * Block))).Contains(Trilean.True));
    }

    private static async Task<List<string>> RowsAsync(VortexFile file, VortexExpr filter)
    {
        List<string> rows = [];
        await foreach (RecordBatch batch in file.Scan().Where(filter).ExecuteAsync())
        {
            ListShapes.Describe(batch, rows);
        }

        return rows;
    }

    /// <summary>An in-memory struct of an id and nullable lists of nullable i64.</summary>
    private static Batch LongLists(long?[]?[] rows)
    {
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        DType element = types.Primitive(PType.I64, Nullability.Nullable);
        DType list = types.List(element, Nullability.Nullable);
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType schema = types.Struct(["id", "items"], [i64, list], Nullability.NonNullable);

        List<long> values = [];
        List<bool> present = [];
        long[] offsets = new long[rows.Length];
        long[] sizes = new long[rows.Length];
        bool[] valid = new bool[rows.Length];
        for (int row = 0; row < rows.Length; row++)
        {
            valid[row] = rows[row] is not null;
            offsets[row] = values.Count;
            foreach (long? value in rows[row] ?? [])
            {
                values.Add(value ?? 0);
                present.Add(value is not null);
            }

            sizes[row] = values.Count - offsets[row];
        }

        int elementsNode = Longs(arena, element, [.. values], Mask(arena, types, [.. present]));
        int lists = View(arena, list, elementsNode, offsets, sizes, Mask(arena, types, valid));
        int ids = Longs(arena, i64, [.. Enumerable.Range(0, rows.Length).Select(row => (long)row)]);
        int root = arena.AddStruct(schema, rows.Length, Validity.NonNullable, [ids, lists]);
        return new Batch(arena, root, rows.Length) { Schema = schema };
    }

    private sealed class Batch(CanonicalArena arena, int root, int rows)
    {
        internal CanonicalArena Arena { get; } = arena;

        internal int Root { get; } = root;

        internal DType Schema { get; init; }

        /// <summary>The filter's verdict per row: T, F or U.</summary>
        internal string Evaluate(VortexExpr filter)
        {
            byte[] truth = new byte[rows];
            FilterEvaluator.Evaluate(filter, Arena, Root, rows, truth);
            return string.Concat(truth.Select(t => t == Trilean.True ? 'T' : t == Trilean.False ? 'F' : 'U'));
        }
    }

    private static int View(CanonicalArena arena, DType dtype, int elements, long[] offsets, long[] sizes, Validity validity)
    {
        VortexBuffer offsetBuffer = arena.Allocate(Math.Max(offsets.Length, 1) * sizeof(long), sizeof(long), out Span<byte> offsetBytes);
        VortexBuffer sizeBuffer = arena.Allocate(Math.Max(sizes.Length, 1) * sizeof(long), sizeof(long), out Span<byte> sizeBytes);
        MemoryMarshal.AsBytes(offsets.AsSpan()).CopyTo(offsetBytes);
        MemoryMarshal.AsBytes(sizes.AsSpan()).CopyTo(sizeBytes);
        return arena.AddListView(
            dtype, offsets.Length, validity, elements,
            offsetBuffer.Slice(0, offsets.Length * sizeof(long)), PType.I64,
            sizeBuffer.Slice(0, sizes.Length * sizeof(long)), PType.I64);
    }

    private static int Longs(CanonicalArena arena, DType dtype, long[] values, Validity? validity = null)
    {
        VortexBuffer buffer = arena.Allocate(Math.Max(values.Length, 1) * sizeof(long), sizeof(long), out Span<byte> bytes);
        MemoryMarshal.AsBytes(values.AsSpan()).CopyTo(bytes);
        return arena.AddPrimitive(
            dtype, values.Length, validity ?? Validity.FromNullability(dtype.Nullability), PType.I64,
            buffer.Slice(0, values.Length * sizeof(long)));
    }

    private static int Ints(CanonicalArena arena, DType dtype, int[] values)
    {
        VortexBuffer buffer = arena.Allocate(values.Length * sizeof(int), sizeof(int), out Span<byte> bytes);
        MemoryMarshal.AsBytes(values.AsSpan()).CopyTo(bytes);
        return arena.AddPrimitive(dtype, values.Length, Validity.NonNullable, PType.I32, buffer);
    }

    private static int Doubles(CanonicalArena arena, DType dtype, double[] values)
    {
        VortexBuffer buffer = arena.Allocate(values.Length * sizeof(double), sizeof(double), out Span<byte> bytes);
        MemoryMarshal.AsBytes(values.AsSpan()).CopyTo(bytes);
        return arena.AddPrimitive(dtype, values.Length, Validity.NonNullable, PType.F64, buffer);
    }

    private static int Strings(CanonicalArena arena, DType dtype, string[] values)
    {
        byte[][] utf8 = [.. values.Select(Encoding.UTF8.GetBytes)];
        int heap = utf8.Sum(v => v.Length > 12 ? v.Length : 0);
        VortexBuffer data = arena.Allocate(Math.Max(heap, 1), 1, out Span<byte> dataBytes);
        VortexBuffer views = arena.Allocate(values.Length * 16, 16, out Span<byte> view);
        view.Clear();
        int offset = 0;
        for (int i = 0; i < utf8.Length; i++)
        {
            Span<byte> one = view.Slice(i * 16, 16);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(one, (uint)utf8[i].Length);
            if (utf8[i].Length <= 12)
            {
                utf8[i].CopyTo(one[4..]);
                continue;
            }

            utf8[i].AsSpan(0, 4).CopyTo(one[4..]);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(one[12..], offset);
            utf8[i].CopyTo(dataBytes[offset..]);
            offset += utf8[i].Length;
        }

        return arena.AddVarBinView(dtype, values.Length, Validity.FromNullability(dtype.Nullability), views, [data]);
    }

    private static Validity Mask(CanonicalArena arena, DTypeArena types, bool[] valid)
    {
        VortexBuffer bits = arena.Allocate(Math.Max((valid.Length + 7) / 8, 1), 8, out Span<byte> raw);
        raw.Clear();
        for (int i = 0; i < valid.Length; i++)
        {
            if (valid[i])
            {
                raw[i >> 3] |= (byte)(1 << (i & 7));
            }
        }

        return Validity.Bitmap(arena.AddBool(types.Bool(Nullability.NonNullable), valid.Length, Validity.NonNullable, bits, 0));
    }
}
