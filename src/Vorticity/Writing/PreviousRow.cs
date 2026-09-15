// The one row a column has to remember between batches - docs/11-write-strategy.md §3.0, "the
// previous value for run and delta continuity", and §3.1, "copied, for strings, since the previous
// batch's arena will be recycled".
//
// A RUN IS NOT A PROPERTY OF A BATCH. Whether row 8 192 starts a new run is a question about row
// 8 191, which arrived in another batch, on another arena, that the scan has since reset. Answering
// it needs the value itself, owned here, and not a reference to where it was.
//
// One value, whatever the column's width or length: at most 32 bytes for a decimal, a growable
// buffer for a string. The buffer grows to the longest value the column has held and is then reused
// for the life of the file, so a steady-state batch allocates nothing.
using System;

namespace Vorticity.Writing;

/// <summary>The last row a column saw, owned rather than borrowed.</summary>
internal sealed class PreviousRow
{
    /// <summary>Fixed-width values up to a 256-bit decimal fit without ever growing.</summary>
    private const int Inline = 32;

    /// <summary>
    /// Allocated on the first value, not in the field initializer.
    /// </summary>
    /// <remarks>
    /// The writer keeps one <see cref="ColumnWriter"/> per node of the schema tree, and the interior
    /// nodes -- a struct, an extension, a variant -- never hold a value: their kind has no row
    /// equality, so nothing ever calls <see cref="Set"/>. Allocating eagerly charged every one of
    /// them 32 bytes and an object header for a buffer they would never write into.
    /// </remarks>
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

    /// <summary>Records a valid row by copying its bytes.</summary>
    /// <param name="value">The row's value; copied, never retained.</param>
    internal void Set(ReadOnlySpan<byte> value)
    {
        if (_bytes is null || value.Length > _bytes.Length)
        {
            // Grown to the value, not doubled: a column's widest value is usually its first long
            // one, and a growth here is paid once for the file. The first allocation takes the
            // inline size so a fixed-width column never grows at all.
            _bytes = new byte[Math.Max(value.Length, Inline)];
        }

        value.CopyTo(_bytes);
        _length = value.Length;
        HasValue = true;
        IsNull = false;
    }

    /// <summary>Whether a row with these bytes would continue the run this one started.</summary>
    /// <param name="valid">Whether the candidate row holds a value.</param>
    /// <param name="value">Its bytes, ignored when <paramref name="valid"/> is false.</param>
    /// <returns>
    /// <see langword="true"/> when the two rows are equal under the compressor's rule: two nulls are
    /// equal, a null and a value are not, and two values are equal byte for byte.
    /// </returns>
    internal bool Equals(bool valid, ReadOnlySpan<byte> value)
    {
        if (IsNull || !valid)
        {
            return IsNull && !valid;
        }

        return Bytes.SequenceEqual(value);
    }
}
