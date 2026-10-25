using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Indexes;
using Vorticity.Serialization.Protobuf;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Dataset;

/// <summary>
/// The counts of the rows deletes marked in an object, which a question subtracts from the object's own
/// (<see cref="ColumnCounts"/>): for each column the object counts, the entries of the rows taken, each its rows and the
/// count of values, exact sum and extremes of each measure the object's counts of that column carry. Extremes do not
/// subtract, and a question of them reads a marked object's rows; they say whether a float sum does
/// (<see cref="Less"/>). A delete after another adds its rows to these, entry by entry.
/// </summary>
/// <remarks>
/// Encoded as its version, its count of columns, then each column's place and its counts' bytes, a length first.
/// </remarks>
internal sealed class DeletedCounts
{
    private const byte Version = 1;

    private DeletedCounts(IReadOnlyList<(int Field, ColumnCounts Counts)> columns) => Columns = columns;

    /// <summary>Each counted column's counts of the rows taken, by the column's place.</summary>
    internal IReadOnlyList<(int Field, ColumnCounts Counts)> Columns { get; }

    /// <summary>The rows taken, as every column's counts count them; 0 for counts of no column.</summary>
    internal long Rows => Columns.Count == 0 ? 0 : RowsOf(Columns[0].Counts);

    /// <summary>The rows <paramref name="counts"/> count, every entry's.</summary>
    private static long RowsOf(ColumnCounts counts)
    {
        long rows = 0;
        for (int entry = 0; entry < counts.Entries; entry++)
        {
            rows += counts.WordsOf(entry)[0];
        }

        return rows;
    }

    /// <summary>The counts of column <paramref name="field"/>'s rows taken; null for a column the object does not count.</summary>
    internal ColumnCounts? CountsOf(int field)
    {
        foreach ((int column, ColumnCounts counts) in Columns)
        {
            if (column == field)
            {
                return counts;
            }
        }

        return null;
    }

    /// <summary>The bytes the counts hold in memory.</summary>
    internal long Bytes
    {
        get
        {
            long bytes = 0;
            foreach ((_, ColumnCounts counts) in Columns)
            {
                bytes += counts.Bytes;
            }

            return bytes;
        }
    }

    /// <summary>
    /// The rows of <paramref name="earlier"/> and of <paramref name="later"/> together, column by column, entry by entry; null
    /// when they count other columns or other measures, as when the object's counts changed between two deletes, which no
    /// rewrite in place does.
    /// </summary>
    internal static DeletedCounts? Merge(DeletedCounts earlier, DeletedCounts later)
    {
        if (earlier.Columns.Count != later.Columns.Count)
        {
            return null;
        }

        List<(int Field, ColumnCounts Counts)> merged = [];
        foreach ((int field, ColumnCounts counts) in later.Columns)
        {
            if (earlier.CountsOf(field) is not { } before
                || !before.MeasureFields.AsSpan().SequenceEqual(counts.MeasureFields)
                || !before.Kinds.AsSpan().SequenceEqual(counts.Kinds))
            {
                return null;
            }

            merged.Add((field, Merged(before, counts)));
        }

        return new DeletedCounts(merged);
    }

    /// <summary>
    /// <paramref name="counts"/>, and <paramref name="more"/> when given, as one: an entry a value, the nulls first, then the
    /// values left out past the entries counts keep, the values of fewest rows, then the others by their bytes.
    /// </summary>
    private static ColumnCounts Merged(ColumnCounts counts, ColumnCounts? more)
    {
        CountsMerge merge = new CountsMerge();
        merge.Add(counts, null);
        if (more is not null)
        {
            merge.Add(more, null);
        }

        return merge.Finish() is ({ } sum, _) ? sum : counts;
    }

