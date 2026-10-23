using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
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
/// bin: the table then cannot say, and the object is read as rows. So it is read for a table of sketches.
/// </summary>
internal static class SynopsisCorrection
{
    /// <summary>
    /// Takes the rows of <paramref name="removed"/> out of a table's rows, the count and state columns of each of its
    /// chunks among <paramref name="chunks"/> replaced by corrected copies laid in <paramref name="arena"/>; false, the
    /// columns left as they were, when a row cannot come out exactly or names no row of the chunks.
    /// </summary>
    /// <param name="arena">The arena the table's rows lie in.</param>
    /// <param name="table">The table.</param>
    /// <param name="rowType">Its rows' struct: bucket, keys, count, then each measure's states.</param>
    /// <param name="chunks">The columns of each chunk's rows read, by field of <paramref name="rowType"/>.</param>
    /// <param name="schema">The object's schema, which names the keys and measures.</param>
    /// <param name="removed">
    /// The rows taken out, each batch counted from the object's first physical row by its start row and
    /// holding the table's keys and measures by their names: its selected rows, every row without one.
    /// </param>
    /// <param name="slices">The object's slices, which place a row in its bucket.</param>
    /// <param name="unit">Their unit.</param>
    /// <param name="folded">The part, whose buckets are folded; a row of another bucket is not charged, since nothing reads it.</param>
    /// <param name="reads">
    /// Which of a measure's states the question reads, by its column: its sum, its least, its greatest. A state
    /// no question reads is left as it is, and cannot make a row fail to come out.
    /// </param>
    internal static bool TryRemove(
        CanonicalArena arena, SynopsisTableInfo table, DType rowType, IReadOnlyList<int[]> chunks, DType schema,
        IReadOnlyList<RecordBatch> removed, IReadOnlyList<TimeSlice> slices, TimeUnit unit, SynopsisPart folded,
        Func<string, (bool Sum, bool Least, bool Greatest)> reads)
    {
        // A sketch is no word a row's value comes off: a distribution under a delete reads the rows.
        if (table.Kind == SynopsisTableKind.Sketches)
        {
            return false;
        }

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
        int width = rowType.FieldCount;
        int measures = table.Measures.Length;

        // Which of each measure's states the question reads, and whether its sum takes a float's five words.
        (bool Sum, bool Least, bool Greatest)[] needs = new (bool, bool, bool)[measures];
        bool[] floats = new bool[measures];
        for (int m = 0; m < measures; m++)
        {
            needs[m] = reads(schema.GetFieldName(table.Measures[m]));
            floats[m] = SynopsisTableInfo.SumsFloats(table.Measures[m], schema, []);
        }

        // The table rows the rows taken out are charged to, by bucket and keys, each once, and each row taken out with
        // its table row: built from the rows taken out alone, where a dictionary of every row of the table made a
        // correction cost what the table holds, however few rows came out of it.
        Dictionary<RowKey, int> slots = [];
        List<RowKey> wanted = [];
        List<(int Slot, int Batch, int Row)> charges = [];
        int[][] measureNodes = new int[removed.Count][];
        for (int b = 0; b < removed.Count; b++)
        {
            RecordBatch batch = removed[b];
            CanonicalArena from = batch.Arena;
            CanonicalNode struct_ = from.GetNode(batch.RootIndex);
            int[] keyNodes = new int[keys];
            for (int k = 0; k < keys; k++)
            {
                // The rows hold a key under its column's name, which a table may have marked (StateRequest.KeyColumn).
                int field = struct_.DType.IndexOfField(schema.GetFieldName(table.Keys[k]));
                if (field < 0)
                {
                    return false;
                }

                keyNodes[k] = struct_.GetFieldIndex(field);
            }

            // A measure no question reads is not taken out of: its node is -1.
            int[] nodes = new int[measures];
            for (int m = 0; m < measures; m++)
            {
                int field = struct_.DType.IndexOfField(schema.GetFieldName(table.Measures[m]));
                if (needs[m] is (false, false, false))
                {
                    nodes[m] = -1;
                    continue;
                }

                if (field < 0)
                {
                    return false;
                }

                nodes[m] = struct_.GetFieldIndex(field);
            }

            measureNodes[b] = nodes;
            ReadOnlySpan<ulong> selected = batch.SelectionWords;
            for (int r = 0; r < batch.RowCount; r++)
            {
                if (!selected.IsEmpty && ((selected[r >> 6] >> (r & 63)) & 1) == 0)
                {
                    continue;
                }

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
                if (!folded.Holds(bucket))
                {
                    continue;
                }

                FilterLiteral[] values = new FilterLiteral[keys];
                for (int k = 0; k < keys; k++)
                {
                    LiteralReader.TryRead(from, keyNodes[k], r, out values[k]);
                }

                RowKey key = new RowKey(bucket, values);
                if (!slots.TryGetValue(key, out int slot))
                {
                    slot = wanted.Count;
                    slots[key] = slot;
                    wanted.Add(key);
                }

                charges.Add((slot, b, r));
            }
        }

        if (charges.Count == 0)
        {
            return true;
        }

        // Each table row charged, found among the rows of the buckets charged by the hash of its bucket and keys, read
        // where they lie, then by its keys: a row of another bucket costs a comparison, and no row is copied.
        Dictionary<int, int> firstOfHash = new Dictionary<int, int>(wanted.Count);
        int[] nextOfHash = new int[wanted.Count];
        HashSet<long> charged = [];
        for (int s = 0; s < wanted.Count; s++)
        {
            int hash = wanted[s].GetHashCode();
            nextOfHash[s] = firstOfHash.TryGetValue(hash, out int next) ? next : -1;
            firstOfHash[hash] = s;
            charged.Add(wanted[s].Bucket);
        }

        // The hashes of the values charged of the last key, the one of most values in a table's order of keys: a row whose
        // value of it hashes to none of them is charged nothing, a code tested where its column is a dictionary, its view's
        // bytes hashed where they lie where it is a column of views. A null charged leaves every row of a bucket charged
        // to the hash of all its keys.
        HashSet<int>? lasts = keys > 0 ? [] : null;
        foreach (RowKey key in wanted)
        {
            if (key.Last.Kind == FilterLiteralKind.Null)
            {
                lasts = null;
                break;
            }

            lasts?.Add(key.Last.GetHashCode());
        }

        (int Chunk, int Row)[] at = new (int, int)[wanted.Count];
        at.AsSpan().Fill((-1, -1));
        for (int c = 0; c < chunks.Count; c++)
        {
            int[] fields = chunks[c];
            ReadOnlySpan<long> buckets = arena.GetNode(fields[0]).Values.Cast<long>();
            ReadOnlySpan<byte> codes = default;
            int values = 0;
            bool[]? held = lasts is null ? null : Held(arena, fields[keys], lasts, out codes, out values);
            CanonicalNode last = arena.GetNode(ComparisonKernels.Unwrap(arena, fields[keys]));
            bool viewed = lasts is not null && held is null && last.Kind == CanonicalKind.VarBinView;
            ValidityMask valid = ValidityMask.From(arena, last.Validity);
            try
            {
                for (int r = 0; r < buckets.Length; r++)
                {
                    if (!charged.Contains(buckets[r]))
                    {
                        continue;
                    }

                    if (held is not null)
                    {
                        uint code = BinaryPrimitives.ReadUInt32LittleEndian(codes[(r * sizeof(uint))..]);
                        if (code < (uint)values && !held[code])
                        {
                            continue;
                        }
                    }
                    else if (viewed)
                    {
                        if (!valid.IsValid(r))
                        {
                            continue;
                        }

                        HashCode view = default;
                        view.Add(FilterLiteralKind.Bytes);
                        view.AddBytes(LiteralReader.ViewAt(last, r));
                        if (!lasts!.Contains(view.ToHashCode()))
                        {
                            continue;
                        }
                    }

                    if (!firstOfHash.TryGetValue(HashOf(arena, fields, r, buckets[r], keys), out int s))
                    {
                        continue;
                    }

                    for (; s >= 0; s = nextOfHash[s])
                    {
                        if (wanted[s].Holds(arena, fields, r, buckets[r]))
                        {
                            at[s] = (c, r);
                            break;
                        }
                    }
                }
            }
            finally
            {
                if (held is not null)
                {
                    ArrayPool<bool>.Shared.Return(held);
                }
            }
        }

        foreach ((int chunk, _) in at)
        {
            if (chunk < 0)
            {
                return false;
            }
        }

        // The count and state values of each table row charged, copied out of its chunk the first time a row comes out of
        // it; per table row charged and measure, whether a row taken out held one of its extremes: past that, the top of
        // its float sum may be gone with it.
        long[]?[] corrected = new long[]?[wanted.Count];
        bool[] spent = new bool[wanted.Count * measures];
        foreach ((int slot, int b, int r) in charges)
        {
            (int c, int row) = at[slot];
            long[] states = corrected[slot] ??= Copied(arena, chunks[c], row, count, width);
            states[count]--;
            CanonicalArena from = removed[b].Arena;
            int column = count + 1;
            for (int m = 0; m < measures; m++)
            {
                if (measureNodes[b][m] >= 0 && LiteralReader.TryRead(from, measureNodes[b][m], r, out FilterLiteral value)
                    && !Remove(states, column, floats[m], value, needs[m], ref spent[(slot * measures) + m]))
                {
                    return false;
                }

                column += MeasureLayout.Words(floats[m]);
            }
        }

        // Each chunk charged, its count and state columns copied into the arena once, the rows charged laid over them.
        List<int>?[] slotsOf = new List<int>?[chunks.Count];
        for (int s = 0; s < wanted.Count; s++)
        {
            (slotsOf[at[s].Chunk] ??= []).Add(s);
        }

        for (int c = 0; c < chunks.Count; c++)
        {
            if (slotsOf[c] is not { } laid)
            {
                continue;
            }

            int length = arena.GetNode(chunks[c][0]).Length;
            for (int f = count; f < width; f++)
            {
                VortexBuffer buffer = arena.AllocateUninitialized(length * sizeof(long), sizeof(long), out Span<byte> bytes);
                Span<long> states = MemoryMarshal.Cast<byte, long>(bytes);
                arena.GetNode(chunks[c][f]).Values.Cast<long>().CopyTo(states);
                foreach (int s in laid)
                {
                    states[at[s].Row] = corrected[s]![f];
                }

                chunks[c][f] = arena.AddPrimitive(rowType.GetField(f), length, Validity.NonNullable, PType.I64, buffer);
            }
        }

        return true;
    }

