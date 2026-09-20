using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Vorticity.Buffers;
using Vorticity.Serialization.Schemas;

namespace Vorticity.IO;

/// <summary>
/// The validation the I/O layer's memory safety depends on: every file-supplied segment locator
/// passes through here, unconditionally, before a single byte of it is addressed.
/// </summary>
internal static class SegmentIo
{
    /// <summary>
    /// The longest segment this reader will materialize. A <see cref="VortexBuffer"/> is bounded
    /// by <see cref="int"/>, and a coalesced run adds up to <see cref="VortexLimits.MaxAlignment"/>
    /// bytes of leading padding when its start is rounded down to 64, so the run length must still
    /// fit an <see cref="int"/> after that.
    /// </summary>
    internal const int MaxSegmentLength = int.MaxValue - VortexLimits.MaxAlignment;

    /// <summary>
    /// Validates one file-supplied segment locator and narrows it to host types.
    /// </summary>
    /// <param name="spec">The locator, straight off the wire and entirely attacker-controlled.</param>
    /// <param name="offset">The validated file offset, non-negative.</param>
    /// <param name="length">The validated length in bytes, non-negative.</param>
    /// <exception cref="VortexFormatException">
    /// The alignment exponent exceeds <see cref="VortexLimits.MaxAlignmentExponent"/>, the length
    /// exceeds <see cref="MaxSegmentLength"/>, or <c>offset + length</c> overflows <see cref="ulong"/>
    /// or escapes the addressable <see cref="long"/> range.
    /// </exception>
    internal static void ValidateSpec(in SegmentSpec spec, out long offset, out int length)
    {
        // Never cast an exponent without this: the cap lives in VortexLimits, and a local copy of
        // it would be one more place to forget when it moves.
        VortexLimits.CheckAlignmentExponent(spec.AlignmentExponent);

        if (spec.Length > (uint)MaxSegmentLength)
        {
            ThrowLength(spec.Length);
        }

        // Formed in u64 so the wrap is observable rather than undefined; `end < offset` is the
        // wrap, and the long cap is what makes the later pointer arithmetic representable.
        ulong end = spec.Offset + spec.Length;
        if (end < spec.Offset || end > (ulong)long.MaxValue)
        {
            ThrowRange(spec.Offset, spec.Length);
        }

        offset = (long)spec.Offset;
        length = (int)spec.Length;
    }

    /// <summary>
    /// Rejects a validated range that does not lie wholly inside a file of
    /// <paramref name="fileLength"/> bytes.
    /// </summary>
    /// <param name="offset">A validated, non-negative file offset.</param>
    /// <param name="length">A validated, non-negative length.</param>
    /// <param name="fileLength">The real file length.</param>
    /// <exception cref="VortexFormatException">The range escapes the file.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void CheckInFile(long offset, int length, long fileLength)
    {
        // offset and length are already known non-negative and their sum fits a long.
        if (offset + length > fileLength)
        {
            ThrowPastEof(offset, length, fileLength);
        }
    }

    /// <summary>Validates a caller-supplied alignment request.</summary>
    /// <param name="alignment">A power of two in <c>[1, <see cref="VortexLimits.MaxAlignment"/>]</c>.</param>
    /// <param name="paramName">The caller's parameter name.</param>
    /// <exception cref="ArgumentOutOfRangeException">The alignment is not usable.</exception>
    internal static void CheckAlignmentArgument(int alignment, string paramName)
    {
        // A caller says this, not a file, so a bad value is an argument error rather than a
        // malformed-file error.
        if (!Alignment.IsPowerOfTwo(alignment) || alignment > VortexLimits.MaxAlignment)
        {
            ThrowAlignmentArgument(alignment, paramName);
        }
    }

    /// <summary>
    /// The clamping rule <see cref="ISegmentSource.ReadRangeAsync"/> shares across sources:
    /// negative arguments are caller errors, a start past the end is a file error, and a length
    /// that runs off the end is truncated rather than rejected.
    /// </summary>
    /// <param name="offset">The requested start.</param>
    /// <param name="length">The requested length.</param>
    /// <param name="alignment">The requested base alignment.</param>
    /// <param name="fileLength">The real file length.</param>
    /// <returns>The number of bytes actually available, possibly zero.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A caller argument is out of range.</exception>
    /// <exception cref="VortexFormatException">The range starts wholly past the end.</exception>
    internal static int ClampRange(long offset, int length, int alignment, long fileLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        CheckAlignmentArgument(alignment, nameof(alignment));

        if (offset > fileLength)
        {
            ThrowRangePastEof(offset, fileLength);
        }

        long available = fileLength - offset;
        if (available == 0)
        {
            if (length > 0)
            {
                ThrowRangePastEof(offset, fileLength);
            }

            return 0;
        }

        return length <= available ? length : (int)available;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowRangePastEof(long offset, long fileLength) =>
        throw new VortexFormatException(
            $"A range starting at {offset} lies wholly past the end of a {fileLength}-byte file.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowLength(uint length) =>
        throw new VortexFormatException(
            $"Segment length {length} exceeds the {MaxSegmentLength}-byte maximum this reader can " +
            "address in a single buffer.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowRange(ulong offset, uint length) =>
        throw new VortexFormatException(
            $"Segment at offset {offset} with length {length} overflows the addressable file range.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowPastEof(long offset, int length, long fileLength) =>
        throw new VortexFormatException(
            $"Segment [{offset}, {offset + length}) escapes a file of {fileLength} bytes.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowAlignmentArgument(int alignment, string paramName) =>
        throw new ArgumentOutOfRangeException(
            paramName,
            alignment,
            $"Alignment must be a power of two in [1, {VortexLimits.MaxAlignment}].");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    internal static void ThrowTruncatedRead(long offset, int wanted, int got) =>
        throw new VortexFormatException(
            $"Read of {wanted} bytes at offset {offset} returned {got}: the file is shorter than " +
            "its own footer claims.");
}