    /// <summary>
    /// An object's counts of a column less <paramref name="taken"/>, the counts of its rows deletes took: each value's
    /// entry its rows, and each measure's count of values and sum, less the deleted rows' of that value, or of the values
    /// left out for a value the object's counts leave out; an entry left with no row dropped, its value gone from the
    /// object. Null when that does not give the live rows' exactly: other measures, a deleted value the counts do not
    /// hold, values the deleted rows' counts leave out, or a sum of floats whose top a deleted value may have set
    /// (<see cref="WideIndexedSum.TryTakeOut"/>). The extremes stay those of every row, which no question reads of these.
    /// </summary>
    internal static ColumnCounts? Less(ColumnCounts counts, ColumnCounts taken)
    {
        if (counts.Buckets != 0 || !counts.MeasureFields.AsSpan().SequenceEqual(taken.MeasureFields) || !counts.Kinds.AsSpan().SequenceEqual(taken.Kinds))
        {
            return null;
        }

        int width = counts.WordsPerEntry;
        long[] words = (long[])counts.Words.Clone();
        foreach (CountGroup group in taken.Groups)
        {
            int entry = group.Kind switch
            {
                CountedValue.Null => EntryOf(counts.Groups, CountedValue.Null, null),
                CountedValue.Value => EntryOf(counts.Groups, CountedValue.Value, group.Value) is int listed and >= 0
                    ? listed
                    : EntryOf(counts.Groups, CountedValue.Others, null),
                _ => -1,
            };
            if (entry < 0 || !TakeOut(words.AsSpan(entry * width, width), taken.WordsOf(group.FirstEntry), counts.Kinds))
            {
                return null;
            }
        }

        // The entries left with rows, their groups numbered again.
        List<CountGroup> groups = new List<CountGroup>(counts.Groups.Length);
        int kept = 0;
        foreach (CountGroup group in counts.Groups)
        {
            if (words[group.FirstEntry * width] > 0)
            {
                Array.Copy(words, group.FirstEntry * width, words, kept * width, width);
                groups.Add(group with { FirstEntry = kept, Entries = 1 });
                kept++;
            }
        }

        return new ColumnCounts(counts.MeasureFields, counts.Kinds, [.. groups], words.AsSpan(0, kept * width).ToArray());
    }

    /// <summary>The entry of the group of <paramref name="kind"/>, of <paramref name="value"/> for a value; -1 for none.</summary>
    private static int EntryOf(CountGroup[] groups, CountedValue kind, byte[]? value)
    {
        if (kind != CountedValue.Value)
        {
            foreach (CountGroup group in groups)
            {
                if (group.Kind == kind)
                {
                    return group.FirstEntry;
                }
            }

            return -1;
        }

        // The values follow the nulls and those left out, in the order of their bytes.
        int low = 0;
        while (low < groups.Length && groups[low].Kind != CountedValue.Value)
        {
            low++;
        }

        int high = groups.Length - 1;
        while (low <= high)
        {
            int middle = (low + high) >>> 1;
            int order = groups[middle].Value.AsSpan().SequenceCompareTo(value);
            if (order == 0)
            {
                return groups[middle].FirstEntry;
            }

            (low, high) = order < 0 ? (middle + 1, high) : (low, middle - 1);
        }

        return -1;
    }

    /// <summary>Takes the words <paramref name="taken"/> out of an entry's <paramref name="words"/>; false when the result would not be the live rows' exactly.</summary>
    private static bool TakeOut(Span<long> words, ReadOnlySpan<long> taken, MeasureKind[] kinds)
    {
        if (taken[0] > words[0])
        {
            return false;
        }

        words[0] -= taken[0];
        int at = 1;
        foreach (MeasureKind kind in kinds)
        {
            bool floats = kind == MeasureKind.Float;
            if (taken[at + MeasureLayout.Values] > words[at + MeasureLayout.Values])
            {
                return false;
            }

            words[at + MeasureLayout.Values] -= taken[at + MeasureLayout.Values];
            if (floats)
            {
                // The top stays when the least and the greatest of every row lie past the deleted rows' own: both live.
                bool stays = BitConverter.Int64BitsToDouble(taken[at + MeasureLayout.FloatLeast]) > BitConverter.Int64BitsToDouble(words[at + MeasureLayout.FloatLeast])
                    && BitConverter.Int64BitsToDouble(taken[at + MeasureLayout.FloatGreatest]) < BitConverter.Int64BitsToDouble(words[at + MeasureLayout.FloatGreatest]);
                WideIndexedSum sum = StateWords.SumOf(words, at + MeasureLayout.Sum);
                if (!sum.TryTakeOut(StateWords.SumOf(taken, at + MeasureLayout.Sum), stays))
                {
                    return false;
                }

                StateWords.Write(sum, words, at + MeasureLayout.Sum);
            }
            else
            {
                StateWords.Write(StateWords.IntegerOf(words, at + MeasureLayout.Sum) - StateWords.IntegerOf(taken, at + MeasureLayout.Sum), words, at + MeasureLayout.Sum);
            }

            at += MeasureLayout.Words(floats);
        }

        return true;
    }

