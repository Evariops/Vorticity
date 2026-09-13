// Cutting a decoded byte heap into Arrow views, once, for the five encodings that all do it.
//
// `vortex.varbin`, `vortex.fsst`, `vortex.onpair`, `vortex.zstd` and `vortex.varbinview` each ended
// up with their own copy of the same loop: read this row's length or offset through
// `switch (ptype)`, slice the heap, CALL `Utf8.IsValid` ON THOSE FIVE TO TWENTY BYTES, then write
// a 16-byte view. On the 1M-row axis those encodings read 9x to 14x the reference.
//
// THE UTF-8 CHECK IS THE INTERESTING ONE, because the fix is an equivalence rather than a faster
// loop. `Utf8.IsValid` is vectorized and good at it; what it is not good at is being CALLED a
// million times on a five-byte span, where the whole cost is the call and the fixed set-up of a
// vector loop that then runs for zero iterations.
//
// The rows of these encodings TILE the heap: row i occupies [offset, offset + length) and row i+1
// starts where it ends, with null rows contributing zero bytes. For a tiling,
//
//     every row is valid UTF-8   <=>   the heap is valid UTF-8
//                                      AND no row boundary is a continuation byte.
//
// (=>) a concatenation of valid sequences is valid, and each boundary begins a code point.
// (<=) a valid heap decomposes uniquely into code points; a boundary that is not a continuation
// byte is a code-point start, so each row is a whole number of code points.
//
// So the check becomes ONE vectorized pass over the whole heap plus one byte test per row. When it
// fails, the row is located by the per-row loop the fast path replaced -- an error path may cost
// whatever it likes.
//
// THE TILING IS THE PRECONDITION, and `vortex.varbin` only satisfies it when every row is valid:
// its offsets are arbitrary, a null row's span is skipped by the per-row check today, and folding
// those bytes into a whole-heap check would refuse a file the reference accepts. So VarBin takes
// the fast path when its validity says every row is valid, and the row-at-a-time path otherwise.
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Unicode;

using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>Builds Arrow binary views over a decoded heap.</summary>
internal static class ViewKernels
{
    /// <summary>Bytes in one view.</summary>
    private const int ViewSize = CanonicalSupport.ViewSize;

    /// <summary>Longest value a view carries inline.</summary>
    private const int Inline = CanonicalSupport.MaxInlineViewLength;

    /// <summary>
    /// Sums the per-row lengths that cut a heap, and reports the longest single row.
    /// </summary>
    /// <param name="lengths">One length per row, as <paramref name="ptype"/>.</param>
    /// <param name="ptype">Their physical type.</param>
    /// <param name="wanted">Row indices, or empty for rows 0..count-1.</param>
    /// <param name="count">Rows to sum.</param>
    /// <returns>
    /// The total, the longest single row (only tracked when <paramref name="wanted"/> is given,
    /// because only a selective decode asks), and the index of the first NEGATIVE length or -1.
    /// </returns>
    /// <remarks>
    /// Shared by `vortex.fsst` and `vortex.onpair`, which both cut their decoded heap with the same
    /// child and both had their own copy of this loop going through <c>ReadInteger</c>'s switch on
    /// every row. It was 15% of a 1M-row scan of either.
    /// </remarks>
    internal static (long Total, long Longest, int Negative) SumLengths(
        ReadOnlySpan<byte> lengths, PType ptype, ReadOnlySpan<int> wanted, int count)
    {
        bool selective = !wanted.IsEmpty;
        return ptype switch
        {
            PType.U8 => SumLengths<byte>(lengths, wanted, selective, count),
            PType.U16 => SumLengths<ushort>(lengths, wanted, selective, count),
            PType.U32 => SumLengths<uint>(lengths, wanted, selective, count),
            PType.U64 => SumLengths<ulong>(lengths, wanted, selective, count),
            PType.I8 => SumLengths<sbyte>(lengths, wanted, selective, count),
            PType.I16 => SumLengths<short>(lengths, wanted, selective, count),
            PType.I32 => SumLengths<int>(lengths, wanted, selective, count),
            _ => SumLengths<long>(lengths, wanted, selective, count),
        };
    }

