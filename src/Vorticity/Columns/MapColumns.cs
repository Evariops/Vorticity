using System;
using Vorticity.Arrays;

namespace Vorticity;

/// <summary>
/// A map column in a delivered batch: each row's range among the entries, and every row's keys and
/// values, in row order, as two columns. Borrowed, like every column.
/// </summary>
/// <typeparam name="TKey">The keys' .NET type.</typeparam>
/// <typeparam name="TValue">The values' .NET type.</typeparam>
public readonly ref struct MapColumns<TKey, TValue>
{
    private readonly VortexType _type;
    private readonly VortexExtensionRegistry? _extensions;

    internal MapColumns(CanonicalArena arena, int node, VortexType type, VortexExtensionRegistry? extensions)
    {
        Arena = arena;
        Node = EncodedForms.Canonical(arena, node);
        _type = type;
        _extensions = extensions;
        ClrFit.Require<TKey>(type.Fields[0].Type, "The map's keys", extensions);
        ClrFit.Require<TValue>(type.Fields[1].Type, "The map's values", extensions);
    }

    internal CanonicalArena Arena { get; }

    /// <summary>The map node: a list view of entries.</summary>
    internal int Node { get; }

    /// <summary>The number of rows.</summary>
    public int Length => Arena.RecordRef(Node).Length;

    /// <summary>Whether no row is null, so that <see cref="ValidityWords"/> is empty.</summary>
    public bool IsAllValid => Arena.RecordRef(Node).Validity.IsAllValid;

    /// <summary>The validity as 64-bit words, as <see cref="Column{T}.ValidityWords"/> has it; empty when <see cref="IsAllValid"/>.</summary>
    public ReadOnlySpan<ulong> ValidityWords => ArenaWords.Validity(Arena, Node);

    /// <summary>Whether row <paramref name="index"/> holds a map, rather than a null.</summary>
    /// <param name="index">A row of the batch.</param>
    /// <returns>False for a null.</returns>
    public bool IsValid(int index) => ArenaWords.IsValid(Arena, Node, index);

    /// <summary>The entries of row <paramref name="index"/>, as a range of <see cref="Keys"/> and <see cref="Values"/>.</summary>
    /// <param name="index">A row of the batch.</param>
    public Range this[int index] => ColumnData.ListRange(Arena, Node, index);

    /// <summary>Every row's keys, contiguous, in row order.</summary>
    public Column<TKey> Keys => new Column<TKey>(Arena, Entry(0), _type.Fields[0].Type, _extensions);

    /// <summary>Every row's values, contiguous, in row order, beside their keys.</summary>
    public Column<TValue> Values => new Column<TValue>(Arena, Entry(1), _type.Fields[1].Type, _extensions);

    private int Entry(int field) => Arena.GetNode(ColumnData.Elements(Arena, Node)).GetFieldIndex(field);
}
