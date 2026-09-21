using System;
using Vorticity.Arrays;

namespace Vorticity.Columns;

/// <summary>A boolean column: a bit-packed, LSB-first bitmap plus a starting bit offset.</summary>
/// <remarks>
/// The bitmap is never shifted to put row 0 on a byte boundary, because that would cost an
/// allocation and a copy per batch; consumers apply <see cref="BitOffset"/> instead.
/// <see cref="Bits"/> is borrowed from the owning <see cref="RecordBatch"/> and is invalid once
/// that batch is disposed.
/// </remarks>
internal readonly ref struct BoolColumn
{
    private readonly RecordBatch _batch;
    private readonly int _node;

    internal BoolColumn(RecordBatch batch, int node)
    {
        _batch = batch;
        _node = node;
    }

    /// <summary>Rows in this column.</summary>
    public int Length => _batch.Node(_node).Length;

    /// <summary>Whether row <paramref name="index"/> is not null. Independent of its bit.</summary>
    /// <param name="index">0-based row index, below <see cref="Length"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    public bool IsValid(int index) => ColumnCore.IsValid(_batch, _node, index);

    /// <summary>
    /// The raw bitmap, LSB-first, with row 0 at bit <see cref="BitOffset"/>. Zero-copy, and
    /// invalid after the owning batch is disposed.
    /// </summary>
    public ReadOnlySpan<byte> Bits => _batch.Node(_node).Bits.Span;

    /// <summary>Bit position of row 0 inside the first byte of <see cref="Bits"/>; 0..7.</summary>
    public int BitOffset => _batch.Node(_node).BitOffset;

    /// <summary>
    /// The value of row <paramref name="index"/>, with <see cref="BitOffset"/> applied. A null
    /// row's bit is whatever the writer left there; check <see cref="IsValid"/> first.
    /// </summary>
    /// <param name="index">0-based row index, below <see cref="Length"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    public bool this[int index]
    {
        get
        {
            CanonicalNode node = _batch.Node(_node);
            ColumnCore.CheckRow(index, node.Length);
            return RecordBatch.GetBit(node.Bits.Span, node.BitOffset + index);
        }
    }
}
