using System;
using Vorticity.Arrays;

namespace Vorticity;

/// <summary>
/// A column of lists of records in a delivered batch: each row's range among the elements, and
/// every row's elements, in row order, as the columns of <typeparamref name="TRecord"/>. Borrowed,
/// like every column.
/// </summary>
/// <typeparam name="TRecord">The record the lists hold.</typeparam>
public readonly ref struct ListColumns<TRecord>
    where TRecord : IVortexRecord<TRecord>
{
    private readonly RecordBatch _batch;
    private readonly RecordBinding _elements;

    internal ListColumns(RecordBatch batch, CanonicalArena arena, int node, RecordBinding elements)
    {
        _batch = batch;
        Arena = arena;
        Node = EncodedForms.Canonical(arena, node);
        _elements = elements;
    }

    internal CanonicalArena Arena { get; }

    /// <summary>The list node.</summary>
    internal int Node { get; }

    /// <summary>The number of rows.</summary>
    public int Length => Arena.RecordRef(Node).Length;

    /// <summary>Whether no row is null, so that <see cref="ValidityWords"/> is empty.</summary>
    public bool IsAllValid => Arena.RecordRef(Node).Validity.IsAllValid;

    /// <summary>The validity as 64-bit words, as <see cref="Column{T}.ValidityWords"/> has it; empty when <see cref="IsAllValid"/>.</summary>
    public ReadOnlySpan<ulong> ValidityWords => ArenaWords.Validity(Arena, Node);

    /// <summary>Whether row <paramref name="index"/> holds a list, rather than a null.</summary>
    /// <param name="index">A row of the batch.</param>
    /// <returns>False for a null.</returns>
    public bool IsValid(int index) => ArenaWords.IsValid(Arena, Node, index);

    /// <summary>The elements of row <paramref name="index"/>, as a range of <see cref="Elements"/>.</summary>
    /// <param name="index">A row of the batch.</param>
    public Range this[int index] => ColumnData.ListRange(Arena, Node, index);

    /// <summary>Every row's elements, contiguous, in row order, as the columns of <typeparamref name="TRecord"/>.</summary>
    public Columns<TRecord> Elements =>
        new Columns<TRecord>(_batch, Arena, ColumnData.Elements(Arena, Node), _elements, startRow: 0, selection: default, selected: 0, projected: false);
}
