using System;
using Vorticity.Arrays;

namespace Vorticity;

/// <summary>
/// One delivered batch seen by index and name, for a caller that has no record type: vxdump, an ad
/// hoc query, a test. Borrowed, like every column.
/// </summary>
public readonly ref struct BatchView
{
    private readonly ReadOnlySpan<ulong> _selection;
    private readonly ReadOnlySpan<int> _columns;
    private readonly int _selected;

    internal BatchView(RecordBatch? batch, CanonicalArena arena, int node, VortexSchema schema, long startRow, ReadOnlySpan<ulong> selection, int selected)
    {
        Batch = batch;
        Arena = arena;
        Node = node;
        Schema = schema;
        StartRow = startRow;
        _selection = selection;
        _selected = selected;
    }

    /// <summary>A view over loose columns of one length, one node per field of <paramref name="schema"/>, with no struct above them.</summary>
    internal BatchView(CanonicalArena arena, ReadOnlySpan<int> columns, VortexSchema schema)
    {
        Arena = arena;
        Node = columns[0];
        Schema = schema;
        _columns = columns;
    }

    internal RecordBatch? Batch { get; }

    internal CanonicalArena Arena { get; }

    internal int Node { get; }

    /// <summary>The batch's columns.</summary>
    public VortexSchema Schema { get; }

    /// <summary>The number of rows.</summary>
    public int RowCount => Arena.RecordRef(Node).Length;

    /// <summary>
    /// The file row of the block's first row: row <c>i</c> is file row <c>StartRow + i</c> unless a
    /// filter compacted the batch, which keeps the kept rows only; with
    /// <c>ScanOptions.Compact = false</c> the positions hold and <see cref="Selection"/> says which passed.
    /// </summary>
    public long StartRow { get; }

    /// <summary>Which rows passed the filter.</summary>
    public Selection Selection => _selection.IsEmpty ? new Selection(RowCount) : new Selection(_selection, RowCount, _selected);

    /// <summary>The rows the filter kept as words, or empty when every row is selected.</summary>
    internal ReadOnlySpan<ulong> SelectionWords => _selection;

    internal VortexExtensionRegistry? Extensions => Batch?.Session?.Options.Extensions;

    /// <summary>Column <paramref name="index"/> of the batch.</summary>
    /// <typeparam name="T">A .NET type the column's dtype maps to.</typeparam>
    /// <param name="index">The column's position in <see cref="Schema"/>.</param>
    /// <returns>The column.</returns>
    /// <exception cref="VortexSchemaException"><typeparamref name="T"/> does not fit the column.</exception>
    public Column<T> Column<T>(int index)
    {
        if ((uint)index >= (uint)Schema.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"The batch has {Schema.Count} columns.");
        }

        VortexField field = Schema[index];
        ClrFit.Require<T>(field.Type, $"Column '{field.Name}'", Extensions);
        if (field.Type.IsNullable && typeof(T).IsValueType && Nullable.GetUnderlyingType(typeof(T)) is null && ClrShape.For<T>.Value.Kind is not (ClrKind.List or ClrKind.Unsupported))
        {
            // A list, and a registered extension, have their accessors on the non-nullable form only.
            throw new VortexSchemaException(
                $"Column '{field.Name}' is {field.Type}, which is nullable; read it as Column<{ClrFit.Name(typeof(T))}?>, whose accessors say which rows are null.");
        }

        return new Column<T>(Arena, ColumnNode(index), field.Type, Extensions);
    }

    /// <summary>The arena node of column <paramref name="index"/>, extension or not.</summary>
    internal int ColumnNode(int index) =>
        !_columns.IsEmpty ? _columns[index]
        : Schema.RootIsStruct ? Arena.GetNode(StructNode()).GetFieldIndex(index)
        : Node;

    /// <summary>The column named <paramref name="name"/>.</summary>
    /// <typeparam name="T">A .NET type the column's dtype maps to.</typeparam>
    /// <param name="name">The column's name, exactly.</param>
    /// <returns>The column.</returns>
    /// <exception cref="VortexSchemaException">No column has that name, or <typeparamref name="T"/> does not fit it.</exception>
    public Column<T> Column<T>(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        for (int i = 0; i < Schema.Count; i++)
        {
            if (Schema[i].Name == name)
            {
                return Column<T>(i);
            }
        }

        throw new VortexSchemaException($"The batch has no column '{name}'; its schema is {Schema}.");
    }

    /// <summary>Copies the batch once into buffers the caller owns.</summary>
    /// <returns>The owned batch; the caller disposes it.</returns>
    /// <exception cref="InvalidOperationException">The view is the key columns a writer lends a key encoder, which are not a batch.</exception>
    public RecordBatch ToOwned() => _columns.IsEmpty
        ? RecordBatch.Own(Arena, Node, StartRow, Schema, Batch?.Session, _selection, _selected)
        : throw new InvalidOperationException("The key columns a writer lends an encoder are not a batch; encode them and keep the keys instead.");

    private int StructNode()
    {
        int node = Node;
        while (Arena.RecordRef(node).Kind == CanonicalKind.Extension)
        {
            node = Arena.GetNode(node).StorageIndex;
        }

        return node;
    }
}
