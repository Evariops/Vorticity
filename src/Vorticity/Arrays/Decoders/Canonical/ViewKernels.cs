using System;
using System.Buffers.Binary;
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
        // narrower types only leave more room. A signed type's answer is also the index of the
        // first negative length, which a reduction discards: 32-bit lengths, the signed type files
        // use, are summed as a reduction that notes whether any is negative, and only then does the
        // scalar loop run, to name it. The other signed types keep the scalar loop.
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
                case PType.I32:
                    if (SumWidening(MemoryMarshal.Cast<byte, int>(lengths)[..count], out long signedTotal))
                    {
                        return (signedTotal, 0, -1);
                    }

                    break;
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

    /// <summary>
    /// Sums 32-bit signed lengths widened to 64-bit lanes; false when one is negative, for the
    /// scalar loop to name it.
    /// </summary>
    /// <remarks>
    /// A negative length is found by ORing every length and testing the signs once, rather than by
    /// a compare and a branch a length: a file's lengths are never negative, so there is nothing to
    /// stop early for. Every length below 2^31, the sum of a 32-bit array of them cannot wrap.
    /// </remarks>
    private static bool SumWidening(ReadOnlySpan<int> values, out long total)
    {
        long sum = 0;
        int signs = 0;
        int i = 0;

        if (Vector.IsHardwareAccelerated)
        {
            int lanes = Vector<int>.Count;
            ref int source = ref MemoryMarshal.GetReference(values);
            Vector<long> wide = Vector<long>.Zero;
            Vector<int> ored = Vector<int>.Zero;
            for (; i <= values.Length - lanes; i += lanes)
            {
                Vector<int> chunk = Vector.LoadUnsafe(ref source, (nuint)i);
                ored |= chunk;
                Vector.Widen(chunk, out Vector<long> low, out Vector<long> high);
                wide += low + high;
            }

            sum = Vector.Sum(wide);
            signs = Vector.LessThanAny(ored, Vector<int>.Zero) ? -1 : 0;
        }

        for (; i < values.Length; i++)
        {
            signs |= values[i];
            sum += values[i];
        }

        total = sum;
        return signs >= 0;
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
        where TLen : unmanaged, INumber<TLen>
    {
        ReadOnlySpan<TLen> typed = MemoryMarshal.Cast<byte, TLen>(lengths);
        bool referenced = false;
        int offset = 0;

        if (wanted.IsEmpty)
        {
            // The dense loop is its own loop: `selective` is loop-invariant, and testing it per row
            // would add a branch and a bounds check on a path that has no selection at all.
            return DenseFromLengths(typed[..count], heap, views, requireUtf8);
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
    /// The dense arm of <see cref="FromLengths{TLen}"/>: every row, its length from
    /// <paramref name="lengths"/>, its bytes the next ones of the heap.
    /// </summary>
    /// <returns><see langword="true"/> when any view references the heap rather than inlining.</returns>
    /// <exception cref="VortexFormatException">
    /// A row runs past the heap, or a UTF-8 row starts off a character.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The rows are taken a block at a time, as <see cref="DenseFromOffsets{TOff}"/> takes them. A
    /// block's lengths are surveyed in vector passes before any of its rows is read: the shortest
    /// and the longest, each inside the heap (a negative one, widened, is not), and their sum, which
    /// must end inside it. A length is checked because it comes from the file: the sum of the
    /// lengths is what the caller allocated the heap from, so a row running past the end means the
    /// two disagree. The block is then cut by the loop its rows suit, each row starting where the
    /// one before it ends.
    /// </para>
    /// <para>
    /// The whole heap is already known valid UTF-8, so a row is valid exactly when it starts on a
    /// code-point boundary; the last row ends at the heap's end, a boundary by construction, so
    /// only the starts are tested.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool DenseFromLengths<TLen>(
        ReadOnlySpan<TLen> lengths, ReadOnlySpan<byte> heap, Span<byte> views, bool requireUtf8)
        where TLen : unmanaged, INumber<TLen>
    {
        ref TLen lengthRef = ref MemoryMarshal.GetReference(lengths);
        ref byte heapRef = ref MemoryMarshal.GetReference(heap);
        ref byte viewRef = ref MemoryMarshal.GetReference(views);
        int heapLength = heap.Length;
        int count = lengths.Length;
        bool referenced = false;
        long start = 0;
        for (int row = 0; row < count; row += OffsetBlockRows)
        {
            int rows = Math.Min(OffsetBlockRows, count - row);
            ref TLen block = ref Unsafe.Add(ref lengthRef, row);
            Extremes(ref block, rows, out TLen least, out TLen greatest);
            ulong shortest = Widen(least);
            ulong longest = Widen(greatest);
            if (shortest > (ulong)heapLength || longest > (ulong)heapLength)
            {
                ThrowFirstBadLengthRow(lengths, heap, requireUtf8);
            }

            long end = start + BlockSum(ref block, rows);
            if (end > heapLength)
            {
                ThrowFirstBadLengthRow(lengths, heap, requireUtf8);
            }

            nint lastStart = (nint)(end - (long)Widen(Unsafe.Add(ref block, rows - 1)));
            ref byte blockViews = ref Unsafe.Add(ref viewRef, (nint)row * ViewSize);
            bool cut = requireUtf8
                ? CutBlock<TLen, ByLengths, Utf8Starts>(
                    ref block, (nint)start, rows, shortest, longest, lastStart, ref heapRef, heapLength, ref blockViews)
                : CutBlock<TLen, ByLengths, AnyStarts>(
                    ref block, (nint)start, rows, shortest, longest, lastStart, ref heapRef, heapLength, ref blockViews);
            if (!cut)
            {
                ThrowFirstBadLengthRow(lengths, heap, requireUtf8);
            }

            referenced |= longest > Inline;
            start = end;
        }

        return referenced;
    }

    /// <summary>The least and the greatest of the <paramref name="count"/> values from <paramref name="first"/>.</summary>
    private static void Extremes<T>(ref T first, int count, out T least, out T greatest)
        where T : unmanaged, INumber<T>
    {
        least = first;
        greatest = first;
        int i = 0;
        if (Vector.IsHardwareAccelerated && count >= Vector<T>.Count)
        {
            int lanes = Vector<T>.Count;
            Vector<T> low = Vector.LoadUnsafe(ref first);
            Vector<T> high = low;
            for (i = lanes; i <= count - lanes; i += lanes)
            {
                Vector<T> values = Vector.LoadUnsafe(ref first, (nuint)i);
                low = Vector.Min(low, values);
                high = Vector.Max(high, values);
            }

            for (int lane = 0; lane < lanes; lane++)
            {
                least = T.Min(least, low[lane]);
                greatest = T.Max(greatest, high[lane]);
            }
        }

        for (; i < count; i++)
        {
            least = T.Min(least, Unsafe.Add(ref first, i));
            greatest = T.Max(greatest, Unsafe.Add(ref first, i));
        }
    }

    /// <summary>
    /// The sum of <paramref name="count"/> lengths from <paramref name="first"/>, every one of them
    /// already known to lie between zero and the heap's length, so that a signed one reads as the
    /// unsigned one of the same width and nothing can wrap.
    /// </summary>
    private static long BlockSum<T>(ref T first, int count)
        where T : unmanaged
    {
        if (typeof(T) == typeof(byte) || typeof(T) == typeof(sbyte))
        {
            return SumWidening(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<T, byte>(ref first), count));
        }

        if (typeof(T) == typeof(ushort) || typeof(T) == typeof(short))
        {
            return SumWidening(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<T, ushort>(ref first), count));
        }

        if (typeof(T) == typeof(uint) || typeof(T) == typeof(int))
        {
            return SumWidening(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<T, uint>(ref first), count));
        }

        ref ulong wide = ref Unsafe.As<T, ulong>(ref first);
        ulong total = 0;
        for (nint i = 0; i < count; i++)
        {
            total += Unsafe.Add(ref wide, i);
        }

        return (long)total;
    }

    /// <summary>
    /// <see cref="DenseFromLengths{TLen}"/> refused its rows; reports the first that runs past the
    /// heap or starts off a character, the way a row-at-a-time cut meets them. An error path may cost
    /// whatever it likes.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowFirstBadLengthRow<TLen>(ReadOnlySpan<TLen> lengths, ReadOnlySpan<byte> heap, bool requireUtf8)
        where TLen : unmanaged
    {
        int offset = 0;
        for (int i = 0; i < lengths.Length; i++)
        {
            int size = (int)Widen(lengths[i]);
            if ((uint)size > (uint)(heap.Length - offset))
            {
                ThrowRowPastHeap(i, offset, size, heap.Length);
            }

            if (requireUtf8 && size != 0 && (heap[offset] & 0xC0) == 0x80)
            {
                ThrowInvalidRow(i);
            }

            offset += size;
        }

        throw new VortexFormatException(
            "A row's length was refused, though every row lies inside the decoded heap.");
    }

    /// <summary>Rows whose offsets <see cref="DenseFromOffsets{TOff}"/> checks before cutting any of them.</summary>
    /// <remarks>
    /// Its offsets, four or eight kilobytes, are still in the first-level cache when the rows are
    /// cut, so the check costs the file's offsets one read from memory rather than two.
    /// </remarks>
    private const int OffsetBlockRows = 1024;

    /// <summary>
    /// The dense arm of <see cref="BuildFromOffsets"/>, every row valid: cuts
    /// <paramref name="count"/> rows by <c>offsets[i]..offsets[i + 1]</c>, and gives the first and
    /// the last offset, checked; false when an offset decreases or leaves the heap, or a UTF-8 row
    /// starts off a character, for the caller to say which.
    /// </summary>
    /// <remarks>
    /// The offsets come from the file and nothing upstream is trusted with them: a block's are
    /// surveyed before any of its rows is read, never decreasing and ending inside the heap, which
    /// puts every row of the block inside the heap, so that its rows are then cut with no check of
    /// their own. The survey is a vector pass, a lane per offset, and it also finds the block's
    /// shortest and longest row, by which <see cref="CutBlock{T, TSizes, TStarts}"/> picks the loop that
    /// cuts it.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool DenseFromOffsets<TOff>(
        ReadOnlySpan<TOff> offsets, ReadOnlySpan<byte> heap, Span<byte> views, int count, bool requireUtf8,
        out int first, out int end)
        where TOff : unmanaged, INumber<TOff>
    {
        ref TOff offsetRef = ref MemoryMarshal.GetReference(offsets);
        ref byte heapRef = ref MemoryMarshal.GetReference(heap);
        ref byte viewRef = ref MemoryMarshal.GetReference(views);
        int heapLength = heap.Length;
        first = 0;
        end = 0;

        // Widened, a negative offset is larger than any heap, so one unsigned compare refuses both.
        if (Widen(offsetRef) > (ulong)heapLength)
        {
            return false;
        }

        for (int row = 0; row < count; row += OffsetBlockRows)
        {
            int rows = Math.Min(OffsetBlockRows, count - row);
            ref TOff block = ref Unsafe.Add(ref offsetRef, row);
            if (!Survey(ref block, rows + 1, out TOff shortest, out TOff longest)
                || Widen(Unsafe.Add(ref block, rows)) > (ulong)heapLength)
            {
                return false;
            }

            nint start = (nint)Widen(block);
            nint lastStart = (nint)Widen(Unsafe.Add(ref block, rows - 1));
            ref byte blockViews = ref Unsafe.Add(ref viewRef, (nint)row * ViewSize);
            bool cut = requireUtf8
                ? CutBlock<TOff, ByOffsets, Utf8Starts>(
                    ref block, start, rows, Widen(shortest), Widen(longest), lastStart, ref heapRef, heapLength, ref blockViews)
                : CutBlock<TOff, ByOffsets, AnyStarts>(
                    ref block, start, rows, Widen(shortest), Widen(longest), lastStart, ref heapRef, heapLength, ref blockViews);
            if (!cut)
            {
                return false;
            }
        }

        first = (int)Widen(offsetRef);
        end = (int)Widen(Unsafe.Add(ref offsetRef, count));
        return true;
    }

    /// <summary>
    /// Whether none of the <paramref name="length"/> values from <paramref name="first"/> is less
    /// than the one before it; and the least and the greatest step between neighbours, which mean
    /// something only when it is so.
    /// </summary>
    /// <remarks>
    /// The shifted compares are ORed and the result tested once, rather than a branch per vector:
    /// a block of offsets from a well-formed file never decreases, so there is nothing to stop
    /// early for. At least two values.
    /// </remarks>
    private static bool Survey<T>(ref T first, int length, out T least, out T greatest)
        where T : unmanaged, INumber<T>
    {
        least = Unsafe.Add(ref first, 1) - first;
        greatest = least;
        int i = 1;
        if (Vector.IsHardwareAccelerated && length > Vector<T>.Count)
        {
            int lanes = Vector<T>.Count;
            Vector<T> fell = Vector<T>.Zero;
            Vector<T> low = new Vector<T>(least);
            Vector<T> high = low;
            for (; i <= length - lanes; i += lanes)
            {
                Vector<T> next = Vector.LoadUnsafe(ref first, (nuint)i);
                Vector<T> previous = Vector.LoadUnsafe(ref first, (nuint)(i - 1));
                Vector<T> step = next - previous;
                fell |= Vector.LessThan(next, previous);
                low = Vector.Min(low, step);
                high = Vector.Max(high, step);
            }

            if (fell != Vector<T>.Zero)
            {
                return false;
            }

            for (int lane = 0; lane < lanes; lane++)
            {
                least = T.Min(least, low[lane]);
                greatest = T.Max(greatest, high[lane]);
            }
        }

        for (; i < length; i++)
        {
            T next = Unsafe.Add(ref first, i);
            T previous = Unsafe.Add(ref first, i - 1);
            if (next < previous)
            {
                return false;
            }

            least = T.Min(least, next - previous);
            greatest = T.Max(greatest, next - previous);
        }

        return true;
    }

    /// <summary>
    /// Cuts one surveyed block by the loop its rows suit: every row out of line, every row inline,
    /// or a mix; and byte-exact when a row lies too near the heap's end to be read as words.
    /// </summary>
    /// <remarks>
    /// Each loop is the cheapest one for its rows, and none branches on a row. A row longer than
    /// the inline limit has thirteen bytes of heap from its start, so a block of such rows only is
    /// read as words wherever it lies; a block with a shorter row is, when its last row, and so
    /// every row, starts twelve bytes or more before the heap's end. The caller has checked that
    /// every row of the block lies inside the heap.
    /// </remarks>
    /// <param name="rows">The block's offsets or lengths, as <typeparamref name="TSizes"/> reads them.</param>
    /// <param name="start">Where the block's first row starts.</param>
    /// <param name="count">Rows in the block.</param>
    /// <param name="shortest">The block's shortest row.</param>
    /// <param name="longest">The block's longest row.</param>
    /// <param name="lastStart">Where the block's last row starts.</param>
    /// <param name="heap">The value bytes.</param>
    /// <param name="heapLength">Their length.</param>
    /// <param name="views">The block's first view.</param>
    private static bool CutBlock<T, TSizes, TStarts>(
        ref T rows, nint start, int count, ulong shortest, ulong longest, nint lastStart, ref byte heap,
        int heapLength, ref byte views)
        where T : unmanaged
        where TSizes : struct, IRowSizes
        where TStarts : struct, IStartCheck
    {
        if (shortest > Inline)
        {
            return CutOutOfLine<T, TSizes, TStarts>(ref rows, start, count, ref heap, ref views);
        }

        if (lastStart > heapLength - Inline)
        {
            return CutExactly<T, TSizes>(ref rows, start, count, ref heap, heapLength, ref views, TStarts.Checked);
        }

        return longest <= Inline
            ? CutInline<T, TSizes, TStarts>(ref rows, start, count, ref heap, ref views)
            : CutMixed<T, TSizes, TStarts>(ref rows, start, count, ref heap, ref views);
    }

    /// <summary>
    /// Cuts <paramref name="count"/> rows of twelve bytes or fewer, each with twelve bytes of heap
    /// from its start; false when a UTF-8 row starts off a character.
    /// </summary>
    /// <remarks>
    /// A row is read as two words and masked down to its size, whatever the size: two loads, two
    /// masks and two stores. The loop calls nothing, so that what it holds stays in registers: a
    /// call in it, even one only an error takes, has every row store and reload its state around
    /// it. A fault is ORed into a flag instead and reported once the loop is done.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool CutInline<T, TSizes, TStarts>(ref T rows, nint start, int count, ref byte heap, ref byte views)
        where T : unmanaged
        where TSizes : struct, IRowSizes
        where TStarts : struct, IStartCheck
    {
        ref ulong lowMasks = ref MemoryMarshal.GetReference(InlineLowMasks);
        ref uint highMasks = ref MemoryMarshal.GetReference(InlineHighMasks);
        int offChar = 0;
        for (nint i = 0; i < count; i++)
        {
            nint size = TSizes.SizeAt(ref rows, i, start);
            ref byte value = ref Unsafe.Add(ref heap, start);
            ulong head = Unsafe.ReadUnaligned<ulong>(ref value) & Unsafe.Add(ref lowMasks, size);
            uint tail = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref value, sizeof(ulong))) & Unsafe.Add(ref highMasks, size);
            if (TStarts.Checked)
            {
                // Masked to the size, an empty row's first byte reads as zero, which starts nothing.
                offChar |= ((int)head & 0xC0) == 0x80 ? 1 : 0;
            }

            ref byte view = ref Unsafe.Add(ref views, i * ViewSize);
            Unsafe.WriteUnaligned(ref view, (ulong)size | (head << 32));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref view, sizeof(ulong)), (head >> 32) | ((ulong)tail << 32));
            start += size;
        }

        return offChar == 0;
    }

    /// <summary>
    /// Cuts <paramref name="count"/> rows each longer than the inline limit; false when a UTF-8 row
    /// starts off a character.
    /// </summary>
    /// <remarks>
    /// A view is the length, the first four bytes and the offset in buffer zero: one load and two
    /// stores a row. Calls nothing, for <see cref="CutInline{T, TSizes, TStarts}"/>'s reason.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool CutOutOfLine<T, TSizes, TStarts>(ref T rows, nint start, int count, ref byte heap, ref byte views)
        where T : unmanaged
        where TSizes : struct, IRowSizes
        where TStarts : struct, IStartCheck
    {
        int offChar = 0;
        for (nint i = 0; i < count; i++)
        {
            nint size = TSizes.SizeAt(ref rows, i, start);
            uint prefix = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref heap, start));
            if (TStarts.Checked)
            {
                offChar |= ((int)prefix & 0xC0) == 0x80 ? 1 : 0;
            }

            ref byte view = ref Unsafe.Add(ref views, i * ViewSize);
            Unsafe.WriteUnaligned(ref view, (ulong)size | ((ulong)prefix << 32));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref view, sizeof(ulong)), (ulong)start << 32);
            start += size;
        }

        return offChar == 0;
    }

    /// <summary>
    /// Cuts <paramref name="count"/> rows of both kinds, each with twelve bytes of heap from its
    /// start; false when a UTF-8 row starts off a character.
    /// </summary>
    /// <remarks>
    /// A row is read as two words masked to its size, whatever its size, and both views are
    /// composed from them: the inline one, and the out-of-line one of length, prefix and offset,
    /// whose first word is the inline one's with the prefix unmasked. The second word is selected
    /// by a mask made of the size's sign against the inline limit, and the size is clamped the same
    /// way, rather than by a conditional: the compiler keeps a conditional inside a loop a branch,
    /// and a column mixing short and long values mispredicts it. Calls nothing, for
    /// <see cref="CutInline{T, TSizes, TStarts}"/>'s reason.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool CutMixed<T, TSizes, TStarts>(ref T rows, nint start, int count, ref byte heap, ref byte views)
        where T : unmanaged
        where TSizes : struct, IRowSizes
        where TStarts : struct, IStartCheck
    {
        ref ulong lowMasks = ref MemoryMarshal.GetReference(InlineLowMasks);
        ref uint highMasks = ref MemoryMarshal.GetReference(InlineHighMasks);
        int offChar = 0;
        for (nint i = 0; i < count; i++)
        {
            nint size = TSizes.SizeAt(ref rows, i, start);

            // Negative exactly for a value too long to inline, so its sign is the out-of-line mask,
            // and adding it back where negative clamps the size to the limit.
            long room = Inline - (long)size;
            long outOfLine = room >> 63;
            nint kept = (nint)(size + (room & outOfLine));

            ref byte value = ref Unsafe.Add(ref heap, start);
            ulong head = Unsafe.ReadUnaligned<ulong>(ref value) & Unsafe.Add(ref lowMasks, kept);
            uint tail = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref value, sizeof(ulong))) & Unsafe.Add(ref highMasks, kept);
            if (TStarts.Checked)
            {
                offChar |= ((int)head & 0xC0) == 0x80 ? 1 : 0;
            }

            ulong inline = (head >> 32) | ((ulong)tail << 32);
            ulong reference = (ulong)start << 32;
            ref byte view = ref Unsafe.Add(ref views, i * ViewSize);
            Unsafe.WriteUnaligned(ref view, (ulong)size | (head << 32));
            Unsafe.WriteUnaligned(
                ref Unsafe.Add(ref view, sizeof(ulong)), inline ^ ((inline ^ reference) & (ulong)outOfLine));
            start += size;
        }

        return offChar == 0;
    }

    /// <summary>
    /// Cuts <paramref name="count"/> rows of which some lie within twelve bytes of the heap's end,
    /// each by <see cref="Place"/>; false when a UTF-8 row starts off a character.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool CutExactly<T, TSizes>(
        ref T rows, nint start, int count, ref byte heap, int heapLength, ref byte views, bool requireUtf8)
        where T : unmanaged
        where TSizes : struct, IRowSizes
    {
        for (nint i = 0; i < count; i++)
        {
            nint size = TSizes.SizeAt(ref rows, i, start);
            if (!Place(ref Unsafe.Add(ref views, i * ViewSize), ref heap, (int)start, (int)size, heapLength, requireUtf8))
            {
                return false;
            }

            start += size;
        }

        return true;
    }

    /// <summary>How a kernel reads the size of a row, as a type.</summary>
    private interface IRowSizes
    {
        /// <summary>The size of row <paramref name="row"/> of a block, which starts at <paramref name="start"/>.</summary>
        static abstract nint SizeAt<T>(ref T rows, nint row, nint start)
            where T : unmanaged;
    }

    /// <summary>Rows cut by offsets: row i ends where offset i + 1 says.</summary>
    private readonly struct ByOffsets : IRowSizes
    {
        public static nint SizeAt<T>(ref T rows, nint row, nint start)
            where T : unmanaged =>
            (nint)Widen(Unsafe.Add(ref rows, row + 1)) - start;
    }

    /// <summary>Rows cut by lengths: row i is length i long.</summary>
    private readonly struct ByLengths : IRowSizes
    {
        public static nint SizeAt<T>(ref T rows, nint row, nint start)
            where T : unmanaged =>
            (nint)Widen(Unsafe.Add(ref rows, row));
    }

    /// <summary>Whether a kernel checks that each row starts on a character, as a type.</summary>
    private interface IStartCheck
    {
        static abstract bool Checked { get; }
    }

    /// <summary>A UTF-8 column's rows, each of which must start on a character.</summary>
    private readonly struct Utf8Starts : IStartCheck
    {
        public static bool Checked => true;
    }

    /// <summary>A binary column's rows, which may start anywhere.</summary>
    private readonly struct AnyStarts : IStartCheck
    {
        public static bool Checked => false;
    }

    /// <summary>
    /// Cuts a stream of values each behind its little-endian <c>u32</c> length into
    /// <paramref name="count"/> views, the lengths left in the heap and the views pointing past
    /// them; the rows built, fewer when a length or a value runs past the stream.
    /// </summary>
    /// <remarks>
    /// Calls nothing, for <see cref="CutInline{T, TSizes, TStarts}"/>'s reason; a row that runs past the
    /// stream ends the loop, and the caller walks the rows again to say which.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static int BuildFromPrefixed(ReadOnlySpan<byte> heap, Span<byte> views, int count)
    {
        ref byte heapRef = ref MemoryMarshal.GetReference(heap);
        ref byte viewRef = ref MemoryMarshal.GetReference(views);
        int heapLength = heap.Length;
        int offset = 0;
        int row = 0;
        for (; row < count; row++)
        {
            if (offset > heapLength - sizeof(uint))
            {
                break;
            }

            uint size = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref heapRef, offset));
            if (!BitConverter.IsLittleEndian)
            {
                size = BinaryPrimitives.ReverseEndianness(size);
            }

            int start = offset + sizeof(uint);
            if (size > (uint)(heapLength - start))
            {
                break;
            }

            Place(ref Unsafe.Add(ref viewRef, row * ViewSize), ref heapRef, start, (int)size, heapLength, requireUtf8: false);
            offset = start + (int)size;
        }

        return row;
    }

    /// <summary>
    /// <see cref="BuildFromPrefixed"/> over a stream whose runs each start on a value of their own,
    /// four runs at a time; false when a value runs past its run, or a whole run does not end where
    /// its last value does, for the caller to cut the stream as one.
    /// </summary>
    /// <remarks>
    /// Where a value starts is known only once the length before it is read, so cutting one stream
    /// is a chain of dependent loads, a load's latency a row. Runs that start on values of their own,
    /// the frames of a compressed stream, are independent chains, and cutting four of them in one
    /// loop, or two when fewer are left, overlaps their latencies. The loops call nothing, for
    /// <see cref="CutInline{T, TSizes, TStarts}"/>'s reason; the run left over, and the rows a run
    /// has past the shortest of its group, are cut one run at a time.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static bool BuildFromPrefixedRuns(ReadOnlySpan<byte> heap, Span<byte> views, ReadOnlySpan<PrefixedRun> runs)
    {
        ref byte heapRef = ref MemoryMarshal.GetReference(heap);
        ref byte viewRef = ref MemoryMarshal.GetReference(views);
        int heapLength = heap.Length;
        int r = 0;
        for (; r + 4 <= runs.Length; r += 4)
        {
            PrefixedRun a = runs[r];
            PrefixedRun b = runs[r + 1];
            PrefixedRun c = runs[r + 2];
            PrefixedRun d = runs[r + 3];
            int common = Math.Min(Math.Min(a.Rows, b.Rows), Math.Min(c.Rows, d.Rows));
            int atA = a.Start;
            int atB = b.Start;
            int atC = c.Start;
            int atD = d.Start;
            if (!FourRuns(
                    ref heapRef, heapLength, ref viewRef, common,
                    ref atA, a.End, a.FirstRow, ref atB, b.End, b.FirstRow,
                    ref atC, c.End, c.FirstRow, ref atD, d.End, d.FirstRow)
                || !RestOfRun(ref heapRef, heapLength, ref viewRef, a, atA, common)
                || !RestOfRun(ref heapRef, heapLength, ref viewRef, b, atB, common)
                || !RestOfRun(ref heapRef, heapLength, ref viewRef, c, atC, common)
                || !RestOfRun(ref heapRef, heapLength, ref viewRef, d, atD, common))
            {
                return false;
            }
        }

        if (r + 2 <= runs.Length)
        {
            PrefixedRun a = runs[r];
            PrefixedRun b = runs[r + 1];
            int common = Math.Min(a.Rows, b.Rows);
            int atA = a.Start;
            int atB = b.Start;
            if (!TwoRuns(ref heapRef, heapLength, ref viewRef, common, ref atA, a.End, a.FirstRow, ref atB, b.End, b.FirstRow)
                || !RestOfRun(ref heapRef, heapLength, ref viewRef, a, atA, common)
                || !RestOfRun(ref heapRef, heapLength, ref viewRef, b, atB, common))
            {
                return false;
            }

            r += 2;
        }

        return r == runs.Length || RestOfRun(ref heapRef, heapLength, ref viewRef, runs[r], runs[r].Start, 0);
    }

    /// <summary>
    /// A stretch of a length-prefixed stream that starts on a value of its own: bytes
    /// [<paramref name="Start"/>, <paramref name="End"/>) of the stream hold the values of rows
    /// [<paramref name="FirstRow"/>, <paramref name="FirstRow"/> + <paramref name="Rows"/>).
    /// </summary>
    /// <param name="Start">Where the length of its first value sits.</param>
    /// <param name="End">Where it ends.</param>
    /// <param name="FirstRow">The row of its first value.</param>
    /// <param name="Rows">How many of its values are wanted.</param>
    /// <param name="Whole">Whether those are all it holds, so that the last one ends at <paramref name="End"/>.</param>
    internal readonly record struct PrefixedRun(int Start, int End, int FirstRow, int Rows, bool Whole);

    /// <summary>
    /// The first <paramref name="rows"/> rows of four runs, a row of each a turn, each run's length
    /// at its <c>at</c>, which it moves on; false when a value runs past its run.
    /// </summary>
    /// <remarks>
    /// The runs come in as values rather than by reference, so that their ends stay in registers
    /// instead of being read again past every view the loop stores.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool FourRuns(
        ref byte heap, int heapLength, ref byte views, int rows,
        ref int atA, int endA, int rowA, ref int atB, int endB, int rowB,
        ref int atC, int endC, int rowC, ref int atD, int endD, int rowD)
    {
        int offsetA = atA;
        int offsetB = atB;
        int offsetC = atC;
        int offsetD = atD;
        ref byte viewA = ref Unsafe.Add(ref views, (nint)rowA * ViewSize);
        ref byte viewB = ref Unsafe.Add(ref views, (nint)rowB * ViewSize);
        ref byte viewC = ref Unsafe.Add(ref views, (nint)rowC * ViewSize);
        ref byte viewD = ref Unsafe.Add(ref views, (nint)rowD * ViewSize);
        bool inside = true;
        for (nint i = 0; i < rows && inside; i++)
        {
            // Non-short-circuiting: the four steps are independent, and each checks its own run.
            inside = NextPrefixed(ref heap, heapLength, ref offsetA, endA, ref Unsafe.Add(ref viewA, i * ViewSize))
                & NextPrefixed(ref heap, heapLength, ref offsetB, endB, ref Unsafe.Add(ref viewB, i * ViewSize))
                & NextPrefixed(ref heap, heapLength, ref offsetC, endC, ref Unsafe.Add(ref viewC, i * ViewSize))
                & NextPrefixed(ref heap, heapLength, ref offsetD, endD, ref Unsafe.Add(ref viewD, i * ViewSize));
        }

        atA = offsetA;
        atB = offsetB;
        atC = offsetC;
        atD = offsetD;
        return inside;
    }

    /// <summary><see cref="FourRuns"/> for two runs.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TwoRuns(
        ref byte heap, int heapLength, ref byte views, int rows,
        ref int atA, int endA, int rowA, ref int atB, int endB, int rowB)
    {
        int offsetA = atA;
        int offsetB = atB;
        ref byte viewA = ref Unsafe.Add(ref views, (nint)rowA * ViewSize);
        ref byte viewB = ref Unsafe.Add(ref views, (nint)rowB * ViewSize);
        bool inside = true;
        for (nint i = 0; i < rows && inside; i++)
        {
            inside = NextPrefixed(ref heap, heapLength, ref offsetA, endA, ref Unsafe.Add(ref viewA, i * ViewSize))
                & NextPrefixed(ref heap, heapLength, ref offsetB, endB, ref Unsafe.Add(ref viewB, i * ViewSize));
        }

        atA = offsetA;
        atB = offsetB;
        return inside;
    }

    /// <summary>
    /// The rows of <paramref name="run"/> from its row <paramref name="done"/>, whose length sits at
    /// <paramref name="offset"/>; false when one runs past the run, or a whole run does not end
    /// where its last value does.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool RestOfRun(ref byte heap, int heapLength, ref byte views, in PrefixedRun run, int offset, int done)
    {
        ref byte view = ref Unsafe.Add(ref views, (nint)run.FirstRow * ViewSize);
        for (nint i = done; i < run.Rows; i++)
        {
            if (!NextPrefixed(ref heap, heapLength, ref offset, run.End, ref Unsafe.Add(ref view, i * ViewSize)))
            {
                return false;
            }
        }

        return !run.Whole || offset == run.End;
    }

    /// <summary>
    /// The view of the value whose length sits at <paramref name="offset"/>, which it moves past the
    /// value; false, and nothing written, when the length or the value runs past <paramref name="end"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool NextPrefixed(ref byte heap, int heapLength, ref int offset, int end, ref byte view)
    {
        int at = offset;
        if (at > end - sizeof(uint))
        {
            return false;
        }

        uint size = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref heap, at));
        if (!BitConverter.IsLittleEndian)
        {
            size = BinaryPrimitives.ReverseEndianness(size);
        }

        int start = at + sizeof(uint);
        if (size > (uint)(end - start))
        {
            return false;
        }

        Place(ref view, ref heap, start, (int)size, heapLength, requireUtf8: false);
        offset = start + (int)size;
        return true;
    }

    /// <summary>
    /// Writes the view of the <paramref name="size"/> bytes at <paramref name="offset"/> of a heap
    /// they lie inside; false, and nothing written, when a UTF-8 row starts off a character.
    /// </summary>
    /// <remarks>
    /// A value too long to inline is its length, its first four bytes and its offset. One short
    /// enough, with twelve bytes of heap from its start, is read as two words whatever its size and
    /// masked down to it, where gathering exactly its bytes branches on the size three ways. The
    /// branch left, inline or not, is the one a column predicts: its values are mostly one or the
    /// other. A short value nearer the heap's end than twelve bytes is gathered byte-exact by
    /// <see cref="CanonicalSupport.WriteView(ref byte, ref byte, int, int, int)"/>, since the two
    /// words would read past the heap.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Place(
        ref byte view, ref byte heap, int offset, int size, int heapLength, bool requireUtf8)
    {
        ref byte value = ref Unsafe.Add(ref heap, offset);
        if (requireUtf8 && size != 0 && (value & 0xC0) == 0x80)
        {
            return false;
        }

        if (size > Inline)
        {
            Unsafe.WriteUnaligned(ref view, (uint)size | ((ulong)Unsafe.ReadUnaligned<uint>(ref value) << 32));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref view, sizeof(ulong)), (ulong)(uint)offset << 32);
            return true;
        }

        if (heapLength - offset < Inline)
        {
            CanonicalSupport.WriteView(ref view, ref value, size, 0, offset);
            return true;
        }

        ulong low = Unsafe.ReadUnaligned<ulong>(ref value) & InlineLowMasks[size];
        ulong high = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref value, sizeof(ulong))) & (ulong)InlineHighMasks[size];
        Unsafe.WriteUnaligned(ref view, (uint)size | (low << 32));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref view, sizeof(ulong)), (low >> 32) | (high << 32));
        return true;
    }

    /// <summary>
    /// Per size up to <see cref="Inline"/>, the bits of a value's first eight bytes that are the
    /// value's.
    /// </summary>
    private static ReadOnlySpan<ulong> InlineLowMasks =>
    [
        0x0000_0000_0000_0000, 0x0000_0000_0000_00FF, 0x0000_0000_0000_FFFF, 0x0000_0000_00FF_FFFF,
        0x0000_0000_FFFF_FFFF, 0x0000_00FF_FFFF_FFFF, 0x0000_FFFF_FFFF_FFFF, 0x00FF_FFFF_FFFF_FFFF,
        0xFFFF_FFFF_FFFF_FFFF, 0xFFFF_FFFF_FFFF_FFFF, 0xFFFF_FFFF_FFFF_FFFF, 0xFFFF_FFFF_FFFF_FFFF,
        0xFFFF_FFFF_FFFF_FFFF,
    ];

    /// <summary>Per size up to <see cref="Inline"/>, the bits of a value's ninth to twelfth bytes that are the value's.</summary>
    private static ReadOnlySpan<uint> InlineHighMasks =>
    [
        0x0000_0000, 0x0000_0000, 0x0000_0000, 0x0000_0000, 0x0000_0000, 0x0000_0000, 0x0000_0000,
        0x0000_0000, 0x0000_0000, 0x0000_00FF, 0x0000_FFFF, 0x00FF_FFFF, 0xFFFF_FFFF,
    ];

    /// <summary>
    /// Cuts <paramref name="heap"/> into views by <c>offsets[i]..offsets[i + 1]</c>.
    /// </summary>
    /// <param name="offsets"><paramref name="count"/> + 1 offsets, as the file holds them.</param>
    /// <param name="ptype">The offsets' physical type.</param>
    /// <param name="heap">The value bytes.</param>
    /// <param name="views">Exactly <paramref name="count"/> views of room; every byte is written.</param>
    /// <param name="count">Rows to build.</param>
    /// <param name="requireUtf8">Whether the dtype is Utf8.</param>
    /// <param name="mask">Per-row validity; null rows get an empty view and are not validated.</param>
    /// <param name="encodingId">The encoding asking, for the messages.</param>
    /// <exception cref="VortexFormatException">
    /// An offset a row is cut by decreases or leaves the heap, or a valid row is not valid UTF-8.
    /// </exception>
    /// <remarks>
    /// With every row valid, every offset is checked here, which is the whole of an ascending walk
    /// over them. With nulls in play a null row's offsets are not read at all, so a caller wanting
    /// them checked walks them itself; a valid row is still checked against the heap as it is cut.
    /// </remarks>
    internal static void BuildFromOffsets(
        ReadOnlySpan<byte> offsets, PType ptype, ReadOnlySpan<byte> heap, Span<byte> views,
        int count, bool requireUtf8, in ValidityMask mask, string encodingId)
    {
        if (mask.AllValid)
        {
            if (!DenseFromOffsets(offsets, ptype, heap, views, count, requireUtf8, out int first, out int end))
            {
                ThrowFirstBadOffsetRow(offsets, ptype, heap, count, requireUtf8, encodingId);
            }

            // The tiling argument needs every row to contribute its bytes. With nulls in play the
            // rows the loop skips would be folded into a whole-heap check that the per-row check
            // never applied, so it is the all-valid arm's alone.
            if (requireUtf8 && !Utf8.IsValid(heap[first..end]))
            {
                ThrowFirstInvalidOffsetRow(offsets, ptype, heap, count);
            }

            return;
        }

        // With nulls in play, the rows are first cut as though every one were valid, and the null
        // rows' views emptied after: a writer leaves a null row's span empty, or holds a real value
        // in it, so its offsets are in order and its bytes are text, and the dense kernel and the
        // tiling argument apply to the whole column. A null row is not bound to either, though, so
        // when that cut refuses the rows the column is cut one row at a time, each valid row checked
        // on its own and no null row read.
        if (DenseFromOffsets(offsets, ptype, heap, views, count, requireUtf8, out int start, out int stop)
            && (!requireUtf8 || Utf8.IsValid(heap[start..stop])))
        {
            EmptyNullViews(views, in mask, count);
            return;
        }

        switch (ptype)
        {
            case PType.U8:
                FromOffsets<byte>(offsets, heap, views, count, requireUtf8, in mask);
                break;
            case PType.U16:
                FromOffsets<ushort>(offsets, heap, views, count, requireUtf8, in mask);
                break;
            case PType.U32:
                FromOffsets<uint>(offsets, heap, views, count, requireUtf8, in mask);
                break;
            case PType.U64:
                FromOffsets<ulong>(offsets, heap, views, count, requireUtf8, in mask);
                break;
            case PType.I8:
                FromOffsets<sbyte>(offsets, heap, views, count, requireUtf8, in mask);
                break;
            case PType.I16:
                FromOffsets<short>(offsets, heap, views, count, requireUtf8, in mask);
                break;
            case PType.I32:
                FromOffsets<int>(offsets, heap, views, count, requireUtf8, in mask);
                break;
            default:
                FromOffsets<long>(offsets, heap, views, count, requireUtf8, in mask);
                break;
        }
    }

    /// <summary>Writes the empty view over every null row's, sixty-four rows of validity at a time.</summary>
    private static void EmptyNullViews(Span<byte> views, in ValidityMask mask, int count)
    {
        ReadOnlySpan<byte> bits = mask.Bits;
        int bitOffset = mask.BitOffset;
        ref byte viewRef = ref MemoryMarshal.GetReference(views);
        for (int row = 0; row < count; row += 64)
        {
            // A mask with no bits, every row null, reads as zeroes.
            ulong nulls = ~BitWords.Load(bits, bitOffset + row) & BitWords.Mask(Math.Min(64, count - row));
            while (nulls != 0)
            {
                ref byte view = ref Unsafe.Add(ref viewRef, (nint)(row + BitOperations.TrailingZeroCount(nulls)) * ViewSize);
                Unsafe.WriteUnaligned(ref view, 0UL);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref view, sizeof(ulong)), 0UL);
                nulls &= nulls - 1;
            }
        }
    }

    /// <summary><see cref="DenseFromOffsets{TOff}"/> with the offsets' type resolved.</summary>
    private static bool DenseFromOffsets(
        ReadOnlySpan<byte> offsets, PType ptype, ReadOnlySpan<byte> heap, Span<byte> views, int count,
        bool requireUtf8, out int first, out int end) =>
        ptype switch
        {
            PType.U8 => DenseFromOffsets(OffsetsAs<byte>(offsets, count), heap, views, count, requireUtf8, out first, out end),
            PType.U16 => DenseFromOffsets(OffsetsAs<ushort>(offsets, count), heap, views, count, requireUtf8, out first, out end),
            PType.U32 => DenseFromOffsets(OffsetsAs<uint>(offsets, count), heap, views, count, requireUtf8, out first, out end),
            PType.U64 => DenseFromOffsets(OffsetsAs<ulong>(offsets, count), heap, views, count, requireUtf8, out first, out end),
            PType.I8 => DenseFromOffsets(OffsetsAs<sbyte>(offsets, count), heap, views, count, requireUtf8, out first, out end),
            PType.I16 => DenseFromOffsets(OffsetsAs<short>(offsets, count), heap, views, count, requireUtf8, out first, out end),
            PType.I32 => DenseFromOffsets(OffsetsAs<int>(offsets, count), heap, views, count, requireUtf8, out first, out end),
            _ => DenseFromOffsets(OffsetsAs<long>(offsets, count), heap, views, count, requireUtf8, out first, out end),
        };

    /// <summary>The <paramref name="count"/> + 1 offsets that cut <paramref name="count"/> rows, typed.</summary>
    private static ReadOnlySpan<T> OffsetsAs<T>(ReadOnlySpan<byte> offsets, int count)
        where T : unmanaged =>
        MemoryMarshal.Cast<byte, T>(offsets)[..(count + 1)];

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

    /// <summary>
    /// Views for a few rows whose offsets were decoded apart from the rest: row <c>i</c> of the
    /// output spans <c>offsets[pairs[i]]</c> to <c>offsets[pairs[i] + 1]</c>.
    /// </summary>
    /// <returns>
    /// Whether every valid row fits the heap and, for text, is UTF-8; false at the first that does
    /// not, for a read of the whole offsets to report it by its row.
    /// </returns>
    internal static bool TryBuildFromPairs(
        ReadOnlySpan<byte> offsets, PType ptype, ReadOnlySpan<byte> heap, Span<byte> views,
        ReadOnlySpan<int> pairs, bool requireUtf8, in ValidityMask mask) =>
        ptype switch
        {
            PType.U8 => FromPairs<byte>(offsets, heap, views, pairs, requireUtf8, in mask),
            PType.U16 => FromPairs<ushort>(offsets, heap, views, pairs, requireUtf8, in mask),
            PType.U32 => FromPairs<uint>(offsets, heap, views, pairs, requireUtf8, in mask),
            PType.U64 => FromPairs<ulong>(offsets, heap, views, pairs, requireUtf8, in mask),
            PType.I8 => FromPairs<sbyte>(offsets, heap, views, pairs, requireUtf8, in mask),
            PType.I16 => FromPairs<short>(offsets, heap, views, pairs, requireUtf8, in mask),
            PType.I32 => FromPairs<int>(offsets, heap, views, pairs, requireUtf8, in mask),
            _ => FromPairs<long>(offsets, heap, views, pairs, requireUtf8, in mask),
        };

    private static bool FromPairs<TOff>(
        ReadOnlySpan<byte> offsets, ReadOnlySpan<byte> heap, Span<byte> views,
        ReadOnlySpan<int> pairs, bool requireUtf8, in ValidityMask mask)
        where TOff : unmanaged
    {
        ReadOnlySpan<TOff> typed = MemoryMarshal.Cast<byte, TOff>(offsets);
        bool allValid = mask.AllValid;
        ref byte heapRef = ref MemoryMarshal.GetReference(heap);
        ref byte viewRef = ref MemoryMarshal.GetReference(views);
        int heapLength = heap.Length;
        for (int i = 0; i < pairs.Length; i++)
        {
            ref byte view = ref Unsafe.Add(ref viewRef, i * ViewSize);
            if (!allValid && !mask.IsValid(i))
            {
                Unsafe.WriteUnaligned(ref view, 0UL);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref view, sizeof(ulong)), 0UL);
                continue;
            }

            int pair = pairs[i];
            ulong start = Widen(typed[pair]);
            ulong end = Widen(typed[pair + 1]);
            if (end < start || end > (ulong)heapLength)
            {
                return false;
            }

            int size = (int)(end - start);
            ref byte value = ref Unsafe.Add(ref heapRef, (nint)start);
            if (requireUtf8 && !Utf8.IsValid(MemoryMarshal.CreateReadOnlySpan(ref value, size)))
            {
                return false;
            }

            CanonicalSupport.WriteView(ref view, ref value, size, 0, (int)start);
        }

        return true;
    }

    /// <summary>
    /// The arm of <see cref="BuildFromOffsets"/> with nulls in play: a null row gets the empty
    /// view, and a valid row is checked against the heap, and as UTF-8, on its own.
    /// </summary>
    private static void FromOffsets<TOff>(
        ReadOnlySpan<byte> offsets, ReadOnlySpan<byte> heap, Span<byte> views, int count,
        bool requireUtf8, in ValidityMask mask)
        where TOff : unmanaged
    {
        // Addressed by reference, so that slicing the heap and the views does not cost a bounds
        // check and a span construction per row for a method that takes the reference of each
        // immediately. `offsets` is sliced once to the count + 1 entries the caller promised, and
        // the heap range is checked per row because the offsets come from the file.
        ReadOnlySpan<TOff> typed = MemoryMarshal.Cast<byte, TOff>(offsets)[..(count + 1)];
        int start = (int)Widen(typed[0]);

        ref byte heapRef = ref MemoryMarshal.GetReference(heap);
        ref byte viewRef = ref MemoryMarshal.GetReference(views);
        int heapLength = heap.Length;

        for (int i = 0; i < count; i++)
        {
            int end = (int)Widen(typed[i + 1]);
            ref byte view = ref Unsafe.Add(ref viewRef, i * ViewSize);

            if (!mask.IsValid(i))
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
            if (requireUtf8 && !Utf8.IsValid(MemoryMarshal.CreateReadOnlySpan(ref value, size)))
            {
                ThrowInvalidRow(i);
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

    /// <summary>
    /// The bytes a column's rows tile failed as a whole, every row valid and every offset checked,
    /// so at least one row fails; finds which, one row at a time.
    /// </summary>
    private static void ThrowFirstInvalidOffsetRow(
        ReadOnlySpan<byte> offsets, PType ptype, ReadOnlySpan<byte> heap, int count)
    {
        for (int i = 0; i < count; i++)
        {
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

    /// <summary>
    /// <see cref="DenseFromOffsets{TOff}"/> refused its rows; reports the first fault in the order
    /// a row-at-a-time cut meets them: an offset out of order or outside the heap, then the heap's
    /// UTF-8 as a whole, then a row starting off a character. An error path may cost whatever it
    /// likes.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowFirstBadOffsetRow(
        ReadOnlySpan<byte> offsets, PType ptype, ReadOnlySpan<byte> heap, int count, bool requireUtf8,
        string encodingId)
    {
        long first = 0;
        long previous = 0;
        int offCharacter = -1;
        for (int i = 0; i <= count; i++)
        {
            long offset = CanonicalSupport.ReadInteger(offsets, ptype, i);
            if (i > 0 && offset < previous)
            {
                throw new VortexFormatException(
                    $"{encodingId} offsets must not decrease; offset {i} is {offset} after {previous}.");
            }

            if (offset < 0 || offset > heap.Length)
            {
                throw new VortexFormatException(
                    $"{encodingId} offset {i} is {offset}, outside the {heap.Length}-byte value heap.");
            }

            // Row i - 1 is [previous, offset), both now inside the heap.
            if (i == 0)
            {
                first = offset;
            }
            else if (offCharacter < 0 && previous < offset && (heap[(int)previous] & 0xC0) == 0x80)
            {
                offCharacter = i - 1;
            }

            previous = offset;
        }

        if (requireUtf8)
        {
            if (!Utf8.IsValid(heap[(int)first..(int)previous]))
            {
                ThrowFirstInvalidOffsetRow(offsets, ptype, heap, count);
            }

            if (offCharacter >= 0)
            {
                ThrowInvalidRow(offCharacter);
            }
        }

        throw new VortexFormatException(
            $"{encodingId} rows were refused, though every offset and every row is well-formed.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInvalidRow(int row) =>
        throw new VortexFormatException($"Row {row} of a Utf8 array is not valid UTF-8.");
}