    /// <summary>Sums and maxes the lengths with the physical type resolved before the loop.</summary>
    /// <returns>The total, the longest row, and the index of the first negative length or -1.</returns>
    /// <remarks>
    /// Two loops, not one with a flag in it. The dense path is the one that runs a million times
    /// per chunk, and it needs neither the <c>wanted</c> indirection nor the longest row --
    /// <c>longestRow</c> decides whether a SELECTIVE decode may use the stack, and a dense one
    /// never asks. For an unsigned length type the sign test is dropped too, because there is
    /// nothing to test.
    /// </remarks>
    private static (long Total, long Longest, int Negative) SumLengths<TLen>(
        ReadOnlySpan<byte> raw, ReadOnlySpan<int> wanted, bool selective, int count)
        where TLen : unmanaged
    {
        ReadOnlySpan<TLen> typed = MemoryMarshal.Cast<byte, TLen>(raw);
        long total = 0;
        long longest = 0;

        if (!selective)
        {
            typed = typed[..count];
            if (Signed<TLen>())
            {
                for (int i = 0; i < typed.Length; i++)
                {
                    long value = WidenLength(typed[i]);
                    if (value < 0)
                    {
                        return (total, longest, i);
                    }

                    total += value;
                }

                return (total, longest, -1);
            }

            for (int i = 0; i < typed.Length; i++)
            {
                total += WidenLength(typed[i]);
            }

            return (total, longest, -1);
        }

        for (int i = 0; i < count; i++)
        {
            long value = WidenLength(typed[wanted[i]]);
            if (value < 0)
            {
                return (total, longest, i);
            }

            total += value;
            longest = Math.Max(longest, value);
        }

        return (total, longest, -1);
    }

    /// <summary>Whether <typeparamref name="TLen"/> can hold a negative value.</summary>
    private static bool Signed<TLen>()
        where TLen : unmanaged =>
        typeof(TLen) == typeof(sbyte) || typeof(TLen) == typeof(short) ||
        typeof(TLen) == typeof(int) || typeof(TLen) == typeof(long);

    /// <summary>
    /// Widens one length, saturating a <see cref="ulong"/> above <see cref="long.MaxValue"/> so the
    /// sum's cap refuses it rather than wrapping.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long WidenLength<TLen>(TLen value)
        where TLen : unmanaged
    {
        if (typeof(TLen) == typeof(byte))
        {
            return Unsafe.As<TLen, byte>(ref value);
        }

        if (typeof(TLen) == typeof(ushort))
        {
            return Unsafe.As<TLen, ushort>(ref value);
        }

        if (typeof(TLen) == typeof(uint))
        {
            return Unsafe.As<TLen, uint>(ref value);
        }

        if (typeof(TLen) == typeof(ulong))
        {
            ulong wide = Unsafe.As<TLen, ulong>(ref value);
            return wide > long.MaxValue ? long.MaxValue : (long)wide;
        }

        if (typeof(TLen) == typeof(sbyte))
        {
            return Unsafe.As<TLen, sbyte>(ref value);
        }

        if (typeof(TLen) == typeof(short))
        {
            return Unsafe.As<TLen, short>(ref value);
        }

        if (typeof(TLen) == typeof(int))
        {
            return Unsafe.As<TLen, int>(ref value);
        }

        return Unsafe.As<TLen, long>(ref value);
    }

    /// <summary>
    /// Cuts <paramref name="heap"/> into <paramref name="count"/> views by consecutive lengths.
    /// </summary>
    /// <param name="lengths">One length per row, as <paramref name="ptype"/>.</param>
    /// <param name="ptype">The lengths' physical type.</param>
    /// <param name="wanted">
    /// Row indices into <paramref name="lengths"/>, or empty for rows 0..count-1. The heap holds
    /// the produced rows back to back in selection order either way, so the OFFSET walks the output
    /// while the LENGTH is read at the row's own index.
    /// </param>
    /// <param name="heap">The decoded bytes, exactly tiled by the rows.</param>
    /// <param name="views">Exactly <paramref name="count"/> views of room; every byte is written.</param>
    /// <param name="count">Rows to build.</param>
    /// <param name="requireUtf8">Whether the dtype is Utf8.</param>
    /// <returns><see langword="true"/> when any view references the heap rather than inlining.</returns>
    /// <exception cref="VortexFormatException">A row is not valid UTF-8.</exception>
    internal static bool BuildFromLengths(
        ReadOnlySpan<byte> lengths, PType ptype, ReadOnlySpan<int> wanted,
        ReadOnlySpan<byte> heap, Span<byte> views, int count, bool requireUtf8)
    {
        if (requireUtf8 && !Utf8.IsValid(heap))
        {
            ThrowFirstInvalidRow(lengths, ptype, wanted, heap, count);
        }

        return ptype switch
        {
            PType.U8 => FromLengths<byte>(lengths, wanted, heap, views, count, requireUtf8),
            PType.U16 => FromLengths<ushort>(lengths, wanted, heap, views, count, requireUtf8),
            PType.U32 => FromLengths<uint>(lengths, wanted, heap, views, count, requireUtf8),
            PType.U64 => FromLengths<ulong>(lengths, wanted, heap, views, count, requireUtf8),
            PType.I8 => FromLengths<sbyte>(lengths, wanted, heap, views, count, requireUtf8),
            PType.I16 => FromLengths<short>(lengths, wanted, heap, views, count, requireUtf8),
            PType.I32 => FromLengths<int>(lengths, wanted, heap, views, count, requireUtf8),
            _ => FromLengths<long>(lengths, wanted, heap, views, count, requireUtf8),
        };
    }

