using System;
using System.Buffers;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Layouts;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>
/// Narrows a chunk's shared children to the window its rows actually name, before it is written, so
/// that a chunk carries the rows it declares and the bytes those rows name and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="CanonicalKind.ListView"/>'s offsets are absolute into its elements child, so a slice
/// of one shares that child whole -- deliberately, because a slice is a view, and that is what makes
/// a scan of a large list chunk cost its rows rather than its column. The writer is the one place
/// where that sharing is wrong: what it hands the blob writer is what lands in the file, so without
/// this pass a chunk writes out the elements of the whole batch it was cut from. It runs here, and
/// not inside the blob writer, because the blob writer and the zone summariser must see one and the
/// same node.
/// </para>
/// <para>
/// The window is <c>[min(offset), max(offset + size))</c> while its rows name every element it
/// holds: one pass over the rows, and the child stays a view. Rows cut from a batch as it was built
/// lie one after the other in their child, and that window is tight.
/// </para>
/// <para>
/// Rows that lie apart in their child are gathered instead: the elements each names, copied in the
/// order of the rows. A batch whose rows were laid out in another order than they came, a sort by a
/// key for one, keeps its elements where they came, and a few of its rows taken together span
/// nearly all of them: written as a window, each such chunk carried the batch's elements whole. The
/// same pass tells the two apart, by the elements the rows name against the extent they span, so a
/// window is copied only when it would write an element no row asks for. Rows that share their
/// elements name more than they span, and stay a view. The blocks of a gathered chunk summarized
/// the window, gaps included, so they name more elements than the chunk writes:
/// <see cref="ColumnWriter.ElementsOver"/> tells it by that number, and the chooser measures the
/// chunk's elements itself.
/// </para>
/// <para>
/// Nothing is rebuilt when nothing moves: every arm returns the index it was given when the window
/// is already the whole child and no descendant changed, so a file without lists pays one switch per
/// chunk and per field.
/// </para>
/// </remarks>
internal static class ChunkCompactor
{
    /// <summary>
    /// Whether <paramref name="dtype"/> can hold a node that shares a child with its neighbours,
    /// which is the only shape <see cref="Compact"/> has anything to do about.
    /// </summary>
    /// <remarks>
    /// Asked once per file, which is what keeps the rest free: narrowing needs a node referenced
    /// into the transit arena before it is owned, and those records are an allocation per batch that
    /// a file holding no list would pay for nothing.
    /// </remarks>
    /// <param name="dtype">The file's schema, or any dtype under it.</param>
    /// <returns><see langword="true"/> when a list or a map appears anywhere in it.</returns>
    internal static bool MayShareChildren(DType dtype)
    {
        switch (dtype.Kind)
        {
            case DTypeKind.List:
            case DTypeKind.Map:
                return true;
            case DTypeKind.FixedSizeList:
                return MayShareChildren(dtype.ElementType);
            case DTypeKind.Extension:
                return MayShareChildren(dtype.StorageType);
            case DTypeKind.Struct:
                for (int i = 0; i < dtype.FieldCount; i++)
                {
                    if (MayShareChildren(dtype.GetField(i)))
                    {
                        return true;
                    }
                }

                return false;
            default:
                return false;
        }
    }

    /// <summary>Narrows <paramref name="nodeIndex"/> and everything under it.</summary>
    /// <param name="arena">The arena holding the node; the result is appended to it.</param>
    /// <param name="nodeIndex">The node to narrow.</param>
    /// <returns>The narrowed node's index, or <paramref name="nodeIndex"/> when nothing moved.</returns>
    internal static int Compact(CanonicalArena arena, int nodeIndex) =>
        arena.GetNode(nodeIndex).Kind switch
        {
            CanonicalKind.ListView => CompactList(arena, nodeIndex),
            CanonicalKind.Struct => CompactStruct(arena, nodeIndex),
            CanonicalKind.FixedSizeList => CompactFixedSizeList(arena, nodeIndex),
            CanonicalKind.Extension => CompactExtension(arena, nodeIndex),
            // Bool, Primitive, Decimal, VarBinView, Null and Constant hold no child whose storage a
            // window could narrow: a VarBinView's data buffers are addressed per row and are
            // compacted by CanonicalArena.CopyFrom, not here.
            _ => nodeIndex,
        };

