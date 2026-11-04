using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Numerics;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Expressions;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.Compute;

/// <summary>
/// The tuples of a <see cref="TupleInExpr"/>, hashed once for the kinds of its columns: each value a 64-bit key, an
/// integer's or a decimal's of eight bytes its value, a float's as <see cref="InSet"/> keys it, a text's its XXH3-64; a
/// row's keys mixed column after column and looked up, and a tuple found compared value by value, a text's by its bytes.
/// </summary>
/// <remarks>
/// A row with a null value looks its other values up among the tuples' values of those columns, in a table built for that
/// set of columns the first time a row asks it: found, the row is unknown, as the tuples' equalities say; false otherwise.
/// A tuple holding a value its column's kind cannot hold, a text for a number, leaves the set: no row equals it.
/// </remarks>
internal sealed class TupleSet
{
    /// <summary>The state bit of a row whose value no tuple can hold: a float's NaN.</summary>
    private const ushort Impossible = 0x100;

    private const ulong Golden = 0x9E3779B97F4A7C15UL;

    private readonly CandidateKind[] _kinds;
    private readonly int _arity;
    private readonly int _count;

    // Tuple t's key of column j at t × arity + j; a text column's value there too, null for the others.
    private readonly ulong[] _keys;
    private readonly byte[]?[] _bytes;

    // By the mask of the columns a row holds values of: the full mask's built with the set, the others at their first ask.
    private readonly Table?[] _tables;

    private TupleSet(CandidateKind[] kinds, int count, ulong[] keys, byte[]?[] bytes)
    {
        _kinds = kinds;
        _arity = kinds.Length;
        _count = count;
        _keys = keys;
        _bytes = bytes;
        _tables = new Table?[1 << _arity];
        _tables[^1] = Build(_tables.Length - 1);
    }

    /// <summary>The tuples the set holds: those whose values their columns' kinds can hold.</summary>
    internal int Count => _count;

    /// <summary>
    /// The set of <paramref name="tuples"/> for columns of <paramref name="kinds"/>; null when one kind has none, a decimal
    /// of sixteen bytes or more, a list, a boolean: the tuples are then compared one by one.
    /// </summary>
    internal static TupleSet? TryBuild(TupleInExpr tuples, ReadOnlySpan<CandidateKind> kinds)
    {
        foreach (CandidateKind kind in kinds)
        {
            if (kind is not (CandidateKind.Signed or CandidateKind.Unsigned or CandidateKind.Float or CandidateKind.Decimal or CandidateKind.Bytes))
            {
                return null;
            }
        }

        int arity = kinds.Length;
        List<ulong> keys = new List<ulong>(tuples.Values.Length);
        List<byte[]?> bytes = new List<byte[]?>(tuples.Values.Length);
        Span<ulong> row = stackalloc ulong[arity];
        int count = 0;
        for (int t = 0; t < tuples.Count; t++)
        {
            ReadOnlySpan<FilterLiteral> tuple = tuples.Tuple(t);
            bool holds = true;
            for (int column = 0; column < arity && holds; column++)
            {
                if (kinds[column] == CandidateKind.Bytes)
                {
                    holds = tuple[column].Kind == FilterLiteralKind.Bytes;
                    row[column] = holds ? XxHash3.HashToUInt64(tuple[column].BytesValue) : 0;
                }
                else
                {
                    holds = InSet.TryKey(tuple[column], kinds[column], out row[column]);
                }
            }

            if (!holds)
            {
                continue;
            }

            for (int column = 0; column < arity; column++)
            {
                keys.Add(row[column]);
                bytes.Add(kinds[column] == CandidateKind.Bytes ? tuple[column].BytesValue.ToArray() : null);
            }

            count++;
        }

        return new TupleSet(kinds.ToArray(), count, [.. keys], [.. bytes]);
    }

