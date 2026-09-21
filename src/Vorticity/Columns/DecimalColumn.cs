using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Types.Numerics;

namespace Vorticity.Columns;

/// <summary>
/// A fixed-point decimal column: little-endian two's-complement unscaled integers of the width the
/// file declares, plus the dtype's precision and scale.
/// </summary>
/// <remarks>
/// Rows are read as <see cref="VortexDecimal"/> rather than <see cref="decimal"/>: the format allows
/// a precision of up to 76 digits, well past what <see cref="decimal"/> holds, so that mapping would
/// be silently lossy over a legal range of the format.
/// Every span here is borrowed from the owning <see cref="RecordBatch"/> and is invalid once that
/// batch is disposed.
/// </remarks>
internal readonly ref struct DecimalColumn
{
    private readonly RecordBatch _batch;
    private readonly int _node;

    internal DecimalColumn(RecordBatch batch, int node)
    {
        _batch = batch;
        _node = node;
    }

    /// <summary>Rows in this column.</summary>
    public int Length => _batch.Node(_node).Length;

    /// <summary>Whether row <paramref name="index"/> is not null.</summary>
    /// <param name="index">0-based row index, below <see cref="Length"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    public bool IsValid(int index) => ColumnCore.IsValid(_batch, _node, index);

    /// <summary>The dtype's precision, 1..76.</summary>
    public byte Precision => _batch.Node(_node).Precision;

    /// <summary>The dtype's scale. Negative scales are legal.</summary>
    public sbyte Scale => _batch.Node(_node).Scale;

    /// <summary>
    /// The width the values are actually stored at - the file's <c>values_type</c>, not a function
    /// of <see cref="Precision"/>.
    /// </summary>
    /// <remarks>
    /// It is never narrower than the width <see cref="DecimalStorage.ForPrecision"/> selects, but it
    /// may be wider: upstream lets a <c>DecimalArray</c> hold precision-2 values in an
    /// <c>i256</c> buffer, and every Arrow-sourced decimal column is <c>i128</c> or <c>i256</c>
    /// whatever its precision. Read this before choosing a narrowed accessor.
    /// </remarks>
    public DecimalStorageType Storage => _batch.Node(_node).Storage;

    /// <summary>
    /// The raw storage: <c>Length * DecimalStorage.ByteWidth(Storage)</c> bytes, little-endian
    /// two's complement, zero-copy. Exposed so a caller can do its own arithmetic without widening.
    /// </summary>
    public ReadOnlySpan<byte> StorageBytes
    {
        get
        {
            CanonicalNode node = _batch.Node(_node);
            return Bytes(node);
        }
    }

    /// <summary>The storage narrowed to <see cref="sbyte"/>.</summary>
    /// <exception cref="InvalidOperationException"><see cref="Storage"/> is not
    /// <see cref="DecimalStorageType.I8"/>.</exception>
    public ReadOnlySpan<sbyte> AsInt8 => Narrow<sbyte>(DecimalStorageType.I8);

    /// <summary>The storage narrowed to <see cref="short"/>.</summary>
    /// <exception cref="InvalidOperationException"><see cref="Storage"/> is not
    /// <see cref="DecimalStorageType.I16"/>.</exception>
    public ReadOnlySpan<short> AsInt16 => Narrow<short>(DecimalStorageType.I16);

    /// <summary>The storage narrowed to <see cref="int"/>.</summary>
    /// <exception cref="InvalidOperationException"><see cref="Storage"/> is not
    /// <see cref="DecimalStorageType.I32"/>.</exception>
    public ReadOnlySpan<int> AsInt32 => Narrow<int>(DecimalStorageType.I32);

    /// <summary>The storage narrowed to <see cref="long"/>.</summary>
    /// <exception cref="InvalidOperationException"><see cref="Storage"/> is not
    /// <see cref="DecimalStorageType.I64"/>.</exception>
    public ReadOnlySpan<long> AsInt64 => Narrow<long>(DecimalStorageType.I64);

    /// <summary>The storage narrowed to <see cref="Int128"/>.</summary>
    /// <exception cref="InvalidOperationException"><see cref="Storage"/> is not
    /// <see cref="DecimalStorageType.I128"/>.</exception>
    public ReadOnlySpan<Int128> AsInt128 => Narrow<Int128>(DecimalStorageType.I128);

    /// <summary>
    /// Row <paramref name="index"/> as a <see cref="VortexDecimal"/>: the unscaled integer widened
    /// to <see cref="Int256"/>, carrying this column's precision and scale. Never lossy.
    /// </summary>
    /// <param name="index">0-based row index, below <see cref="Length"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    /// <exception cref="VortexFormatException">
    /// The storage width is not one of the six defined ones, or the buffer is short.
    /// </exception>
    public VortexDecimal this[int index]
    {
        get
        {
            CanonicalNode node = _batch.Node(_node);
            ColumnCore.CheckRow(index, node.Length);

            DecimalStorageType storage = node.Storage;
            int width = DecimalStorage.ByteWidth(storage);
            ReadOnlySpan<byte> bytes = Bytes(node);
            ReadOnlySpan<byte> slot = bytes.Slice(index * width, width);

            Int256 unscaled = storage switch
            {
                DecimalStorageType.I8 => new Int256((sbyte)slot[0]),
                DecimalStorageType.I16 => new Int256(BinaryPrimitives.ReadInt16LittleEndian(slot)),
                DecimalStorageType.I32 => new Int256(BinaryPrimitives.ReadInt32LittleEndian(slot)),
                DecimalStorageType.I64 => new Int256(BinaryPrimitives.ReadInt64LittleEndian(slot)),
                DecimalStorageType.I128 => new Int256(BinaryPrimitives.ReadInt128LittleEndian(slot)),
                _ => Int256.FromLittleEndianBytes(slot),
            };

            return new VortexDecimal(unscaled, node.Precision, node.Scale);
        }
    }

    private static ReadOnlySpan<byte> Bytes(CanonicalNode node)
    {
        int width = DecimalStorage.ByteWidth(node.Storage);
        long needed = (long)node.Length * width;
        ReadOnlySpan<byte> all = node.Values.Span;
        if (needed > all.Length)
        {
            ColumnsThrow.Format(
                $"A decimal column of {node.Length} rows of {width} bytes needs {needed} bytes; " +
                $"the buffer holds {all.Length}.");
        }

        return all[..(int)needed];
    }

    private ReadOnlySpan<T> Narrow<T>(DecimalStorageType required)
        where T : unmanaged
    {
        CanonicalNode node = _batch.Node(_node);
        DecimalStorageType actual = node.Storage;
        if (actual != required)
        {
            // A caller error, not a file error: the column is well-formed, the accessor is wrong.
            ColumnsThrow.WrongKind($"a decimal stored as {actual}", $"storage {required}");
        }

        // MemoryMarshal.Cast, not VortexBuffer.Cast: a file-supplied buffer carries no alignment
        // guarantee, and demanding one would reject well-formed files.
        return MemoryMarshal.Cast<byte, T>(Bytes(node));
    }
}
