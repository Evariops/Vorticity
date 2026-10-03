using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Vorticity.Zstd.Internal;

/// <summary>
/// libzstd's <c>BIT_CStream_t</c>: the writer of the bit streams a <see cref="BackwardBitReader"/>
/// reads, bits added from the lowest up and flushed eight bytes at a time, little-endian. The last
/// bits written are the first read, after the end marker <see cref="Close"/> adds.
/// </summary>
/// <remarks>
/// A flush writes the whole container and moves on by the bytes it completed, so the buffer must
/// have eight bytes past the last one kept: the stream stops at <see cref="_end"/>, eight bytes
/// before the end of its buffer, and a stream that reaches it reports an overflow on closing.
/// </remarks>
internal unsafe struct BitWriter
{
    private ulong _container;
    private int _bitPosition;
    private readonly byte* _start;
    private byte* _ptr;
    private readonly byte* _end;

    /// <summary>libzstd's <c>BIT_initCStream</c>; <see cref="IsValid"/> is false for a buffer of eight bytes or less.</summary>
    public BitWriter(byte* start, nuint capacity)
    {
        _start = start;
        _ptr = start;
        _end = start + capacity - sizeof(ulong);
        IsValid = capacity > sizeof(ulong);
    }

    public readonly bool IsValid { get; }

    /// <summary>libzstd's <c>BIT_addBits</c>: the low <paramref name="count"/> bits (0 to 31) of <paramref name="value"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddBits(ulong value, int count)
    {
        _container |= (value & ((1UL << count) - 1)) << _bitPosition;
        _bitPosition += count;
    }

    /// <summary>libzstd's <c>BIT_addBitsFast</c>: <paramref name="value"/> has no bit above the <paramref name="count"/> it adds.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddBitsFast(ulong value, int count)
    {
        _container |= value << _bitPosition;
        _bitPosition += count;
    }

    /// <summary>
    /// libzstd's <c>BIT_flushBits</c>: the whole bytes out, never past <see cref="_end"/>. The
    /// container holds at most 63 bits, so that at most seven bytes leave it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Flush()
    {
        Debug.Assert(_bitPosition < 64);
        int bytes = _bitPosition >> 3;
        Unsafe.WriteUnaligned(_ptr, _container);
        _ptr += bytes;
        if (_ptr > _end)
        {
            _ptr = _end;
        }

        _bitPosition &= 7;
        _container >>= bytes * 8;
    }

    /// <summary>libzstd's <c>BIT_closeCStream</c>: the end marker, then the last bytes.</summary>
    /// <returns>The size of the stream, or 0 when it did not fit.</returns>
    public nuint Close()
    {
        AddBitsFast(1, 1);
        Flush();
        if (_ptr >= _end)
        {
            return 0;
        }

        return (nuint)(_ptr - _start) + (_bitPosition > 0 ? 1u : 0u);
    }
}
