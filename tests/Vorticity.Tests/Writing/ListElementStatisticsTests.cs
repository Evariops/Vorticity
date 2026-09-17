// A list's elements, summarized in the parent's blocks - docs/11-write-strategy.md §3.2.4, step 28.
//
// Block i of a list's elements covers the elements of parent rows [8192·i, 8192·(i+1)), so the
// chooser reads a list chunk's elements from their blocks the way it reads a struct field's. That
// is only sound when those blocks summarized exactly the array the chunk writes, in its order, and
// a bound or a step read from the wrong elements writes wrong values. So these tests hold three
// things:
//
//   * the merge of the element blocks IS a summary of the elements array (unit, then every list
//     chunk of every write, through the audit hook);
//   * a block whose window does not abut the one before is scattered, and the chooser measures the
//     chunks that cover it itself, while rows in any order inside abutting windows are served;
//   * what is written reads back to what was given, and a single-chunk write is byte for byte the
//     write that measured its elements.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Layouts;
using Vorticity.Scan;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class ListElementStatisticsTests
{
    private const int Rows = 20_000;

    /// <summary>One identity for every write, so that two writes compare byte for byte.</summary>
    private static readonly Guid Pinned = new Guid("28a28a28-28a2-48a2-8a28-a28a28a28a28");

    // ------------------------------------------------------------------------------ the blocks

    [Theory]
    [InlineData(8192)]
    [InlineData(512)]
    [InlineData(100)]
    public void TheElementBlocksOfOrderedRowsMergeToTheElementsArray(int blockRows)
    {
        ListShape shape = ListShapes.Build("list_i64", Rows);
        CanonicalArena arena = shape.Arena;
        int list = arena.GetNode(shape.Root).GetFieldIndex(1);
        int elements = arena.GetNode(list).ElementsIndex;
        ColumnWriter column = Feed(arena, list, Rows, blockRows);

        int total = arena.GetNode(elements).Length;
        ColumnWriter? child = column.ElementsOver(0, column.Blocks.Count, total);
        Assert.NotNull(child);
        Assert.Equal(column.Blocks.Count, child!.Blocks.Count);
        AssertSame(Direct(arena, elements, 0, total), child.Chunk(0, column.Blocks.Count));

        // A range that starts past row 0: its elements start where the rows before it ended.
        int first = 2;
        int from = NamedBefore(arena, list, first * blockRows);
        int count = total - from;
        ColumnWriter? tail = column.ElementsOver(first, column.Blocks.Count - first, count);
        Assert.NotNull(tail);
        AssertSame(Direct(arena, elements, from, count), tail!.Chunk(first, column.Blocks.Count - first));

        // Another count than the blocks name is not this chunk's.
        Assert.Null(column.ElementsOver(0, column.Blocks.Count, total - 1));
    }

    [Fact]
    public void BlocksWhoseRowsNameNoElementStillCloseInStep()
    {
        // Two blocks of empty lists, then a block of one element a row.
        const int block = 100;
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        DType element = types.Primitive(PType.I64, Nullability.NonNullable);
        DType dtype = types.List(element, Nullability.NonNullable);
        int list = Lists(arena, dtype, element, 3 * block, row => row < 2 * block ? (0, 0) : (row - (2 * block), 1), elements: block);

        ColumnWriter column = Feed(arena, list, 3 * block, block);
        Assert.Equal(3, column.Blocks.Count);
        ColumnWriter? empty = column.ElementsOver(0, 2, 0);
        Assert.NotNull(empty);
        Assert.Equal(3, empty!.Blocks.Count);
        Assert.False(empty.Chunk(0, 2).IsPresent);
        Assert.Equal(block, column.ElementsOver(2, 1, block)!.Chunk(2, 1).Rows);
        Assert.Equal(block, column.ElementsOver(0, 3, block)!.Chunk(0, 3).Rows);
    }

    [Fact]
    public void AWindowThatDoesNotAbutTheOneBeforeScattersItsBlockAndItsSubtree()
    {
        // One element a row, in order, except that rows 150 and 250 trade theirs. Block 1's window
        // is [100, 251), which abuts block 0's; block 2's is [150, 300), which does not abut
        // block 1's: element 150 would be summarized twice and elements 251-299 laid after it.
        const int block = 100;
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        DType a = types.Primitive(PType.I64, Nullability.NonNullable);
        DType item = types.Struct(["a"], [a], Nullability.NonNullable);
        DType dtype = types.List(item, Nullability.NonNullable);
        int list = Lists(
            arena, dtype, a, 3 * block,
            row => row switch { 150 => (250, 1), 250 => (150, 1), _ => (row, 1) },
            elements: 3 * block,
            wrap: (values, count) => arena.AddStruct(item, count, Validity.NonNullable, [values]));

        ColumnWriter column = Feed(arena, list, 3 * block, block);
        Assert.NotNull(column.ElementsOver(0, 1, block));
        Assert.NotNull(column.ElementsOver(1, 1, 151));
        Assert.NotNull(column.ElementsOver(0, 2, 251));
        Assert.Null(column.ElementsOver(2, 1, 150));
        Assert.Null(column.ElementsOver(0, 3, 3 * block));

        // The struct under the elements, and its field, carry the flag too.
        ColumnWriter items = column.Field(0)!;
        Assert.True(items.Chunk(2, 1).Scattered);
        Assert.True(items.Field(0)!.Chunk(2, 1).Scattered);
        Assert.False(items.Field(0)!.Chunk(1, 1).Scattered);
    }

    [Fact]
    public void RowsInAnyOrderInsideAWindowAreItsWindow()
    {
        // Rows trade their elements two by two: each block's window still abuts the one before,
        // and holds the elements in the order the file will.
        ListShape shape = ListShapes.Build(ListShapes.Swapped, 1000);
        CanonicalArena arena = shape.Arena;
        int list = arena.GetNode(shape.Root).GetFieldIndex(1);
        int elements = arena.GetNode(list).ElementsIndex;
        ColumnWriter column = Feed(arena, list, 1000, 100);
        ColumnWriter? child = column.ElementsOver(0, 10, 2000);
        Assert.NotNull(child);
        AssertSame(Direct(arena, elements, 0, 2000), child!.Chunk(0, 10));
        AssertSame(Direct(arena, elements, 200, 200), column.ElementsOver(1, 1, 200)!.Chunk(1, 1));
    }

    [Fact]
    public void ReversedRowsScatterEveryBlockAfterTheFirst()
    {
        ListShape shape = ListShapes.Build(ListShapes.Scattered, 1000);
        CanonicalArena arena = shape.Arena;
        int list = arena.GetNode(shape.Root).GetFieldIndex(1);
        ColumnWriter column = Feed(arena, list, 1000, 100);

        // The first range of a batch answers to nothing: its window is what the compactor keeps.
        Assert.NotNull(column.ElementsOver(0, 1, 200));
        for (int block = 1; block < 10; block++)
        {
            Assert.Null(column.ElementsOver(block, 1, 200));
        }
    }

    [Fact]
    public void AFixedSizeListNamesItsRowsTimesTheSize()
    {
        ListShape shape = ListShapes.Build("fsl_f64", Rows);
        CanonicalArena arena = shape.Arena;
        int list = arena.GetNode(shape.Root).GetFieldIndex(1);
        int elements = arena.GetNode(list).ElementsIndex;
        ColumnWriter column = Feed(arena, list, Rows, 512);
        ColumnWriter? child = column.ElementsOver(0, column.Blocks.Count, Rows * 3);
        Assert.NotNull(child);
        AssertSame(Direct(arena, elements, 0, Rows * 3), child!.Chunk(0, column.Blocks.Count));
        AssertSame(Direct(arena, elements, 3 * 512, 3 * 512), column.ElementsOver(1, 1, 3 * 512)!.Chunk(1, 1));
    }

    // ------------------------------------------------------------------------------ the writer

    public static TheoryData<string, int, int?> Writes()
    {
        TheoryData<string, int, int?> data = [];
        foreach (string shape in ListShapes.Contiguous)
        {
            foreach (int batchRows in new[] { 97, 8193, Rows })
            {
                data.Add(shape, batchRows, 8192);
                data.Add(shape, batchRows, 512);
                data.Add(shape, batchRows, null);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task EveryListChunkReadsItsElementsFromTheirBlocks(string name, int batchRows, int? rowBlock)
    {
        ListShape shape = ListShapes.Build(name, Rows);
        Written written = await Write(shape, batchRows, rowBlock, elementStatistics: true);
        try
        {
            Assert.True(
                written.Unserved == 0,
                $"{name} at batch {batchRows}, block {rowBlock?.ToString() ?? "null"}: {written.Unserved} list " +
                "chunk(s) measured their elements");
            Assert.True(written.Handed > 0, "no list chunk reached the chooser with its elements' summary");
            Assert.True(written.Wrong.Count == 0, string.Join("\n", written.Wrong.Take(10)));
            Assert.Equal(Source(shape), await Read(written.Path));
        }
        finally
        {
            System.IO.File.Delete(written.Path);
        }
    }

    [Theory]
    [InlineData(1000, 512)]
    [InlineData(1000, 8192)]
    [InlineData(97, 512)]
    public async Task WindowsThatDoNotAbutAreMeasuredAndReadBackAsWritten(int batchRows, int rowBlock)
    {
        // Batches that straddle their blocks, so that a batch's second range answers to its first.
        ListShape shape = ListShapes.Build(ListShapes.Scattered, Rows);
        Written written = await Write(shape, batchRows, rowBlock, elementStatistics: true);
        try
        {
            Assert.True(written.Unserved > 0, "a chunk of reversed rows was summarized from its blocks");
            Assert.True(written.Wrong.Count == 0, string.Join("\n", written.Wrong.Take(10)));
            Assert.Equal(Source(shape), await Read(written.Path));
        }
        finally
        {
            System.IO.File.Delete(written.Path);
        }
    }

    [Theory]
    [InlineData(8192, 512, true)]
    [InlineData(Rows, 8192, true)]
    [InlineData(97, 512, false)]
    [InlineData(1000, 8192, false)]
    public async Task RowsTradingTheirElementsAreSummarizedWhereTheirWindowsAbut(int batchRows, int rowBlock, bool whole)
    {
        // Pairs of rows trade their elements. A cut between the two rows of a pair leaves windows
        // that do not abut, which is measured; a cut between pairs leaves nothing to measure.
        ListShape shape = ListShapes.Build(ListShapes.Swapped, Rows);
        Written written = await Write(shape, batchRows, rowBlock, elementStatistics: true);
        try
        {
            if (whole)
            {
                Assert.Equal(0, written.Unserved);
            }

            Assert.True(written.Handed > 0);
            Assert.True(written.Wrong.Count == 0, string.Join("\n", written.Wrong.Take(10)));
            Assert.Equal(Source(shape), await Read(written.Path));
        }
        finally
        {
            System.IO.File.Delete(written.Path);
        }
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task AOneChunkWriteIsTheWriteThatMeasuredItsElements(string name)
    {
        // One batch, one chunk: plan memory has no chunk to carry a plan into, so the summary and
        // the measurement must choose the same plans, byte for byte.
        ListShape shape = ListShapes.Build(name, Rows);
        Written summarized = await Write(shape, Rows, rowBlock: null, elementStatistics: true);
        Written measured = await Write(shape, Rows, rowBlock: null, elementStatistics: false);
        try
        {
            Assert.Equal(1, summarized.Chunks);
            Assert.True(summarized.Handed > 0);
            Assert.Equal(0, measured.Handed);
            Assert.Equal(
                await System.IO.File.ReadAllBytesAsync(measured.Path),
                await System.IO.File.ReadAllBytesAsync(summarized.Path));
        }
        finally
        {
            System.IO.File.Delete(summarized.Path);
            System.IO.File.Delete(measured.Path);
        }
    }

    public static TheoryData<string> Shapes() => [.. ListShapes.Contiguous];

    [Theory]
    [InlineData("list_i64")]
    [InlineData("map_utf8_i64")]
    [InlineData("list_list_i32")]
    public async Task TheFirstAppendedChunkConsultsTheElementsPlan(string name)
    {
        // docs/11 §3.8: the seed reaches a list's elements, which keep a memory since step 28. The
        // append is one block, so without the seed its elements would have no plan to consult.
        const int Block = 8192;
        ListShape shape = ListShapes.Build(name, 2 * Block);
        Written first = await Write(shape, Block, Block, elementStatistics: true, rows: Block);
        string path = first.Path;
        try
        {
            ColumnWriter elements;
            await using (VortexFileWriter writer = await VortexFileWriter.AppendAsync(path))
            {
                int slice = CanonicalSlice.SliceAcross(shape.Arena, shape.Arena, shape.Root, Block, Block);
                using (RecordBatch batch = new RecordBatch(shape.Arena, slice, Block))
                {
                    await writer.WriteAsync(batch);
                }

                await writer.CompleteAsync();

                // The first leaf under the list: the elements, a map's keys, the inner values.
                elements = writer.ColumnState(1).Field(0)!;
                while (elements.Field(0) is { } child)
                {
                    elements = child;
                }

                Assert.Equal(0, writer.ElementChunksWithoutStatistics);
            }

            Assert.Equal(1, elements.PlansPriced);
            Assert.Equal(Source(shape), await Read(path));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>Every corpus file with a list: its chunks' elements come from their blocks.</summary>
    [Theory]
    [MemberData(nameof(CorpusLists))]
    public async Task TheCorpusListsReadTheirElementsFromTheirBlocks(string id)
    {
        Decoders.EnsureRegistered();
        string summarized = Path.Combine(Path.GetTempPath(), $"vorticity-elements-{Guid.NewGuid():N}.vortex");
        string measured = Path.Combine(Path.GetTempPath(), $"vorticity-elements-{Guid.NewGuid():N}.vortex");
        try
        {
            (long unserved, int handed, List<string> wrong) = await Rewrite(id, summarized, elementStatistics: true);
            await Rewrite(id, measured, elementStatistics: false);
            Assert.True(unserved == 0, $"{id}: {unserved} list chunk(s) measured their elements");
            Assert.True(wrong.Count == 0, string.Join("\n", wrong.Take(10)));
            Assert.Equal(
                await System.IO.File.ReadAllBytesAsync(measured),
                await System.IO.File.ReadAllBytesAsync(summarized));
            _ = handed;
        }
        finally
        {
            System.IO.File.Delete(summarized);
            System.IO.File.Delete(measured);
        }
    }

    public static TheoryData<string> CorpusLists()
    {
        TheoryData<string> data = [];
        string root = Path.Combine(Corpus.Root, "corpus");
        foreach (string file in Directory.EnumerateFiles(root, "*.vortex", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string id = Path.GetRelativePath(root, file)[..^".vortex".Length].Replace(Path.DirectorySeparatorChar, '/');
            string leaf = id[(id.LastIndexOf('/') + 1)..];
            if (leaf.StartsWith("list", StringComparison.Ordinal) || leaf.StartsWith("map", StringComparison.Ordinal)
                || leaf.StartsWith("fixed_size_list", StringComparison.Ordinal) || leaf.StartsWith("fsl", StringComparison.Ordinal))
            {
                data.Add(id);
            }
        }

        return data;
    }

    // ------------------------------------------------------------------------------ plumbing

    private sealed record Written(string Path, long Unserved, int Handed, int Chunks, List<string> Wrong);

    private static async Task<Written> Write(
        ListShape shape, int batchRows, int? rowBlock, bool elementStatistics, int? rows = null)
    {
        int written = rows ?? shape.Rows;
        Decoders.EnsureRegistered();
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-elements-{Guid.NewGuid():N}.vortex");
        // A small byte target, so that a block-aligned write is several chunks with tails carried
        // between them, rather than the one chunk twenty thousand rows of lists make under 1 MiB.
        VortexWriteOptions options = new VortexWriteOptions
        {
            RowBlockSize = rowBlock,
            DataBlockTargetBytes = 1 << 15,
            Identity = Pinned,
            ElementStatistics = elementStatistics,
        };

        List<string> wrong = [];
        int handed = 0;
        ArrayBlobWriter.ElementsHanded.Value = (arena, elements, stats) => Audit(arena, elements, stats, wrong, ref handed);
        try
        {
            await using VortexFileWriter writer = VortexFileWriter.Create(path, shape.Schema, options);
            for (int start = 0; start < written; start += batchRows)
            {
                int count = Math.Min(batchRows, written - start);
                int slice = CanonicalSlice.SliceAcross(shape.Arena, shape.Arena, shape.Root, start, count);
                using RecordBatch batch = new RecordBatch(shape.Arena, slice, start);
                await writer.WriteAsync(batch);
            }

            WriteReport report = await writer.CompleteAsync();
            return new Written(path, writer.ElementChunksWithoutStatistics, handed, report.ChunkRows.Count, wrong);
        }
        finally
        {
            ArrayBlobWriter.ElementsHanded.Value = null;
        }
    }

    private static async Task<(long Unserved, int Handed, List<string> Wrong)> Rewrite(
        string id, string path, bool elementStatistics)
    {
        List<string> wrong = [];
        int handed = 0;
        ArrayBlobWriter.ElementsHanded.Value = (arena, elements, stats) => Audit(arena, elements, stats, wrong, ref handed);
        try
        {
            await using VortexFile source = await VortexFile.OpenAsync(Corpus.Path(id), CancellationToken.None);
            VortexWriteOptions options = new VortexWriteOptions
            {
                Identity = Pinned,
                ElementStatistics = elementStatistics,
            };

            await using VortexFileWriter writer = VortexFileWriter.Create(path, source.Schema, options);
            await foreach (RecordBatch batch in source.Scan().WithMaxBatchRows(1024).ExecuteAsync())
            {
                await writer.WriteAsync(batch);
            }

            await writer.CompleteAsync();
            return (writer.ElementChunksWithoutStatistics, handed, wrong);
        }
        finally
        {
            ArrayBlobWriter.ElementsHanded.Value = null;
        }
    }

    /// <summary>
    /// A summary handed to the chooser against a measurement of the node it was handed with: every
    /// number the chooser reads.
    /// </summary>
    private static void Audit(CanonicalArena arena, int elements, BlockStats handed, List<string> wrong, ref int count)
    {
        if (!handed.IsPresent)
        {
            return;
        }

        count++;
        int length = arena.GetNode(elements).Length;
        BlockStats direct = Direct(arena, elements, 0, length);
        string? difference = Difference(direct, handed);
        if (difference is not null)
        {
            wrong.Add($"elements of {length}: {difference}");
        }
    }

    private static BlockStats Direct(CanonicalArena arena, int node, int start, int count)
    {
        BlockStats direct = default;
        BlockStatsPass.Accumulate(arena, node, start, count, ref direct);
        return direct;
    }

    private static void AssertSame(BlockStats direct, BlockStats merged)
    {
        string? difference = Difference(direct, merged);
        Assert.True(difference is null, difference);
    }

    private static string? Difference(BlockStats direct, BlockStats merged)
    {
        List<string> differences = [];
        void Check<T>(string what, T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                differences.Add($"{what}: measured {expected}, summarized {actual}");
            }
        }

        Check("rows", direct.Rows, merged.Rows);
        Check("nulls", direct.NullCount, merged.NullCount);
        Check("bytes", direct.TotalBytes, merged.TotalBytes);
        Check("bounded", direct.HasBounds, merged.HasBounds);
        if (direct.HasBounds && merged.HasBounds)
        {
            Check("min", direct.Min, merged.Min);
            Check("max", direct.Max, merged.Max);
        }

        Check("run boundaries tracked", direct.HasRunBoundaries, merged.HasRunBoundaries);
        if (direct.HasRunBoundaries)
        {
            Check("runs", direct.RunCount, merged.RunCount);
        }

        Check("sorted", direct.IsSorted, merged.IsSorted);
        Check("strictly sorted", direct.IsStrictSorted, merged.IsStrictSorted);
        bool directSteps = direct.DeltaKnown && !direct.DeltaBroken;
        bool mergedSteps = merged.DeltaKnown && !merged.DeltaBroken;
        Check("progression", directSteps, mergedSteps);
        if (directSteps && mergedSteps)
        {
            Check("step", direct.Delta, merged.Delta);
        }

        Check("scattered", false, merged.Scattered);
        return differences.Count == 0 ? null : string.Join("; ", differences);
    }

    /// <summary>Feeds rows of a list node in block ranges, closing each block.</summary>
    private static ColumnWriter Feed(CanonicalArena arena, int list, int rows, int blockRows)
    {
        ColumnWriter column = new ColumnWriter();
        int filled = 0;
        for (int offset = 0; offset < rows;)
        {
            int take = Math.Min(blockRows - filled, rows - offset);
            column.Accumulate(arena, list, offset, take);
            offset += take;
            filled += take;
            if (filled == blockRows)
            {
                column.CloseBlock();
                filled = 0;
            }
        }

        if (filled > 0)
        {
            column.CloseBlock();
        }

        return column;
    }

    /// <summary>The elements the rows before <paramref name="row"/> name, in a list whose rows name them in order.</summary>
    private static int NamedBefore(CanonicalArena arena, int list, int row)
    {
        CanonicalNode node = arena.GetNode(list);
        ListElements.Window(node, arena.GetNode(node.ElementsIndex).Length, row, node.Length - row, out int from, out _);
        return from;
    }

    /// <summary>A list view of i64 elements <c>7 000 000 + e</c> whose row <c>r</c> is <paramref name="range"/>(r).</summary>
    private static int Lists(
        CanonicalArena arena, DType dtype, DType element, int rows, Func<int, (int Offset, int Size)> range, int elements,
        Func<int, int, int>? wrap = null)
    {
        VortexBuffer values = arena.Allocate(elements * sizeof(long), sizeof(long), out Span<byte> bytes);
        Span<long> longs = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(bytes);
        for (int e = 0; e < elements; e++)
        {
            longs[e] = 7_000_000 + e;
        }

        int child = arena.AddPrimitive(element, elements, Validity.NonNullable, PType.I64, values);
        if (wrap is not null)
        {
            child = wrap(child, elements);
        }

        VortexBuffer offsets = arena.Allocate(rows * sizeof(int), sizeof(int), out Span<byte> offsetBytes);
        VortexBuffer sizes = arena.Allocate(rows * sizeof(int), sizeof(int), out Span<byte> sizeBytes);
        Span<int> offsetValues = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(offsetBytes);
        Span<int> sizeValues = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(sizeBytes);
        for (int row = 0; row < rows; row++)
        {
            (offsetValues[row], sizeValues[row]) = range(row);
        }

        return arena.AddListView(dtype, rows, Validity.NonNullable, child, offsets, PType.I32, sizes, PType.I32);
    }

    private static List<string> Source(ListShape shape)
    {
        List<string> rows = [];
        using RecordBatch batch = new RecordBatch(shape.Arena, shape.Root, 0);
        ListShapes.Describe(batch, rows);
        return rows;
    }

    private static async Task<List<string>> Read(string path)
    {
        List<string> rows = [];
        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
        await foreach (RecordBatch batch in file.Scan().ExecuteAsync())
        {
            ListShapes.Describe(batch, rows);
        }

        return rows;
    }
}
