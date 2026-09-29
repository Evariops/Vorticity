using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Expressions;
using Vorticity.Layouts;
using Vorticity.Types;

namespace Vorticity.Compute;

/// <summary>
/// Per-row evaluation of a list predicate. The elements are compared once, over the window the
/// batch's rows name, and each row then looks for a match in its own range: a batch is a slice of a
/// chunk and shares the chunk's elements whole, so comparing the whole child would make every batch
/// cost its chunk. Equality is the comparison kernel's, so an element matches exactly when
/// <c>element = literal</c> would select it, and a null element is not a match.
/// </summary>
internal static class ListKernels
{
    /// <summary>Evaluates <c>list_contains(column, literal)</c> into <paramref name="destination"/>.</summary>
    /// <param name="arena">The arena the node lives in.</param>
    /// <param name="nodeIndex">The list column.</param>
    /// <param name="literal">The element sought.</param>
    /// <param name="destination">One <see cref="Trilean"/> state per row.</param>
    /// <exception cref="NotSupportedException">
    /// The column is not a list, or its elements are outside what a comparison evaluates.
    /// </exception>
    internal static void Contains(
        CanonicalArena arena, int nodeIndex, FilterLiteral literal, Span<byte> destination)
    {
        // An extension's validity is its storage's, so the storage is the whole column here.
        int storage = ComparisonKernels.Unwrap(arena, nodeIndex);
        CanonicalNode node = arena.GetNode(storage);
        if (literal.Kind == FilterLiteralKind.Null || node.Kind == CanonicalKind.Null)
        {
            Trilean.Fill(destination, Trilean.Unknown);
            return;
        }

        if (node.Kind is not (CanonicalKind.ListView or CanonicalKind.FixedSizeList))
        {
            throw new NotSupportedException(
                $"ListContains reads a list or a fixed-size list; this column is {node.Kind}.");
        }

        ValidityMask mask = ValidityMask.From(arena, node.Validity);
        if (mask.AllInvalid)
        {
            Trilean.Fill(destination, Trilean.Unknown);
            return;
        }

        if (node.Kind == CanonicalKind.FixedSizeList)
        {
            FixedSize(arena, node.ElementsIndex, checked((int)node.FixedSize), mask, literal, destination);
            return;
        }

        switch (node.OffsetPType)
        {
            case PType.U8: OnSizes<byte>(arena, node, mask, literal, destination); return;
            case PType.U16: OnSizes<ushort>(arena, node, mask, literal, destination); return;
            case PType.U32: OnSizes<uint>(arena, node, mask, literal, destination); return;
            case PType.U64: OnSizes<ulong>(arena, node, mask, literal, destination); return;
            case PType.I8: OnSizes<sbyte>(arena, node, mask, literal, destination); return;
            case PType.I16: OnSizes<short>(arena, node, mask, literal, destination); return;
            case PType.I32: OnSizes<int>(arena, node, mask, literal, destination); return;
            default: OnSizes<long>(arena, node, mask, literal, destination); return;
        }
    }

