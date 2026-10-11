using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Vorticity.Arrays.Decoders.Canonical;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>
/// Dense values spread back over the rows that hold one, for the encodings that store no value for
/// a null row: <c>vortex.zstd</c> and <c>vortex.pco</c>, whose reference readers both rebuild the
/// rows with <c>PrimitiveData::from_values_byte_buffer</c>, a null row's bytes zero.
/// </summary>
internal static class ValidRows
{
    /// <summary>Counts the rows that hold a value.</summary>
    /// <param name="context">The decode context owning the validity's bitmap.</param>
    /// <param name="validity">The rows' validity.</param>
    /// <param name="length">Rows.</param>
    /// <returns>The valid rows.</returns>
    internal static int Count(ArrayDecodeContext context, Validity validity, int length)
    {
        ValidityMask mask = ValidityMask.From(context, validity);
        if (mask.AllValid)
        {
            return length;
        }

        if (mask.AllInvalid)
        {
            return 0;
        }

        // A popcount per word rather than a call per row. The two uniform kinds returned above
        // leave only a bitmap here, so the bits are exactly what the kernel counts.
        return BitmapKernels.CountSet(mask.Bits, mask.BitOffset, length);
    }

    /// <summary>Whether the rows a spread writes into hold zeros before it, as a null row's bytes must.</summary>
    internal interface IRows
    {
        /// <summary>Whether the rows are zeroed, so that a null row is left as it is rather than written.</summary>
        static abstract bool Zeroed { get; }
    }

    /// <summary>Rows their caller zeroed.</summary>
    internal readonly struct ZeroedRows : IRows
    {
        public static bool Zeroed => true;
    }

    /// <summary>Rows of any bytes: a null row's are written zero with the rest.</summary>
    internal readonly struct UnzeroedRows : IRows
    {
        public static bool Zeroed => false;
    }

    /// <summary>
    /// Spreads <paramref name="source"/>'s values of <paramref name="byteWidth"/> bytes over the
    /// mask's valid rows of <paramref name="destination"/>, which is zeroed.
    /// </summary>
    /// <param name="source">The dense values, at least one per valid row.</param>
    /// <param name="destination">The rows, zeroed.</param>
    /// <param name="mask">Which rows hold a value.</param>
    /// <param name="length">Rows.</param>
    /// <param name="byteWidth">Bytes per value.</param>
    /// <param name="encodingId">The encoding, for the error a short source raises.</param>
    internal static void Spread(
        ReadOnlySpan<byte> source, Span<byte> destination, in ValidityMask mask, int length, int byteWidth, string encodingId) =>
        Spread<ZeroedRows>(source, destination, in mask, length, byteWidth, encodingId);

    /// <summary>
    /// Spreads <paramref name="source"/>'s values over the mask's valid rows of
    /// <paramref name="destination"/>, whatever it holds: a null row's bytes are written zero, so
    /// that no pass clears the rows before and none of them is written twice.
    /// </summary>
    /// <param name="source">The dense values, at least one per valid row.</param>
    /// <param name="destination">The rows, of any bytes.</param>
    /// <param name="mask">Which rows hold a value.</param>
    /// <param name="length">Rows.</param>
    /// <param name="byteWidth">Bytes per value.</param>
    /// <param name="encodingId">The encoding, for the error a short source raises.</param>
    internal static void SpreadOver(
        ReadOnlySpan<byte> source, Span<byte> destination, in ValidityMask mask, int length, int byteWidth, string encodingId) =>
        Spread<UnzeroedRows>(source, destination, in mask, length, byteWidth, encodingId);

    private static void Spread<TRows>(
        ReadOnlySpan<byte> source, Span<byte> destination, in ValidityMask mask, int length, int byteWidth, string encodingId)
        where TRows : struct, IRows
    {
        // The width is resolved once here and not per row: a copy whose length is only known at
        // run time costs a call per valid row to move four or eight bytes, which dominates the
        // scatter on a nullable column. Sixteen bytes are a view, a decimal of up to 38 digits or
        // a UUID, which a Parquet column of strings, decimals or UUIDs spreads.
        switch (byteWidth)
        {
            case 1:
                Expand<byte, TRows>(source, destination, in mask, length, encodingId);
                break;
            case 2:
                Expand<ushort, TRows>(source, destination, in mask, length, encodingId);
                break;
            case 4:
                Expand<uint, TRows>(source, destination, in mask, length, encodingId);
                break;
            case 8:
                Expand<ulong, TRows>(source, destination, in mask, length, encodingId);
                break;
            case 16:
                Expand<UInt128, TRows>(source, destination, in mask, length, encodingId);
                break;
            default:
                ExpandWide<TRows>(source, destination, in mask, length, byteWidth);
                break;
        }
    }

