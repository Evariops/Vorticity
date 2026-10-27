using System;
using System.Collections.Generic;
using Vorticity.Arrays;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.RowEncoding;
using Vorticity.Types;

namespace Vorticity.Dataset;

/// <summary>
/// The values a dataset orders rows by, worked out on a batch and row-encoded: each component read from its source (a
/// column, a field of a struct, a key of a variant column in one class), through its function, then the components
/// encoded one after the other, each ascending with nulls after every value, so that <c>memcmp</c> of two keys orders
/// them as the components do. The partition may lead them, as a tree key's does.
/// </summary>
/// <remarks>
/// The components are worked out in the batch's own arena, records over its buffers where a column is read as it is, a
/// node of the function's values where a function applies; the keys are pooled and the caller disposes them.
/// </remarks>
internal sealed class SortKey
{
    private readonly Source[] _sources;
    private readonly RowSortField[] _fields;
    private readonly DType[] _types;

    private SortKey(Source[] sources, bool descending, bool reversed = false)
    {
        _sources = sources;
        _fields = new RowSortField[sources.Length];
        Array.Fill(_fields, KeyOrderedMerge.Field(descending));

        // The reverse of the ascending order with nulls last is the descending one with nulls first, but for the first
        // value, whose nulls a downward read gives last.
        if (reversed)
        {
            for (int i = 1; i < _fields.Length; i++)
            {
                _fields[i] = RowSortField.Ascending.WithDescending(true);
            }
        }
        _types = new DType[sources.Length];
        DTypeArena arena = new DTypeArena();
        for (int i = 0; i < sources.Length; i++)
        {
            _types[i] = sources[i].Type(arena);
        }
    }

    /// <summary>How many values a key holds.</summary>
    internal int Count => _sources.Length;

    /// <summary>The type of the value at <paramref name="position"/>, which a seek's literal is encoded at.</summary>
    internal DType TypeAt(int position) => _types[position];

    /// <summary>
    /// The key of <paramref name="plan"/>: its components, the first <paramref name="components"/> of them, led by the
    /// partition when <paramref name="partition"/> asks and the layout has one.
    /// </summary>
    internal static SortKey Of(LayoutPlan plan, bool partition, int components, bool descending = false)
    {
        List<Source> sources = [];
        if (partition && plan.Partition is { } leading)
        {
            sources.Add(Source.Of(leading));
        }

        for (int i = 0; i < Math.Min(components, plan.Components.Count); i++)
        {
            sources.Add(Source.Of(plan.Components[i]));
        }

        return new SortKey([.. sources], descending);
    }

    /// <summary>
    /// A key of the columns <paramref name="paths"/> of <paramref name="schema"/>, each as it is: what a read ordered by columns
    /// merges on. With <paramref name="reversed"/>, downward as the reverse of the upward order, nulls last for the first
    /// column alone: the order a downward read of objects sorted by the columns, nulls last, gives.
    /// </summary>
    /// <exception cref="ArgumentException">A path names no column.</exception>
    internal static SortKey OfColumns(DType schema, IReadOnlyList<string> paths, bool descending, bool reversed = false)
    {
        Source[] sources = new Source[paths.Count];
        for (int i = 0; i < sources.Length; i++)
        {
            sources[i] = new Source(paths[i], LayoutColumn.Resolve(schema, paths[i]), null, null, null);
        }

        return new SortKey(sources, descending, reversed && descending);
    }

    /// <summary>The keys of every row of <paramref name="batch"/>, row-encoded; the caller disposes them.</summary>
    /// <exception cref="ArgumentException">A source names no column of the batch.</exception>
    internal RowKeys Encode(RecordBatch batch)
    {
        Span<int> nodes = _sources.Length <= 32 ? stackalloc int[_sources.Length] : new int[_sources.Length];
        Components(batch, nodes);
        return RowEncoder.Encode(batch.Arena, nodes, _fields);
    }

