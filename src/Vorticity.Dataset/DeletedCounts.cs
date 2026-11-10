using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
    /// (<see cref="WideIndexedSum.TryTakeOut"/>). The extremes stay those of every row, which no question reads of these;
    /// so do the measures <paramref name="taken"/> does not carry, for a question that reads none of them.
    /// </summary>
    internal static ColumnCounts? Less(ColumnCounts counts, ColumnCounts taken)
    {
        if (counts.Buckets != 0 || Offsets(counts, taken) is not { } offsets)
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
            if (entry < 0 || !TakeOut(words.AsSpan(entry * width, width), taken.WordsOf(group.FirstEntry), taken.Kinds, offsets))
            {
                return null;
            }
        }

        // Every entry left with rows, each group one entry in its place, as a value left out of a question leaves them: the
        // copy is the counts, its groups the object's, where copying it again and numbering its groups anew doubled what a
        // question of every row but one request's allocated (2026-11-08).
        if (Untouched(counts, words, width))
        {
            return new ColumnCounts(counts.MeasureFields, counts.Kinds, counts.Groups, words);
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

    /// <summary>Whether <paramref name="words"/>, of <paramref name="counts"/>' groups, keep each group one entry in its place, with rows, and nothing past them.</summary>
    private static bool Untouched(ColumnCounts counts, long[] words, int width)
    {
        CountGroup[] groups = counts.Groups;
        if (words.Length != groups.Length * width)
        {
            return false;
        }

        for (int g = 0; g < groups.Length; g++)
        {
            if (groups[g].FirstEntry != g || groups[g].Entries != 1 || words[g * width] <= 0)
            {
                return false;
            }
        }

        return true;
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

    /// <summary>
    /// Where each measure of <paramref name="taken"/> starts among an entry's words of <paramref name="counts"/>, the same
    /// measure summed alike; null when <paramref name="counts"/> carries one of them not, or not so.
    /// </summary>
    private static int[]? Offsets(ColumnCounts counts, ColumnCounts taken)
    {
        int[] offsets = new int[taken.MeasureFields.Length];
        for (int m = 0; m < offsets.Length; m++)
        {
            int place = Array.IndexOf(counts.MeasureFields, taken.MeasureFields[m]);
            if (place < 0 || counts.Kinds[place] != taken.Kinds[m])
            {
                return null;
            }

            offsets[m] = 1;
            for (int p = 0; p < place; p++)
            {
                offsets[m] += MeasureLayout.Words(counts.Kinds[p] == MeasureKind.Float);
            }
        }

        return offsets;
    }

    /// <summary>
    /// Takes the words <paramref name="taken"/>, its measures of <paramref name="kinds"/>, out of an entry's
    /// <paramref name="words"/>, each measure at its offset among them; false when the result would not be the rows kept's
    /// exactly.
    /// </summary>
    private static bool TakeOut(Span<long> words, ReadOnlySpan<long> taken, MeasureKind[] kinds, int[] offsets)
    {
        if (taken[0] > words[0])
        {
            return false;
        }

        words[0] -= taken[0];
        int from = 1;
        for (int m = 0; m < kinds.Length; m++)
        {
            bool floats = kinds[m] == MeasureKind.Float;
            Span<long> into = words[offsets[m]..];
            ReadOnlySpan<long> own = taken[from..];
            from += MeasureLayout.Words(floats);
            if (own[MeasureLayout.Values] > into[MeasureLayout.Values])
            {
                return false;
            }

            into[MeasureLayout.Values] -= own[MeasureLayout.Values];
            if (floats)
            {
                // The top stays when the least and the greatest of every row lie past the taken rows' own: both kept.
                bool stays = BitConverter.Int64BitsToDouble(own[MeasureLayout.FloatLeast]) > BitConverter.Int64BitsToDouble(into[MeasureLayout.FloatLeast])
                    && BitConverter.Int64BitsToDouble(own[MeasureLayout.FloatGreatest]) < BitConverter.Int64BitsToDouble(into[MeasureLayout.FloatGreatest]);
                WideIndexedSum sum = StateWords.SumOf(into, MeasureLayout.Sum);
                if (!sum.TryTakeOut(StateWords.SumOf(own, MeasureLayout.Sum), stays))
                {
                    return false;
                }

                StateWords.Write(sum, into, MeasureLayout.Sum);
            }
            else
            {
                StateWords.Write(StateWords.IntegerOf(into, MeasureLayout.Sum) - StateWords.IntegerOf(own, MeasureLayout.Sum), into, MeasureLayout.Sum);
            }
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
                column.Add(arena, root, batch.RowCount, selection);
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
            // The rows a count takes at a time, their entries found before their measures are folded.
            private const int Tranche = 1024;

            private readonly int _width = StateWords.Count(kinds);
            private readonly Dictionary<byte[], long[]> _entries = new Dictionary<byte[], long[]>(ByteKeys.Instance);
            private long[]? _nulls;

            internal int Field { get; } = field;

            /// <summary>
            /// Counts the rows of the batch whose root is <paramref name="root"/>, of <paramref name="count"/> rows, whose bits
            /// <paramref name="selection"/> sets, every one when it is empty: the column's storage and validity found once a batch,
            /// each row taken found its entry, then each measure folded over the rows taken. A row at a time, each row looked
            /// its column and every measure up again, extensions, storage and validity: 69 ns a row of a column counted with two
            /// measures (2026-11-10).
            /// </summary>
            [SkipLocalsInit]
            internal void Add(CanonicalArena arena, CanonicalNode root, int count, ReadOnlySpan<ulong> selection)
            {
                // The entries of the rows taken and the rows, a tranche at a time: the measures folded over each.
                long[][] entries = System.Buffers.ArrayPool<long[]>.Shared.Rent(Tranche);
                Span<int> rows = stackalloc int[Tranche];
                try
                {
                    AddRows(arena, root, count, selection, entries, rows);
                }
                finally
                {
                    Array.Clear(entries, 0, Tranche);
                    System.Buffers.ArrayPool<long[]>.Shared.Return(entries);
                }
            }

            private void AddRows(CanonicalArena arena, CanonicalNode root, int count, ReadOnlySpan<ulong> selection, long[][] entries, Span<int> rows)
            {
                Stored key = new Stored(arena, root.GetFieldIndex(place));
                CanonicalNode node = key.Node;
                CanonicalKind kind = node.Kind;
                if (kind is not (CanonicalKind.Constant or CanonicalKind.Primitive or CanonicalKind.VarBinView))
                {
                    throw new NotSupportedException($"A counted column of {dtype} holds a {kind} array, whose values no counts key.");
                }

                ReadOnlySpan<byte> values = kind == CanonicalKind.Constant ? node.ConstantElement : kind == CanonicalKind.Primitive ? node.Values.Span : node.Views.Span;
                ReadOnlySpan<VortexBuffer> buffers = kind == CanonicalKind.VarBinView ? node.DataBuffers : default;
                int width = kind == CanonicalKind.Primitive ? node.PType.ByteWidth() : 0;
                Dictionary<byte[], long[]>.AlternateLookup<ReadOnlySpan<byte>> lookup = _entries.GetAlternateLookup<ReadOnlySpan<byte>>();
                int taken = 0;
                int words = (count + 63) >> 6;
                for (int word = 0; word < words; word++)
                {
                    ulong bits = selection.IsEmpty ? ulong.MaxValue : selection[word];
                    if (word == words - 1 && (count & 63) != 0)
                    {
                        bits &= (1UL << (count & 63)) - 1;
                    }

                    while (bits != 0)
                    {
                        int row = (word << 6) + System.Numerics.BitOperations.TrailingZeroCount(bits);
                        bits &= bits - 1;
                        long[] held;
                        if (key.Valid(row))
                        {
                            ReadOnlySpan<byte> value = kind switch
                            {
                                CanonicalKind.Constant => values,
                                CanonicalKind.Primitive => values.Slice(row * width, width),
                                _ => BloomBuilder.ValueAt(values, buffers, row),
                            };
                            if (!lookup.TryGetValue(value, out long[]? found))
                            {
                                found = Opened();
                                lookup[value] = found;
                            }

                            held = found;
                        }
                        else
                        {
                            held = _nulls ??= Opened();
                        }

                        held[0]++;
                        entries[taken] = held;
                        rows[taken++] = row;
                        if (taken == Tranche)
                        {
                            Measures(arena, root, entries.AsSpan(0, taken), rows[..taken]);
                            taken = 0;
                        }
                    }
                }

                Measures(arena, root, entries.AsSpan(0, taken), rows[..taken]);
            }

            /// <summary>Folds each measure of the rows <paramref name="rows"/> into their entries <paramref name="entries"/>.</summary>
            private void Measures(CanonicalArena arena, CanonicalNode root, ReadOnlySpan<long[]> entries, ReadOnlySpan<int> rows)
            {
                int at = 1;
                for (int m = 0; m < measureFields.Length; m++)
                {
                    Fold(new Stored(arena, root.GetFieldIndex(measurePlaces[m])), kinds[m], entries, rows, at);
                    at += MeasureLayout.Words(kinds[m] == MeasureKind.Float);
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

        /// <summary>
        /// Folds into each of <paramref name="taken"/>, the entries of the rows <paramref name="rows"/>, from word
        /// <paramref name="at"/>, the row's value of <paramref name="measure"/>, of kind <paramref name="kind"/>: its count, exact
        /// sum and extremes.
        /// </summary>
        private static void Fold(Stored measure, MeasureKind kind, ReadOnlySpan<long[]> taken, ReadOnlySpan<int> rows, int at)
        {
            CanonicalNode node = measure.Node;
            bool constant = node.Kind == CanonicalKind.Constant;
            ReadOnlySpan<byte> values = constant ? node.ConstantElement : node.Values.Span;
            int width = node.PType.ByteWidth();
            int words = MeasureLayout.Words(kind == MeasureKind.Float);
            for (int i = 0; i < rows.Length; i++)
            {
                int row = rows[i];
                if (!measure.Valid(row))
                {
                    continue;
                }

                Span<long> state = taken[i].AsSpan(at, words);
                int place = constant ? 0 : row;
                state[MeasureLayout.Values]++;
                if (kind == MeasureKind.Float)
                {
                    double value = width == 8 ? MemoryMarshal.Read<double>(values[(place * 8)..]) : MemoryMarshal.Read<float>(values[(place * 4)..]);
                    WideIndexedSum sum = StateWords.SumOf(state, MeasureLayout.Sum);
                    sum.Add(value);
                    StateWords.Write(sum, state, MeasureLayout.Sum);
                    state[MeasureLayout.FloatLeast] = BitConverter.DoubleToInt64Bits(Math.Min(BitConverter.Int64BitsToDouble(state[MeasureLayout.FloatLeast]), value));
                    state[MeasureLayout.FloatGreatest] = BitConverter.DoubleToInt64Bits(Math.Max(BitConverter.Int64BitsToDouble(state[MeasureLayout.FloatGreatest]), value));
                    continue;
                }

                long integer = CountsPlan.Integer(values, width, place, kind == MeasureKind.Signed);
                Int128 total = StateWords.IntegerOf(state, MeasureLayout.Sum) + (kind == MeasureKind.Signed ? integer : (Int128)(ulong)integer);
                StateWords.Write(total, state, MeasureLayout.Sum);
                (state[MeasureLayout.IntegerLeast], state[MeasureLayout.IntegerGreatest]) = kind == MeasureKind.Signed
                    ? (Math.Min(state[MeasureLayout.IntegerLeast], integer), Math.Max(state[MeasureLayout.IntegerGreatest], integer))
                    : ((long)Math.Min((ulong)state[MeasureLayout.IntegerLeast], (ulong)integer), (long)Math.Max((ulong)state[MeasureLayout.IntegerGreatest], (ulong)integer));
            }
        }

        /// <summary>A column of a batch as a count reads it: its storage node under its extensions, and the validity of both, found once.</summary>
        private readonly ref struct Stored
        {
            private readonly ValidityMask _own;
            private readonly ValidityMask _wrapper;
            private readonly bool _none;
            private readonly bool _all;

            internal Stored(CanonicalArena arena, int nodeIndex)
            {
                CanonicalNode outer = arena.GetNode(nodeIndex);
                CanonicalNode node = outer;
                while (node.Kind == CanonicalKind.Extension)
                {
                    node = arena.GetNode(node.StorageIndex);
                }

                Node = node;
                _own = ValidityMask.From(arena, node.Validity);
                _wrapper = ValidityMask.From(arena, outer.Validity);
                _none = _own.AllInvalid || _wrapper.AllInvalid;
                _all = !_none && _own.AllValid && _wrapper.AllValid;
            }

            /// <summary>The storage node.</summary>
            internal CanonicalNode Node { get; }

            /// <summary>Whether row <paramref name="row"/> holds a value: valid in the storage and under its extensions.</summary>
            internal bool Valid(int row) =>
                _all || (!_none && (_own.AllValid || _own.IsValid(row)) && (_wrapper.AllValid || _wrapper.IsValid(row)));
        }
    }
}
