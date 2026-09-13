// pco's bit reader - pco-1.0.3/src/bit_reader.rs.
//
// FIRST PIECE OF THE PCO PORT that is testable on its own, which is why it comes before anything
// that uses it. Bits are read LSB-FIRST within a little-endian window: the byte at the current
// index is loaded as a `u64`, shifted right by the bit offset within that byte, and masked to the
// requested width.
//
// TWO WORDS, NOT ONE, and the overlap is deliberate upstream. A single `u64` yields at most 57 bits
// safely, because up to 7 of its bits may be consumed by the offset. Wider reads take a second word
// starting SEVEN bytes on rather than eight - overlapping by a byte - which avoids a left shift by
// 64 when the reader happens to be byte-aligned. `calc_max_bytes(64) = 9` selects that path for
// every 64-bit latent, so it is the normal case and not a corner.
//
// BOUNDS INSTEAD OF PADDING. Upstream reads past the logical end and relies on the caller having
// padded the buffer; the extra bits are masked away. This reads zeroes past the end instead, which
// produces the same masked value without requiring the padding, and refuses a read that would need
// bits the buffer does not have.
using System;

namespace Vorticity.Arrays.Decoders.Compressed.Pco;

/// <summary>Reads LSB-first bit fields out of a pco byte stream.</summary>
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
        if ((uint)width > 64u)
        {
            CompressedThrow.Format($"A pco bit read of {width} bits is outside [0, 64].");
        }

        if (width == 0)
        {
            return 0;
        }

        if (BitsRemaining < width)
        {
            CompressedThrow.Format(
                $"A pco bit read of {width} bits ran past the end: {BitsRemaining} bits remain.");
        }

        int byteIndex = (int)(_bitPosition >> 3);
        int bitsPastByte = (int)(_bitPosition & 7);

        ulong first = WordAt(byteIndex) >> bitsPastByte;
        ulong value;
        if (width <= 57)
        {
            value = first;
        }
        else
        {
            // Seven bytes on, not eight: the deliberate one-byte overlap that keeps the shift below
            // 64 when `bitsPastByte` is zero.
            int processed = 56 - bitsPastByte;
            value = first | (WordAt(byteIndex + 7) << processed);
        }

        _bitPosition += width;
        return value & Mask(width);
    }

    /// <summary>Reads <paramref name="width"/> bits at an absolute position, without moving.</summary>
    /// <param name="bitPosition">Absolute bit position to read from.</param>
    /// <param name="width">Bits to read.</param>
    /// <returns>The value.</returns>
    /// <remarks>
    /// pco's offsets are addressed rather than streamed: the symbol pass records each value's width
    /// and a running sum, and the offset pass reads value <c>i</c> at <c>base + csum[i]</c>. That is
    /// a random access into the same buffer, not a second cursor.
    /// </remarks>
    internal ulong ReadAt(long bitPosition, int width)
    {
        long saved = _bitPosition;
        _bitPosition = bitPosition;
        try
        {
            return ReadUInt(width);
        }
        finally
        {
            _bitPosition = saved;
        }
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

    /// <summary>Eight bytes little-endian at <paramref name="byteIndex"/>, zero past the end.</summary>
    private readonly ulong WordAt(int byteIndex)
    {
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
