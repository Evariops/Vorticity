using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
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