    /// <summary>The count and state values of row <paramref name="row"/> of a chunk, by field from <paramref name="count"/> on.</summary>
    private static long[] Copied(CanonicalArena arena, int[] fields, int row, int count, int width)
    {
        long[] states = new long[width];
        for (int f = count; f < width; f++)
        {
            states[f] = arena.GetNode(fields[f]).Values.Cast<long>()[row];
        }

        return states;
    }

    /// <summary>
    /// The hash of a table row's bucket and keys, as <see cref="RowKey"/> hashes the literals they read as, read where
    /// they lie: a byte string's bytes with no copy.
    /// </summary>
    private static int HashOf(CanonicalArena arena, int[] fields, int row, long bucket, int keys)
    {
        HashCode hash = default;
        hash.Add(bucket);
        for (int k = 0; k < keys; k++)
        {
            hash.Add(ValueHash(arena, fields[1 + k], row));
        }

        return hash.ToHashCode();
    }

    /// <summary>
    /// The hash of row <paramref name="row"/>'s value as the literal it reads as hashes: a byte string's bytes read with
    /// no copy, a null, or a type no literal reads, as the null literal.
    /// </summary>
    private static int ValueHash(CanonicalArena arena, int node, int row)
    {
        if (LiteralReader.TryReadBytes(arena, node, row, out ReadOnlySpan<byte> bytes))
        {
            HashCode hash = default;
            hash.Add(FilterLiteralKind.Bytes);
            hash.AddBytes(bytes);
            return hash.ToHashCode();
        }

        LiteralReader.TryRead(arena, node, row, out FilterLiteral literal);
        return literal.GetHashCode();
    }

