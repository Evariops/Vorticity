// What the writer keeps of a list's child before it writes a chunk: the elements the chunk's rows name. Rows one after
// the other in their child keep it as a view, narrowed to the extent they span; rows that lie apart in it, as the rows of
// a batch laid out in another order than it was built in do, have their elements gathered in their own order.
//
// THE ORACLE IS THE LISTS THE ROWS HELD BEFORE: every row reads the same elements after, and the child holds no element
// a row does not name once rows lay apart.
using System;
using System.Linq;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class ChunkCompactorTests
{
    private static readonly DTypeArena Types = new DTypeArena();

    /// <summary>Rows taken one in ten of a batch name an eightieth of the extent they span: their elements are gathered, in their order.</summary>
    [Fact]
    public void RowsThatLieApartAreGatheredInTheirOrder()
    {
        long[] elements = Counting(1_000);
        long[] offsets = [.. Enumerable.Range(0, 8).Select(row => (long)(700 - (row * 100)))];
        long[] sizes = [.. Enumerable.Repeat(10L, 8)];
        CanonicalArena arena = new CanonicalArena();
        int list = List(arena, Primitive(arena, elements), offsets, sizes);
        long[][] before = Lists(arena, list);

        int compacted = ChunkCompactor.Compact(arena, list);

        Assert.NotEqual(list, compacted);
        Assert.Equal(before, Lists(arena, compacted));
        Assert.Equal(80, arena.GetNode(arena.GetNode(compacted).ElementsIndex).Length);
        Assert.Equal([0L, 10, 20, 30, 40, 50, 60, 70], Offsets(arena, compacted));
    }

    /// <summary>Rows one after the other over the whole child are the node they were: nothing is rebuilt.</summary>
    [Fact]
    public void RowsOverTheirWholeChildInOrderAreLeftAsTheyAre()
    {
        long[] elements = Counting(120);
        long[] offsets = [.. Enumerable.Range(0, 12).Select(row => (long)(row * 10))];
        long[] sizes = [.. Enumerable.Repeat(10L, 12)];
        CanonicalArena arena = new CanonicalArena();
        int list = List(arena, Primitive(arena, elements), offsets, sizes);

        Assert.Equal(list, ChunkCompactor.Compact(arena, list));
    }

    /// <summary>
    /// Rows one after the other in a part of their child, in the order of the child or against it, name all of the extent
    /// they span: the child is narrowed to it, a view still, and no element is copied a row at a time.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RowsThatFollowOneAnotherKeepAViewOfTheirExtent(bool backwards)
    {
        long[] elements = Counting(1_000);
        long[] offsets = [.. Enumerable.Range(0, 8).Select(row => (long)(400 + ((backwards ? 7 - row : row) * 10)))];
        long[] sizes = [.. Enumerable.Repeat(10L, 8)];
        CanonicalArena arena = new CanonicalArena();
        int list = List(arena, Primitive(arena, elements), offsets, sizes);
        long[][] before = Lists(arena, list);

        int compacted = ChunkCompactor.Compact(arena, list);

        Assert.Equal(before, Lists(arena, compacted));
        Assert.Equal(80, arena.GetNode(arena.GetNode(compacted).ElementsIndex).Length);
        Assert.Equal(offsets.Select(offset => offset - 400), Offsets(arena, compacted));
    }

    /// <summary>Rows that share their elements name more than the extent they span, and keep it as it lies: no row gets a copy of its own.</summary>
    [Fact]
    public void RowsThatShareTheirElementsStayAView()
    {
        long[] elements = Counting(1_000);
        long[] offsets = [.. Enumerable.Range(0, 16).Select(row => (long)(200 + (row % 4)))];
        long[] sizes = [.. Enumerable.Repeat(50L, 16)];
        CanonicalArena arena = new CanonicalArena();
        int list = List(arena, Primitive(arena, elements), offsets, sizes);
        long[][] before = Lists(arena, list);

        int compacted = ChunkCompactor.Compact(arena, list);

        Assert.Equal(before, Lists(arena, compacted));
        Assert.Equal(53, arena.GetNode(arena.GetNode(compacted).ElementsIndex).Length);
    }

    /// <summary>A gap between two runs of rows is an extent no row names: the elements on either side are gathered, and the gap is not written.</summary>
    [Fact]
    public void ElementsNoRowNamesBetweenTwoRunsAreLeftOut()
    {
        long[] elements = Counting(1_000);
        long[] offsets = [0, 10, 20, 500, 510];
        long[] sizes = [10, 10, 10, 10, 10];
        CanonicalArena arena = new CanonicalArena();
        int list = List(arena, Primitive(arena, elements), offsets, sizes);
        long[][] before = Lists(arena, list);

        int compacted = ChunkCompactor.Compact(arena, list);

        Assert.Equal(before, Lists(arena, compacted));
        Assert.Equal(50, arena.GetNode(arena.GetNode(compacted).ElementsIndex).Length);
    }

    /// <summary>
    /// An empty row names nothing whatever its offset says: it neither widens the extent nor counts, and among gathered rows
    /// it is given a place within the child.
    /// </summary>
    [Fact]
    public void AnEmptyRowNamesNothingWhereverItPoints()
    {
        long[] elements = Counting(1_000);
        long[] offsets = [900, 999, 100, 0, 300];
        long[] sizes = [5, 0, 5, 0, 5];
        CanonicalArena arena = new CanonicalArena();
        int list = List(arena, Primitive(arena, elements), offsets, sizes);
        long[][] before = Lists(arena, list);

        int compacted = ChunkCompactor.Compact(arena, list);

        Assert.Equal(before, Lists(arena, compacted));
        int held = arena.GetNode(arena.GetNode(compacted).ElementsIndex).Length;
        Assert.Equal(15, held);
        Assert.All(Offsets(arena, compacted), offset => Assert.InRange(offset, 0, held));
    }

    /// <summary>Rows that are all empty hold no element at all.</summary>
    [Fact]
    public void RowsThatAreAllEmptyKeepNoElement()
    {
        long[] elements = Counting(100);
        long[] offsets = [40, 90, 3];
        long[] sizes = [0, 0, 0];
        CanonicalArena arena = new CanonicalArena();
        int list = List(arena, Primitive(arena, elements), offsets, sizes);

        int compacted = ChunkCompactor.Compact(arena, list);

        Assert.Equal(0, arena.GetNode(arena.GetNode(compacted).ElementsIndex).Length);
        Assert.Equal(new long[3][] { [], [], [] }, Lists(arena, compacted));
    }

    /// <summary>
    /// A list of lists whose rows lie apart at both depths: the lists each row names are gathered, and since those share
    /// their own child still, their elements are gathered in turn.
    /// </summary>
    [Fact]
    public void AListOfListsIsGatheredAtBothDepths()
    {
        long[] elements = Counting(600);
        long[] innerOffsets = [.. Enumerable.Range(0, 200).Select(row => (long)(597 - (row * 3)))];
        long[] innerSizes = [.. Enumerable.Repeat(3L, 200)];
        long[] offsets = [180, 20, 100, 60];
        long[] sizes = [2, 2, 2, 2];
        CanonicalArena arena = new CanonicalArena();
        int inner = List(arena, Primitive(arena, elements), innerOffsets, innerSizes);
        int list = List(arena, inner, offsets, sizes);
        long[][][] before = ListsOfLists(arena, list);

        int compacted = ChunkCompactor.Compact(arena, list);

        Assert.Equal(before, ListsOfLists(arena, compacted));
        int gathered = arena.GetNode(compacted).ElementsIndex;
        Assert.Equal(8, arena.GetNode(gathered).Length);
        Assert.Equal(24, arena.GetNode(arena.GetNode(gathered).ElementsIndex).Length);
    }

    /// <summary>The numbers from 0, each its own place: what a row reads says where it read it.</summary>
    private static long[] Counting(int count) => [.. Enumerable.Range(0, count).Select(i => (long)i)];

    private static int Primitive(CanonicalArena arena, long[] values)
    {
        VortexBuffer buffer = arena.Allocate(values.Length * sizeof(long), sizeof(long), out Span<byte> bytes);
        values.AsSpan().CopyTo(MemoryMarshal.Cast<byte, long>(bytes));
        return arena.AddPrimitive(Types.Primitive(PType.I64, Nullability.NonNullable), values.Length, Validity.NonNullable, PType.I64, buffer);
    }

    private static int List(CanonicalArena arena, int child, long[] offsets, long[] sizes)
    {
        VortexBuffer offsetBuffer = arena.Allocate(offsets.Length * sizeof(long), sizeof(long), out Span<byte> offsetBytes);
        offsets.AsSpan().CopyTo(MemoryMarshal.Cast<byte, long>(offsetBytes));
        VortexBuffer sizeBuffer = arena.Allocate(sizes.Length * sizeof(long), sizeof(long), out Span<byte> sizeBytes);
        sizes.AsSpan().CopyTo(MemoryMarshal.Cast<byte, long>(sizeBytes));
        DType type = Types.List(arena.GetNode(child).DType, Nullability.NonNullable);
        return arena.AddListView(type, offsets.Length, Validity.NonNullable, child, offsetBuffer, PType.I64, sizeBuffer, PType.I64);
    }

    /// <summary>The offsets of a list's rows, whatever integers hold them.</summary>
    private static long[] Offsets(CanonicalArena arena, int list)
    {
        CanonicalNode node = arena.GetNode(list);
        return Widened(node.Offsets, node.OffsetPType, node.Length);
    }

    /// <summary>The numbers each row of a list of numbers names.</summary>
    private static long[][] Lists(CanonicalArena arena, int list)
    {
        CanonicalNode node = arena.GetNode(list);
        long[] offsets = Widened(node.Offsets, node.OffsetPType, node.Length);
        long[] sizes = Widened(node.Sizes, node.SizePType, node.Length);
        CanonicalNode child = arena.GetNode(node.ElementsIndex);
        long[] values = MemoryMarshal.Cast<byte, long>(child.Values.Span)[..child.Length].ToArray();
        long[][] rows = new long[node.Length][];
        for (int row = 0; row < rows.Length; row++)
        {
            rows[row] = values.AsSpan((int)offsets[row], (int)sizes[row]).ToArray();
        }

        return rows;
    }

    /// <summary>The lists each row of a list of lists names.</summary>
    private static long[][][] ListsOfLists(CanonicalArena arena, int list)
    {
        CanonicalNode node = arena.GetNode(list);
        long[] offsets = Widened(node.Offsets, node.OffsetPType, node.Length);
        long[] sizes = Widened(node.Sizes, node.SizePType, node.Length);
        long[][] inner = Lists(arena, node.ElementsIndex);
        long[][][] rows = new long[node.Length][][];
        for (int row = 0; row < rows.Length; row++)
        {
            rows[row] = inner.AsSpan((int)offsets[row], (int)sizes[row]).ToArray();
        }

        return rows;
    }

    private static long[] Widened(VortexBuffer buffer, PType type, int rows) => type switch
    {
        PType.I64 => MemoryMarshal.Cast<byte, long>(buffer.Span)[..rows].ToArray(),
        PType.U32 => [.. MemoryMarshal.Cast<byte, uint>(buffer.Span)[..rows].ToArray().Select(value => (long)value)],
        _ => throw new NotSupportedException($"These tests read offsets of 64 or of 32 unsigned bits, not {type}."),
    };
}