    /// <summary>
    /// The nodes of the batch's values of each source, in its arena: a column's own node, or a node of the function's
    /// values over it; extensions unwrapped to the storage the encoding orders.
    /// </summary>
    internal void Components(RecordBatch batch, Span<int> nodes)
    {
        CanonicalArena arena = batch.Arena;
        for (int i = 0; i < _sources.Length; i++)
        {
            Source source = _sources[i];
            int node = ColumnPath.NodeOf(batch, source.Path);
            if (node < 0)
            {
                throw new ArgumentException($"'{source.Path}' names no column of the batch's schema.", nameof(batch));
            }

            if (source.Read is { } read)
            {
                node = read.Apply(arena, node);
            }

            if (source.Function is { } function)
            {
                node = function.Apply(arena, node);
            }

            nodes[i] = RowEncoder.Storage(arena, node);
        }
    }

    /// <summary>
    /// The row encoding of a leading tuple of the key's values, for a seek or a bound: each at its value's type, nulls
    /// last as the rows' keys put them.
    /// </summary>
    /// <exception cref="ArgumentException">There are no values, or more than the key holds.</exception>
    internal byte[] Encode(ReadOnlySpan<FilterLiteral> values)
    {
        if (values.Length == 0 || values.Length > _sources.Length)
        {
            throw new ArgumentException(
                $"A key of {_sources.Length} value(s) is encoded from 1 to {_sources.Length} values, not {values.Length}.", nameof(values));
        }

        return RowEncoder.EncodeKey(values, _types.AsSpan(0, values.Length), _fields.AsSpan(0, values.Length));
    }

    /// <summary>The key of row <paramref name="row"/> of the nodes <see cref="Components"/> gave, row-encoded.</summary>
    internal byte[] EncodeRow(CanonicalArena arena, ReadOnlySpan<int> nodes, int row)
    {
        FilterLiteral[] values = new FilterLiteral[nodes.Length];
        for (int i = 0; i < nodes.Length; i++)
        {
            values[i] = LiteralReader.TryRead(arena, nodes[i], row, out FilterLiteral value) ? value : FilterLiteral.Null;
        }

        return RowEncoder.EncodeKey(values, _types, _fields);
    }

    /// <summary>
    /// The bytes the first <paramref name="components"/> values of the row-encoded key <paramref name="key"/> take; -1
    /// when one is a struct or a list whose width its bytes do not tell.
    /// </summary>
    internal int PrefixLength(ReadOnlySpan<byte> key, int components)
    {
        int at = 0;
        for (int i = 0; i < components; i++)
        {
            int length = RowEncoder.ValueLength(key[at..], _types[i]);
            if (length < 0)
            {
                return -1;
            }

            at += length;
        }

        return at;
    }

    /// <summary>
    /// Whether two row-encoded keys hold the same first <paramref name="components"/> values: their bytes up to the end of
    /// the last, which a value's encoding ends whatever follows it.
    /// </summary>
    internal bool SamePrefix(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, int components)
    {
        int length = PrefixLength(left, components);
        return length >= 0 && right.Length >= length && left[..length].SequenceEqual(right[..length]);
    }


    /// <summary>Where a value comes from and what it applies: a column's path, a variant key's read, a function.</summary>
    private sealed class Source(string path, DType column, VariantFieldExpr? read, ValueFunction? function, VortexType? readType)
    {
        internal string Path { get; } = path;

        internal VariantFieldExpr? Read { get; } = read;

        internal ValueFunction? Function { get; } = function;

        /// <summary>The type of the value: the column's, a function keeping it; the class's, nullable, for a variant key.</summary>
        internal DType Type(DTypeArena arena) => Read is null ? DTypeImport.Into(arena, column) : VortexTypes.ToDType(readType!, arena);

        internal static Source Of(LayoutColumn column) =>
            new Source(column.Value.SourcePath, column.Source, column.Read, column.Function, column.Type);
    }
}
