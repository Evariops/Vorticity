using System;
using System.Buffers.Binary;

namespace Vorticity.Writing;

/// <summary>
/// The last row a column saw, copied rather than referenced: the batch it came from has since been
/// recycled.
/// </summary>
internal sealed class PreviousRow
{
    /// <summary>Fixed-width values up to a 256-bit decimal fit without ever growing.</summary>
    private const int Inline = 32;

    /// <summary>Allocated on the first value: interior schema nodes never hold one.</summary>
    private byte[]? _bytes;
    private int _length;

    /// <summary>Whether a row has been seen at all; false before the file's first row.</summary>
    internal bool HasValue { get; private set; }

    /// <summary>Whether that row was null. A null equals a null and differs from every value.</summary>
    internal bool IsNull { get; private set; }

    /// <summary>The row's bytes, empty when it was null.</summary>
    internal ReadOnlySpan<byte> Bytes => _bytes is null ? default : _bytes.AsSpan(0, _length);

    /// <summary>Records a null row.</summary>
    internal void SetNull()
    {
        HasValue = true;
        IsNull = true;
        _length = 0;
    }

    /// <summary>Records a valid row by copying its bytes; the span is never retained.</summary>
    internal void Set(ReadOnlySpan<byte> value)
    {
        if (_bytes is null || value.Length > _bytes.Length)
        {
            // Grown to the value, not doubled: the buffer settles on the column's widest value.
            _bytes = new byte[Math.Max(value.Length, Inline)];
        }

        value.CopyTo(_bytes);
        _length = value.Length;
        HasValue = true;
        IsNull = false;
    }

    // A list column has no row equality, so its node never calls Set and reuses the same buffer to
    // track where the last row's elements ended: the parent's blocks describe the elements array
    // only when each row's elements start where the previous row's ended.

    /// <summary>
    /// Whether a range starting at <paramref name="row"/> continues the recorded one, and where its
    /// elements ended, or -1 when no row named any.
    /// </summary>
    internal bool ListContinues(long row, out long end)
    {
        end = -1;
        if (_bytes is null || _length != 2 * sizeof(long)
            || BinaryPrimitives.ReadInt64LittleEndian(_bytes) != row)
        {
            return false;
        }

        end = BinaryPrimitives.ReadInt64LittleEndian(_bytes.AsSpan(sizeof(long)));
        return true;
    }

    /// <summary>Records the row after a list range and where its named elements ended.</summary>
    internal void SetListEnd(long row, long end)
    {
        _bytes ??= new byte[Inline];
        BinaryPrimitives.WriteInt64LittleEndian(_bytes, row);
        BinaryPrimitives.WriteInt64LittleEndian(_bytes.AsSpan(sizeof(long)), end);
        _length = 2 * sizeof(long);
    }

    /// <summary>
    /// Whether a row with these bytes continues the run: two nulls are equal, a null and a value are
    /// not, and two values are equal byte for byte.
    /// </summary>
    internal bool Equals(bool valid, ReadOnlySpan<byte> value)
    {
        if (IsNull || !valid)
        {
            return IsNull && !valid;
        }

        return Bytes.SequenceEqual(value);
    }
}
