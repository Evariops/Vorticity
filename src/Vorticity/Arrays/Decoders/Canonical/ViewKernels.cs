using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Unicode;

using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>
/// Builds Arrow binary views over a decoded heap, in one place for the several encodings that cut
/// a heap into rows the same way.
/// </summary>
/// <remarks>
/// <para>
/// The rows of those encodings tile the heap: row i occupies [offset, offset + length), row i + 1
/// starts where it ends, and a null row contributes no bytes. For a tiling, every row is valid
/// UTF-8 exactly when the heap is valid UTF-8 and no row boundary falls on a continuation byte: a
/// concatenation of valid sequences is valid and every boundary begins a code point, and
/// conversely a valid heap decomposes uniquely into code points, so a boundary that is not a
/// continuation byte starts one. The check is therefore one vectorized pass over the heap plus one
/// byte test per row, rather than a validator call per row on a few bytes, where the whole cost is
/// the call and the set-up of a vector loop that then runs for no iterations. When the heap fails,
/// the offending row is found with the per-row loop the fast path replaces; an error path may cost
/// whatever it likes.
/// </para>
/// <para>
/// The tiling is the precondition. Where rows are cut by arbitrary offsets, a null row's span is
/// skipped by the per-row check, so folding those bytes into a whole-heap check would refuse a
/// file that is well-formed; such a heap takes the whole-heap check only when validity says every
/// row is valid, and the row-at-a-time path otherwise.
/// </para>
/// </remarks>
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
    /// because only a selective decode asks), and the index of the first negative length, or -1.
    /// </returns>
    /// <remarks>
    /// Shared by the encodings that cut their decoded heap with a lengths child, so that the
    /// physical type is resolved once here instead of once per row in each of them.
    /// </remarks>
    internal static (long Total, long Longest, int Negative) SumLengths(
        ReadOnlySpan<byte> lengths, PType ptype, ReadOnlySpan<int> wanted, int count)
    {
        bool selective = !wanted.IsEmpty;

        // The dense unsigned sum is a reduction, so it is written as one rather than as a scalar
        // loop reading one length at a time through a bounds check. Widening into 64-bit lanes
        // keeps the total exact for every unsigned width: the widest length and the largest row
        // count a 32-bit array can hold still multiply to less than a `ulong` holds, and the
        // narrower types only leave more room. The signed types keep the scalar loop, because their
        // answer is not the sum but the index of the first negative length, which a reduction
        // discards.
        if (!selective)
        {
            switch (ptype)
            {
                case PType.U8:
                    return (SumWidening(lengths[..count]), 0, -1);
                case PType.U16:
                    return (SumWidening(MemoryMarshal.Cast<byte, ushort>(lengths)[..count]), 0, -1);
                case PType.U32:
                    return (SumWidening(MemoryMarshal.Cast<byte, uint>(lengths)[..count]), 0, -1);
                default:
                    break;
            }
        }

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
    /// Two loops, not one with a flag in it. The dense path is the one that runs once per row of a
    /// chunk, and it needs neither the <c>wanted</c> indirection nor the longest row, which only a
    /// selective decode asks for, to decide whether it may use the stack. For an unsigned length
    /// type the sign test is dropped too, because there is nothing to test.
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

    /// <summary>Sums unsigned lengths, widening to 64-bit lanes so nothing can wrap.</summary>
    /// <param name="values">The lengths, exactly as many as are wanted.</param>
    /// <returns>The total.</returns>
    /// <remarks>
    /// <para>
    /// <c>Vector.Widen</c> splits each vector into two of the next width up, so a byte vector takes
    /// three splits to reach 64-bit lanes and a <see cref="uint"/> one takes a single split. The
    /// accumulators stay in 64-bit lanes throughout rather than summing narrow and widening at the
    /// end, because the whole point is that no intermediate can overflow.
    /// </para>
    /// <para>
    /// Written out per width rather than once generically: the ladder changes the element type at
    /// every rung, and a generic version re-entering through <c>Vector.As</c> reinterprets the bits
    /// instead of widening them, so its type parameter never changes and the recursion never ends.
    /// </para>
    /// <para>
    /// The guard is on hardware acceleration, not on the element type. Asking whether
    /// <see cref="uint"/> is a legal vector element type is always answered yes, so such a guard
    /// would send a machine without SIMD down this ladder in software emulation, where each rung
    /// costs a split and an add for the one add a scalar loop would pay. No kernel here guards on
    /// the element type.
    /// </para>
    /// </remarks>
    private static long SumWidening(ReadOnlySpan<uint> values)
    {
        ulong total = 0;
        int i = 0;

        if (Vector.IsHardwareAccelerated)
        {
            int lanes = Vector<uint>.Count;
            ref uint source = ref MemoryMarshal.GetReference(values);
            Vector<ulong> sum = Vector<ulong>.Zero;
            for (; i <= values.Length - lanes; i += lanes)
            {
                Vector.Widen(
                    Vector.LoadUnsafe(ref source, (nuint)i),
                    out Vector<ulong> low,
                    out Vector<ulong> high);
                sum += low + high;
            }

            total = Vector.Sum(sum);
        }

        for (; i < values.Length; i++)
        {
            total += values[i];
        }

        return (long)total;
    }

    /// <summary>Sums 16-bit lengths, widening in two rungs so nothing can wrap.</summary>
    private static long SumWidening(ReadOnlySpan<ushort> values)
    {
        ulong total = 0;
        int i = 0;

        if (Vector.IsHardwareAccelerated)
        {
            int lanes = Vector<ushort>.Count;
            ref ushort source = ref MemoryMarshal.GetReference(values);
            Vector<ulong> sum = Vector<ulong>.Zero;
            for (; i <= values.Length - lanes; i += lanes)
            {
                Vector.Widen(
                    Vector.LoadUnsafe(ref source, (nuint)i),
                    out Vector<uint> low,
                    out Vector<uint> high);
                Vector.Widen(low, out Vector<ulong> a, out Vector<ulong> b);
                Vector.Widen(high, out Vector<ulong> c, out Vector<ulong> d);
                sum += a + b + c + d;
            }

            total = Vector.Sum(sum);
        }

        for (; i < values.Length; i++)
        {
            total += values[i];
        }

        return (long)total;
    }

    /// <summary>Sums byte lengths, widening in three rungs so nothing can wrap.</summary>
    private static long SumWidening(ReadOnlySpan<byte> values)
    {
        ulong total = 0;
        int i = 0;

        if (Vector.IsHardwareAccelerated)
        {
            int lanes = Vector<byte>.Count;
            ref byte source = ref MemoryMarshal.GetReference(values);
            Vector<ulong> sum = Vector<ulong>.Zero;
            for (; i <= values.Length - lanes; i += lanes)
            {
                Vector.Widen(
                    Vector.LoadUnsafe(ref source, (nuint)i),
                    out Vector<ushort> low,
                    out Vector<ushort> high);
                sum += WidenPair(low) + WidenPair(high);
            }

            total = Vector.Sum(sum);
        }

        for (; i < values.Length; i++)
        {
            total += values[i];
        }

        return (long)total;
    }

    /// <summary>Widens a 16-bit vector to 64-bit lanes and sums the four quarters.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector<ulong> WidenPair(Vector<ushort> value)
    {
        Vector.Widen(value, out Vector<uint> low, out Vector<uint> high);
        Vector.Widen(low, out Vector<ulong> a, out Vector<ulong> b);
        Vector.Widen(high, out Vector<ulong> c, out Vector<ulong> d);
        return a + b + c + d;
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
    /// The offsets that cut a heap must not decrease.
    /// </summary>
    /// <param name="offsets"><paramref name="count"/> offsets, as <paramref name="ptype"/>.</param>
    /// <param name="ptype">Their physical type.</param>
    /// <param name="count">How many to check.</param>
    /// <param name="encodingId">The encoding asking, for the message.</param>
    /// <exception cref="VortexFormatException">A pair decreases.</exception>
    /// <remarks>
    /// A row's length is the difference of two consecutive offsets, so a decreasing pair produces a
    /// length that reaches past the heap; the check is therefore hard rather than an assertion. A
    /// shifted compare puts it on the vector unit, and the scalar loop names the offending pair
    /// once a block has failed.
    /// </remarks>
    internal static void RequireAscending(
        ReadOnlySpan<byte> offsets, PType ptype, int count, string encodingId)
    {
        switch (ptype)
        {
            case PType.U8:
                RequireAscending<byte>(offsets, count, encodingId);
                break;
            case PType.U16:
                RequireAscending<ushort>(offsets, count, encodingId);
                break;
            case PType.U32:
                RequireAscending<uint>(offsets, count, encodingId);
                break;
            case PType.U64:
                RequireAscending<ulong>(offsets, count, encodingId);
                break;
            case PType.I8:
                RequireAscending<sbyte>(offsets, count, encodingId);
                break;
            case PType.I16:
                RequireAscending<short>(offsets, count, encodingId);
                break;
            case PType.I32:
                RequireAscending<int>(offsets, count, encodingId);
                break;
            default:
                RequireAscending<long>(offsets, count, encodingId);
                break;
        }
    }

    private static void RequireAscending<T>(ReadOnlySpan<byte> offsets, int count, string encodingId)
        where T : unmanaged, INumber<T>
    {
        ReadOnlySpan<T> typed = MemoryMarshal.Cast<byte, T>(offsets)[..count];

        int i = 1;
        if (Vector.IsHardwareAccelerated && count > Vector<T>.Count)
        {
            // The base reference is taken once. Loading through the indexer instead would
            // bounds-check it before handing over a reference the load treats as unchecked anyway,
            // adding a compare and a branch per vector to a loop whose body is one compare.
            ref T source = ref MemoryMarshal.GetReference(typed);
            int lanes = Vector<T>.Count;
            for (; i <= count - lanes; i += lanes)
            {
                if (Vector.LessThanAny(
                        Vector.LoadUnsafe(ref source, (nuint)i),
                        Vector.LoadUnsafe(ref source, (nuint)(i - 1))))
                {
                    break;
                }
            }
        }

        for (; i < count; i++)
        {
            if (typed[i] < typed[i - 1])
            {
                throw new VortexFormatException(
                    $"{encodingId} offsets must not decrease; offset {i} is {typed[i]} after " +
                    $"{typed[i - 1]}.");
            }
        }
    }

    /// <summary>
    /// Cuts <paramref name="heap"/> into <paramref name="count"/> views by consecutive lengths.
    /// </summary>
    /// <param name="lengths">One length per row, as <paramref name="ptype"/>.</param>
    /// <param name="ptype">The lengths' physical type.</param>
    /// <param name="wanted">
    /// Row indices into <paramref name="lengths"/>, or empty for rows 0..count-1. The heap holds
    /// the produced rows back to back in selection order either way, so the offset walks the output
    /// while the length is read at the row's own index.
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
        bool referenced = false;
        int offset = 0;

        if (wanted.IsEmpty)
        {
            // The dense loop is its own loop: `selective` is loop-invariant, and testing it per row
            // would add a branch and a bounds check on a path that has no selection at all. The two
            // spans are addressed by reference for the same reason: slicing them per row is a check
            // and a span construction, handed to a method that takes the reference of each
            // immediately.
            //
            // The remaining per-row check is the one that is not redundant: `size` comes from the
            // file, and the sum of the sizes is what the caller allocated the heap from, so a row
            // running past the end means the two disagree and the file is malformed.
            ReadOnlySpan<TLen> dense = typed[..count];
            ref byte heapRef = ref MemoryMarshal.GetReference(heap);
            ref byte viewRef = ref MemoryMarshal.GetReference(views);
            int heapLength = heap.Length;

            for (int i = 0; i < count; i++)
            {
                int size = (int)Widen(dense[i]);
                if ((uint)size > (uint)(heapLength - offset))
                {
                    ThrowRowPastHeap(i, offset, size, heapLength);
                }

                ref byte value = ref Unsafe.Add(ref heapRef, offset);

                // The whole heap is already known valid, so a row is valid exactly when it starts
                // on a code-point boundary. The last row ends at the heap's end, which is a
                // boundary by construction, so only the starts are tested.
                if (requireUtf8 && size != 0 && (value & 0xC0) == 0x80)
                {
                    ThrowInvalidRow(i);
                }

                referenced |= CanonicalSupport.WriteView(
                    ref Unsafe.Add(ref viewRef, i * ViewSize), ref value, size, 0, offset);
                offset += size;
            }

            return referenced;
        }

        for (int i = 0; i < count; i++)
        {
            int size = (int)Widen(typed[wanted[i]]);
            ReadOnlySpan<byte> value = heap.Slice(offset, size);

            if (requireUtf8 && size != 0 && (heap[offset] & 0xC0) == 0x80)
            {
                ThrowInvalidRow(i);
            }

            referenced |= Write(views.Slice(i * ViewSize, ViewSize), value, size, offset);
            offset += size;
        }

        return referenced;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowRowPastHeap(int row, int offset, int size, int heapLength) =>
        throw new VortexFormatException(
            $"Row {row} spans [{offset}, {(long)offset + size}) of a {heapLength}-byte decoded " +
            "heap; the row lengths and the heap disagree.");

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

    /// <summary>
    /// <see cref="BuildFromOffsets"/> for a selection: one view per entry of
    /// <paramref name="wanted"/>, cut at <c>offsets[w]..offsets[w + 1]</c>.
    /// </summary>
    /// <param name="offsets">
    /// The whole node's offsets, already validated non-decreasing and inside the heap.
    /// </param>
    /// <param name="ptype">The offsets' physical type.</param>
    /// <param name="heap">The value bytes.</param>
    /// <param name="views">Exactly <c>wanted.Length</c> views of room; every byte is written.</param>
    /// <param name="wanted">Row indices into the node, strictly ascending.</param>
    /// <param name="requireUtf8">Whether the dtype is Utf8.</param>
    /// <param name="mask">Validity of the selected rows, indexed by position in
    /// <paramref name="wanted"/>.</param>
    /// <exception cref="VortexFormatException">A valid row is not valid UTF-8.</exception>
    /// <remarks>
    /// The whole-heap check is not available here and must not be borrowed: it is sound only
    /// because every byte of the heap belongs to a row the loop then visits, and a selection visits
    /// few of them. A take pays a per-row check instead, on the bytes it actually returns.
    /// </remarks>
    internal static void BuildFromOffsetsSelected(
        ReadOnlySpan<byte> offsets, PType ptype, ReadOnlySpan<byte> heap, Span<byte> views,
        ReadOnlySpan<int> wanted, bool requireUtf8, in ValidityMask mask)
    {
        switch (ptype)
        {
            case PType.U8:
                SelectedFromOffsets<byte>(offsets, heap, views, wanted, requireUtf8, in mask);
                break;
            case PType.U16:
                SelectedFromOffsets<ushort>(offsets, heap, views, wanted, requireUtf8, in mask);
                break;
            case PType.U32:
                SelectedFromOffsets<uint>(offsets, heap, views, wanted, requireUtf8, in mask);
                break;
            case PType.U64:
                SelectedFromOffsets<ulong>(offsets, heap, views, wanted, requireUtf8, in mask);
                break;
            case PType.I8:
                SelectedFromOffsets<sbyte>(offsets, heap, views, wanted, requireUtf8, in mask);
                break;
            case PType.I16:
                SelectedFromOffsets<short>(offsets, heap, views, wanted, requireUtf8, in mask);
                break;
            case PType.I32:
                SelectedFromOffsets<int>(offsets, heap, views, wanted, requireUtf8, in mask);
                break;
            default:
                SelectedFromOffsets<long>(offsets, heap, views, wanted, requireUtf8, in mask);
                break;
        }
    }

    private static void SelectedFromOffsets<TOff>(
        ReadOnlySpan<byte> offsets, ReadOnlySpan<byte> heap, Span<byte> views,
        ReadOnlySpan<int> wanted, bool requireUtf8, in ValidityMask mask)
        where TOff : unmanaged
    {
        ReadOnlySpan<TOff> typed = MemoryMarshal.Cast<byte, TOff>(offsets);
        bool allValid = mask.AllValid;

        ref byte heapRef = ref MemoryMarshal.GetReference(heap);
        ref byte viewRef = ref MemoryMarshal.GetReference(views);
        int heapLength = heap.Length;

        for (int i = 0; i < wanted.Length; i++)
        {
            ref byte view = ref Unsafe.Add(ref viewRef, i * ViewSize);

            if (!allValid && !mask.IsValid(i))
            {
                // The empty view, written out rather than inherited from the allocator.
                Unsafe.WriteUnaligned(ref view, 0UL);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref view, sizeof(ulong)), 0UL);
                continue;
            }

            // The pair is read fresh for every row: `start` cannot be carried across the gaps a
            // selection leaves, which is the one structural difference from `FromOffsets`.
            int row = wanted[i];
            int start = (int)Widen(typed[row]);
            int size = (int)Widen(typed[row + 1]) - start;
            if ((uint)start > (uint)heapLength || (uint)size > (uint)(heapLength - start))
            {
                ThrowRowPastHeap(row, start, size, heapLength);
            }

            ref byte value = ref Unsafe.Add(ref heapRef, start);
            if (requireUtf8 && !Utf8.IsValid(MemoryMarshal.CreateReadOnlySpan(ref value, size)))
            {
                ThrowInvalidRow(row);
            }

            CanonicalSupport.WriteView(ref view, ref value, size, 0, start);
        }
    }

    private static void FromOffsets<TOff>(
        ReadOnlySpan<byte> offsets, ReadOnlySpan<byte> heap, Span<byte> views, int count,
        bool requireUtf8, bool wholeHeap, in ValidityMask mask)
        where TOff : unmanaged
    {
        // Addressed by reference, so that slicing the heap and the views does not cost a bounds
        // check and a span construction per row for a method that takes the reference of each
        // immediately. `offsets` is sliced once to the count + 1 entries the caller promised, and
        // the heap range is checked per row because the offsets come from the file: the ascending
        // and in-heap properties are established elsewhere, and this check is what keeps them from
        // being load-bearing here.
        ReadOnlySpan<TOff> typed = MemoryMarshal.Cast<byte, TOff>(offsets)[..(count + 1)];
        bool allValid = mask.AllValid;
        int start = (int)Widen(typed[0]);

        ref byte heapRef = ref MemoryMarshal.GetReference(heap);
        ref byte viewRef = ref MemoryMarshal.GetReference(views);
        int heapLength = heap.Length;

        for (int i = 0; i < count; i++)
        {
            int end = (int)Widen(typed[i + 1]);
            ref byte view = ref Unsafe.Add(ref viewRef, i * ViewSize);

            if (!allValid && !mask.IsValid(i))
            {
                // The empty view, written out rather than inherited from the allocator.
                Unsafe.WriteUnaligned(ref view, 0UL);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref view, sizeof(ulong)), 0UL);
                start = end;
                continue;
            }

            int size = end - start;
            if ((uint)start > (uint)heapLength || (uint)size > (uint)(heapLength - start))
            {
                ThrowRowPastHeap(i, start, size, heapLength);
            }

            ref byte value = ref Unsafe.Add(ref heapRef, start);

            if (requireUtf8)
            {
                if (wholeHeap)
                {
                    if (size != 0 && (value & 0xC0) == 0x80)
                    {
                        ThrowInvalidRow(i);
                    }
                }
                else if (!Utf8.IsValid(MemoryMarshal.CreateReadOnlySpan(ref value, size)))
                {
                    ThrowInvalidRow(i);
                }
            }

            CanonicalSupport.WriteView(ref view, ref value, size, 0, start);
            start = end;
        }
    }

    /// <summary>Writes one view, every byte of it.</summary>
    /// <returns><see langword="true"/> when the view references the heap.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Write(Span<byte> view, ReadOnlySpan<byte> value, int size, int offset) =>

        // Two register stores, with neither a clear nor a move: at twelve bytes or fewer, an
        // out-of-line call per row costs more than the bytes it writes.
        CanonicalSupport.WriteView(view, value, size, bufferIndex: 0, offset: offset);

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
