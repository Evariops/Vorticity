using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.InteropServices;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Parquet.Metadata;
using Vorticity.Parquet.Reading;
using Vorticity.Types;

namespace Vorticity.Parquet.Writing;

/// <summary>
/// The rows a writer is given held to the order its sorting columns declare: each batch's rows in
/// order among themselves, and its first after the last batch's last, so that no file says of its
/// rows what they do not bear out.
/// </summary>
/// <remarks>
/// A first key of integers with no null, the common declaration, is walked a register at a time by
/// the core's pair kernel, its ties handed to the keys after it; any other batch's rows are ranked a
/// pair at a time by a chain of the core's column orders, typed on each column's values. The last
/// row's values are kept as literals, which the next batch's first is ranked against. A null comes
/// first or last as its column declares.
/// </remarks>
internal sealed class SortedRows
{
    /// <summary>The rows the pair walk decides before it hands back to the lanes.</summary>
    private const int Window = 64;

    private readonly Key[] _keys;
    private readonly FilterLiteral[] _last;
    private readonly bool[] _lastNull;

    /// <summary>The batch's node of each key, its storage under an extension.</summary>
    private readonly int[] _nodes;

    /// <summary>The batch's order of each key, made when a pair first needs it.</summary>
    private readonly ColumnOrder?[] _orders;
    private bool _any;

    private SortedRows(Key[] keys)
    {
        _keys = keys;
        _last = new FilterLiteral[keys.Length];
        _lastNull = new bool[keys.Length];
        _nodes = new int[keys.Length];
        _orders = new ColumnOrder?[keys.Length];
    }

    /// <summary>The leaves' ordinals and orders the file's row groups declare.</summary>
    internal SortingColumn[] Declared
    {
        get
        {
            SortingColumn[] declared = new SortingColumn[_keys.Length];
            for (int k = 0; k < _keys.Length; k++)
            {
                declared[k] = new SortingColumn(_keys[k].Leaf, _keys[k].Descending, _keys[k].NullsFirst);
            }

            return declared;
        }
    }

    /// <summary>
    /// The check of <paramref name="sorting"/> over the columns of <paramref name="map"/>; null when it
    /// declares none.
    /// </summary>
    /// <exception cref="ArgumentException">A sorting column is not a flat column of the file, or of a type this writer orders.</exception>
    internal static SortedRows? For(IReadOnlyList<ParquetSortingColumn>? sorting, WriteSchema map, VortexSchema schema)
    {
        if (sorting is not { Count: > 0 })
        {
            return null;
        }

        Key[] keys = new Key[sorting.Count];
        for (int k = 0; k < keys.Length; k++)
        {
            ParquetSortingColumn declared = sorting[k];
            int leaf = Array.FindIndex(map.Columns, c => !c.Nested && string.Equals(c.Name, declared.Column, StringComparison.Ordinal));
            if (leaf < 0)
            {
                throw new ArgumentException($"The sorting column '{declared.Column}' is not a flat column of the file.", nameof(sorting));
            }

            WriteColumn column = map.Columns[leaf];
            VortexType type = schema[column.Field].Type;
            VortexType values = type.Kind == VortexTypeKind.Extension ? type.StorageType! : type;
            bool ordered = values.Kind is VortexTypeKind.Bool or VortexTypeKind.Decimal or VortexTypeKind.Utf8 or VortexTypeKind.Binary
                || (values.Kind == VortexTypeKind.Primitive && values.PrimitiveType.IsInteger());
            if (!ordered)
            {
                throw new ArgumentException(
                    $"The sorting column '{declared.Column}' is {type}; a file declares its rows sorted on integers, decimals, booleans, text, bytes or times, whose order has no NaN to place.",
                    nameof(sorting));
            }

            keys[k] = new Key(column.Field, leaf, type, declared.Descending, declared.NullsFirst, values.Kind == VortexTypeKind.Decimal);
        }

        return new SortedRows(keys);
    }

    /// <summary>Holds the <paramref name="rows"/> rows of <paramref name="root"/>, a struct of the file's columns, to the declared order.</summary>
    /// <exception cref="ArgumentException">A row comes before the one before it.</exception>
    internal void Check(CanonicalArena arena, int root, int rows)
    {
        if (rows == 0)
        {
            return;
        }

        try
        {
            CanonicalNode struct_ = arena.GetNode(root);
            for (int k = 0; k < _keys.Length; k++)
            {
                // A time, or any extension, is held to the order of its storage, nulls and all.
                int node = EncodedForms.Canonical(arena, struct_.GetFieldIndex(_keys[k].Field));
                while (arena.RecordRef(node).Kind == CanonicalKind.Extension)
                {
                    node = EncodedForms.Canonical(arena, arena.GetNode(node).StorageIndex);
                }

                _nodes[k] = node;
            }

            if (_any && Across(arena) > 0)
            {
                Refuse(0);
            }

            if (!Lanes(arena, rows))
            {
                for (int row = 1; row < rows; row++)
                {
                    if (Within(arena, 0, row - 1, row) > 0)
                    {
                        Refuse(row);
                    }
                }
            }

            for (int k = 0; k < _keys.Length; k++)
            {
                _lastNull[k] = !Literal(arena, _nodes[k], rows - 1, _keys[k].Decimals, out _last[k]);
            }

            _any = true;
        }
        finally
        {
            for (int k = 0; k < _orders.Length; k++)
            {
                _orders[k]?.Release();
                _orders[k] = null;
            }
        }
    }