    /// <summary>Spreads a dense run of <typeparamref name="T"/> over the mask's valid rows.</summary>
    /// <typeparam name="T">The value type, chosen from the byte width by the caller.</typeparam>
    /// <typeparam name="TRows">Whether the rows are zeroed before.</typeparam>
    /// <remarks>
    /// Sixty-four rows at a time: a word of valid rows is one copy of its values, a word of nulls is
    /// nothing where the destination is zeroed and its zeros otherwise, and a mixed word of many
    /// values writes every one of its rows, the next value or zero as its bit says, the next value
    /// moving on by the bit. Walking the set bits instead chains each value's row to the bit before
    /// it, which pays only for a word of few values, <see cref="SparseScatter"/> or fewer, and is
    /// cleared first where the destination is not.
    /// </remarks>
    private static void Expand<T, TRows>(
        ReadOnlySpan<byte> source, Span<byte> destination, in ValidityMask mask, int length, string encodingId)
        where T : unmanaged, IBinaryInteger<T>
        where TRows : struct, IRows
    {
        ReadOnlySpan<T> values = MemoryMarshal.Cast<byte, T>(source);
        Span<T> rows = MemoryMarshal.Cast<byte, T>(destination)[..length];
        ReadOnlySpan<byte> bits = mask.Bits;
        int bitOffset = mask.BitOffset;
        int next = 0;
        bool expands = Expands<T>();
        Compute.WordBytes spread = expands ? Compute.WordBytes.Create() : default;
        for (int row = 0; row < length; row += 64)
        {
            int span = Math.Min(64, length - row);
            ulong full = BitWords.Mask(span);
            ulong word = BitWords.Load(bits, bitOffset + row) & full;
            if (word == full)
            {
                values.Slice(next, span).CopyTo(rows.Slice(row, span));
                next += span;
                continue;
            }

            if (word == 0)
            {
                if (!TRows.Zeroed)
                {
                    rows.Slice(row, span).Clear();
                }

                continue;
            }

            int count = BitOperations.PopCount(word);
            if (next + count > values.Length)
            {
                CompressedThrow.Format($"{encodingId} has more valid rows than decompressed values.");
            }

            // A whole word whose values can be read a vector at a time is expanded in registers.
            if (expands && span == 64 && next + 64 <= values.Length)
            {
                next = Expanded(values, ref MemoryMarshal.GetReference(rows[row..]), word, next, spread);
                continue;
            }

            if (count > SparseScatter)
            {
                next = Scatter(values, ref MemoryMarshal.GetReference(rows[row..]), span, word, next);
                continue;
            }

            if (!TRows.Zeroed)
            {
                rows.Slice(row, span).Clear();
            }

            next = Walk(values, ref MemoryMarshal.GetReference(rows[row..]), word, next);
        }
    }

    /// <summary>Whether <typeparamref name="T"/>'s lanes expand in registers on this machine: a primitive's, of up to eight bytes.</summary>
    private static bool Expands<T>() =>
        Compute.WordBytes.IsAccelerated && Unsafe.SizeOf<T>() <= sizeof(ulong)
            && (Unsafe.SizeOf<T>() >= 4 ? Avx512F.IsSupported : Avx512Vbmi2.IsSupported);

