// The membership test over list views as it searched each row's elements for a match: the
// original that `ListContainsBenchmarks` measures the library against, its offsets and sizes 64-bit.
using System;
using System.Buffers;
using System.Runtime.InteropServices;

using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Layouts;

namespace Vorticity.Benchmarks.Complexity;

internal static class ListContainsBefore
{
    /// <summary>A list view, its two physical types resolved.</summary>
    internal static void Views(
        CanonicalArena arena, CanonicalNode node, ValidityMask mask, FilterLiteral literal, Span<byte> destination)
    {
        int rows = destination.Length;
        int elements = node.ElementsIndex;
        int childRows = arena.GetNode(elements).Length;
        ReadOnlySpan<long> offsets = MemoryMarshal.Cast<byte, long>(node.Offsets.Span)[..rows];
        ReadOnlySpan<long> sizes = MemoryMarshal.Cast<byte, long>(node.Sizes.Span)[..rows];
        bool allValid = mask.AllValid;

        long low = long.MaxValue;
        long high = -1;
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
        }

        if (high <= low)
        {
            throw new InvalidOperationException("The bench's rows name elements.");
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
            for (int row = 0; row < rows; row++)
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
        finally
        {
            ArrayPool<byte>.Shared.Return(matches);
        }
    }
}