    /// <summary>
    /// How these counts differ from <paramref name="other"/>, the same rows counted again: by column, value, rows, and each
    /// measure's count of values and sum, a sum of floats by its value, which an indexed sum gives whatever the order its
    /// values came in. Null when they do not; extremes, which no question takes from these, are not compared.
    /// </summary>
    internal string? Differs(DeletedCounts other)
    {
        if (Columns.Count != other.Columns.Count)
        {
            return $"{Columns.Count} columns counted where {other.Columns.Count} are";
        }

        for (int c = 0; c < Columns.Count; c++)
        {
            (int field, ColumnCounts mine) = Columns[c];
            ColumnCounts? theirs = other.CountsOf(field);
            if (theirs is null || theirs.Entries != mine.Entries || theirs.Groups.Length != mine.Groups.Length
                || !theirs.MeasureFields.AsSpan().SequenceEqual(mine.MeasureFields))
            {
                return $"column {field} holds {mine.Entries} entries where {theirs?.Entries ?? 0} are, or other measures";
            }

            for (int group = 0; group < mine.Groups.Length; group++)
            {
                if (mine.Groups[group].Kind != theirs.Groups[group].Kind || !mine.Groups[group].Value.AsSpan().SequenceEqual(theirs.Groups[group].Value))
                {
                    return $"column {field} counts other values at group {group}";
                }
            }

            for (int entry = 0; entry < mine.Entries; entry++)
            {
                ReadOnlySpan<long> left = mine.WordsOf(entry);
                ReadOnlySpan<long> right = theirs.WordsOf(entry);
                bool same = left[0] == right[0];
                int at = 1;
                foreach (MeasureKind kind in mine.Kinds)
                {
                    bool floats = kind == MeasureKind.Float;
                    same &= left[at + MeasureLayout.Values] == right[at + MeasureLayout.Values]
                        && (floats
                            ? StateWords.SumOf(left, at + MeasureLayout.Sum).Value.Equals(StateWords.SumOf(right, at + MeasureLayout.Sum).Value)
                            : StateWords.IntegerOf(left, at + MeasureLayout.Sum) == StateWords.IntegerOf(right, at + MeasureLayout.Sum));
                    at += MeasureLayout.Words(floats);
                }

                if (!same)
                {
                    return $"column {field} counts other rows or sums at entry {entry}";
                }
            }
        }

        return null;
    }

    /// <summary>The counts' bytes, the payload of their fragment.</summary>
    internal byte[] ToBytes()
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            writer.WriteVarint(Version);
            writer.WriteVarint((ulong)Columns.Count);
            foreach ((int field, ColumnCounts counts) in Columns)
            {
                byte[] bytes = counts.ToBytes();
                writer.WriteVarint((ulong)field);
                writer.WriteVarint((ulong)bytes.Length);
                writer.WriteRawBytes(bytes);
            }

            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>The counts <paramref name="bytes"/> encode.</summary>
    /// <exception cref="VortexFormatException">They do not decode as counts of this version.</exception>
    internal static DeletedCounts From(ReadOnlySpan<byte> bytes)
    {
        ProtoReader reader = new ProtoReader(bytes);
        if (reader.ReadVarint() != Version)
        {
            throw new VortexFormatException("The counts of an object's deleted rows are of a version this library does not read.");
        }

        ulong count = reader.ReadVarint();
        if (count > (ulong)bytes.Length)
        {
            throw new VortexFormatException($"The counts of an object's deleted rows name {count} columns in {bytes.Length} bytes.");
        }

        List<(int Field, ColumnCounts Counts)> columns = new List<(int Field, ColumnCounts Counts)>((int)count);
        for (ulong c = 0; c < count; c++)
        {
            int field = checked((int)reader.ReadVarint());
            columns.Add((field, ColumnCounts.From(reader.ReadLengthDelimited())));
        }

        if (!reader.End)
        {
            throw new VortexFormatException("The counts of an object's deleted rows run past their columns.");
        }

        // Every column counts the same rows, those the deletes took.
        for (int c = 1; c < columns.Count; c++)
        {
            if (RowsOf(columns[c].Counts) != RowsOf(columns[0].Counts))
            {
                throw new VortexFormatException(
                    $"The counts of an object's deleted rows count {RowsOf(columns[c].Counts)} rows in column {columns[c].Field} and {RowsOf(columns[0].Counts)} in column {columns[0].Field}.");
            }
        }

        return new DeletedCounts(columns);
    }