    /// <summary>
    /// Holds the batch to its first key a register at a time where the key's values are integers
    /// with no null, its ties to the keys after it; false where they are not, for the pair walk.
    /// </summary>
    private bool Lanes(CanonicalArena arena, int rows)
    {
        int node = _nodes[0];
        ref readonly CanonicalRecord record = ref arena.RecordRef(node);
        if (record.Kind != CanonicalKind.Primitive || !record.PType.IsInteger() || ArenaWords.NullCount(arena, node) != 0)
        {
            return false;
        }

        ReadOnlySpan<byte> bytes = record.BufferA.Span;
        switch (record.PType)
        {
            case PType.I8:
                Walk(arena, MemoryMarshal.Cast<byte, sbyte>(bytes)[..rows]);
                break;
            case PType.I16:
                Walk(arena, MemoryMarshal.Cast<byte, short>(bytes)[..rows]);
                break;
            case PType.I32:
                Walk(arena, MemoryMarshal.Cast<byte, int>(bytes)[..rows]);
                break;
            case PType.I64:
                Walk(arena, MemoryMarshal.Cast<byte, long>(bytes)[..rows]);
                break;
            case PType.U8:
                Walk(arena, bytes[..rows]);
                break;
            case PType.U16:
                Walk(arena, MemoryMarshal.Cast<byte, ushort>(bytes)[..rows]);
                break;
            case PType.U32:
                Walk(arena, MemoryMarshal.Cast<byte, uint>(bytes)[..rows]);
                break;
            default:
                Walk(arena, MemoryMarshal.Cast<byte, ulong>(bytes)[..rows]);
                break;
        }

        return true;
    }

    private void Walk<T>(CanonicalArena arena, ReadOnlySpan<T> values)
        where T : unmanaged, INumber<T>
    {
        bool strict = _keys.Length > 1;
        if (_keys[0].Descending)
        {
            if (strict)
            {
                Walk<T, PairOrder.StrictlyFalling>(arena, values);
            }
            else
            {
                Walk<T, PairOrder.Falling>(arena, values);
            }
        }
        else if (strict)
        {
            Walk<T, PairOrder.StrictlyRising>(arena, values);
        }
        else
        {
            Walk<T, PairOrder.Rising>(arena, values);
        }
    }

    /// <summary>
    /// The lanes clear the windows whose every pair goes the first key's way, strictly where keys
    /// follow it; the walk decides the window where one may not, pair by pair, a tie by the keys
    /// after, then hands back to them.
    /// </summary>
    private void Walk<T, TOrder>(CanonicalArena arena, ReadOnlySpan<T> values)
        where T : unmanaged, INumber<T>
        where TOrder : struct, IPairOrder
    {
        bool repeats = false;
        int row = 1;
        while (row < values.Length)
        {
            row += PairOrder.Cleared<T, TOrder>(values[(row - 1)..], ref repeats) - 1;
            int stop = Math.Min(values.Length, row + Window);
            for (; row < stop; row++)
            {
                T previous = values[row - 1];
                T current = values[row];
                if (TOrder.Descending ? current > previous : current < previous)
                {
                    Refuse(row);
                }

                if (TOrder.Strict && current == previous && Within(arena, 1, row - 1, row) > 0)
                {
                    Refuse(row);
                }
            }
        }
    }

    /// <summary>The order of rows <paramref name="a"/> and <paramref name="b"/> of the batch, by the chain of keys from <paramref name="from"/>.</summary>
    private int Within(CanonicalArena arena, int from, int a, int b)
    {
        for (int k = from; k < _keys.Length; k++)
        {
            bool left = ArenaWords.IsValid(arena, _nodes[k], a);
            bool right = ArenaWords.IsValid(arena, _nodes[k], b);
            if (!left || !right)
            {
                if (left == right)
                {
                    continue;
                }

                return Nulls(k, left);
            }

            int order = (_orders[k] ??= ColumnOrder.For(arena, _nodes[k], _keys[k].Type, _keys[k].Descending)).Compare(a, b);
            if (order != 0)
            {
                return order;
            }
        }

        return 0;
    }

    /// <summary>The order of the last batch's last row and this batch's first.</summary>
    private int Across(CanonicalArena arena)
    {
        for (int k = 0; k < _keys.Length; k++)
        {
            bool right = Literal(arena, _nodes[k], 0, _keys[k].Decimals, out FilterLiteral first);
            bool left = !_lastNull[k];
            if (!left || !right)
            {
                if (left == right)
                {
                    continue;
                }

                return Nulls(k, left);
            }

            if (!ColumnBounds.TryOrder(_last[k], first, _keys[k].Decimals, out int order))
            {
                throw new ArgumentException($"The values of the sorting column of field {_keys[k].Field} do not compare.");
            }

            if (order != 0)
            {
                return _keys[k].Descending ? -order : order;
            }
        }

        return 0;
    }

    /// <summary>The order of a present value against a null, <paramref name="leftPresent"/> saying which is which, where the key places its nulls.</summary>
    private int Nulls(int key, bool leftPresent) => (leftPresent ? -1 : 1) * (_keys[key].NullsFirst ? -1 : 1);

    /// <summary>Row <paramref name="row"/>'s value as a literal; false for a null.</summary>
    private static bool Literal(CanonicalArena arena, int node, int row, bool decimals, out FilterLiteral value)
    {
        value = default;
        if (!ArenaWords.IsValid(arena, node, row))
        {
            return false;
        }

        bool read = decimals ? LiteralReader.TryReadDecimal(arena, node, row, out value) : LiteralReader.TryRead(arena, node, row, out value);
        if (!read)
        {
            throw new ArgumentException("A sorting column holds a value no literal reads.");
        }

        return true;
    }

    [DoesNotReturn]
    private static void Refuse(int row) =>
        throw new ArgumentException($"Row {row} of the batch comes before the row before it in the order the file's sorting columns declare.");

    private readonly record struct Key(int Field, int Leaf, VortexType Type, bool Descending, bool NullsFirst, bool Decimals);
}