    /// <summary>
    /// Writes one state per row of <paramref name="destination"/>: whether the row's values of <paramref name="columns"/>,
    /// the storage of each column, neither an extension nor encoded, are one of the tuples.
    /// </summary>
    internal void Apply(CanonicalArena arena, ReadOnlySpan<int> columns, Span<byte> destination)
    {
        int rows = destination.Length;
        int arity = _arity;
        ulong[] keys = ArrayPool<ulong>.Shared.Rent(Math.Max(rows * arity, 1));
        ushort[] states = ArrayPool<ushort>.Shared.Rent(Math.Max(rows, 1));
        try
        {
            Span<ushort> held = states.AsSpan(0, rows);
            held.Clear();
            for (int column = 0; column < arity; column++)
            {
                KeysOf(arena, columns[column], _kinds[column], keys.AsSpan(column * rows, rows), held, (ushort)(1 << column));
            }

            int full = (1 << arity) - 1;
            for (int row = 0; row < rows; row++)
            {
                ushort state = held[row];
                if ((state & Impossible) != 0)
                {
                    destination[row] = Trilean.False;
                    continue;
                }

                int mask = full & ~state;
                if (mask == 0)
                {
                    destination[row] = _count > 0 ? Trilean.Unknown : Trilean.False;
                    continue;
                }

                bool found = Holds(TableFor(mask), arena, columns, keys, rows, row);
                destination[row] = found ? (mask == full ? Trilean.True : Trilean.Unknown) : Trilean.False;
            }
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(keys);
            ArrayPool<ushort>.Shared.Return(states);
        }
    }

    /// <summary>The table of the columns of <paramref name="mask"/>, built at its first ask.</summary>
    private Table TableFor(int mask)
    {
        if (System.Threading.Volatile.Read(ref _tables[mask]) is { } held)
        {
            return held;
        }

        Table made = Build(mask);
        return System.Threading.Interlocked.CompareExchange(ref _tables[mask], made, null) ?? made;
    }

    /// <summary>The tuples hashed on the columns of <paramref name="mask"/>, open addressing a power of two past twice their count.</summary>
    private Table Build(int mask)
    {
        int slots = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(_count * 2, 8));
        int shift = 64 - BitOperations.Log2((uint)slots);
        int[] table = new int[slots];
        ulong[] hashes = new ulong[_count];
        for (int t = 0; t < _count; t++)
        {
            ulong hash = Seed();
            for (int column = 0; column < _arity; column++)
            {
                if ((mask & (1 << column)) != 0)
                {
                    hash = Mix(hash, _keys[(t * _arity) + column]);
                }
            }

            hashes[t] = hash;
            int slot = (int)(hash >> shift);
            while (table[slot] != 0)
            {
                slot = (slot + 1) & (slots - 1);
            }

            table[slot] = t + 1;
        }