    /// <summary>
    /// Counts the rows a delete takes, batch by batch: for each column the object counts, by its value as the column stores
    /// it, the rows and each measure its counts carry.
    /// </summary>
    internal sealed class Builder
    {
        private readonly Column[] _columns;

        /// <param name="schema">The object's schema, a struct of its columns.</param>
        /// <param name="counted">Each column the object counts, and its counts, whose measures these carry.</param>
        /// <param name="read">The columns the batches hold, ascending; null when they hold every column in its place.</param>
        internal Builder(DType schema, IReadOnlyList<(int Field, ColumnCounts Counts)> counted, IReadOnlyList<int>? read = null)
        {
            _columns = new Column[counted.Count];
            for (int c = 0; c < counted.Count; c++)
            {
                (int field, ColumnCounts counts) = counted[c];
                int[] measures = new int[counts.MeasureFields.Length];
                for (int m = 0; m < measures.Length; m++)
                {
                    measures[m] = PlaceOf(counts.MeasureFields[m], read);
                }

                _columns[c] = new Column(field, PlaceOf(field, read), schema.GetField(field), counts.MeasureFields, measures, counts.Kinds);
            }
        }

        /// <summary>
        /// The columns a batch must hold for these counts: each counted column and the measures its counts carry, ascending.
        /// </summary>
        internal static int[] ColumnsRead(IReadOnlyList<(int Field, ColumnCounts Counts)> counted)
        {
            SortedSet<int> fields = [];
            foreach ((int field, ColumnCounts counts) in counted)
            {
                fields.Add(field);
                fields.UnionWith(counts.MeasureFields);
            }

            return [.. fields];
        }

        private static int PlaceOf(int field, IReadOnlyList<int>? read)
        {
            if (read is null)
            {
                return field;
            }

            for (int place = 0; place < read.Count; place++)
            {
                if (read[place] == field)
                {
                    return place;
                }
            }

            throw new ArgumentException($"The batches counted do not hold column {field}.", nameof(read));
        }

        /// <summary>Counts the rows of <paramref name="batch"/> its selection keeps, every one when it selects none.</summary>
        internal void Add(RecordBatch batch) => Add(batch, batch.SelectionWords);

        /// <summary>Counts the rows of <paramref name="batch"/> whose bits <paramref name="selection"/> sets, every one when it is empty.</summary>
        internal void Add(RecordBatch batch, ReadOnlySpan<ulong> selection)
        {
            CanonicalArena arena = batch.Arena;
            CanonicalNode root = arena.GetNode(batch.RootIndex);
            foreach (Column column in _columns)
            {
                for (int row = 0; row < batch.RowCount; row++)
                {
                    if (selection.IsEmpty || ((selection[row >> 6] >> (row & 63)) & 1) != 0)
                    {
                        column.Add(arena, root, row);
                    }
                }
            }
        }

        /// <summary>The counts of every row added.</summary>
        internal DeletedCounts Finish()
        {
            List<(int Field, ColumnCounts Counts)> columns = new List<(int Field, ColumnCounts Counts)>(_columns.Length);
            foreach (Column column in _columns)
            {
                columns.Add((column.Field, Merged(column.Counts(), null)));
            }

            return new DeletedCounts(columns);
        }

