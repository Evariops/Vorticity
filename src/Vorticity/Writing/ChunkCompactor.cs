// "A chunk carries the rows it declares, and the bytes those rows name -- nothing else."
// WRITE-AUDIT.md W-35. The writer boundary, beside ArrayBlobWriter.Materialize, for the same reason:
// the blob writer and the zone summariser must both see the same node.
using System;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Layouts;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>
/// Narrows a chunk's shared children to the window its rows actually name, before it is written.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="CanonicalKind.ListView"/>'s offsets are ABSOLUTE into its elements child, so a slice
/// of one shares that child whole -- deliberately, because a slice is a view and that is what makes
/// a scan of a large list chunk cost its rows rather than its column (`CanonicalSlice.cs:63-69`).
/// The writer is the one place where that is wrong: what it hands the blob writer is what lands in
/// the file, and a chunk of 8 192 rows was carrying the elements of the whole batch it was cut from.
/// Measured on the 1M-row `list` file: <b>921 732 316 bytes written for a 10 001 744-byte source</b>,
/// and the same mechanism on `listview` and `map`.
/// </para>
/// <para>
/// THE WINDOW IS <c>[min(offset), max(offset + size))</c> and not a gather: it keeps interior gaps,
/// costs one pass over the rows, and leaves the child a VIEW. A gather would be tighter and would
/// copy every element; the rows of a chunk are contiguous in their child by construction here, so
/// the window is already tight and the copy would buy nothing.
/// </para>
/// <para>
/// NOTHING IS REBUILT WHEN NOTHING MOVES. Every arm returns the index it was given when the window
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
    /// ASKED ONCE PER FILE, AND IT IS WHY THE REST COSTS NOTHING. Narrowing needs a node referenced
    /// into the transit arena before it is owned, and those records are an allocation per batch that
    /// a file without lists would pay for nothing: it put `containers/zoned_many_zones_nulls` 0,9 %
    /// over its `WriteAllocationTests` ceiling, which is exactly the kind of quiet toll that ratchet
    /// exists to catch.
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
            // compacted by CanonicalArena.CopyFrom (W-31a), not here.
            _ => nodeIndex,
        };

    /// <remarks>
    /// THE TWO PHYSICAL TYPES ARE RESOLVED BEFORE THE ROWS, the way `ListViewDecoder.ValidateRanges`
    /// resolves them (PERF-AUDIT-v2.md R7, annexe A): one switch picks the offsets' type, a second
    /// picks the sizes', and the loop that runs is monomorphic in both. A type switch per row, on a
    /// property of the CALL and not of the row, is what R7 measured at 93,6 % of a listview scan;
    /// this walk is the same shape and does not get to repeat it.
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
        // BY VALUE BEFORE ANYTHING IS APPENDED: the calls below add records to this arena, which can
        // reallocate its record array, and a CanonicalNode read across that is a use-after-move.
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
        }

        if (low == long.MaxValue)
        {
            low = 0;
            high = 0;
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

        // U32 WHATEVER CAME IN: every rebased offset is an index into a child of at most
        // `int.MaxValue` elements, so four bytes always hold it and the walk that writes them is
        // monomorphic. A `vortex.list` written by the reference carries u64 offsets, and a chunk of
        // it does not need them -- the narrowing is a size gain as well as a simpler loop.
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
