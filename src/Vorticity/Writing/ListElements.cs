using System;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>
/// Where a list range's elements lie, and whether a block may summarize them. The check is exact
/// rather than approximate: a window that missed a gap, or elements read out of order, would not
/// cost a pass but write wrong values.
/// </summary>
internal static class ListElements
{
    /// <summary>
    /// The window rows <c>[start, start + count)</c> of a list view name, and whether it abuts the
    /// window of the range before it. <paramref name="cursor"/> is updated for the next range, and
    /// <paramref name="length"/> is 0 when no row names an element.
    /// </summary>
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

    /// <summary>
    /// The window rows <c>[start, start + count)</c> name: what the compactor keeps of them.
    /// <paramref name="length"/> is 0 when no row names an element.
    /// </summary>
    internal static void Window(CanonicalNode node, int elements, int start, int count, out int from, out int length)
    {
        Range range = Walk(node, elements, start, count);
        from = range.Named ? checked((int)range.First) : 0;
        length = range.Named ? checked((int)(range.End - range.First)) : 0;
    }

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

        // A row that names nothing takes no part: its offset is not guaranteed to mean anything.
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

        // Clamped so that nothing reads past the elements even if a range named outside them.
        high = Math.Min(high, elements);
        low = Math.Clamp(low, 0, high);
        return new Range(high > low, low, high, valid);
    }
}