    /// <summary>
    /// Which codes of a key's column, when it is a dictionary, name a value whose hash is among <paramref name="hashes"/>,
    /// in an array of the process's pool, with the column's codes and the dictionary's values; null when it is none.
    /// </summary>
    private static bool[]? Held(CanonicalArena arena, int nodeIndex, HashSet<int> hashes, out ReadOnlySpan<byte> codes, out int values)
    {
        codes = default;
        values = 0;
        CanonicalNode node = arena.GetNode(ComparisonKernels.Unwrap(arena, nodeIndex));
        if (node.Kind != CanonicalKind.Dictionary)
        {
            return null;
        }

        int dictionary = node.EncodedValuesIndex;
        values = arena.GetNode(dictionary).Length;
        bool[] held = ArrayPool<bool>.Shared.Rent(values);
        for (int v = 0; v < values; v++)
        {
            held[v] = hashes.Contains(ValueHash(arena, dictionary, v));
        }

        codes = node.Codes.Span;
        return held;
    }

    /// <summary>
    /// Takes one value out of a measure's states at <paramref name="column"/>, laid out as <see cref="MeasureLayout"/>
    /// says, that <paramref name="needs"/> says the question reads; false when an extreme or a float sum it reads cannot
    /// say what is left.
    /// </summary>
    /// <param name="states">The count and states of the table row charged, by field.</param>
    /// <param name="column">The field of the measure's count of values, its first word.</param>
    /// <param name="floats">Whether its sum is of floats.</param>
    /// <param name="value">The value taken out.</param>
    /// <param name="needs">Which of the measure's states the question reads.</param>
    /// <param name="spent">Whether a row taken out of this table row before held one of the measure's extremes; set when this one does.</param>
    private static bool Remove(long[] states, int column, bool floats, FilterLiteral value, (bool Sum, bool Least, bool Greatest) needs, ref bool spent)
    {
        long left = --states[column + MeasureLayout.Values];
        int sumAt = column + MeasureLayout.Sum;
        int least = column + MeasureLayout.Least(floats);
        int greatest = column + MeasureLayout.Greatest(floats);
        if (floats)
        {
            double number = value.FloatValue;
            bool extreme = !double.IsNaN(number)
                && (number == BitConverter.Int64BitsToDouble(states[least]) || number == BitConverter.Int64BitsToDouble(states[greatest]));
            if (needs.Sum)
            {
                // The least and the greatest call for the top; while both stay, so does it.
                WideIndexedSum sum = StateWords.SumOf(states, sumAt);
                if (!sum.TryRemove(number, topStays: !spent && !extreme))
                {
                    return false;
                }

                StateWords.Write(in sum, states, sumAt);
            }

            spent |= extreme;
            return left == 0 || double.IsNaN(number)
                || ((!needs.Least || number != BitConverter.Int64BitsToDouble(states[least]))
                    && (!needs.Greatest || number != BitConverter.Int64BitsToDouble(states[greatest])));
        }

        Int128 integer = value.Kind == FilterLiteralKind.Unsigned ? (Int128)value.UnsignedValue : value.SignedValue;
        StateWords.Write(StateWords.IntegerOf(states, sumAt) - integer, states, sumAt);
        long bits = value.Kind == FilterLiteralKind.Unsigned ? unchecked((long)value.UnsignedValue) : value.SignedValue;
        return left == 0 || ((!needs.Least || bits != states[least]) && (!needs.Greatest || bits != states[greatest]));
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

        /// <summary>The bucket.</summary>
        public long Bucket => _bucket;

        /// <summary>The last key's value.</summary>
        public FilterLiteral Last => _values[^1];

        /// <summary>
        /// Whether row <paramref name="row"/> of a chunk, its bucket <paramref name="bucket"/> and its keys in
        /// <paramref name="fields"/> after the bucket's, is this one: its keys compared where they lie, a byte string's
        /// bytes with no copy, a null as the null literal.
        /// </summary>
        public bool Holds(CanonicalArena arena, int[] fields, int row, long bucket)
        {
            if (bucket != _bucket)
            {
                return false;
            }

            for (int k = 0; k < _values.Length; k++)
            {
                if (LiteralReader.TryReadBytes(arena, fields[1 + k], row, out ReadOnlySpan<byte> bytes))
                {
                    if (_values[k].Kind != FilterLiteralKind.Bytes || !bytes.SequenceEqual(_values[k].BytesValue))
                    {
                        return false;
                    }
                }
                else
                {
                    LiteralReader.TryRead(arena, fields[1 + k], row, out FilterLiteral value);
                    if (!value.Equals(_values[k]))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

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