    private static bool FromLengths<TLen>(
        ReadOnlySpan<byte> lengths, ReadOnlySpan<int> wanted, ReadOnlySpan<byte> heap,
        Span<byte> views, int count, bool requireUtf8)
        where TLen : unmanaged
    {
        ReadOnlySpan<TLen> typed = MemoryMarshal.Cast<byte, TLen>(lengths);
        bool selective = !wanted.IsEmpty;
        bool referenced = false;
        int offset = 0;

        for (int i = 0; i < count; i++)
        {
            int size = (int)Widen(typed[selective ? wanted[i] : i]);
            ReadOnlySpan<byte> value = heap.Slice(offset, size);

            // The whole heap is already known valid; a row is valid iff it starts on a code-point
            // boundary. The row AFTER the last one ends at the heap's end, which is a boundary by
            // construction, so only the starts are tested.
            if (requireUtf8 && size != 0 && (heap[offset] & 0xC0) == 0x80)
            {
                ThrowInvalidRow(i);
            }

            referenced |= Write(views.Slice(i * ViewSize, ViewSize), value, size, offset);
            offset += size;
        }

        return referenced;
    }

    /// <summary>
    /// Cuts <paramref name="heap"/> into views by <c>offsets[i]..offsets[i + 1]</c>.
    /// </summary>
    /// <param name="offsets">
    /// <paramref name="count"/> + 1 offsets, already validated non-decreasing and inside the heap.
    /// </param>
    /// <param name="ptype">The offsets' physical type.</param>
    /// <param name="heap">The value bytes.</param>
    /// <param name="views">Exactly <paramref name="count"/> views of room; every byte is written.</param>
    /// <param name="count">Rows to build.</param>
    /// <param name="requireUtf8">Whether the dtype is Utf8.</param>
    /// <param name="mask">Per-row validity; null rows get an empty view and are not validated.</param>
    /// <exception cref="VortexFormatException">A valid row is not valid UTF-8.</exception>
    internal static void BuildFromOffsets(
        ReadOnlySpan<byte> offsets, PType ptype, ReadOnlySpan<byte> heap, Span<byte> views,
        int count, bool requireUtf8, in ValidityMask mask)
    {
        // The tiling argument needs every row to contribute its bytes. With nulls in play the rows
        // this loop skips would be folded into a whole-heap check that the per-row check never
        // applied, so the fast path is taken only when there are none.
        bool wholeHeap = requireUtf8 && mask.AllValid;
        if (wholeHeap)
        {
            int end = (int)CanonicalSupport.ReadInteger(offsets, ptype, count);
            if (!Utf8.IsValid(heap[..end]))
            {
                ThrowFirstInvalidOffsetRow(offsets, ptype, heap, count, in mask);
            }
        }

        switch (ptype)
        {
            case PType.U8:
                FromOffsets<byte>(offsets, heap, views, count, requireUtf8, wholeHeap, in mask);
                break;
            case PType.U16:
                FromOffsets<ushort>(offsets, heap, views, count, requireUtf8, wholeHeap, in mask);
                break;
            case PType.U32:
                FromOffsets<uint>(offsets, heap, views, count, requireUtf8, wholeHeap, in mask);
                break;
            case PType.U64:
                FromOffsets<ulong>(offsets, heap, views, count, requireUtf8, wholeHeap, in mask);
                break;
            case PType.I8:
                FromOffsets<sbyte>(offsets, heap, views, count, requireUtf8, wholeHeap, in mask);
                break;
            case PType.I16:
                FromOffsets<short>(offsets, heap, views, count, requireUtf8, wholeHeap, in mask);
                break;
            case PType.I32:
                FromOffsets<int>(offsets, heap, views, count, requireUtf8, wholeHeap, in mask);
                break;
            default:
                FromOffsets<long>(offsets, heap, views, count, requireUtf8, wholeHeap, in mask);
                break;
        }
    }

