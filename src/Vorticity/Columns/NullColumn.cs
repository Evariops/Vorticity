namespace Vorticity.Columns;

/// <summary>A column of the <c>Null</c> dtype: every row is null and there are no buffers.</summary>
/// <remarks>Valid only until the owning <see cref="RecordBatch"/> is disposed.</remarks>
public readonly ref struct NullColumn
{
    private readonly RecordBatch _batch;
    private readonly int _node;

    internal NullColumn(RecordBatch batch, int node)
    {
        _batch = batch;
        _node = node;
    }

    /// <summary>Rows in this column. Every one of them is null.</summary>
    public int Length => _batch.Node(_node).Length;

    /// <summary>Always <see langword="false"/>; present so the shape matches every other view.</summary>
    /// <param name="index">0-based row index, below <see cref="Length"/>.</param>
    /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    public bool IsValid(int index) => ColumnCore.IsValid(_batch, _node, index);
}
