using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.Types;

namespace Vorticity.Dataset;

/// <summary>
/// Takes rows out of a synopsis table's states, to the states the table would hold without them: what a
/// delete leaves of an object's synopsis, or the part a question excludes. Each row is charged to the table's
/// row of its slice's bucket and its keys' values, and its count, its measures' counts and their exact sums
/// come off. An extreme stands unless the row holds it, and a float sum unless the row's value sets its top
/// bin: the table then cannot say, and the object is read as rows.
/// </summary>
internal static class SynopsisCorrection
{
    /// <summary>
    /// Takes the rows of <paramref name="removed"/> out of a table's rows, its count and state columns among
    /// <paramref name="fields"/> replaced by corrected copies laid in <paramref name="arena"/>; false, the
    /// columns left as they were, when a row cannot come out exactly or names no row of the table.
    /// </summary>
    /// <param name="arena">The arena the table's rows lie in.</param>
    /// <param name="table">The table.</param>
    /// <param name="rowType">Its rows' struct: bucket, keys, count, then each measure's states.</param>
    /// <param name="fields">Its rows' columns, by field of <paramref name="rowType"/>.</param>
    /// <param name="schema">The object's schema, which names the keys and measures.</param>
    /// <param name="removed">
    /// The rows taken out, each batch counted from the object's first physical row by its start row and
    /// holding the table's keys and measures by their names.
    /// </param>
    /// <param name="slices">The object's slices, which place a row in its bucket.</param>
    /// <param name="unit">Their unit.</param>
    /// <param name="folded">The buckets folded; a row of another is not charged, since nothing reads it.</param>
    /// <param name="reads">
    /// Which of a measure's states the question reads, by its column: its sum, its least, its greatest. A state
    /// no question reads is left as it is, and cannot make a row fail to come out.
    /// </param>
    internal static bool TryRemove(
        CanonicalArena arena, SynopsisTableInfo table, DType rowType, int[] fields, DType schema,
        IReadOnlyList<RecordBatch> removed, IReadOnlyList<TimeSlice> slices, TimeUnit unit, HashSet<long> folded,
        Func<string, (bool Sum, bool Least, bool Greatest)> reads)
    {
        int keys = table.Keys.Length;
        foreach (int key in table.Keys)
        {
            if (SynopsisPath.Names(key))
            {
                return false;
            }
        }

        foreach (int measure in table.Measures)
        {
            if (SynopsisPath.Names(measure))
            {
                return false;
            }
        }

        int count = keys + 1;
        int rows = arena.GetNode(fields[0]).Length;
        long[][] states = new long[fields.Length][];
        for (int f = count; f < fields.Length; f++)
        {
            states[f] = arena.GetNode(fields[f]).Values.Cast<long>().ToArray();
        }

        // The table's rows by bucket and keys.
        ReadOnlySpan<long> buckets = arena.GetNode(fields[0]).Values.Cast<long>();
        Dictionary<RowKey, int> at = new Dictionary<RowKey, int>(rows);
        for (int r = 0; r < rows; r++)
        {
            FilterLiteral[] values = new FilterLiteral[keys];
            for (int k = 0; k < keys; k++)
            {
                LiteralReader.TryRead(arena, fields[1 + k], r, out values[k]);
            }

            at[new RowKey(buckets[r], values)] = r;
        }

        // Per measure and table row, whether a row taken out held one of its extremes: past that, the top of
        // its float sum may be gone with it.
        bool[][] spent = new bool[table.Measures.Length][];
        for (int m = 0; m < spent.Length; m++)
        {
            spent[m] = new bool[rows];
        }

        foreach (RecordBatch batch in removed)
        {
            CanonicalArena from = batch.Arena;
            CanonicalNode struct_ = from.GetNode(batch.RootIndex);
            int[] keyNodes = new int[keys];
            for (int k = 0; k < keys; k++)
            {
                int field = struct_.DType.IndexOfField(rowType.GetFieldName(1 + k));
                if (field < 0)
                {
                    return false;
                }

                keyNodes[k] = struct_.GetFieldIndex(field);
            }

            // A measure no question reads is not taken out of: its node is -1.
            int[] measureNodes = new int[table.Measures.Length];
            (bool Sum, bool Least, bool Greatest)[] needs = new (bool, bool, bool)[measureNodes.Length];
            for (int m = 0; m < measureNodes.Length; m++)
            {
                string name = schema.GetFieldName(table.Measures[m]);
                needs[m] = reads(name);
                int field = struct_.DType.IndexOfField(name);
                if (needs[m] is (false, false, false))
                {
                    measureNodes[m] = -1;
                    continue;
                }

                if (field < 0)
                {
                    return false;
                }

                measureNodes[m] = struct_.GetFieldIndex(field);
            }

            for (int r = 0; r < batch.RowCount; r++)
            {
                if (SliceOf(slices, batch.StartRow + r) is not { } slice)
                {
                    return false;
                }

                long bucket = table.Bucket switch
                {
                    SynopsisBucket.Slice => slice.Start,
                    SynopsisBucket.Hour => TimeSlicer.Floor(slice.Start, unit, SliceWidth.Hour),
                    _ => 0,
                };
                if (!folded.Contains(bucket))
                {
                    continue;
                }

                FilterLiteral[] values = new FilterLiteral[keys];
                for (int k = 0; k < keys; k++)
                {
                    LiteralReader.TryRead(from, keyNodes[k], r, out values[k]);
                }

                if (!at.TryGetValue(new RowKey(bucket, values), out int row))
                {
                    return false;
                }

                states[count][row]--;
                int column = count + 1;
                for (int m = 0; m < measureNodes.Length; m++)
                {
                    bool floats = rowType.GetFieldName(column + 1).EndsWith(".sum.m0", StringComparison.Ordinal);
                    int words = floats ? 5 : 2;
                    if (measureNodes[m] >= 0 && LiteralReader.TryRead(from, measureNodes[m], r, out FilterLiteral value)
                        && !Remove(states, column, words, row, value, needs[m], ref spent[m][row]))
                    {
                        return false;
                    }

                    column += 1 + words + 2;
                }
            }
        }

        for (int f = count; f < fields.Length; f++)
        {
            VortexBuffer buffer = arena.AllocateUninitialized(rows * sizeof(long), sizeof(long), out Span<byte> bytes);
            MemoryMarshal.AsBytes(states[f].AsSpan()).CopyTo(bytes);
            fields[f] = arena.AddPrimitive(rowType.GetField(f), rows, Validity.NonNullable, PType.I64, buffer);
        }

        return true;
    }

