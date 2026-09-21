// The split planner in isolation, over layout trees built here rather than found in the corpus.
//
// The corpus cannot exercise this: its chunked layouts have three chunks and 300 rows (the writer
// filters empty chunks and collapses single-child chunked layouts), it contains no layout tree with
// an unknown id in a position the planner must walk past, and nothing in it claims three billion
// rows. Those are exactly the shapes where a split planner goes wrong - an off-by-one at a chunk
// boundary, a throw that belongs to LayoutReaderTable, an allocation proportional to the row count.
//
// The trees are detached (LayoutTree.Parse's tools overload): no file, no segments to read, just
// the structure the planner walks.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

using Vorticity.Arrays;
using Vorticity.Layouts;
using Vorticity.Scanning;
using Vorticity.Serialization.FlatBuffers;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Scan;

public sealed class SplitPlanTests
{
    private static readonly string[] Ids = ["vortex.flat", "vortex.chunked", "vortex.struct", "vortex.unknown77"];
    private const ushort Flat = 0;
    private const ushort Chunked = 1;
    private const ushort Struct = 2;
    private const ushort Unknown = 3;

    [Fact]
    public void AFlatLayoutIsOneSplit()
    {
        LayoutTree tree = Tree(Node(Flat, 1000, segments: [0]), I64());
        Assert.Equal([new RowRange(0, 1000)], Splits(tree, new RowRange(0, 1000), 8192));
    }

    [Fact]
    public void AFlatLayoutSplitsEvenlyUnderACap()
    {
        LayoutTree tree = Tree(Node(Flat, 1000, segments: [0]), I64());

        // 1000 rows capped at 400 is three splits of 334, 334, 332 - not 400, 400, 200.
        Assert.Equal(
            [new RowRange(0, 334), new RowRange(334, 668), new RowRange(668, 1000)],
            Splits(tree, new RowRange(0, 1000), 400));
    }

    [Fact]
    public void ChunkBoundariesAreTheNaturalSplits()
    {
        LayoutTree tree = Tree(
            Node(Chunked, 300, children: [Node(Flat, 100, segments: [0]), Node(Flat, 50, segments: [1]), Node(Flat, 150, segments: [2])]),
            I64());

        Assert.Equal(
            [new RowRange(0, 100), new RowRange(100, 150), new RowRange(150, 300)],
            Splits(tree, new RowRange(0, 300), 8192));
    }

    [Theory]
    [InlineData(0, 300)]
    [InlineData(0, 100)]
    [InlineData(99, 101)]
    [InlineData(100, 100)]
    [InlineData(100, 150)]
    [InlineData(149, 151)]
    [InlineData(150, 300)]
    [InlineData(1, 299)]
    [InlineData(250, 300)]
    public void ARangeIsCoveredExactlyOnceWhateverTheChunkBoundaries(long start, long end)
    {
        LayoutTree tree = Tree(
            Node(Chunked, 300, children: [Node(Flat, 100, segments: [0]), Node(Flat, 50, segments: [1]), Node(Flat, 150, segments: [2])]),
            I64());

        List<RowRange> splits = Splits(tree, new RowRange(start, end), 8192);

        long cursor = start;
        for (int i = 0; i < splits.Count; i++)
        {
            Assert.Equal(cursor, splits[i].Start);
            Assert.True(splits[i].Length > 0, "a split must not be empty");
            cursor = splits[i].End;
        }

        Assert.Equal(end, cursor);
    }

    [Fact]
    public void AChunkedLayoutWithNoChunksAndNoRowsHasNoSplits()
    {
        LayoutTree tree = Tree(Node(Chunked, 0), I64());
        Assert.Empty(Splits(tree, new RowRange(0, 0), 8192));
    }

    [Fact]
    public void NestedChunkedLayoutsContributeTheirInteriorBoundaries()
    {
        LayoutTree tree = Tree(
            Node(Chunked, 300, children:
            [
                Node(Chunked, 200, children: [Node(Flat, 120, segments: [0]), Node(Flat, 80, segments: [1])]),
                Node(Flat, 100, segments: [2]),
            ]),
            I64());

        Assert.Equal(
            [new RowRange(0, 120), new RowRange(120, 200), new RowRange(200, 300)],
            Splits(tree, new RowRange(0, 300), 8192));
    }