    /// <summary>Row <c>r</c>'s elements are <c>[r·size, (r+1)·size)</c>, all of them the batch's.</summary>
    private static void FixedSize(
        CanonicalArena arena, int elements, int size, ValidityMask mask, FilterLiteral literal,
        Span<byte> destination)
    {
        int rows = destination.Length;
        int count = checked(rows * size);
        if (count == 0)
        {
            Rows(mask, destination);
            return;
        }

        byte[] matches = ArrayPool<byte>.Shared.Rent(count);
        try
        {
            ComparisonKernels.Compare(arena, elements, ComparisonOp.Equal, literal, matches.AsSpan(0, count));
            for (int row = 0; row < rows; row++)
            {
                destination[row] = !mask.AllValid && !mask.IsValid(row)
                    ? Trilean.Unknown
                    : matches.AsSpan(row * size, size).Contains(Trilean.True) ? Trilean.True : Trilean.False;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(matches);
        }
    }

    /// <summary>Every row false, or unknown where the list is null: no element to compare.</summary>
    private static void Rows(ValidityMask mask, Span<byte> destination)
    {
        for (int row = 0; row < destination.Length; row++)
        {
            destination[row] = mask.AllValid || mask.IsValid(row) ? Trilean.False : Trilean.Unknown;
        }
    }

    private static void OnSizes<TOffset>(
        CanonicalArena arena, CanonicalNode node, ValidityMask mask, FilterLiteral literal, Span<byte> destination)
        where TOffset : unmanaged
    {
        switch (node.SizePType)
        {
            case PType.U8: Views<TOffset, byte>(arena, node, mask, literal, destination); return;
            case PType.U16: Views<TOffset, ushort>(arena, node, mask, literal, destination); return;
            case PType.U32: Views<TOffset, uint>(arena, node, mask, literal, destination); return;
            case PType.U64: Views<TOffset, ulong>(arena, node, mask, literal, destination); return;
            case PType.I8: Views<TOffset, sbyte>(arena, node, mask, literal, destination); return;
            case PType.I16: Views<TOffset, short>(arena, node, mask, literal, destination); return;
            case PType.I32: Views<TOffset, int>(arena, node, mask, literal, destination); return;
            default: Views<TOffset, long>(arena, node, mask, literal, destination); return;
        }
    }

    /// <summary>A list view, its two physical types resolved.</summary>
    private static void Views<TOffset, TSize>(
        CanonicalArena arena, CanonicalNode node, ValidityMask mask, FilterLiteral literal, Span<byte> destination)
        where TOffset : unmanaged
        where TSize : unmanaged
    {
        // Everything is read off the node before the slice below appends records: a node read across
        // that append is a use-after-move. The spans are over buffers, which do not move.
        int rows = destination.Length;
        int elements = node.ElementsIndex;
        int childRows = arena.GetNode(elements).Length;
        ReadOnlySpan<TOffset> offsets = MemoryMarshal.Cast<byte, TOffset>(node.Offsets.Span)[..rows];
        ReadOnlySpan<TSize> sizes = MemoryMarshal.Cast<byte, TSize>(node.Sizes.Span)[..rows];
        bool allValid = mask.AllValid;

        // The window the valid rows name. A null row's offset and size are unspecified, so they
        // take no part, as an empty row's offset does not.
        long low = long.MaxValue;
        long high = -1;
        long named = 0;
        for (int row = 0; row < rows; row++)
        {
            long size = ListViewDecoder.Widen(sizes[row]);
            if (size <= 0 || (!allValid && !mask.IsValid(row)))
            {
                continue;
            }

            long offset = ListViewDecoder.Widen(offsets[row]);
            low = Math.Min(low, offset);
            high = Math.Max(high, offset + size);
            named += size;
        }

        if (high <= low)
        {
            Rows(mask, destination);
            return;
        }

        if (low < 0 || high > childRows)
        {
            throw new VortexFormatException(
                $"A list row names elements [{low}, {high}) of a child of {childRows}.");
        }

        int from = (int)low;
        int length = (int)(high - low);
        int window = from == 0 && length == childRows
            ? elements
            : CanonicalSlice.SliceAcross(arena, arena, elements, from, length);

        byte[] matches = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            ReadOnlySpan<byte> equal = matches.AsSpan(0, length);
            ComparisonKernels.Compare(arena, window, ComparisonOp.Equal, literal, matches.AsSpan(0, length));

            // Rows whose views overlap name far more elements than the window holds: each asks a
            // count of the matches before its end and before its start rather than reading its
            // elements again.
            if (named > (long)OverlapFactor * length)
            {
                Overlapping<TOffset, TSize>(equal, offsets, sizes, mask, low, destination);
            }
            else
            {
                Searched<TOffset, TSize>(equal, offsets, sizes, mask, low, destination);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(matches);
        }
    }

    /// <summary>The rows answered by a search of each row's elements for a match.</summary>
    /// <remarks>Each way of answering the rows is a method of its own, so that neither loop takes its shape from the other.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Searched<TOffset, TSize>(
        ReadOnlySpan<byte> equal, ReadOnlySpan<TOffset> offsets, ReadOnlySpan<TSize> sizes, ValidityMask mask, long low,
        Span<byte> destination)
        where TOffset : unmanaged
        where TSize : unmanaged
    {
        bool allValid = mask.AllValid;
        for (int row = 0; row < destination.Length; row++)
        {
            if (!allValid && !mask.IsValid(row))
            {
                destination[row] = Trilean.Unknown;
                continue;
            }

            long size = ListViewDecoder.Widen(sizes[row]);
            destination[row] = size > 0
                && equal.Slice((int)(ListViewDecoder.Widen(offsets[row]) - low), (int)size).Contains(Trilean.True)
                ? Trilean.True
                : Trilean.False;
        }
    }

    /// <summary>
    /// How many times the window the rows name may be read, summed over the rows, before counting
    /// the matches once costs less than a search in each row's elements: a vector search reads a
    /// match state in about a thirtieth of the time the count takes to add it.
    /// </summary>
    private const int OverlapFactor = 32;

    /// <summary>The rows answered from a running count of the window's matches: one subtraction a row.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Overlapping<TOffset, TSize>(
        ReadOnlySpan<byte> equal, ReadOnlySpan<TOffset> offsets, ReadOnlySpan<TSize> sizes, ValidityMask mask, long low,
        Span<byte> destination)
        where TOffset : unmanaged
        where TSize : unmanaged
    {
        int[] counted = ArrayPool<int>.Shared.Rent(equal.Length + 1);
        try
        {
            Span<int> before = counted.AsSpan(0, equal.Length + 1);
            int count = 0;
            for (int i = 0; i < equal.Length; i++)
            {
                before[i] = count;
                count += equal[i] == Trilean.True ? 1 : 0;
            }

            before[equal.Length] = count;
            bool allValid = mask.AllValid;
            for (int row = 0; row < destination.Length; row++)
            {
                if (!allValid && !mask.IsValid(row))
                {
                    destination[row] = Trilean.Unknown;
                    continue;
                }

                long size = ListViewDecoder.Widen(sizes[row]);
                if (size <= 0)
                {
                    destination[row] = Trilean.False;
                    continue;
                }

                int start = (int)(ListViewDecoder.Widen(offsets[row]) - low);
                destination[row] = before[start + (int)size] > before[start] ? Trilean.True : Trilean.False;
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(counted);
        }
    }
}