    /// <remarks>
    /// The two physical types are resolved before the rows, the way `ListViewDecoder.ValidateRanges`
    /// resolves them: one switch picks the offsets' type, a second picks the sizes', and the loop
    /// that runs is monomorphic in both. Both types are a property of the call and not of the row,
    /// and deciding them per row dominates the cost of a walk this tight.
    /// </remarks>
    private static int CompactList(CanonicalArena arena, int nodeIndex) =>
        arena.GetNode(nodeIndex).OffsetPType switch
        {
            PType.U8 => OnSizes<byte>(arena, nodeIndex),
            PType.U16 => OnSizes<ushort>(arena, nodeIndex),
            PType.U32 => OnSizes<uint>(arena, nodeIndex),
            PType.U64 => OnSizes<ulong>(arena, nodeIndex),
            PType.I8 => OnSizes<sbyte>(arena, nodeIndex),
            PType.I16 => OnSizes<short>(arena, nodeIndex),
            PType.I32 => OnSizes<int>(arena, nodeIndex),
            // The integer-ness of both is established when the node is built; the last arm widens
            // rather than throwing, exactly as ValidateRanges' does.
            _ => OnSizes<long>(arena, nodeIndex),
        };

    /// <summary>The second half of the dispatch: the sizes' physical type.</summary>
    private static int OnSizes<TOffset>(CanonicalArena arena, int nodeIndex)
        where TOffset : unmanaged =>
        arena.GetNode(nodeIndex).SizePType switch
        {
            PType.U8 => Core<TOffset, byte>(arena, nodeIndex),
            PType.U16 => Core<TOffset, ushort>(arena, nodeIndex),
            PType.U32 => Core<TOffset, uint>(arena, nodeIndex),
            PType.U64 => Core<TOffset, ulong>(arena, nodeIndex),
            PType.I8 => Core<TOffset, sbyte>(arena, nodeIndex),
            PType.I16 => Core<TOffset, short>(arena, nodeIndex),
            PType.I32 => Core<TOffset, int>(arena, nodeIndex),
            _ => Core<TOffset, long>(arena, nodeIndex),
        };

    private static int Core<TOffset, TSize>(CanonicalArena arena, int nodeIndex)
        where TOffset : unmanaged
        where TSize : unmanaged
    {
        // Read by value before anything is appended: the calls below add records to this arena,
        // which can reallocate its record array, and a CanonicalNode held across that is stale.
        CanonicalNode node = arena.GetNode(nodeIndex);
        int rows = node.Length;
        DType dtype = node.DType;
        Validity validity = node.Validity;
        VortexBuffer offsets = node.Offsets;
        VortexBuffer sizes = node.Sizes;
        PType offsetPType = node.OffsetPType;
        PType sizePType = node.SizePType;
        int elements = node.ElementsIndex;
        int childRows = arena.GetNode(elements).Length;

        ReadOnlySpan<TOffset> typedOffsets = MemoryMarshal.Cast<byte, TOffset>(offsets.Span)[..rows];
        ReadOnlySpan<TSize> typedSizes = MemoryMarshal.Cast<byte, TSize>(sizes.Span)[..rows];

        long low = long.MaxValue;
        long high = 0;
        long named = 0;
        for (int i = 0; i < rows; i++)
        {
            long size = ListViewDecoder.Widen(typedSizes[i]);
            if (size <= 0)
            {
                // An empty or null row names no element, and its offset is not guaranteed to mean
                // anything -- widening the window to it would keep bytes no row asks for.
                continue;
            }

            long offset = ListViewDecoder.Widen(typedOffsets[i]);
            if (offset < low) { low = offset; }
            if (offset + size > high) { high = offset + size; }
            named += size;
        }

        if (low == long.MaxValue)
        {
            low = 0;
            high = 0;
        }

        // The rows name fewer elements than lie between the first and the last of them: they lie
        // apart in the child, and the window would write what lies between.
        if (high - low > named)
        {
            return Gathered(arena, dtype, rows, validity, elements, typedOffsets, typedSizes, sizes, sizePType, named);
        }

        if (low == 0 && high == childRows)
        {
            int inPlace = Compact(arena, elements);
            return inPlace == elements
                ? nodeIndex
                : arena.AddListView(dtype, rows, validity, inPlace, offsets, offsetPType, sizes, sizePType);
        }

        int narrowed = CanonicalSlice.SliceAcross(
            arena, arena, elements, checked((int)low), checked((int)(high - low)));
        narrowed = Compact(arena, narrowed);

        // Rebased offsets are u32 whatever came in: each is an index into a child of at most
        // `int.MaxValue` elements, so four bytes always hold it and the walk that writes them is
        // monomorphic. A file written elsewhere may carry wider offsets, which a narrowed chunk
        // does not need, so this is a size gain as much as a simpler loop.
        VortexBuffer rebased = arena.AllocateUninitialized(
            checked(rows * sizeof(uint)), sizeof(uint), out Span<byte> writable);
        Span<uint> destination = MemoryMarshal.Cast<byte, uint>(writable);
        for (int i = 0; i < rows; i++)
        {
            long size = ListViewDecoder.Widen(typedSizes[i]);
            destination[i] = size <= 0 ? 0 : (uint)(ListViewDecoder.Widen(typedOffsets[i]) - low);
        }

        return arena.AddListView(dtype, rows, validity, narrowed, rebased, PType.U32, sizes, sizePType);
    }

