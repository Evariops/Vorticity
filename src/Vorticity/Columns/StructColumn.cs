using System;
using Vorticity.Arrays;
using Vorticity.Types;

namespace Vorticity.Columns;

/// <summary>A struct column: named fields, each its own column.</summary>
/// <remarks>
/// The field views borrow from the owning <see cref="RecordBatch"/> and are invalid once that batch
/// is disposed. Field names come from the dtype rather than from the decoded array, and a name may
/// be empty or contain a '.', so lookup here is exact and never splits a path; a field that cannot
/// be addressed by name is reached through <see cref="GetField(int)"/>.
/// </remarks>
public readonly ref struct StructColumn
{
    private readonly RecordBatch _batch;
    private readonly int _node;

    internal StructColumn(RecordBatch batch, int node)
    {
        _batch = batch;
        _node = node;
    }

    /// <summary>Rows in this column.</summary>
    public int Length => _batch.Node(_node).Length;

    /// <summary>
    /// Whether row <paramref name="index"/> of the <em>struct itself</em> is not null. A struct row
    /// may be null while its fields' own slots hold values; a field's own
    /// <see cref="VortexColumn.IsValid"/> answers only for that field.
    /// </summary>
    /// <param name="index">0-based row index, below <see cref="Length"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    public bool IsValid(int index) => ColumnCore.IsValid(_batch, _node, index);

    /// <summary>Number of fields.</summary>
    public int FieldCount => _batch.Node(_node).FieldCount;

    /// <summary>Field <paramref name="index"/>'s name. <b>Allocates a string</b>; prefer
    /// <see cref="GetFieldNameUtf8"/>.</summary>
    /// <param name="index">0-based field index, below <see cref="FieldCount"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    public string GetFieldName(int index)
    {
        CanonicalNode node = _batch.Node(_node);
        CheckField(index, node.FieldCount);
        return node.DType.GetFieldName(index);
    }

    /// <summary>Field <paramref name="index"/>'s name as UTF-8 bytes, zero-copy.</summary>
    /// <param name="index">0-based field index, below <see cref="FieldCount"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    public ReadOnlySpan<byte> GetFieldNameUtf8(int index)
    {
        CanonicalNode node = _batch.Node(_node);
        CheckField(index, node.FieldCount);
        return node.DType.GetFieldNameUtf8(index);
    }

    /// <summary>Resolves a field by its exact UTF-8 name.</summary>
    /// <param name="nameUtf8">The field name. Exact match; no path splitting, no escaping.</param>
    /// <param name="index">The 0-based field index when found.</param>
    /// <returns><see langword="true"/> when a field carries that exact name.</returns>
    public bool TryGetFieldIndex(ReadOnlySpan<byte> nameUtf8, out int index)
    {
        CanonicalNode node = _batch.Node(_node);
        DType dtype = node.DType;
        if (dtype.IsDefault || dtype.Kind != DTypeKind.Struct)
        {
            index = -1;
            return false;
        }

        int found = dtype.IndexOfField(nameUtf8);

        // A dtype with more fields than the decoded node would let a name resolve to a child that
        // does not exist; the batch constructor rejects that at the root, and this rejects it here.
        if (found >= node.FieldCount)
        {
            found = -1;
        }

        index = found;
        return found >= 0;
    }

    /// <summary>Field <paramref name="index"/> as a column.</summary>
    /// <param name="index">0-based field index, below <see cref="FieldCount"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    public VortexColumn GetField(int index)
    {
        CanonicalNode node = _batch.Node(_node);
        CheckField(index, node.FieldCount);
        return new VortexColumn(_batch, node.GetFieldIndex(index));
    }

    /// <summary>The field named <paramref name="nameUtf8"/>, as a column.</summary>
    /// <param name="nameUtf8">The exact field name, UTF-8.</param>
    /// <exception cref="ArgumentException">No field carries that name.</exception>
    public VortexColumn GetField(ReadOnlySpan<byte> nameUtf8)
    {
        if (!TryGetFieldIndex(nameUtf8, out int index))
        {
            ColumnsThrow.UnknownField(nameUtf8);
        }

        return GetField(index);
    }

    private static void CheckField(int index, int count)
    {
        if ((uint)index >= (uint)count)
        {
            ColumnsThrow.FieldIndex(index, count);
        }
    }
}
