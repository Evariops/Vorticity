using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;

namespace Vorticity.Arrays.Decoders.Compressed.Pco;

/// <summary>
/// Reads LSB-first bit fields out of a pco byte stream: the byte at the current index is loaded as
/// a <c>u64</c>, shifted right by the bit offset within that byte, and masked to the width asked
/// for.
/// </summary>
/// <remarks>
/// Past the logical end this reads zeroes and refuses a read that would need bits the buffer does
/// not hold, where the reference implementation reads past the end and relies on the caller having
/// padded the buffer. The masked value is the same either way, and no padding is required.
/// </remarks>
internal ref struct PcoBitReader
{
    private readonly ReadOnlySpan<byte> _source;
    private long _bitPosition;

    /// <summary>Creates a reader over <paramref name="source"/>, positioned at its first bit.</summary>
    /// <param name="source">The bytes to read.</param>
    internal PcoBitReader(ReadOnlySpan<byte> source)
    {
        _source = source;
        _bitPosition = 0;
    }

    /// <summary>The current position, in bits from the start.</summary>
    internal long BitPosition => _bitPosition;

    /// <summary>Bits still available.</summary>
    internal long BitsRemaining => ((long)_source.Length * 8) - _bitPosition;

    /// <summary>Reads <paramref name="width"/> bits, LSB first, and advances.</summary>
    /// <param name="width">Bits to read, 0 to 64.</param>
    /// <returns>The value, zero-extended.</returns>
    /// <exception cref="VortexFormatException">The stream has fewer bits left.</exception>
    internal ulong ReadUInt(int width)
    {
        ulong value = ReadCore(_bitPosition, width);
        _bitPosition += width;
        return value;
    }

    /// <summary>Reads <paramref name="width"/> bits at an absolute position, without moving.</summary>
    /// <param name="bitPosition">Absolute bit position to read from.</param>
    /// <param name="width">Bits to read.</param>
    /// <returns>The value.</returns>
    /// <remarks>
    /// pco's offsets are addressed rather than streamed: the symbol pass records each value's width
    /// and a running sum, and the offset pass reads value <c>i</c> at <c>base + csum[i]</c>. That is
    /// a random access into the same buffer, not a second cursor -- so this reads from an explicit
    /// position rather than saving, moving and restoring one. Saving and restoring would need a
    /// try/finally, which keeps the method out of line on the decoder's hottest loop.
    /// </remarks>
    internal readonly ulong ReadAt(long bitPosition, int width) => ReadCore(bitPosition, width);

    private readonly ulong ReadCore(long bitPosition, int width)
    {
        // Every refusal is one branch, never taken: a read of zero bits is checked for nothing and
        // reads nothing that matters, since the mask keeps none of the word it loads.
        long remaining = ((long)_source.Length * 8) - bitPosition;
        if ((uint)width > 64u || (width != 0 && (bitPosition < 0 || remaining < width)))
        {
            Refuse(width, remaining);
        }

        int byteIndex = (int)(bitPosition >> 3);
        int bitsPastByte = (int)(bitPosition & 7);
        ulong value = WordAt(byteIndex) >> bitsPastByte;
        if (width > 57)
        {
            // One word yields at most 57 bits safely, since the offset may consume up to seven of
            // them. A wider read takes a second word seven bytes on rather than eight: the
            // one-byte overlap is what keeps the shift below 64 when `bitsPastByte` is zero.
            value |= WordAt(byteIndex + 7) << (56 - bitsPastByte);
        }

        // `bzhi` keeps the low bits for any width, 64 included, without the branch the mask takes.
        return Bmi2.X64.IsSupported ? Bmi2.X64.ZeroHighBits(value, (ulong)width) : value & Mask(width);
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Refuse(int width, long remaining)
    {
        if ((uint)width > 64u)
        {
            CompressedThrow.Format($"A pco bit read of {width} bits is outside [0, 64].");
        }

        CompressedThrow.Format(
            $"A pco bit read of {width} bits ran past the end: {remaining} bits remain.");
    }

    /// <summary>Moves to an absolute bit position.</summary>
    /// <param name="bitPosition">The position; must be within the buffer.</param>
    internal void SeekBits(long bitPosition)
    {
        if (bitPosition < 0 || bitPosition > (long)_source.Length * 8)
        {
            CompressedThrow.Format($"A pco seek to bit {bitPosition} is outside the buffer.");
        }

        _bitPosition = bitPosition;
    }

    /// <summary>Reads one bit as a boolean.</summary>
    /// <returns>The bit.</returns>
    internal bool ReadBool() => ReadUInt(1) != 0;

    /// <summary>Skips to the next byte boundary, requiring the skipped bits to be zero.</summary>
    /// <param name="what">Names the field, for the error message.</param>
    /// <exception cref="VortexFormatException">A skipped bit was set.</exception>
    /// <remarks>
    /// Upstream calls this `drain_empty_byte` and treats a non-zero remainder as corruption rather
    /// than ignoring it, which is what makes a misaligned parse fail loudly instead of drifting.
    /// </remarks>
    internal void DrainEmptyByte(string what)
    {
        int bitsPastByte = (int)(_bitPosition & 7);
        if (bitsPastByte == 0)
        {
            return;
        }

        int slack = 8 - bitsPastByte;
        if (ReadUInt(slack) != 0)
        {
            CompressedThrow.Format($"A pco {what} left non-zero padding bits before a byte boundary.");
        }
    }

    /// <summary>Takes <paramref name="count"/> whole bytes, which requires byte alignment.</summary>
    /// <param name="count">Bytes to take.</param>
    /// <returns>The bytes.</returns>
    /// <exception cref="VortexFormatException">The reader is mid-byte, or the bytes are not there.</exception>
    internal ReadOnlySpan<byte> ReadAlignedBytes(int count)
    {
        if ((_bitPosition & 7) != 0)
        {
            CompressedThrow.Format("A pco aligned read began mid-byte.");
        }

        int byteIndex = (int)(_bitPosition >> 3);
        if (count < 0 || byteIndex + count > _source.Length)
        {
            CompressedThrow.Format(
                $"A pco aligned read of {count} bytes at {byteIndex} runs past the end ({_source.Length}).");
        }

        _bitPosition += (long)count * 8;
        return _source.Slice(byteIndex, count);
    }

    /// <summary>
    /// Eight bytes little-endian at <paramref name="byteIndex"/>, zero-filled past the end.
    /// </summary>
    /// <remarks>
    /// One load when the bytes are there: assembling the word a byte at a time costs eight loads,
    /// eight shifts and eight bitwise ors on every bit read, and a single value can need more than
    /// one read. The byte loop survives only for the last seven bytes of the buffer, where a wide
    /// load would run past the end.
    /// </remarks>
    private readonly ulong WordAt(int byteIndex)
    {
        if ((uint)byteIndex + sizeof(ulong) <= (uint)_source.Length)
        {
            return BinaryPrimitives.ReadUInt64LittleEndian(_source.Slice(byteIndex, sizeof(ulong)));
        }

        ulong word = 0;
        int available = Math.Min(8, Math.Max(0, _source.Length - byteIndex));
        for (int i = 0; i < available; i++)
        {
            word |= (ulong)_source[byteIndex + i] << (i * 8);
        }

        return word;
    }

    private static ulong Mask(int width) => width == 64 ? ulong.MaxValue : (1UL << width) - 1;
}
