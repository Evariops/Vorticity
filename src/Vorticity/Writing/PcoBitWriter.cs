using System;
using System.Buffers;
using System.Buffers.Binary;

namespace Vorticity.Writing;

/// <summary>
/// pco's bit stream written: each value's bits from the lowest, packed straight after the last
/// value's, into a rented buffer that grows as it fills. <see cref="Align"/> pads with zeros to the
/// next byte, as pco's <c>finish_byte</c> does at the end of each section.
/// </summary>
/// <remarks>
/// A value is written a word at a time: up to 63 bits wait in a register and leave eight bytes at
/// once, where a byte loop would pay a load, a shift and a store per byte of every value.
/// </remarks>
internal sealed class PcoBitWriter : IDisposable
{
    private byte[] _buffer;
    private int _bytes;
    private ulong _pending;
    private int _pendingBits;

    /// <summary>Creates a writer whose buffer starts at <paramref name="capacity"/> bytes.</summary>
    internal PcoBitWriter(int capacity)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(capacity, 64));
    }

    /// <summary>The bytes written, from the start of the buffer: the buffer's first <see cref="Length"/> bytes once aligned.</summary>
    internal ReadOnlySpan<byte> Written => _buffer.AsSpan(0, _bytes);

    /// <summary>Bytes written; the position of the next section once <see cref="Align"/> has run.</summary>
    internal int Length => _bytes;

    /// <summary>Writes the low <paramref name="width"/> bits of <paramref name="value"/>, which has no bits above them.</summary>
    /// <param name="value">The value, below 2^<paramref name="width"/>.</param>
    /// <param name="width">Bits, 0 to 64.</param>
    internal void Write(ulong value, int width)
    {
        if (width == 0)
        {
            return;
        }

        _pending |= value << _pendingBits;
        int total = _pendingBits + width;
        if (total < 64)
        {
            _pendingBits = total;
            return;
        }

        // The register is full: out it goes, and what of the value did not fit starts the next.
        Emit(_pending);
        int used = 64 - _pendingBits;
        _pending = used == 64 ? 0 : value >> used;
        _pendingBits = total - 64;
    }

    /// <summary>Pads to the next byte with zeros and writes out every whole byte waiting: <c>finish_byte</c>.</summary>
    /// <returns>The position the next section starts at.</returns>
    internal int Align()
    {
        int bytes = (_pendingBits + 7) >> 3;
        Reserve(8);
        BinaryPrimitives.WriteUInt64LittleEndian(_buffer.AsSpan(_bytes, 8), _pending);
        _bytes += bytes;
        _pending = 0;
        _pendingBits = 0;
        return _bytes;
    }

    /// <summary>Hands the buffer over: the caller returns it to the pool; this writer holds nothing after.</summary>
    internal byte[] TakeBuffer()
    {
        byte[] buffer = _buffer;
        _buffer = [];
        _bytes = 0;
        return buffer;
    }

    /// <summary>Starts over, the buffer kept.</summary>
    internal void Reset()
    {
        _bytes = 0;
        _pending = 0;
        _pendingBits = 0;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = [];
        }
    }

    private void Emit(ulong word)
    {
        Reserve(8);
        BinaryPrimitives.WriteUInt64LittleEndian(_buffer.AsSpan(_bytes, 8), word);
        _bytes += 8;
    }

    private void Reserve(int bytes)
    {
        if (_bytes + bytes <= _buffer.Length)
        {
            return;
        }

        byte[] grown = ArrayPool<byte>.Shared.Rent(Math.Max(_buffer.Length * 2, _bytes + bytes));
        _buffer.AsSpan(0, _bytes).CopyTo(grown);
        if (_buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
        }

        _buffer = grown;
    }
}
