using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Vorticity.Zstd.Internal;

/// <summary>What a reload found, in libzstd's order: anything past <see cref="Completed"/> is an error.</summary>
internal enum BitStreamStatus
{
    /// <summary>At least 57 bits are available.</summary>
    Unfinished = 0,

    /// <summary>The start of the stream is reached; fewer bits may remain.</summary>
    EndOfBuffer = 1,

    /// <summary>Every bit of the stream has been consumed, exactly.</summary>
    Completed = 2,

    /// <summary>More bits were consumed than the stream holds.</summary>
    Overflow = 3,
}

/// <summary>
/// The backward bit stream every entropy-coded section of a frame is written in: read from its last
/// byte towards its first, starting below the highest set bit of the last byte, which is a marker.
/// </summary>
/// <remarks>
/// <para>
/// A transcription of libzstd's <c>BIT_DStream_t</c> on a 64-bit container, statuses included, because
/// several decoders end on them: the Huffman weights stop when the stream overflows, and every
/// section must end with <see cref="IsEndOfStream"/> exactly. Reading the same bits in the same
/// order makes a corrupted section fail where libzstd fails.
/// </para>
/// <para>
/// The container is left-aligned: <see cref="_bitsConsumed"/> counts the bits already taken from its
/// top. Bits past the start of the stream read as zeros, as the last symbols of a stream need.
/// </para>
/// </remarks>
internal ref struct BackwardBitReader
{
    private readonly ReadOnlySpan<byte> _source;
    private ulong _container;
    private int _bitsConsumed;
    private int _position;

    /// <summary>Opens <paramref name="source"/>, whose last byte must hold the end marker.</summary>
    public BackwardBitReader(ReadOnlySpan<byte> source, ZstdError error)
    {
        if (source.IsEmpty)
        {
            Throw.Error(error);
        }

        _source = source;
        byte lastByte = source[^1];
        if (lastByte == 0)
        {
            Throw.Error(error);
        }

        int padding = 8 - HighBit(lastByte);
        if (source.Length >= sizeof(ulong))
        {
            _position = source.Length - sizeof(ulong);
            _container = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(_position));
            _bitsConsumed = padding;
        }
        else
        {
            _position = 0;
            ulong container = 0;
            for (int i = source.Length - 1; i >= 0; i--)
            {
                container = (container << 8) | source[i];
            }

            _container = container;
            _bitsConsumed = padding + ((sizeof(ulong) - source.Length) * 8);
        }
    }

    /// <summary>
    /// A reader already partway through <paramref name="source"/>: the container is the eight bytes at
    /// <paramref name="position"/>, of which <paramref name="bitsConsumed"/> are consumed from the top.
    /// </summary>
    public static BackwardBitReader Resume(ReadOnlySpan<byte> source, int position, int bitsConsumed)
    {
        Debug.Assert(position >= 0 && position + sizeof(ulong) <= source.Length);
        return new BackwardBitReader(source, position, bitsConsumed);
    }

    private BackwardBitReader(ReadOnlySpan<byte> source, int position, int bitsConsumed)
    {
        _source = source;
        _position = position;
        _bitsConsumed = bitsConsumed;
        _container = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(position));
    }

    /// <summary>Bits consumed from the current container; more than 64 once the stream has overflowed.</summary>
    public readonly int BitsConsumed => _bitsConsumed;

    /// <summary>libzstd's <c>BIT_endOfDStream</c>: the start is reached and every bit was consumed.</summary>
    public readonly bool IsEndOfStream => _position == 0 && _bitsConsumed == 64;

    /// <summary>
    /// libzstd's <c>BIT_readBits</c>: any count from 0 to 31, as a shift and a mask. Past the
    /// container the result is meaningless but harmless, and the same as libzstd's; the stream then
    /// fails its end check.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadBits(int count)
    {
        uint value = (uint)(_container >> ((64 - _bitsConsumed - count) & 63)) & (uint)((1UL << count) - 1);
        _bitsConsumed += count;
        return value;
    }

    /// <summary>libzstd's <c>BIT_readBitsFast</c>: a count from 1 to 31; zeros come in past the end.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadBitsFast(int count)
    {
        uint value = (uint)((_container << (_bitsConsumed & 63)) >> ((64 - count) & 63));
        _bitsConsumed += count;
        return value;
    }

    /// <summary>libzstd's <c>BIT_lookBitsFast</c>: the next <paramref name="count"/> bits (1 to 31), unconsumed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly uint PeekBitsFast(int count) =>
        (uint)((_container << (_bitsConsumed & 63)) >> ((64 - count) & 63));

    /// <summary>Consumes <paramref name="count"/> bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SkipBits(int count) => _bitsConsumed += count;

    /// <summary>libzstd's <c>BIT_reloadDStream</c>: refills the container from the bytes below it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BitStreamStatus Reload()
    {
        if (_bitsConsumed > 64)
        {
            return BitStreamStatus.Overflow;
        }

        if (_position >= sizeof(ulong))
        {
            _position -= _bitsConsumed >> 3;
            _bitsConsumed &= 7;
            _container = BinaryPrimitives.ReadUInt64LittleEndian(_source.Slice(_position));
            return BitStreamStatus.Unfinished;
        }

        if (_position == 0)
        {
            return _bitsConsumed < 64 ? BitStreamStatus.EndOfBuffer : BitStreamStatus.Completed;
        }

        // Fewer than eight bytes below the container: move by what is left, no further.
        int bytes = _bitsConsumed >> 3;
        BitStreamStatus status = BitStreamStatus.Unfinished;
        if (_position - bytes < 0)
        {
            bytes = _position;
            status = BitStreamStatus.EndOfBuffer;
        }

        _position -= bytes;
        _bitsConsumed -= bytes * 8;
        _container = BinaryPrimitives.ReadUInt64LittleEndian(_source.Slice(_position));
        return status;
    }

    /// <summary>
    /// Decodes <paramref name="length"/> symbols of a single-symbol Huffman table (entries of
    /// <see cref="HuffmanTable"/>: the code length in the low byte, the symbol in the high byte),
    /// each looked up with the next <paramref name="tableLog"/> bits, the stream reloaded before any
    /// symbol once more than 64 - <see cref="HuffmanTable.MaxTableLog"/> of its bits are consumed:
    /// the reloads of <see cref="Reload"/>, at the same symbols, its statuses ignored.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reader's state is held in locals for the loop: through the reader, every symbol would read
    /// and write it in memory. A reload eight bytes or more from the start takes the loop's own path;
    /// any other goes through <see cref="Reload"/>.
    /// </para>
    /// <para>
    /// The symbols form one chain, each code's length deciding where the next starts. So the chain
    /// runs on the container shifted past the consumed bits, which a symbol shifts by its entry (whose
    /// low six bits are the code's length): a shift, the lookup, a shift. The count of consumed bits,
    /// which only the reloads read, follows on the side. Until 64 bits are consumed the shifted
    /// container is the reader's container shifted by that count; past that, the stream has
    /// overflowed and fails its end check whatever the symbols read.
    /// </para>
    /// </remarks>
    public void DecodeHuffmanSymbols(ref ushort table, int tableLog, ref byte output, nint length)
    {
        ulong container = _container;
        int consumed = _bitsConsumed;
        int position = _position;
        ulong bits = container << (consumed & 63);
        bits = consumed >= 64 ? 0 : bits;
        ref byte source = ref MemoryMarshal.GetReference(_source);
        int shift = (64 - tableLog) & 63;
        for (nint i = 0; i < length; i++)
        {
            if (consumed > 64 - HuffmanTable.MaxTableLog)
            {
                if ((consumed <= 64) & (position >= sizeof(ulong)))
                {
                    position -= consumed >> 3;
                    consumed &= 7;
                    container = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, position));
                }
                else
                {
                    _container = container;
                    _bitsConsumed = consumed;
                    _position = position;
                    Reload();
                    container = _container;
                    consumed = _bitsConsumed;
                    position = _position;
                }

                bits = container << (consumed & 63);
                bits = consumed >= 64 ? 0 : bits;
            }

            uint entry = Unsafe.Add(ref table, (nint)(bits >> shift));
            Unsafe.Add(ref output, i) = (byte)(entry >> 8);
            bits <<= (int)entry;
            consumed += (int)(entry & 0xFF);
        }

        _container = container;
        _bitsConsumed = consumed;
        _position = position;
    }

    /// <summary>libzstd's <c>BIT_reloadDStreamFast</c>: refuses to come within eight bytes of the start.</summary>
    public BitStreamStatus ReloadFast()
    {
        if (_position < sizeof(ulong))
        {
            return BitStreamStatus.Overflow;
        }

        return Reload();
    }

    /// <summary>The index of the highest set bit of a non-zero value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int HighBit(uint value) => 31 - BitOperations.LeadingZeroCount(value);
}
