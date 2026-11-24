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

    /// <summary>
    /// Rented on the first value, a block of the pool's: interior schema nodes never hold one. An array of the column's
    /// widest value, a text of megabytes, stayed in the heap the writer's life; the block goes back as it grows and once
    /// the file is done (<see cref="Release"/>).
    /// </summary>
    private Buffers.Block<byte> _bytes;
    private int _length;

    /// <summary>Whether a row has been seen at all; false before the file's first row.</summary>
    internal bool HasValue { get; private set; }

    /// <summary>Whether that row was null. A null equals a null and differs from every value.</summary>
    internal bool IsNull { get; private set; }

    /// <summary>The row's bytes, empty when it was null.</summary>
    internal ReadOnlySpan<byte> Bytes => _bytes.IsDefault ? default : _bytes.AsSpan(0, _length);

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
        if (_bytes.IsDefault || value.Length > _bytes.Length)
        {
            // Grown to the value, not doubled: the buffer settles on the column's widest value.
            Grow(value.Length);
        }

        value.CopyTo(_bytes.AsSpan());
        _length = value.Length;
        HasValue = true;
        IsNull = false;
    }

    /// <summary>Gives the block back once the file is done: the column holds no row after it.</summary>
    internal void Release()
    {
        Buffers.Blocks.Return(_bytes);
        _bytes = default;
        _length = 0;
        HasValue = false;
        IsNull = false;
    }

    /// <summary>A block of <paramref name="length"/> bytes at least, the one before given back; nothing it held is kept.</summary>
    private void Grow(int length)
    {
        Buffers.Blocks.Return(_bytes);
        _bytes = default;
        _bytes = Buffers.Blocks.Rent<byte>(Math.Max(length, Inline));
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
        if (_bytes.IsDefault || _length != 2 * sizeof(long)
            || BinaryPrimitives.ReadInt64LittleEndian(_bytes.AsSpan()) != row)
        {
            return false;
        }

        end = BinaryPrimitives.ReadInt64LittleEndian(_bytes.AsSpan(sizeof(long)));
        return true;
    }

    /// <summary>Records the row after a list range and where its named elements ended.</summary>
    internal void SetListEnd(long row, long end)
    {
        if (_bytes.IsDefault)
        {
            Grow(Inline);
        }

        BinaryPrimitives.WriteInt64LittleEndian(_bytes.AsSpan(), row);
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