    /// <summary>
    /// One mixed word's rows by <c>vpexpand</c>: for each vector of rows, the next values loaded
    /// whole, laid on the rows whose bits are set and zero elsewhere, and the cursor moved on by
    /// the bits' count.
    /// </summary>
    /// <returns>The next value after the word.</returns>
    /// <remarks>
    /// Eight rows a step for eight-byte values, sixteen for four, thirty-two for two, the whole word
    /// for one: a load, an expand and a store, where the scatter writes each row. The expand runs
    /// from a register to a register; it is its form that writes memory that is slow on Zen 4.
    /// The caller has checked that 64 values can be read from <paramref name="next"/>, which covers
    /// every load of the word, each at most a vector past values the word takes.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Expanded<T>(ReadOnlySpan<T> values, ref T rows, ulong word, int next, Compute.WordBytes spread)
        where T : unmanaged
    {
        ref T value = ref MemoryMarshal.GetReference(values);
        int lanes = Vector512<T>.Count;
        for (int group = 0; group < 64 / lanes; group++)
        {
            Vector512<T> dense = Vector512.LoadUnsafe(ref value, (nuint)next);
            Vector512<T> mask = spread.Lanes<T>(word, group);
            Vector512<T> laid = Unsafe.SizeOf<T>() switch
            {
                1 => Avx512Vbmi2.Expand(Vector512<byte>.Zero, mask.AsByte(), dense.AsByte()).As<byte, T>(),
                2 => Avx512Vbmi2.Expand(Vector512<ushort>.Zero, mask.AsUInt16(), dense.AsUInt16()).As<ushort, T>(),
                4 => Avx512F.Expand(Vector512<uint>.Zero, mask.AsUInt32(), dense.AsUInt32()).As<uint, T>(),
                _ => Avx512F.Expand(Vector512<ulong>.Zero, mask.AsUInt64(), dense.AsUInt64()).As<ulong, T>(),
            };
            laid.StoreUnsafe(ref rows, (nuint)(group * lanes));
            next += BitOperations.PopCount((word >> (group * lanes)) & (lanes == 64 ? ulong.MaxValue : (1UL << lanes) - 1));
        }

        return next;
    }

    /// <summary>
    /// The valid rows of a word at or under which its set bits are walked rather than every row
    /// written: a written row costs about as much as a walked bit whose word holds this many.
    /// </summary>
    private const int SparseScatter = 36;

    /// <summary>
    /// One mixed word's rows: each takes the next value or zero as its bit says, and the next value
    /// moves on by the bit; the value read is held to the last one, so a word's trailing nulls read
    /// nothing past the values.
    /// </summary>
    /// <returns>The next value after the word.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Scatter<T>(ReadOnlySpan<T> values, ref T rows, int span, ulong word, int next)
        where T : unmanaged, IBinaryInteger<T>
    {
        ref T value = ref MemoryMarshal.GetReference(values);
        int last = values.Length - 1;
        for (int k = 0; k < span; k++)
        {
            int bit = (int)(word >> k) & 1;
            // Past the last value by one at most, and then held to it: the sign of last - next.
            int at = next - (int)((uint)(last - next) >> 31);
            Unsafe.Add(ref rows, k) = Unsafe.Add(ref value, at) & (T.Zero - T.CreateTruncating(bit));
            next += bit;
        }

        return next;
    }

    /// <summary>A word of few values: its set bits walked, each taking the next value.</summary>
    /// <returns>The next value after the word.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Walk<T>(ReadOnlySpan<T> values, ref T rows, ulong word, int next)
        where T : unmanaged
    {
        ref T value = ref MemoryMarshal.GetReference(values);
        while (word != 0)
        {
            Unsafe.Add(ref rows, BitOperations.TrailingZeroCount(word)) = Unsafe.Add(ref value, next++);
            word &= word - 1;
        }

        return next;
    }

    /// <summary>The same, for a width no primitive type has, a row at a time.</summary>
    /// <remarks>
    /// No <c>PType</c> is 3, 5, 6 or 7 bytes wide, but a Parquet column of fixed-length byte arrays
    /// is any width: an INTERVAL's twelve bytes, a hash's twenty.
    /// </remarks>
    private static void ExpandWide<TRows>(
        ReadOnlySpan<byte> source, Span<byte> destination, in ValidityMask mask, int length, int width)
        where TRows : struct, IRows
    {
        int next = 0;
        for (int row = 0; row < length; row++)
        {
            if (mask.IsValid(row))
            {
                source.Slice(next * width, width).CopyTo(destination.Slice(row * width, width));
                next++;
            }
            else if (!TRows.Zeroed)
            {
                destination.Slice(row * width, width).Clear();
            }
        }
    }
}