        /// <summary>One counted column's entries of the rows taken.</summary>
        /// <param name="field">The column's place in the schema.</param>
        /// <param name="place">Its place in the batches.</param>
        /// <param name="dtype">Its type.</param>
        /// <param name="measureFields">The measures its counts carry, by their places in the schema.</param>
        /// <param name="measurePlaces">Their places in the batches.</param>
        /// <param name="kinds">How each is summed.</param>
        private sealed class Column(int field, int place, DType dtype, int[] measureFields, int[] measurePlaces, MeasureKind[] kinds)
        {
            private readonly int _width = StateWords.Count(kinds);
            private readonly Dictionary<byte[], long[]> _entries = new Dictionary<byte[], long[]>(ByteKeys.Instance);
            private long[]? _nulls;

            internal int Field { get; } = field;

            /// <summary>Counts row <paramref name="row"/> of the batch whose root is <paramref name="root"/>.</summary>
            internal void Add(CanonicalArena arena, CanonicalNode root, int row)
            {
                long[] words;
                if (TryBytes(arena, root.GetFieldIndex(place), row, dtype, out ReadOnlySpan<byte> value))
                {
                    Dictionary<byte[], long[]>.AlternateLookup<ReadOnlySpan<byte>> lookup = _entries.GetAlternateLookup<ReadOnlySpan<byte>>();
                    if (!lookup.TryGetValue(value, out long[]? held))
                    {
                        held = Opened();
                        lookup[value] = held;
                    }

                    words = held;
                }
                else
                {
                    words = _nulls ??= Opened();
                }

                words[0]++;
                int at = 1;
                for (int m = 0; m < measureFields.Length; m++)
                {
                    bool floats = kinds[m] == MeasureKind.Float;
                    Fold(arena, root.GetFieldIndex(measurePlaces[m]), row, kinds[m], words.AsSpan(at, MeasureLayout.Words(floats)));
                    at += MeasureLayout.Words(floats);
                }
            }

            private long[] Opened()
            {
                long[] words = new long[_width];
                int at = 1;
                foreach (MeasureKind kind in kinds)
                {
                    ColumnCountsBuilder.OpenState(words.AsSpan(at), kind);
                    at += MeasureLayout.Words(kind == MeasureKind.Float);
                }

                return words;
            }

            /// <summary>The counts of the column's rows taken: the nulls first, then the values by their bytes, an entry each.</summary>
            internal ColumnCounts Counts()
            {
                List<byte[]> values = [.. _entries.Keys];
                values.Sort(static (left, right) => left.AsSpan().SequenceCompareTo(right));
                int entries = values.Count + (_nulls is null ? 0 : 1);
                CountGroup[] groups = new CountGroup[entries];
                long[] words = new long[entries * _width];
                int entry = 0;
                if (_nulls is { } nulls)
                {
                    Close(nulls);
                    nulls.CopyTo(words, 0);
                    groups[entry] = new CountGroup(CountedValue.Null, null, entry, 1);
                    entry++;
                }

                foreach (byte[] value in values)
                {
                    long[] held = _entries[value];
                    Close(held);
                    held.CopyTo(words, entry * _width);
                    groups[entry] = new CountGroup(CountedValue.Value, value, entry, 1);
                    entry++;
                }

                return new ColumnCounts(measureFields, kinds, groups, words);
            }

            private void Close(long[] words)
            {
                int at = 1;
                foreach (MeasureKind kind in kinds)
                {
                    ColumnCountsBuilder.CloseState(words.AsSpan(at, MeasureLayout.Words(kind == MeasureKind.Float)), kind);
                    at += MeasureLayout.Words(kind == MeasureKind.Float);
                }
            }
        }