    private static void FromOffsets<TOff>(
        ReadOnlySpan<byte> offsets, ReadOnlySpan<byte> heap, Span<byte> views, int count,
        bool requireUtf8, bool wholeHeap, in ValidityMask mask)
        where TOff : unmanaged
    {
        ReadOnlySpan<TOff> typed = MemoryMarshal.Cast<byte, TOff>(offsets);
        bool allValid = mask.AllValid;
        int start = (int)Widen(typed[0]);

        for (int i = 0; i < count; i++)
        {
            int end = (int)Widen(typed[i + 1]);
            if (!allValid && !mask.IsValid(i))
            {
                // BinaryView::empty_view(), written rather than inherited from the allocator.
                views.Slice(i * ViewSize, ViewSize).Clear();
                start = end;
                continue;
            }

            int size = end - start;
            ReadOnlySpan<byte> value = heap.Slice(start, size);

            if (requireUtf8)
            {
                if (wholeHeap)
                {
                    if (size != 0 && (heap[start] & 0xC0) == 0x80)
                    {
                        ThrowInvalidRow(i);
                    }
                }
                else if (!Utf8.IsValid(value))
                {
                    ThrowInvalidRow(i);
                }
            }

            Write(views.Slice(i * ViewSize, ViewSize), value, size, start);
            start = end;
        }
    }

    /// <summary>Writes one view, every byte of it.</summary>
    /// <returns><see langword="true"/> when the view references the heap.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Write(Span<byte> view, ReadOnlySpan<byte> value, int size, int offset)
    {
        if (size <= Inline)
        {
            // Cleared first: `WriteInlineView` leaves the bytes past the value untouched, and
            // leaving those as the pool found them would make two decodes of one file differ.
            view.Clear();
            CanonicalSupport.WriteInlineView(view, value);
            return false;
        }

        CanonicalSupport.WriteReferenceView(view, size, value, bufferIndex: 0, offset: offset);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Widen<T>(T value)
        where T : unmanaged
    {
        if (typeof(T) == typeof(byte))
        {
            return Unsafe.As<T, byte>(ref value);
        }

        if (typeof(T) == typeof(ushort))
        {
            return Unsafe.As<T, ushort>(ref value);
        }

        if (typeof(T) == typeof(uint))
        {
            return Unsafe.As<T, uint>(ref value);
        }

        if (typeof(T) == typeof(ulong))
        {
            return Unsafe.As<T, ulong>(ref value);
        }

        if (typeof(T) == typeof(sbyte))
        {
            return (ulong)(long)Unsafe.As<T, sbyte>(ref value);
        }

        if (typeof(T) == typeof(short))
        {
            return (ulong)(long)Unsafe.As<T, short>(ref value);
        }

        if (typeof(T) == typeof(int))
        {
            return (ulong)(long)Unsafe.As<T, int>(ref value);
        }

        return (ulong)Unsafe.As<T, long>(ref value);
    }

    /// <summary>
    /// The heap failed as a whole, so at least one row fails; finds which by the loop the fast
    /// path replaced. An error path may cost whatever it likes.
    /// </summary>
    private static void ThrowFirstInvalidRow(
        ReadOnlySpan<byte> lengths, PType ptype, ReadOnlySpan<int> wanted, ReadOnlySpan<byte> heap,
        int count)
    {
        bool selective = !wanted.IsEmpty;
        int offset = 0;
        for (int i = 0; i < count; i++)
        {
            int size = (int)CanonicalSupport.ReadInteger(lengths, ptype, selective ? wanted[i] : i);
            if (!Utf8.IsValid(heap.Slice(offset, size)))
            {
                ThrowInvalidRow(i);
            }

            offset += size;
        }

        // Every row read valid but the heap did not: the rows do not tile it, which this kernel's
        // callers guarantee they do. Report the array rather than a row.
        throw new VortexFormatException(
            "A Utf8 array's decoded heap is not valid UTF-8, though every row is; its rows do not " +
            "tile the heap.");
    }

    private static void ThrowFirstInvalidOffsetRow(
        ReadOnlySpan<byte> offsets, PType ptype, ReadOnlySpan<byte> heap, int count,
        in ValidityMask mask)
    {
        for (int i = 0; i < count; i++)
        {
            if (!mask.AllValid && !mask.IsValid(i))
            {
                continue;
            }

            int start = (int)CanonicalSupport.ReadInteger(offsets, ptype, i);
            int end = (int)CanonicalSupport.ReadInteger(offsets, ptype, i + 1);
            if (!Utf8.IsValid(heap.Slice(start, end - start)))
            {
                ThrowInvalidRow(i);
            }
        }

        throw new VortexFormatException(
            "A Utf8 array's value heap is not valid UTF-8, though every row is; its rows do not " +
            "tile the heap.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInvalidRow(int row) =>
        throw new VortexFormatException($"Row {row} of a Utf8 array is not valid UTF-8.");
}
