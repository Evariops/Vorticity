// Phase 1 contract §12.2. Both vortex.list and vortex.listview decode to the same canonical
// ListView (contract §8.4), which is offsets + sizes over one flattened elements child - the same
// choice upstream makes (Canonical::List is a ListViewArray).
//
// Offsets and sizes are separate buffers with their own physical types, so a row is
// Elements[offset .. offset + size) and rows need not be contiguous or ordered.
using System;
using Vorticity.Arrays;
using Vorticity.Types;

namespace Vorticity.Columns;

/// <summary>A variable-length list column.</summary>
/// <remarks>
/// <see cref="Elements"/> borrows from the owning <see cref="RecordBatch"/> and is invalid once
/// that batch is disposed (docs/07-dotnet-mapping.md §4).
/// </remarks>
public readonly ref struct ListColumn
{
    private readonly RecordBatch _batch;
    private readonly int _node;

    internal ListColumn(RecordBatch batch, int node)
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
    /// Where row <paramref name="index"/> starts in <see cref="Elements"/>. <b>0 for a null row</b>:
    /// a null row's offset and size are unspecified on the wire, so they are not surfaced.
    /// </summary>
    /// <param name="index">0-based row index, below <see cref="Length"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    /// <exception cref="VortexFormatException">The stored offset does not fit a
    /// <see cref="long"/>.</exception>
    public long GetOffset(int index)
    {
        CanonicalNode node = _batch.Node(_node);
        ColumnCore.CheckRow(index, node.Length);
        if (!ColumnCore.IsValid(_batch, _node, index))
        {
            return 0;
        }

        return ColumnCore.ReadInteger(node.Offsets.Span, node.OffsetPType, index, "A list offset");
    }

    /// <summary>
    /// How many elements row <paramref name="index"/> covers. <b>0 for a null row.</b>
    /// </summary>
    /// <param name="index">0-based row index, below <see cref="Length"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    /// <exception cref="VortexFormatException">The stored size does not fit an
    /// <see cref="int"/>.</exception>
    public int GetLength(int index)
    {
        CanonicalNode node = _batch.Node(_node);
        ColumnCore.CheckRow(index, node.Length);
        if (!ColumnCore.IsValid(_batch, _node, index))
        {
            return 0;
        }

        long size = ColumnCore.ReadInteger(node.Sizes.Span, node.SizePType, index, "A list size");
        if (size < 0 || size > int.MaxValue)
        {
            ColumnsThrow.Format($"List row {index} declares {size} elements.");
        }

        return (int)size;
    }

    /// <summary>The physical type of the offsets buffer.</summary>
    public PType OffsetPType => _batch.Node(_node).OffsetPType;

    /// <summary>The physical type of the sizes buffer.</summary>
    public PType SizePType => _batch.Node(_node).SizePType;

    /// <summary>
    /// The flattened elements of every row. Row <c>i</c> covers
    /// <c>[GetOffset(i), GetOffset(i) + GetLength(i))</c>.
    /// </summary>
    public VortexColumn Elements => new VortexColumn(_batch, _batch.Node(_node).ElementsIndex);
}
