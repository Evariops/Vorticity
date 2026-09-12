// Rounding helpers shared by the writer's inter-segment padding (docs/03-architecture.md §3.8)
// and by the reader's coalesced range arithmetic (docs/03-architecture.md §3.5, "Coalescing
// versus alignment"). They are separated from VortexBuffer because both the writer, which has no
// buffer yet, and the I/O layer, which works in file offsets, need them.
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Vorticity.Buffers;

/// <summary>
/// Power-of-two rounding over 64-bit file positions and raw addresses.
/// </summary>
/// <remarks>
/// <para>
/// Every method validates <c>alignment</c> and rejects a value that is not a positive power of
/// two. Positions are file offsets and are therefore required to be non-negative; a negative
/// position can only come from arithmetic on a corrupt file, so it is rejected rather than
/// rounded (docs/03-architecture.md §6: validate before use, never after).
/// </para>
/// <para>
/// <see cref="AlignUp(long, int)"/> never overflows: rounding a value within
/// <c>alignment - 1</c> of <see cref="long.MaxValue"/> throws
/// <see cref="VortexFormatException"/> instead of wrapping to a negative offset that would then
/// pass a naive bounds check.
/// </para>
/// </remarks>
public static class Alignment
{
    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="value"/> is a positive power of two.
    /// </summary>
    /// <param name="value">The candidate alignment.</param>
    /// <remarks>
    /// Zero and negative values (including <see cref="int.MinValue"/>, whose two's-complement bit
    /// pattern would otherwise satisfy the <c>x &amp; (x - 1)</c> test) return
    /// <see langword="false"/>.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;

    /// <summary>
    /// Rounds <paramref name="value"/> up to the next multiple of <paramref name="alignment"/>.
    /// </summary>
    /// <param name="value">A non-negative position.</param>
    /// <param name="alignment">A positive power of two.</param>
    /// <returns>The smallest multiple of <paramref name="alignment"/> that is not less than
    /// <paramref name="value"/>.</returns>
    /// <exception cref="VortexFormatException">
    /// <paramref name="alignment"/> is not a positive power of two, <paramref name="value"/> is
    /// negative, or the rounded result would exceed <see cref="long.MaxValue"/>.
    /// </exception>
    public static long AlignUp(long value, int alignment)
    {
        CheckAlignment(alignment);
        CheckPosition(value);

        long mask = alignment - 1L;
        long remainder = value & mask;
        if (remainder == 0)
        {
            return value;
        }

        long padding = alignment - remainder;

        // The wrap this guards against is the dangerous one: value + padding silently becomes
        // negative, and a caller that then compares it against a length passes the check.
        if (value > long.MaxValue - padding)
        {
            ThrowAlignUpOverflow(value, alignment);
        }

        return value + padding;
    }

    /// <summary>
    /// Rounds <paramref name="value"/> down to the previous multiple of
    /// <paramref name="alignment"/>. This is the <c>start &amp; ~63L</c> of the coalescing rule in
    /// docs/03-architecture.md §3.5.
    /// </summary>
    /// <param name="value">A non-negative position.</param>
    /// <param name="alignment">A positive power of two.</param>
    /// <returns>The largest multiple of <paramref name="alignment"/> that is not greater than
    /// <paramref name="value"/>.</returns>
    /// <exception cref="VortexFormatException">
    /// <paramref name="alignment"/> is not a positive power of two, or <paramref name="value"/>
    /// is negative.
    /// </exception>
    public static long AlignDown(long value, int alignment)
    {
        CheckAlignment(alignment);
        CheckPosition(value);
        return value & ~(alignment - 1L);
    }

    /// <summary>
    /// Returns the number of padding bytes the writer must emit at <paramref name="position"/> so
    /// that the next byte lands on a multiple of <paramref name="alignment"/>.
    /// </summary>
    /// <param name="position">A non-negative write position.</param>
    /// <param name="alignment">A positive power of two.</param>
    /// <returns>A value in <c>[0, alignment)</c>.</returns>
    /// <exception cref="VortexFormatException">
    /// <paramref name="alignment"/> is not a positive power of two, <paramref name="position"/> is
    /// negative, or the aligned position would exceed <see cref="long.MaxValue"/>.
    /// </exception>
    /// <remarks>
    /// Deliberately defined as <c>AlignUp(position, alignment) - position</c> so the two helpers
    /// can never disagree; in particular it throws in exactly the cases <see cref="AlignUp"/>
    /// throws.
    /// </remarks>
    public static int PaddingTo(long position, int alignment) =>
        (int)(AlignUp(position, alignment) - position);

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="pointer"/> is a multiple of
    /// <paramref name="alignment"/>.
    /// </summary>
    /// <param name="pointer">The address to test. A null pointer is aligned to everything.</param>
    /// <param name="alignment">A positive power of two.</param>
    /// <exception cref="VortexFormatException">
    /// <paramref name="alignment"/> is not a positive power of two.
    /// </exception>
    public static unsafe bool IsAligned(void* pointer, int alignment)
    {
        CheckAlignment(alignment);
        return ((nuint)pointer & (nuint)(alignment - 1)) == 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CheckAlignment(int alignment)
    {
        if (!IsPowerOfTwo(alignment))
        {
            ThrowAlignment(alignment);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CheckPosition(long value)
    {
        if (value < 0)
        {
            ThrowNegativePosition(value);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowAlignment(int alignment) =>
        throw new VortexFormatException(
            $"Alignment {alignment} is not a positive power of two.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowNegativePosition(long value) =>
        throw new VortexFormatException(
            $"Position {value} is negative; file positions and lengths are never negative.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowAlignUpOverflow(long value, int alignment) =>
        throw new VortexFormatException(
            $"Aligning {value} up to a multiple of {alignment} overflows a signed 64-bit position.");
}