    [Fact]
    public void AStructUnionsItsSelectedFieldsBoundariesAndIgnoresTheRest()
    {
        // Field 0 chunks at 100/300; field 1 chunks at 150/300. Projecting one must not inherit the
        // other's boundaries - that is the whole point of walking only the selected subtrees.
        LayoutTree tree = Tree(
            Node(Struct, 300, children:
            [
                Node(Chunked, 300, children: [Node(Flat, 100, segments: [0]), Node(Flat, 200, segments: [1])]),
                Node(Chunked, 300, children: [Node(Flat, 150, segments: [2]), Node(Flat, 150, segments: [3])]),
            ]),
            StructOf2());

        Assert.Equal(
            [new RowRange(0, 100), new RowRange(100, 300)],
            Splits(tree, new RowRange(0, 300), 8192, Mask(0)));

        Assert.Equal(
            [new RowRange(0, 150), new RowRange(150, 300)],
            Splits(tree, new RowRange(0, 300), 8192, Mask(1)));

        // Both selected: the union of the two boundary sets, deduplicated and sorted.
        Assert.Equal(
            [new RowRange(0, 100), new RowRange(100, 150), new RowRange(150, 300)],
            Splits(tree, new RowRange(0, 300), 8192, Mask(0, 1)));
    }

    [Fact]
    public void AStructWithNoFieldSelectedStillCoversItsRows()
    {
        LayoutTree tree = Tree(
            Node(Struct, 300, children:
            [
                Node(Flat, 300, segments: [0]),
                Node(Flat, 300, segments: [1]),
            ]),
            StructOf2());

        Assert.Equal([new RowRange(0, 300)], Splits(tree, new RowRange(0, 300), 8192, FieldMask.Empty));
    }

    [Fact]
    public void AnUnknownLayoutIsOpaqueToThePlannerAndFatalOnlyAtTheReaderTable()
    {
        LayoutTree tree = Tree(Node(Unknown, 500, segments: [0]), I64());

        // The planner treats it as indivisible and does NOT throw: the only "layout"
        // VortexUnsupportedException belongs to LayoutReaderTable.
        Assert.Equal([new RowRange(0, 500)], Splits(tree, new RowRange(0, 500), 8192));

        Assert.Equal(LayoutEncodingId.Unknown, tree.Root.Encoding);
        VortexUnsupportedException error = Assert.Throws<VortexUnsupportedException>(
            () => LayoutReaderTable.Get(tree.Root.Encoding, tree.Root.EncodingIdText));
        Assert.Equal("vortex.unknown77", error.ComponentId);
        Assert.Equal(VortexComponentKind.Layout, error.Kind);
    }

    [Fact]
    public void AnUnknownLayoutInAnUnselectedFieldIsNeverWalked()
    {
        LayoutTree tree = Tree(
            Node(Struct, 300, children:
            [
                Node(Chunked, 300, children: [Node(Flat, 100, segments: [0]), Node(Flat, 200, segments: [1])]),
                Node(Unknown, 300, segments: [2]),
            ]),
            StructOf2());

        // Field 1's subtree contributes no boundary because it is never visited.
        Assert.Equal(
            [new RowRange(0, 100), new RowRange(100, 300)],
            Splits(tree, new RowRange(0, 300), 8192, Mask(0)));
    }

    [Fact]
    public void AThreeBillionRowLayoutCappedAtOneRowPlansInConstantSpaceAndTime()
    {
        // The trap: sub-dividing eagerly would materialize three billion boundaries. The plan holds
        // only the layout's own boundaries and SplitCursor sub-divides lazily.
        const long Rows = 3_000_000_000L;
        LayoutTree tree = Tree(Node(Flat, (ulong)Rows, segments: [0]), I64());

        Stopwatch clock = Stopwatch.StartNew();
        SplitPlan plan = SplitPlan.Compute(tree, new RowRange(0, Rows), FieldMask.All, 1);
        clock.Stop();

        Assert.Equal(2, plan.BoundaryCount);
        Assert.True(
            clock.ElapsedMilliseconds < 1000,
            string.Create(CultureInfo.InvariantCulture, $"planning took {clock.ElapsedMilliseconds} ms"));

        // And the cursor really does hand out one row at a time, from the right place.
        SplitCursor cursor = plan.CreateCursor();
        for (long i = 0; i < 5; i++)
        {
            Assert.True(cursor.TryNext(out RowRange range));
            Assert.Equal(new RowRange(i, i + 1), range);
        }
    }

