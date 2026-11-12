using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Vorticity.Compute;

/// <summary>
/// The 64 bits of a word as 64 bytes, a byte per bit: how a mask of 64 rows, a validity word or a
/// word of verdicts, becomes a state per row without a branch or a loop over its bits.
/// </summary>
/// <remarks>
/// The word goes into every 64-bit lane, a byte shuffle sends byte <c>j</c> of the vector to the
/// word's byte holding bit <c>j</c>, and an and keeps that bit: three instructions for 64 rows.
/// The shuffle is AVX-512BW's `vpshufb`, which stays inside each 128-bit lane, and every such lane
/// holds the whole word twice, so it reaches every byte it needs.
/// </remarks>
internal readonly struct WordBytes
{
    private readonly Vector512<byte> _index;
    private readonly Vector512<byte> _bit;

    private WordBytes(Vector512<byte> index, Vector512<byte> bit)
    {
        _index = index;
        _bit = bit;
    }

    /// <summary>Whether this machine spreads words: 512-bit vectors and AVX-512BW.</summary>
    internal static bool IsAccelerated => Vector512.IsHardwareAccelerated && Avx512BW.IsSupported;

    /// <summary>
    /// Whether this machine spreads words sixteen bits at a time where it does not with 512-bit vectors: a byte shuffle of
    /// 128 bits, SSSE3's <c>pshufb</c> on x64 or the table lookup <c>tbl</c> on ARM64.
    /// </summary>
    internal static bool IsAccelerated128 => Vector128.IsHardwareAccelerated && (Ssse3.IsSupported || AdvSimd.Arm64.IsSupported);

    /// <summary>
    /// The 64 rows of verdicts <paramref name="held"/> holds as a <see cref="Trilean"/> a row from <paramref name="into"/>:
    /// true where its bit is set, false where clear, unknown where the bit of <paramref name="unknown"/> is set, which the
    /// caller has cleared in <paramref name="held"/>. Four shuffles of sixteen rows, where AVX-512 is not
    /// (<see cref="IsAccelerated128"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Store128(ulong held, ulong unknown, ref byte into)
    {
        Vector128<byte> index = Vector128.Create((byte)0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1);
        Vector128<byte> bit = Vector128.Create((byte)1, 2, 4, 8, 16, 32, 64, 128, 1, 2, 4, 8, 16, 32, 64, 128);
        for (int k = 0; k < 4; k++)
        {
            Vector128<byte> ones = Vector128.Min(Spread128((ushort)(held >> (16 * k)), index) & bit, Vector128<byte>.One);
            if (unknown != 0)
            {
                Vector128<byte> nulls = Vector128.Min(Spread128((ushort)(unknown >> (16 * k)), index) & bit, Vector128<byte>.One);
                ones |= nulls + nulls;
            }

            ones.StoreUnsafe(ref into, (nuint)(16 * k));
        }
    }

    /// <summary>
    /// A byte per bit of the sixteen bits <paramref name="bits"/>: all ones where the bit is clear, zero where it is set, as a
    /// select mask (<see cref="Clear"/>, sixteen rows at a time; <see cref="IsAccelerated128"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<byte> Clear128(ushort bits) =>
        Vector128.Equals(
            Spread128(bits, Vector128.Create((byte)0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1))
                & Vector128.Create((byte)1, 2, 4, 8, 16, 32, 64, 128, 1, 2, 4, 8, 16, 32, 64, 128),
            Vector128<byte>.Zero);

    /// <summary>
    /// A lane per bit for a 256-bit vector of <typeparamref name="T"/> where AVX2 is: all ones where the bit is set, zero
    /// where it is clear, lane <c>j</c> for bit <c>j</c> of <paramref name="bits"/>, as <see cref="Lanes{T}"/> makes them at
    /// 512 bits.
    /// </summary>
    /// <typeparam name="T">An integer of 1, 2, 4 or 8 bytes.</typeparam>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector256<T> Lanes256<T>(ulong bits)
    {
        if (Unsafe.SizeOf<T>() == 1)
        {
            // Each 128-bit lane holds the bits' four bytes four times, and vpshufb stays inside it.
            Vector256<byte> spread = Avx2.Shuffle(
                Vector256.Create((uint)bits).AsByte(),
                Vector256.Create((byte)0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 3, 3, 3, 3, 3, 3, 3, 3));
            Vector256<byte> bit = Vector256.Create((byte)1, 2, 4, 8, 16, 32, 64, 128, 1, 2, 4, 8, 16, 32, 64, 128, 1, 2, 4, 8, 16, 32, 64, 128, 1, 2, 4, 8, 16, 32, 64, 128);
            return (~Vector256.Equals(spread & bit, Vector256<byte>.Zero)).As<byte, T>();
        }

        if (Unsafe.SizeOf<T>() == 2)
        {
            Vector256<ushort> bit = Vector256.Create((ushort)1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768);
            return (~Vector256.Equals(Vector256.Create((ushort)bits) & bit, Vector256<ushort>.Zero)).As<ushort, T>();
        }

        if (Unsafe.SizeOf<T>() == 4)
        {
            Vector256<uint> bit = Vector256.Create(1u, 2, 4, 8, 16, 32, 64, 128);
            return (~Vector256.Equals(Vector256.Create((uint)bits) & bit, Vector256<uint>.Zero)).As<uint, T>();
        }

        Vector256<ulong> lane = Vector256.Create(1UL, 2, 4, 8);
        return (~Vector256.Equals(Vector256.Create(bits) & lane, Vector256<ulong>.Zero)).As<ulong, T>();
    }

    /// <summary>
    /// <see cref="Lanes256{T}"/> for a 128-bit vector, where its byte shuffle is (<see cref="IsAccelerated128"/>): lane
    /// <c>j</c> for bit <c>j</c> of <paramref name="bits"/>.
    /// </summary>
    /// <typeparam name="T">An integer of 1, 2, 4 or 8 bytes.</typeparam>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<T> Lanes128<T>(ulong bits)
    {
        if (Unsafe.SizeOf<T>() == 1)
        {
            return (~Clear128((ushort)bits)).As<byte, T>();
        }

        if (Unsafe.SizeOf<T>() == 2)
        {
            Vector128<ushort> bit = Vector128.Create((ushort)1, 2, 4, 8, 16, 32, 64, 128);
            return (~Vector128.Equals(Vector128.Create((ushort)bits) & bit, Vector128<ushort>.Zero)).As<ushort, T>();
        }

        if (Unsafe.SizeOf<T>() == 4)
        {
            Vector128<uint> bit = Vector128.Create(1u, 2, 4, 8);
            return (~Vector128.Equals(Vector128.Create((uint)bits) & bit, Vector128<uint>.Zero)).As<uint, T>();
        }

        Vector128<ulong> lane = Vector128.Create(1UL, 2);
        return (~Vector128.Equals(Vector128.Create(bits) & lane, Vector128<ulong>.Zero)).As<ulong, T>();
    }

    /// <summary>Byte <c>j</c> of the result the byte of <paramref name="bits"/> holding bit <c>j</c>, by <paramref name="index"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Spread128(ushort bits, Vector128<byte> index)
    {
        Vector128<byte> pair = Vector128.Create(bits).AsByte();
        return Ssse3.IsSupported ? Ssse3.Shuffle(pair, index) : AdvSimd.Arm64.VectorTableLookup(pair, index);
    }

    /// <summary>The shuffle and the bit masks, loaded once for a loop.</summary>
    internal static WordBytes Create() => new WordBytes(
        Vector512.LoadUnsafe(ref MemoryMarshal.GetReference(Index)),
        Vector512.LoadUnsafe(ref MemoryMarshal.GetReference(Bit)));

    /// <summary>A byte per bit: 1 where the bit is set, 0 where it is clear.</summary>
    /// <remarks>Which is <see cref="Trilean.True"/> and <see cref="Trilean.False"/>.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Vector512<byte> Ones(ulong word) => Vector512.Min(Selected(word), Vector512<byte>.One);

    /// <summary>A byte per bit: all ones where the bit is clear, zero where it is set, as a select mask.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Vector512<byte> Clear(ulong word) => Vector512.Equals(Selected(word), Vector512<byte>.Zero);

    /// <summary>
    /// A lane per bit for a 512-bit vector of <typeparamref name="T"/>: all ones where the bit is
    /// set, zero where it is clear, for the lanes of group <paramref name="group"/> -- the bits
    /// <c>group * Count</c> onward, <c>Count</c> being <see cref="Vector512{T}.Count"/>.
    /// </summary>
    /// <remarks>
    /// A byte lane is the word's shuffle; a wider one is the lane's slice of the word broadcast and
    /// tested against the lane's own bit.
    /// </remarks>
    /// <typeparam name="T">An integer of 1, 2, 4 or 8 bytes.</typeparam>
    /// <param name="word">Sixty-four rows' bits.</param>
    /// <param name="group">Which of the word's <c>64 / Count</c> groups of lanes.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Vector512<T> Lanes<T>(ulong word, int group)
    {
        if (Unsafe.SizeOf<T>() == 1)
        {
            return (~Clear(word)).As<byte, T>();
        }

        if (Unsafe.SizeOf<T>() == 2)
        {
            // The word's byte masks, the group's half of them sign-extended to words: the halves of the word broadcast
            // into a vector of two halves went through memory, 0.21 ns a row of a select against 0.034 at 256 bits.
            Vector512<byte> set = ~Clear(word);
            Vector256<sbyte> half = (group == 0 ? set.GetLower() : set.GetUpper()).AsSByte();
            return Avx512BW.ConvertToVector512Int16(half).As<short, T>();
        }

        if (Unsafe.SizeOf<T>() == 4)
        {
            Vector512<uint> held = Vector512.Create((uint)(word >> (16 * group)) & 0xFFFF);
            Vector512<uint> bit = Vector512.Create(1u, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768);
            return (~Vector512.Equals(held & bit, Vector512<uint>.Zero)).As<uint, T>();
        }

        Vector512<ulong> lanes = Vector512.Create((word >> (8 * group)) & 0xFF);
        Vector512<ulong> lane = Vector512.Create(1UL, 2, 4, 8, 16, 32, 64, 128);
        return (~Vector512.Equals(lanes & lane, Vector512<ulong>.Zero)).As<ulong, T>();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Vector512<byte> Selected(ulong word) =>
        Avx512BW.Shuffle(Vector512.Create(word).AsByte(), _index) & _bit;

    /// <summary>For byte <c>j</c>, the byte of the word holding bit <c>j</c>: <c>j / 8</c>.</summary>
    private static ReadOnlySpan<byte> Index =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1,
        2, 2, 2, 2, 2, 2, 2, 2, 3, 3, 3, 3, 3, 3, 3, 3,
        4, 4, 4, 4, 4, 4, 4, 4, 5, 5, 5, 5, 5, 5, 5, 5,
        6, 6, 6, 6, 6, 6, 6, 6, 7, 7, 7, 7, 7, 7, 7, 7,
    ];

    /// <summary>For byte <c>j</c>, its bit within that byte of the word: <c>1 &lt;&lt; (j % 8)</c>.</summary>
    private static ReadOnlySpan<byte> Bit =>
    [
        1, 2, 4, 8, 16, 32, 64, 128, 1, 2, 4, 8, 16, 32, 64, 128,
        1, 2, 4, 8, 16, 32, 64, 128, 1, 2, 4, 8, 16, 32, 64, 128,
        1, 2, 4, 8, 16, 32, 64, 128, 1, 2, 4, 8, 16, 32, 64, 128,
        1, 2, 4, 8, 16, 32, 64, 128, 1, 2, 4, 8, 16, 32, 64, 128,
    ];
}