        /// <summary>Folds row <paramref name="row"/>'s value of the measure node <paramref name="nodeIndex"/> into <paramref name="state"/>: its count, exact sum and extremes.</summary>
        private static void Fold(CanonicalArena arena, int nodeIndex, int row, MeasureKind kind, Span<long> state)
        {
            CanonicalNode node = arena.GetNode(Storage(arena, nodeIndex, row, out bool valid));
            if (!valid)
            {
                return;
            }

            ReadOnlySpan<byte> values = node.Kind == CanonicalKind.Constant ? node.ConstantElement : node.Values.Span;
            int at = node.Kind == CanonicalKind.Constant ? 0 : row;
            int width = node.PType.ByteWidth();
            state[MeasureLayout.Values]++;
            if (kind == MeasureKind.Float)
            {
                double value = width == 8 ? MemoryMarshal.Read<double>(values[(at * 8)..]) : MemoryMarshal.Read<float>(values[(at * 4)..]);
                WideIndexedSum sum = StateWords.SumOf(state, MeasureLayout.Sum);
                sum.Add(value);
                StateWords.Write(sum, state, MeasureLayout.Sum);
                state[MeasureLayout.FloatLeast] = BitConverter.DoubleToInt64Bits(Math.Min(BitConverter.Int64BitsToDouble(state[MeasureLayout.FloatLeast]), value));
                state[MeasureLayout.FloatGreatest] = BitConverter.DoubleToInt64Bits(Math.Max(BitConverter.Int64BitsToDouble(state[MeasureLayout.FloatGreatest]), value));
                return;
            }

            long integer = CountsPlan.Integer(values, width, at, kind == MeasureKind.Signed);
            Int128 total = StateWords.IntegerOf(state, MeasureLayout.Sum) + (kind == MeasureKind.Signed ? integer : (Int128)(ulong)integer);
            StateWords.Write(total, state, MeasureLayout.Sum);
            (state[MeasureLayout.IntegerLeast], state[MeasureLayout.IntegerGreatest]) = kind == MeasureKind.Signed
                ? (Math.Min(state[MeasureLayout.IntegerLeast], integer), Math.Max(state[MeasureLayout.IntegerGreatest], integer))
                : ((long)Math.Min((ulong)state[MeasureLayout.IntegerLeast], (ulong)integer), (long)Math.Max((ulong)state[MeasureLayout.IntegerGreatest], (ulong)integer));
        }

        /// <summary>The storage node under the extensions of <paramref name="nodeIndex"/>, and whether row <paramref name="row"/> holds a value.</summary>
        private static int Storage(CanonicalArena arena, int nodeIndex, int row, out bool valid)
        {
            CanonicalNode outer = arena.GetNode(nodeIndex);
            int storage = nodeIndex;
            CanonicalNode node = outer;
            while (node.Kind == CanonicalKind.Extension)
            {
                storage = node.StorageIndex;
                node = arena.GetNode(storage);
            }

            ValidityMask own = ValidityMask.From(arena, node.Validity);
            ValidityMask wrapper = ValidityMask.From(arena, outer.Validity);
            valid = !own.AllInvalid && !wrapper.AllInvalid && (own.AllValid || own.IsValid(row)) && (wrapper.AllValid || wrapper.IsValid(row));
            return storage;
        }

        /// <summary>
        /// The bytes of row <paramref name="row"/>'s value of the node <paramref name="nodeIndex"/> of <paramref name="dtype"/>, as the
        /// column stores them; false for a null.
        /// </summary>
        private static bool TryBytes(CanonicalArena arena, int nodeIndex, int row, DType dtype, out ReadOnlySpan<byte> bytes)
        {
            CanonicalNode node = arena.GetNode(Storage(arena, nodeIndex, row, out bool valid));
            if (!valid)
            {
                bytes = default;
                return false;
            }

            bytes = node.Kind switch
            {
                CanonicalKind.Constant => node.ConstantElement,
                CanonicalKind.Primitive => node.Values.Span.Slice(row * node.PType.ByteWidth(), node.PType.ByteWidth()),
                CanonicalKind.VarBinView => BloomBuilder.ValueAt(node.Views.Span, node.DataBuffers, row),
                _ => throw new NotSupportedException($"A counted column of {dtype} holds a {node.Kind} array, whose values no counts key."),
            };
            return true;
        }
    }
}