    /// <summary>
    /// The list whose child holds the <paramref name="named"/> elements its rows name and no other,
    /// in the order of the rows: each row's elements one after the other, the offsets counting on
    /// from one row to the next.
    /// </summary>
    /// <remarks>
    /// The places of the elements are listed once and the child gathered through them, whatever its
    /// kind: a child that is a list itself comes out sharing its own child, which the same pass
    /// narrows in turn. An empty row is given the place the next row starts at, so the offsets never
    /// step back.
    /// </remarks>
    private static int Gathered<TOffset, TSize>(
        CanonicalArena arena, DType dtype, int rows, Validity validity, int elements,
        ReadOnlySpan<TOffset> offsets, ReadOnlySpan<TSize> sizes, VortexBuffer sizeBuffer, PType sizePType, long named)
        where TOffset : unmanaged
        where TSize : unmanaged
    {
        // Fewer than the extent the rows span, which the child's length bounds: an int holds it.
        int count = checked((int)named);
        VortexBuffer rebased = arena.AllocateUninitialized(
            checked(rows * sizeof(uint)), sizeof(uint), out Span<byte> writable);
        Span<uint> destination = MemoryMarshal.Cast<byte, uint>(writable);
        int[] places = ArrayPool<int>.Shared.Rent(Math.Max(count, 1));
        try
        {
            int at = 0;
            for (int i = 0; i < rows; i++)
            {
                destination[i] = (uint)at;
                long size = ListViewDecoder.Widen(sizes[i]);
                if (size <= 0)
                {
                    continue;
                }

                int first = checked((int)ListViewDecoder.Widen(offsets[i]));
                Span<int> row = places.AsSpan(at, (int)size);
                for (int j = 0; j < row.Length; j++)
                {
                    row[j] = first + j;
                }

                at += row.Length;
            }

            int gathered = CanonicalFilter.Apply(arena, elements, places.AsSpan(0, count));
            gathered = Compact(arena, gathered);
            return arena.AddListView(dtype, rows, validity, gathered, rebased, PType.U32, sizeBuffer, sizePType);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(places);
        }
    }

    private static int CompactStruct(CanonicalArena arena, int nodeIndex)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        int fieldCount = node.FieldCount;
        if (fieldCount == 0)
        {
            return nodeIndex;
        }

        DType dtype = node.DType;
        int rows = node.Length;
        Validity validity = node.Validity;

        Span<int> stack = stackalloc int[16];
        Scratch<int> scratch = new Scratch<int>(fieldCount, stack);
        try
        {
            Span<int> fields = scratch.Span;
            bool moved = false;
            for (int i = 0; i < fieldCount; i++)
            {
                // Re-read each iteration for CompactList's reason: a field's own compaction appends.
                int before = arena.GetNode(nodeIndex).GetFieldIndex(i);
                fields[i] = Compact(arena, before);
                moved |= fields[i] != before;
            }

            return moved ? arena.AddStruct(dtype, rows, validity, fields) : nodeIndex;
        }
        finally
        {
            scratch.Dispose();
        }
    }

    private static int CompactFixedSizeList(CanonicalArena arena, int nodeIndex)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        DType dtype = node.DType;
        int rows = node.Length;
        Validity validity = node.Validity;
        uint size = node.FixedSize;
        int elements = node.ElementsIndex;

        int compacted = Compact(arena, elements);
        return compacted == elements
            ? nodeIndex
            : arena.AddFixedSizeList(dtype, rows, validity, compacted, size);
    }

    private static int CompactExtension(CanonicalArena arena, int nodeIndex)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        DType dtype = node.DType;
        int rows = node.Length;
        int storage = node.StorageIndex;

        int compacted = Compact(arena, storage);
        return compacted == storage ? nodeIndex : arena.AddExtension(dtype, rows, compacted);
    }
}
