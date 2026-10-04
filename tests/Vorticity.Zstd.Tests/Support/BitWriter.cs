using System;
using System.Collections.Generic;

namespace Vorticity.Zstd.Tests.Support;

/// <summary>
/// libzstd's <c>BIT_CStream_t</c>: fields are appended from the low bits up and flushed little-endian,
/// then an end marker closes the stream. A backward reader takes the fields back in reverse order,
/// each with its bits in their original order.
/// </summary>
internal sealed class BitWriter
{
    private readonly List<byte> _bytes = [];
    private ulong _container;
    private int _position;

    public void Add(ulong value, int count)
    {
        if (count == 0)
        {
            return;
        }

        if (count > 56 || (count < 64 && value >> count != 0))
        {
            throw new ArgumentOutOfRangeException(nameof(value), $"{value} does not fit {count} bits");
        }

        _container |= value << _position;
        _position += count;
        while (_position >= 8)
        {
            _bytes.Add((byte)_container);
            _container >>= 8;
            _position -= 8;
        }
    }

    /// <summary>Appends the end marker (a single 1 bit) and returns the stream.</summary>
    public byte[] Close()
    {
        Add(1, 1);
        if (_position > 0)
        {
            _bytes.Add((byte)_container);
        }

        _container = 0;
        _position = 0;
        return [.. _bytes];
    }
}