        return new Table(mask, table, hashes, shift);
    }

    /// <summary>Whether a tuple holds row <paramref name="row"/>'s values of the columns of <paramref name="table"/>'s mask.</summary>
    private bool Holds(Table table, CanonicalArena arena, ReadOnlySpan<int> columns, ulong[] keys, int rows, int row)
    {
        int mask = table.Mask;
        ulong hash = Seed();
        for (int column = 0; column < _arity; column++)
        {
            if ((mask & (1 << column)) != 0)
            {
                hash = Mix(hash, keys[(column * rows) + row]);
            }
        }

        int[] slots = table.Slots;
        int slot = (int)(hash >> table.Shift);
        while (slots[slot] != 0)
        {
            int t = slots[slot] - 1;
            if (table.Hashes[t] == hash && Matches(t, mask, arena, columns, keys, rows, row))
            {
                return true;
            }

            slot = (slot + 1) & (slots.Length - 1);
        }

        return false;
    }

    /// <summary>Whether tuple <paramref name="tuple"/> holds row <paramref name="row"/>'s values of the columns of <paramref name="mask"/>.</summary>
    private bool Matches(int tuple, int mask, CanonicalArena arena, ReadOnlySpan<int> columns, ulong[] keys, int rows, int row)
    {
        for (int column = 0; column < _arity; column++)
        {
            if ((mask & (1 << column)) == 0)
            {
                continue;
            }

            if (keys[(column * rows) + row] != _keys[(tuple * _arity) + column])
            {
                return false;
            }

            if (_kinds[column] == CandidateKind.Bytes
                && !new ViewValues(arena.GetNode(columns[column])).At(row).SequenceEqual(_bytes[(tuple * _arity) + column]))
            {
                return false;
            }
        }

        return true;
    }

    private static ulong Seed() => Golden;

    private static ulong Mix(ulong hash, ulong key) => (BitOperations.RotateLeft(hash, 27) ^ key) * Golden;

    /// <summary>
    /// The key of each row of the column <paramref name="node"/> into <paramref name="keys"/>, and <paramref name="bit"/> set
    /// in the state of each row it holds no value of; <see cref="Impossible"/> for a value no tuple holds.
    /// </summary>
    private static void KeysOf(CanonicalArena arena, int node, CandidateKind kind, Span<ulong> keys, Span<ushort> states, ushort bit)
    {
        CanonicalNode column = arena.GetNode(node);
        if (column.Kind == CanonicalKind.Null)
        {
            Mark(states, bit);
            return;
        }

        ValidityMask mask = ValidityMask.From(arena, column.Validity);
        if (mask.AllInvalid)
        {
            Mark(states, bit);
            return;
        }

        switch (kind)
        {
            case CandidateKind.Bytes:
            {
                ViewValues values = new ViewValues(column);
                for (int row = 0; row < keys.Length; row++)
                {
                    keys[row] = mask.AllValid || mask.IsValid(row) ? XxHash3.HashToUInt64(values.At(row)) : 0;
                }

                break;
            }

            case CandidateKind.Float:
                switch (column.PType)
                {
                    case PType.F16:
                        Floats<Half>(column.Values.Span, keys, states);
                        break;
                    case PType.F32:
                        Floats<float>(column.Values.Span, keys, states);
                        break;
                    default:
                        Floats<double>(column.Values.Span, keys, states);
                        break;
                }

                break;

            default:
                PType ptype = column.Kind == CanonicalKind.Primitive
                    ? column.PType
                    : column.Storage switch
                    {
                        DecimalStorageType.I8 => PType.I8,
                        DecimalStorageType.I16 => PType.I16,
                        DecimalStorageType.I32 => PType.I32,
                        _ => PType.I64,
                    };
                ReadOnlySpan<byte> bytes = column.Values.Span;
                switch (ptype)
                {
                    case PType.I8:
                        Integers<sbyte>(bytes, keys);
                        break;
                    case PType.I16:
                        Integers<short>(bytes, keys);
                        break;
                    case PType.I32:
                        Integers<int>(bytes, keys);
                        break;
                    case PType.I64:
                        Integers<long>(bytes, keys);
                        break;
                    case PType.U8:
                        Integers<byte>(bytes, keys);
                        break;
                    case PType.U16:
                        Integers<ushort>(bytes, keys);
                        break;
                    case PType.U32:
                        Integers<uint>(bytes, keys);
                        break;
                    default:
                        Integers<ulong>(bytes, keys);
                        break;
                }

                break;
        }

        if (!mask.AllValid)
        {
            for (int row = 0; row < states.Length; row++)
            {
                if (!mask.IsValid(row))
                {
                    states[row] |= bit;
                }
            }
        }
    }

    private static void Mark(Span<ushort> states, ushort bit)
    {
        for (int row = 0; row < states.Length; row++)
        {
            states[row] |= bit;
        }
    }

    /// <summary>An integer's key, its value widened as <see cref="InSet"/> widens it.</summary>
    private static void Integers<TValue>(ReadOnlySpan<byte> bytes, Span<ulong> keys)
        where TValue : unmanaged, IBinaryInteger<TValue>
    {
        ReadOnlySpan<TValue> values = MemoryMarshal.Cast<byte, TValue>(bytes)[..keys.Length];
        for (int row = 0; row < keys.Length; row++)
        {
            keys[row] = ulong.CreateTruncating(values[row]);
        }
    }

    /// <summary>A float's key, widened to a double as the equality kernel widens it; a NaN equals no tuple's value.</summary>
    private static void Floats<TValue>(ReadOnlySpan<byte> bytes, Span<ulong> keys, Span<ushort> states)
        where TValue : unmanaged, IFloatingPoint<TValue>
    {
        ReadOnlySpan<TValue> values = MemoryMarshal.Cast<byte, TValue>(bytes)[..keys.Length];
        for (int row = 0; row < keys.Length; row++)
        {
            double value = double.CreateTruncating(values[row]);
            if (double.IsNaN(value))
            {
                states[row] |= Impossible;
            }

            keys[row] = InSet.FloatKey(value);
        }
    }

    /// <summary>The tuples hashed on the columns of <see cref="Mask"/>: in each slot a tuple and one, 0 for none.</summary>
    private sealed class Table(int mask, int[] slots, ulong[] hashes, int shift)
    {
        internal int Mask => mask;

        internal int[] Slots => slots;

        internal ulong[] Hashes => hashes;

        internal int Shift => shift;
    }
}
