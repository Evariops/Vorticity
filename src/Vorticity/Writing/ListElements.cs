// The elements a list's rows name, and whether the ingest may summarize them as the file will hold
// them - docs/11-write-strategy.md §3.2.4.
//
// A LIST'S ELEMENTS ARE A COLUMN WHOSE BLOCKS ARE THE PARENT'S. Block i of the elements covers the
// elements of parent rows [8192·i, 8192·(i+1)), found through the offsets, so a chunk's elements
// are the merge of its blocks. That merge describes the array the chunk writes only if the blocks
// summarized exactly that array, in its order. The writer narrows a chunk's elements to the WINDOW
// its rows name, [min offset, max end), gaps included (`ChunkCompactor`), and lays batches window
// after window. So the ingest summarizes each range's window, and the windows of two consecutive
// ranges of one batch must abut: the second starting exactly where the first ended. Then a chunk's
// window is its ranges' windows laid end to end, in the order the file holds them, whatever the
// rows' order inside each range.
//
// WHEN THEY DO NOT ABUT, THE BLOCK SAYS SO, and the chooser measures the elements itself. A frame
// of reference taken from a minimum that missed a gap, or a progression read from elements in the
// wrong order, would not cost a pass: it would write wrong values. So the check is exact, and a
// range it cannot vouch for is `Scattered`, never approximated. A batch's first range answers to
// nothing: batches are narrowed one by one before they are laid together.
//
// ONE PASS OVER THE OFFSETS AND SIZES, MONOMORPHIC IN BOTH, the way `ChunkCompactor` walks them: the
// two physical types are properties of the call, and a switch per row is what PERF-AUDIT-v2.md R7
// measured at 93,6 % of a listview scan.
using System;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>Where a list range's elements lie, and whether a block may summarize them.</summary>
internal static class ListElements
{
    /// <summary>
    /// The window rows <c>[start, start + count)</c> of a list view name, and whether it abuts the
    /// window of the range before it when <paramref name="cursor"/> says this range continues it.
    /// </summary>
    /// <param name="node">The list view.</param>
    /// <param name="elements">Its elements child's length.</param>
    /// <param name="start">The first row.</param>
    /// <param name="count">How many rows.</param>
    /// <param name="cursor">The column's record of the range before; updated for the next one.</param>
    /// <param name="from">The window's first element.</param>
    /// <param name="length">Its length; 0 when no row names an element.</param>
    /// <returns>Whether the window may be summarized into the range's block.</returns>
    internal static bool Contiguous(
        CanonicalNode node, int elements, int start, int count, PreviousRow cursor, out int from, out int length)
    {
        long expected = cursor.ListContinues(start, out long end) ? end : -1;
        Range range = Walk(node, elements, start, count);
        cursor.SetListEnd((long)start + count, range.Named ? range.End : expected);
        from = range.Named ? checked((int)range.First) : 0;
        length = range.Named ? checked((int)(range.End - range.First)) : 0;
        return range.Valid && (!range.Named || expected < 0 || range.First == expected);
    }

    /// <summary>The window rows <c>[start, start + count)</c> name: what the compactor keeps of them.</summary>
    /// <param name="node">The list view.</param>
    /// <param name="elements">Its elements child's length.</param>
    /// <param name="start">The first row.</param>
    /// <param name="count">How many rows.</param>
    /// <param name="from">The window's first element.</param>
    /// <param name="length">Its length; 0 when no row names an element.</param>
    internal static void Window(CanonicalNode node, int elements, int start, int count, out int from, out int length)
    {
        Range range = Walk(node, elements, start, count);
        from = range.Named ? checked((int)range.First) : 0;
        length = range.Named ? checked((int)(range.End - range.First)) : 0;
    }

    /// <summary>What a walk found.</summary>
    /// <param name="Named">Whether any row named an element.</param>
    /// <param name="First">The smallest offset a row named.</param>
    /// <param name="End">The largest end a row named.</param>
    /// <param name="Valid">Whether every named range lay inside the elements.</param>
    private readonly record struct Range(bool Named, long First, long End, bool Valid);

    private static Range Walk(CanonicalNode node, int elements, int start, int count) =>
        node.OffsetPType switch
        {
            PType.U8 => OnSizes<byte>(node, elements, start, count),
            PType.U16 => OnSizes<ushort>(node, elements, start, count),
            PType.U32 => OnSizes<uint>(node, elements, start, count),
            PType.U64 => OnSizes<ulong>(node, elements, start, count),
            PType.I8 => OnSizes<sbyte>(node, elements, start, count),
            PType.I16 => OnSizes<short>(node, elements, start, count),
            PType.I32 => OnSizes<int>(node, elements, start, count),
            _ => OnSizes<long>(node, elements, start, count),
        };

    private static Range OnSizes<TOffset>(CanonicalNode node, int elements, int start, int count)
        where TOffset : unmanaged =>
        node.SizePType switch
        {
            PType.U8 => Core<TOffset, byte>(node, elements, start, count),
            PType.U16 => Core<TOffset, ushort>(node, elements, start, count),
            PType.U32 => Core<TOffset, uint>(node, elements, start, count),
            PType.U64 => Core<TOffset, ulong>(node, elements, start, count),
            PType.I8 => Core<TOffset, sbyte>(node, elements, start, count),
            PType.I16 => Core<TOffset, short>(node, elements, start, count),
            PType.I32 => Core<TOffset, int>(node, elements, start, count),
            _ => Core<TOffset, long>(node, elements, start, count),
        };

    private static Range Core<TOffset, TSize>(CanonicalNode node, int elements, int start, int count)
        where TOffset : unmanaged
        where TSize : unmanaged
    {
        ReadOnlySpan<TOffset> offsets = MemoryMarshal.Cast<byte, TOffset>(node.Offsets.Span).Slice(start, count);
        ReadOnlySpan<TSize> sizes = MemoryMarshal.Cast<byte, TSize>(node.Sizes.Span).Slice(start, count);

        // A row that names nothing takes no part: its offset is not guaranteed to mean anything,
        // and the compactor writes 0 there.
        long low = long.MaxValue;
        long high = -1;
        bool valid = true;
        for (int row = 0; row < count; row++)
        {
            long size = ListViewDecoder.Widen(sizes[row]);
            if (size <= 0)
            {
                continue;
            }

            long offset = ListViewDecoder.Widen(offsets[row]);
            valid &= offset >= 0 && offset + size <= elements;
            low = Math.Min(low, offset);
            high = Math.Max(high, offset + size);
        }

        if (high < 0)
        {
            return new Range(false, 0, 0, valid);
        }

        // A range that names outside the elements never reaches here from a decoder, which
        // validates the ranges; the window is still clamped so that nothing reads past them.
        high = Math.Min(high, elements);
        low = Math.Clamp(low, 0, high);
        return new Range(high > low, low, high, valid);
    }
}