    /// <summary>
    /// Takes one value out of a measure's states at <paramref name="column"/> (its count of values, then its
    /// sum's words, its least and its greatest) that <paramref name="needs"/> says the question reads; false
    /// when an extreme or a float sum it reads cannot say what is left.
    /// </summary>
    /// <param name="states">The table's count and state columns, by field.</param>
    /// <param name="column">The field of the measure's count of values.</param>
    /// <param name="words">The words of its sum: five for a float, two for an integer.</param>
    /// <param name="row">The table row charged.</param>
    /// <param name="value">The value taken out.</param>
    /// <param name="needs">Which of the measure's states the question reads.</param>
    /// <param name="spent">Whether a row taken out of this table row before held one of the measure's extremes; set when this one does.</param>
    private static bool Remove(long[][] states, int column, int words, int row, FilterLiteral value, (bool Sum, bool Least, bool Greatest) needs, ref bool spent)
    {
        long left = --states[column][row];
        int least = column + 1 + words;
        int greatest = least + 1;
        if (words == 5)
        {
            double number = value.FloatValue;
            bool extreme = !double.IsNaN(number)
                && (number == BitConverter.Int64BitsToDouble(states[least][row]) || number == BitConverter.Int64BitsToDouble(states[greatest][row]));
            if (needs.Sum)
            {
                // The least and the greatest call for the top; while both stay, so does it.
                WideIndexedSum sum = default;
                sum.Sum.M0 = states[column + 1][row];
                sum.Sum.M1 = states[column + 2][row];
                sum.Sum.M2 = states[column + 3][row];
                sum.Sum.Meta = states[column + 4][row];
                sum.High = states[column + 5][row];
                if (!sum.TryRemove(number, topStays: !spent && !extreme))
                {
                    return false;
                }

                (states[column + 1][row], states[column + 2][row], states[column + 3][row], states[column + 4][row], states[column + 5][row]) =
                    (sum.Sum.M0, sum.Sum.M1, sum.Sum.M2, sum.Sum.Meta, sum.High);
            }

            spent |= extreme;
            return left == 0 || double.IsNaN(number)
                || ((!needs.Least || number != BitConverter.Int64BitsToDouble(states[least][row]))
                    && (!needs.Greatest || number != BitConverter.Int64BitsToDouble(states[greatest][row])));
        }

        Int128 integer = value.Kind == FilterLiteralKind.Unsigned ? (Int128)value.UnsignedValue : value.SignedValue;
        Int128 total = new Int128(unchecked((ulong)states[column + 2][row]), unchecked((ulong)states[column + 1][row])) - integer;
        states[column + 1][row] = unchecked((long)(ulong)total);
        states[column + 2][row] = (long)(total >> 64);
        long bits = value.Kind == FilterLiteralKind.Unsigned ? unchecked((long)value.UnsignedValue) : value.SignedValue;
        return left == 0 || ((!needs.Least || bits != states[least][row]) && (!needs.Greatest || bits != states[greatest][row]));
    }

    /// <summary>The slice holding physical row <paramref name="row"/>, by halves; null past the slices.</summary>
    private static TimeSlice? SliceOf(IReadOnlyList<TimeSlice> slices, long row)
    {
        int low = 0;
        int high = slices.Count - 1;
        while (low <= high)
        {
            int middle = (low + high) >>> 1;
            TimeSlice slice = slices[middle];
            if (row < slice.FirstRow)
            {
                high = middle - 1;
            }
            else if (row >= slice.FirstRow + slice.Rows)
            {
                low = middle + 1;
            }
            else
            {
                return slice;
            }
        }

        return null;
    }

    /// <summary>A table's row: its bucket and its keys' values, a null as the null literal.</summary>
    private readonly struct RowKey(long bucket, FilterLiteral[] values) : IEquatable<RowKey>
    {
        private readonly long _bucket = bucket;
        private readonly FilterLiteral[] _values = values;

        public bool Equals(RowKey other)
        {
            if (_bucket != other._bucket || _values.Length != other._values.Length)
            {
                return false;
            }

            for (int i = 0; i < _values.Length; i++)
            {
                if (!_values[i].Equals(other._values[i]))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj) => obj is RowKey other && Equals(other);

        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(_bucket);
            foreach (FilterLiteral value in _values)
            {
                hash.Add(value);
            }

            return hash.ToHashCode();
        }
    }
}
