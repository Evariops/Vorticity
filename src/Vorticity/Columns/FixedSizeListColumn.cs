using System;
using Vorticity.Arrays;

namespace Vorticity.Columns;

/// <summary>A fixed-length list column: every row holds exactly <see cref="Size"/> elements.</summary>
/// <remarks>
/// Rows are a constant stride over one flattened child; there is no offsets buffer. A size of zero
/// is legal, so nothing here divides by it.
/// <see cref="Elements"/> borrows from the owning <see cref="RecordBatch"/> and is invalid once
/// that batch is disposed.
/// </remarks>
public readonly ref struct FixedSizeListColumn
{
    private readonly RecordBatch _batch;
    private readonly int _node;

    internal FixedSizeListColumn(RecordBatch batch, int node)
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

    /// <summary>Elements per row. Zero is legal.</summary>
    public uint Size => _batch.Node(_node).FixedSize;

    /// <summary>
    /// The flattened elements. Row <c>i</c> covers
    /// <c>[i * Size, (i + 1) * Size)</c>.
    /// </summary>
    public VortexColumn Elements => new VortexColumn(_batch, _batch.Node(_node).ElementsIndex);

    /// <summary>
    /// The element range of row <paramref name="index"/>, as a start and a count, with the
    /// multiplication checked.
    /// </summary>
    /// <param name="index">0-based row index, below <see cref="Length"/>.</param>
    /// <param name="start">Index of the row's first element in <see cref="Elements"/>.</param>
    /// <param name="count">Elements in the row; equals <see cref="Size"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    /// <exception cref="VortexFormatException">
    /// <c>index * Size</c> overflows an <see cref="int"/>, which a decoder should already have
    /// refused.
    /// </exception>
    public void GetRange(int index, out int start, out int count)
    {
        CanonicalNode node = _batch.Node(_node);
        ColumnCore.CheckRow(index, node.Length);

        long size = node.FixedSize;
        long first = index * size;
        if (first + size > int.MaxValue)
        {
            ColumnsThrow.Format(
                $"Row {index} of a fixed-size list of {size} elements starts past int.MaxValue.");
        }

        start = (int)first;
        count = (int)size;
    }
}