    [Fact]
    public void TheNaturalBatchSizeIsTheDefaultWhenThereIsNoZoneMap()
    {
        LayoutTree tree = Tree(Node(Flat, 10, segments: [0]), I64());
        Assert.Equal(8192, SplitPlan.NaturalBatchRows(tree));
    }

    [Fact]
    public void ComputeRejectsANonPositiveCap()
    {
        LayoutTree tree = Tree(Node(Flat, 10, segments: [0]), I64());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SplitPlan.Compute(tree, new RowRange(0, 10), FieldMask.All, 0));
        Assert.Throws<ArgumentNullException>(
            () => SplitPlan.Compute(null!, new RowRange(0, 10), FieldMask.All, 8192));
    }

    // --------------------------------------------------------------------------------- helpers

    private static List<RowRange> Splits(LayoutTree tree, RowRange rows, long cap) =>
        Splits(tree, rows, cap, FieldMask.All);

    private static List<RowRange> Splits(LayoutTree tree, RowRange rows, long cap, FieldMask mask)
    {
        SplitPlan plan = SplitPlan.Compute(tree, rows, mask, cap);
        SplitCursor cursor = plan.CreateCursor();
        List<RowRange> splits = new List<RowRange>();
        while (cursor.TryNext(out RowRange range))
        {
            splits.Add(range);

            // A runaway cursor is a hang, not a wrong answer; cap it so the test fails instead.
            Assert.True(splits.Count < 10_000, "the cursor did not terminate");
        }

        return splits;
    }

    private static FieldMask Mask(params int[] fields)
    {
        FieldMaskBuilder builder = new FieldMaskBuilder();
        for (int i = 0; i < fields.Length; i++)
        {
            builder.IncludeField(fields[i]);
        }

        return builder.Build();
    }

    private static DType I64()
    {
        DTypeArena arena = new DTypeArena();
        return arena.Primitive(PType.I64, Nullability.NonNullable);
    }

    private static DType StructOf2()
    {
        DTypeArena arena = new DTypeArena();
        DType i64 = arena.Primitive(PType.I64, Nullability.NonNullable);
        return arena.Struct(["a", "b"], [i64, i64], Nullability.NonNullable);
    }

    private static LayoutTree Tree(LayoutSpecNode root, DType schema)
    {
        using FlatBufferBuilder builder = new FlatBufferBuilder();
        int offset = Write(builder, root);
        byte[] bytes = builder.FinishToArray(offset);
        return LayoutTree.Parse(bytes, schema, Ids, segmentCount: 16);
    }

    private static int Write(FlatBufferBuilder builder, LayoutSpecNode node)
    {
        int[] children = new int[node.Children.Count];
        for (int i = 0; i < children.Length; i++)
        {
            children[i] = Write(builder, node.Children[i]);
        }

        int childrenVector = children.Length == 0 ? 0 : builder.CreateOffsetVector(children);
        int segmentVector = node.Segments.Length == 0
            ? 0
            : builder.CreateScalarVector<uint>(node.Segments);

        builder.StartTable();
        builder.AddUInt16(SchemaFieldIds.LayoutEncoding, node.Encoding);
        builder.AddUInt64(SchemaFieldIds.LayoutRowCount, node.RowCount);
        if (childrenVector != 0)
        {
            builder.AddOffset(SchemaFieldIds.LayoutChildren, childrenVector);
        }

        if (segmentVector != 0)
        {
            builder.AddOffset(SchemaFieldIds.LayoutSegments, segmentVector);
        }

        return builder.EndTable();
    }

    private static LayoutSpecNode Node(
        ushort encoding, ulong rowCount, LayoutSpecNode[]? children = null, uint[]? segments = null) =>
        new LayoutSpecNode(encoding, rowCount, children ?? [], segments ?? []);

    private sealed class LayoutSpecNode
    {
        internal LayoutSpecNode(ushort encoding, ulong rowCount, LayoutSpecNode[] children, uint[] segments)
        {
            Encoding = encoding;
            RowCount = rowCount;
            Children = children;
            Segments = segments;
        }

        internal ushort Encoding { get; }

        internal ulong RowCount { get; }

        internal IReadOnlyList<LayoutSpecNode> Children { get; }

        internal uint[] Segments { get; }
    }
}
