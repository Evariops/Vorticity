using System;
using System.Buffers.Binary;
using System.Text;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Columns;

/// <summary>A variable-length byte column: <c>Utf8</c> or <c>Binary</c>.</summary>
/// <remarks>
/// Values are stored as 16-byte views: a size, then either the bytes inline when they fit in twelve
/// bytes or a data buffer index and an offset into it. A null row's view holds arbitrary bytes, so
/// every accessor tests validity before it dereferences a view and re-checks the view's bounds, so
/// that a malformed file cannot become an out-of-bounds read.
/// Spans returned here point into the owning <see cref="RecordBatch"/>'s buffers and are invalid
/// once that batch is disposed. <see cref="GetString"/> is the one accessor that copies.
/// </remarks>
public readonly ref struct BinaryColumn
{
    private const int ViewSize = 16;
    private const int MaxInlineLength = 12;

    private readonly RecordBatch _batch;
    private readonly int _node;

    internal BinaryColumn(RecordBatch batch, int node)
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

    /// <summary>
    /// <see langword="true"/> when the dtype is <c>Utf8</c>; the decoder has already verified every
    /// non-null value is valid UTF-8. <see langword="false"/> for <c>Binary</c>, whose bytes are
    /// arbitrary.
    /// </summary>
    public bool IsUtf8 => _batch.Node(_node).DType.Kind == DTypeKind.Utf8;

    /// <summary>
    /// The bytes of row <paramref name="index"/>, zero-copy. <b>Empty for a null row</b> - a null
    /// row's view is never dereferenced. Invalid after the owning batch is disposed.
    /// </summary>
    /// <param name="index">0-based row index, below <see cref="Length"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    /// <exception cref="VortexFormatException">The view escapes its buffer.</exception>
    public ReadOnlySpan<byte> GetSpan(int index)
    {
        CanonicalNode node = _batch.Node(_node);
        ColumnCore.CheckRow(index, node.Length);
        if (!ColumnCore.IsValid(_batch, _node, index))
        {
            return default;
        }

        ReadOnlySpan<byte> view = View(node, index);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(view);
        if (size <= MaxInlineLength)
        {
            return view.Slice(4, (int)size);
        }

        uint bufferIndex = BinaryPrimitives.ReadUInt32LittleEndian(view[8..12]);
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(view[12..16]);
        if (bufferIndex > int.MaxValue)
        {
            ColumnsThrow.Format($"Row {index} names data buffer {bufferIndex}.");
        }

        // GetDataBuffer bounds-checks the index and throws VortexFormatException.
        VortexBuffer data = node.GetDataBuffer((int)bufferIndex);
        if ((ulong)offset + size > (ulong)(uint)data.Length)
        {
            ColumnsThrow.Format(
                $"Row {index} spans [{offset}, {(ulong)offset + size}) of a data buffer holding " +
                $"{data.Length} bytes.");
        }

        return data.Span.Slice((int)offset, (int)size);
    }

    /// <summary>
    /// The byte length of row <paramref name="index"/>; 0 for a null row.
    /// </summary>
    /// <param name="index">0-based row index, below <see cref="Length"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    /// <exception cref="VortexFormatException">The recorded size does not fit an <see cref="int"/>.</exception>
    public int GetLength(int index)
    {
        CanonicalNode node = _batch.Node(_node);
        ColumnCore.CheckRow(index, node.Length);
        if (!ColumnCore.IsValid(_batch, _node, index))
        {
            return 0;
        }

        uint size = BinaryPrimitives.ReadUInt32LittleEndian(View(node, index));
        if (size > int.MaxValue)
        {
            ColumnsThrow.Format($"Row {index} declares a length of {size} bytes.");
        }

        return (int)size;
    }

    /// <summary>
    /// Row <paramref name="index"/> as a <see cref="string"/>, or <see langword="null"/> for a null
    /// row. This is the one accessor here that allocates, and it exists so a caller can keep a
    /// value past <see cref="RecordBatch.Dispose"/>.
    /// </summary>
    /// <param name="index">0-based row index, below <see cref="Length"/>.</param>
    /// <returns>The decoded string, or <see langword="null"/> when the row is null.</returns>
    /// <remarks>
    /// The bytes are decoded as UTF-8 whether the dtype is <c>Utf8</c> or <c>Binary</c>. For
    /// <c>Binary</c> that is a lossy interpretation - invalid sequences become U+FFFD - so read
    /// binary values with <see cref="GetSpan"/> instead.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    public string? GetString(int index)
    {
        CanonicalNode node = _batch.Node(_node);
        ColumnCore.CheckRow(index, node.Length);
        if (!ColumnCore.IsValid(_batch, _node, index))
        {
            return null;
        }

        return Encoding.UTF8.GetString(GetSpan(index));
    }

    private static ReadOnlySpan<byte> View(CanonicalNode node, int index)
    {
        ReadOnlySpan<byte> views = node.Views.Span;
        long start = (long)index * ViewSize;
        if (start + ViewSize > views.Length)
        {
            ColumnsThrow.Format(
                $"A varbinview column of {node.Length} rows needs {(long)node.Length * ViewSize} " +
                $"bytes of views; the buffer holds {views.Length}.");
        }

        return views.Slice((int)start, ViewSize);
    }
}
