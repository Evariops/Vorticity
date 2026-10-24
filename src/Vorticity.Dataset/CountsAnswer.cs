using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.Keys;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Dataset;

/// <summary>
/// How the objects' counts (<see cref="ColumnCounts"/>) answer an aggregation that asks for states
/// (<see cref="StateRequest"/>) in place of its rows: a question on one column and the measures. Its keys read the column,
/// functions of it, and other columns or functions of them that an object holds constant; its aggregates count rows, sum,
/// take the extremes or the mean of a measure the counts carry, take a quantile or a distribution of one they sketch, or
/// count the column's distinct values; its filter asks the column for values by equality or a set. Worked out once a
/// question, with the columns of its batches of states; object by object, it names the column whose counts answer
/// (<see cref="FieldOf"/>): an object whose summaries prove the filter's other conjuncts true over its every row and the
/// other keys constant, with no row a delete marked, which its counts still hold. Any other object is read as rows.
/// </summary>
/// <remarks>
/// Nothing here knows a time: a window on an instant is a conjunct like any, which an object's summaries prove true over
/// its rows or not, and a truncation of an instant a function like any, constant over an object or not. The objects the
/// application cuts by its values (<see cref="ClusteringOptions"/>) answer whole the questions that keep to those values.
/// </remarks>
internal sealed class CountsAnswer
{
    // The batches' columns, the same for every object: each key's column once, the rows, each measure's states, then the
    // sketches a quantile asks, in a type arena of the answer's own.
    private readonly DTypeArena _types = new DTypeArena();

    private readonly StateRequest _request;

    // The version's columns, which an object's counts answer for when it holds them in their places.
    private readonly DatasetSchema _schema;

    // The conjuncts of the filter the counts do not answer, which an object's summaries prove true over its rows or the
    // object is read as rows; null for none.
    private readonly SummaryPruner? _rest;

    // The keys of another column than the counted one, each its column and the key itself, a function of it or the column,
    // which an object's summaries prove constant or the object is read as rows.
    private readonly (int Field, string Path, FieldExpr Key)[] _constants;

    private CountsAnswer(
        StateRequest request, DatasetSchema version, int? column, bool keyed, byte[][]? values, VortexExpr? rest,
        (int Field, string Path, FieldExpr Key)[] constants)
    {
        VortexSchema columns = version.Columns;
        DType schema = version.DType;
        _request = request;
        _schema = version;
        Column = column;
        Keyed = keyed;
        Values = values;
        _rest = rest is null ? null : new SummaryPruner(rest);
        _constants = constants;
        (KeyFields, Measures, Type) = Layout(request, columns, schema, _types);
        Bool = _types.Bool(Nullability.NonNullable);
        int sketches = 0;
        foreach ((_, _, _, bool sketch) in Measures)
        {
            sketches += sketch ? 1 : 0;
        }

        AsksSketches = sketches > 0;

        // A key's column a node, its validity's and its extension's at most; a word a node; a sketch's two; the struct.
        Nodes = (3 * KeyFields.Length) + (Type.FieldCount - KeyFields.Length - sketches) + (2 * sketches) + 1;
    }

    /// <summary>The column a key or the filter reads, whose counts answer; null for a question of no column, which any column's counts answer.</summary>
    internal int? Column { get; }

    /// <summary>Whether a key or a distinct count reads the column's values, which the entries of the values a budget left out do not give.</summary>
    internal bool Keyed { get; }

    /// <summary>The values an equality or a set asks of the column, each by its bytes as the column stores them, each once; null for every value.</summary>
    internal byte[][]? Values { get; }

    /// <summary>Whether the request asks a quantile or a distribution, which the counts' sketches answer: they are read beside them.</summary>
    internal bool AsksSketches { get; }

    /// <summary>The top-level column of each key's column of the batches, each once, in their order.</summary>
    internal int[] KeyFields { get; }

    /// <summary>Each measure the request reads, once, in its order: its name, its column, whether a float, and whether a quantile asks its sketch.</summary>
    internal (string Name, int Field, bool Floats, bool Sketch)[] Measures { get; }

    /// <summary>The type of the batches of states: a struct of their columns.</summary>
    internal DType Type { get; }

    /// <summary>The type of a validity's bits, in the answer's arena.</summary>
    internal DType Bool { get; }

    /// <summary>The nodes a batch of states adds to its arena at most.</summary>
    internal int Nodes { get; }

    /// <summary>
    /// How the objects' counts answer <paramref name="request"/> under <paramref name="filter"/> over the columns of
    /// <paramref name="version"/>; null when they answer no object of it: a key of two columns that are no function of
    /// each other, a key, a value or a measure on a path, or a measure no column is. Every object is then read as rows.
    /// </summary>
    internal static CountsAnswer? Of(StateRequest request, VortexExpr? filter, DatasetSnapshot version)
    {
        DType schema = version.Schema.DType;
        if (schema.IsDefault)
        {
            return null;
        }

        // The filter: values of one column by equality or a set, which its counts answer; every other conjunct, which an
        // object's summaries prove true over its rows.
        int? column = null;
        List<byte[]>? values = null;
        List<VortexExpr> rest = [];
        if (filter is not null)
        {
            List<VortexExpr> conjuncts = [];
            GroupPredicates.Conjuncts(filter, conjuncts);
            foreach (VortexExpr conjunct in conjuncts)
            {
                (FieldExpr? asked, FilterLiteral[]? literals) = conjunct switch
                {
                    ComparisonExpr { Op: ComparisonOp.Equal } equal => (equal.Field, [equal.Value]),
                    InExpr set => (set.Field, set.Literals),
                    _ => (null, null),
                };
                if (asked is not null && Top(asked, schema) is int field && (column is null || column == field)
                    && ValuesOf(literals!, schema.GetField(field)) is { } wanted)
                {
                    column = field;
                    values = values is null ? wanted : values.FindAll(value => wanted.Exists(other => value.AsSpan().SequenceEqual(other)));
                    continue;
                }

                rest.Add(conjunct);
            }
        }

        // The keys: a column itself is the one counted, the first met when the filter asks none; another column, or a
        // function of one, each object holds constant; a function of the counted column, its counts give.
        List<(int Field, string Path, FieldExpr Key)> constants = [];
        foreach (FieldExpr key in request.Keys)
        {
            if (key is not ColumnFieldExpr)
            {
                continue;
            }

            if (Top(key, schema) is not int field)
            {
                return null;
            }

            if (column is null || column == field)
            {
                column = field;
                continue;
            }

            constants.Add((field, key.Path, key));
        }

        foreach (FieldExpr key in request.Keys)
        {
            FieldExpr inner = key;
            while (inner is FunctionFieldExpr function)
            {
                inner = function.Column;
            }

            if (ReferenceEquals(inner, key))
            {
                continue;
            }

            if (Top(inner, schema) is not int field)
            {
                return null;
            }

            if (field != column)
            {
                constants.Add((field, inner.Path, key));
            }
        }

        // The measures: top-level columns, which the counts carry as numbers.
        foreach (FieldExpr? input in request.InputFields)
        {
            if (input is not null && Top(input, schema) is null)
            {
                return null;
            }
        }

        bool keyed = false;
        foreach (FieldExpr key in request.Keys)
        {
            keyed |= Top(Inner(key), schema) is int field && field == column;
        }

        return new CountsAnswer(
            request, version.Schema, column, keyed, values?.ToArray(), GroupPredicates.And(null, rest), [.. constants]);
    }

    /// <summary>
    /// The column whose counts answer for the object <paramref name="entry"/> names: the column asked, or for a question of
    /// no column the one whose counts' region is least; null when its rows are read: rows a delete marked, which its counts
    /// still hold, a conjunct or a key its summaries do not prove true over every row or constant, no counts of the
    /// column, or columns of an earlier schema, which its counts name by their places.
    /// </summary>
    internal int? FieldOf(ObjectEntry entry)
    {
        if (entry.HasDeletions || entry.Contents is not { Counts.Count: > 0 } contents || !_schema.Holds(entry)
            || (_rest is { } rest && !rest.MustMatch(entry.Summaries.Columns, entry.PhysicalRows)))
        {
            return null;
        }

        foreach ((_, string path, FieldExpr key) in _constants)
        {
            if (ConstantOf(entry.Summaries, path, key) is null)
            {
                return null;
            }
        }

        int counted = Column ?? Least(contents.Counts);
        return contents.CountsOf(counted) is null ? null : counted;
    }

    /// <summary>
    /// The value an object's column <paramref name="path"/> holds that makes <paramref name="key"/> constant over its rows,
    /// as its summaries prove it: the least, when the key's image of it is its greatest's and the column holds no null;
    /// null when they prove no such thing. Bounds that are not exact lie outwards, which keeps the proof: a non-decreasing
    /// function sends every value between them between their images.
    /// </summary>
    private static FilterLiteral? ConstantOf(ObjectSummaries summaries, string path, FieldExpr key)
    {
        if (!summaries.TryGet(path, out ColumnSummary column) || !column.HasMin || !column.HasMax || !column.HasNullCount || column.NullCount != 0
            || Image(key, column.Min) is not { } low || Image(key, column.Max) is not { } high || KeyCursor.Compare(low, high) != 0)
        {
            return null;
        }

        return column.Min;
    }

    /// <summary>The image of <paramref name="value"/> through the functions of <paramref name="key"/>, the innermost first; null when one does not read it.</summary>
    private static FilterLiteral? Image(FieldExpr key, FilterLiteral value)
    {
        if (key is not FunctionFieldExpr function)
        {
            return value;
        }

        return Image(function.Column, value) is { } inner && function.Function.TryMap(inner, out FilterLiteral image) ? image : null;
    }

    /// <summary>The column <paramref name="key"/> reads, under its functions.</summary>
    private static FieldExpr Inner(FieldExpr key)
    {
        while (key is FunctionFieldExpr function)
        {
            key = function.Column;
        }

        return key;
    }

    /// <summary>
    /// The states the counts <paramref name="counts"/> of the object whose summaries are <paramref name="summaries"/> give,
    /// with <paramref name="sketches"/> their sketches when the request asks them: an entry a row of states, those of the
    /// values asked; null when the counts lack a measure the request reads, the sketches a quantile asks, or a value a key
    /// asks the budget left out.
    /// </summary>
    internal StateRows? Rows(ColumnCounts counts, ColumnSketches? sketches, ObjectSummaries summaries)
    {
        // Each measure's place among those the counts carry.
        int[] places = new int[Measures.Length];
        for (int i = 0; i < Measures.Length; i++)
        {
            int place = Array.IndexOf(counts.MeasureFields, Measures[i].Field);
            if (place < 0 || (counts.Kinds[place] == MeasureKind.Float) != Measures[i].Floats)
            {
                return null;
            }

            if (Measures[i].Sketch && (!counts.Sketched || sketches is not { } held || held.Entries != counts.Entries
                || held.Measures != counts.MeasureFields.Length || _request.SketchBits > ColumnCountsBuilder.SketchBits))
            {
                return null;
            }

            places[i] = place;
        }

        // The values a budget left out give no value of their own, which a key or the values asked read.
        foreach (CountGroup group in counts.Groups)
        {
            if (group.Kind == CountedValue.Others && (Keyed || Values is not null))
            {
                return null;
            }
        }

        // The value each constant key's column holds over the object, as the column stores it.
        byte[]?[] constants = new byte[]?[KeyFields.Length];
        foreach ((int field, string path, FieldExpr key) in _constants)
        {
            int at = Array.IndexOf(KeyFields, field);
            if (ConstantOf(summaries, path, key) is not { } value || ValuesOf([value], _schema.DType.GetField(field)) is not [{ } bytes, ..])
            {
                return null;
            }

            constants[at] = bytes;
        }

        // The entries: those of the values asked, or every one.
        int[] entries = new int[counts.Entries];
        int taken = 0;
        if (Values is { } asked)
        {
            foreach (byte[] value in asked)
            {
                if (GroupOf(counts.Groups, value) is { } group)
                {
                    Take(group, entries, ref taken);
                }
            }
        }
        else
        {
            foreach (CountGroup group in counts.Groups)
            {
                Take(group, entries, ref taken);
            }
        }

        return new StateRows(this, counts, sketches, entries, taken, places, constants);
    }

    /// <summary>Takes into <paramref name="entries"/> those of <paramref name="group"/>.</summary>
    private static void Take(CountGroup group, int[] entries, ref int taken)
    {
        for (int entry = group.FirstEntry; entry < group.FirstEntry + group.Entries; entry++)
        {
            entries[taken++] = entry;
        }
    }

    /// <summary>The group of <paramref name="value"/>, searched among the values, which follow the nulls and those left out in the order of their bytes; null for none.</summary>
    private static CountGroup? GroupOf(CountGroup[] groups, byte[] value)
    {
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
                return groups[middle];
            }

            (low, high) = order < 0 ? (middle + 1, high) : (low, middle - 1);
        }

        return null;
    }

    /// <summary>
    /// The batches' columns of <paramref name="request"/> over <paramref name="columns"/>: each key's column once, under the
    /// name a batch of states gives it, the rows, each measure's states, then the sketches a quantile asks, typed in
    /// <paramref name="types"/>.
    /// </summary>
    private static (int[] KeyFields, (string Name, int Field, bool Floats, bool Sketch)[] Measures, DType Type) Layout(
        StateRequest request, VortexSchema columns, DType schema, DTypeArena types)
    {
        List<int> keyFields = [];
        List<VortexField> fields = [];
        foreach (FieldExpr key in request.Keys)
        {
            FieldExpr inner = Inner(key);
            int field = columns.IndexOf(inner.Path);
            if (!keyFields.Contains(field))
            {
                keyFields.Add(field);
                fields.Add(new VortexField(StateRequest.KeyColumn(inner.Path), columns[field].Type.Nullable));
            }
        }

        fields.Add(new VortexField(StateRequest.RowsColumn, VortexType.Int64));
        List<(string Name, int Field, bool Floats, bool Sketch)> measures = [];
        for (int i = 0; i < request.Inputs.Count; i++)
        {
            if (request.Inputs[i] is not { } name)
            {
                continue;
            }

            bool sketch = request.Kinds[i] is AggregateKind.Distribution or AggregateKind.Quantile;
            int at = measures.FindIndex(measure => string.Equals(measure.Name, name, StringComparison.Ordinal));
            if (at >= 0)
            {
                measures[at] = measures[at] with { Sketch = measures[at].Sketch || sketch };
                continue;
            }

            int field = columns.IndexOf(name);
            measures.Add((name, field, schema.GetField(field) is { Kind: DTypeKind.Primitive, PType: PType.F32 or PType.F64 }, sketch));
        }

        foreach ((string name, _, bool floats, _) in measures)
        {
            fields.Add(new VortexField(StateRequest.ValuesOf(name), VortexType.Int64));
            foreach (string word in MeasureLayout.SumWords(floats))
            {
                fields.Add(new VortexField(StateRequest.SumWord(name, word), VortexType.Int64));
            }

            fields.Add(new VortexField(StateRequest.LeastOf(name), VortexType.Int64));
            fields.Add(new VortexField(StateRequest.GreatestOf(name), VortexType.Int64));
        }

        foreach ((string name, _, _, bool sketch) in measures)
        {
            if (sketch)
            {
                fields.Add(new VortexField(StateRequest.SketchOf(name), VortexType.Binary.Nullable));
            }
        }

        return ([.. keyFields], [.. measures], VortexTypes.ToDType(VortexSchema.Create([.. fields]), types));
    }

    /// <summary>The top-level column <paramref name="field"/> reads as it is, or null for a path, a function or a column the schema lacks.</summary>
    private static int? Top(FieldExpr field, DType schema) =>
        field is ColumnFieldExpr && !field.Path.Contains('.', StringComparison.Ordinal) && schema.IndexOfField(field.Path) is int index and >= 0
            ? index
            : null;

    /// <summary>The column of the least counts region among <paramref name="regions"/>.</summary>
    private static int Least(IReadOnlyList<(int Field, IndexSegment Region)> regions)
    {
        (int field, long bytes) least = (regions[0].Field, long.MaxValue);
        foreach ((int field, IndexSegment region) in regions)
        {
            least = region.Length < least.bytes ? (field, region.Length) : least;
        }

        return least.field;
    }

    /// <summary>The bytes of each of <paramref name="literals"/> as a column of <paramref name="dtype"/> stores them, each once; null for one it cannot hold so.</summary>
    private static List<byte[]>? ValuesOf(FilterLiteral[] literals, DType dtype)
    {
        if (!KeyLayout.TryOf(dtype, out KeyLayout layout))
        {
            return null;
        }

        List<byte[]> values = [];
        Span<byte> scratch = stackalloc byte[sizeof(ulong)];
        Span<byte> zero = stackalloc byte[sizeof(ulong)];
        foreach (FilterLiteral literal in literals)
        {
            if (literal.Kind == FilterLiteralKind.Null || !layout.TryEncode(literal, scratch, out ReadOnlySpan<byte> key, zero, out bool hasOtherZero))
            {
                return null;
            }

            Add(values, key);
            if (hasOtherZero)
            {
                Add(values, zero[..layout.Width]);
            }
        }

        return values;

        static void Add(List<byte[]> values, ReadOnlySpan<byte> value)
        {
            foreach (byte[] held in values)
            {
                if (value.SequenceEqual(held))
                {
                    return;
                }
            }

            values.Add(value.ToArray());
        }
    }
}

/// <summary>
/// An object's entries of the counts that answer, as batches of states (<see cref="RecordBatch.CarriesStates"/>): a row an
/// entry, its keys' columns, the column's value and the values the object holds constant, its rows, and each measure's
/// states and sketch under the names <see cref="StateRequest"/> gives them, in the columns the answer lays out.
/// </summary>
internal sealed class StateRows
{
    /// <summary>The rows of states a batch holds at most.</summary>
    internal const int BatchRows = 1 << 14;

    private readonly CountsAnswer _answer;
    private readonly ColumnCounts _counts;
    private readonly ColumnSketches? _sketches;
    private readonly int[] _entries;

    // Each measure's place among the counts', and its first word among an entry's.
    private readonly int[] _places;
    private readonly int[] _wordAt;

    // The value each key's column holds over the object, as the column stores it, for a column the object holds constant;
    // null for the counted column, whose entries give their own.
    private readonly byte[]?[] _constants;

    internal StateRows(CountsAnswer answer, ColumnCounts counts, ColumnSketches? sketches, int[] entries, int count, int[] places, byte[]?[] constants)
    {
        _answer = answer;
        _counts = counts;
        _sketches = sketches;
        _entries = entries;
        Count = count;
        _places = places;
        _constants = constants;
        int[] offsets = new int[counts.MeasureFields.Length];
        for (int m = 0, at = 1; m < offsets.Length; m++)
        {
            offsets[m] = at;
            at += MeasureLayout.Words(counts.Kinds[m] == MeasureKind.Float);
        }

        _wordAt = new int[places.Length];
        for (int i = 0; i < places.Length; i++)
        {
            _wordAt[i] = offsets[places[i]];
        }
    }

    /// <summary>The rows of states: an entry each.</summary>
    internal int Count { get; }

    /// <summary>The batch of states of entries <c>[start, start + count)</c> of the answer, built in <paramref name="arena"/>, its rows counted from <paramref name="firstRow"/>.</summary>
    internal RecordBatch Batch(CanonicalArena arena, int start, int count, long firstRow)
    {
        ReadOnlySpan<int> entries = _entries.AsSpan(start, count);
        DType type = _answer.Type;
        Span<int> nodes = stackalloc int[type.FieldCount];
        int at = 0;
        for (int k = 0; k < _answer.KeyFields.Length; k++)
        {
            nodes[at] = Bytes(arena, type.GetField(at), entries, -1, _constants[k]);
            at++;
        }

        nodes[at] = Words(arena, type.GetField(at), entries, 0);
        at++;
        for (int i = 0; i < _places.Length; i++)
        {
            for (int word = 0; word < MeasureLayout.Words(_answer.Measures[i].Floats); word++)
            {
                nodes[at] = Words(arena, type.GetField(at), entries, _wordAt[i] + word);
                at++;
            }
        }

        for (int i = 0; i < _places.Length; i++)
        {
            if (_answer.Measures[i].Sketch)
            {
                nodes[at] = Bytes(arena, type.GetField(at), entries, _places[i], null);
                at++;
            }
        }

        int root = arena.AddStruct(type, count, Validity.NonNullable, nodes);
        return RecordBatch.OfStates(arena, root, firstRow);
    }

    /// <summary>A column of one word of each entry's states, the word <paramref name="word"/> of its words.</summary>
    private int Words(CanonicalArena arena, DType dtype, ReadOnlySpan<int> entries, int word)
    {
        VortexBuffer buffer = arena.AllocateUninitialized(entries.Length * sizeof(long), sizeof(long), out Span<byte> bytes);
        Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
        for (int i = 0; i < entries.Length; i++)
        {
            values[i] = _counts.WordsOf(entries[i])[word];
        }

        return arena.AddPrimitive(dtype, entries.Length, Validity.NonNullable, PType.I64, buffer);
    }

    /// <summary>
    /// A column of each entry's bytes, null for none: <paramref name="constant"/> for every entry when there is one, else
    /// the counted column's value as it stores it for <paramref name="place"/> -1, the sketch of the measure at
    /// <paramref name="place"/> among the counts' otherwise; fixed-width values in a buffer, texts and binaries as views,
    /// the long ones in one data buffer.
    /// </summary>
    private int Bytes(CanonicalArena arena, DType dtype, ReadOnlySpan<int> entries, int place, byte[]? constant)
    {
        DType storage = Storage(dtype);
        bool nulls = false;
        int data = 0;
        foreach (int entry in entries)
        {
            byte[]? value = constant ?? BytesOf(entry, place);
            nulls |= value is null;
            data += value is { Length: > CanonicalSupport.MaxInlineViewLength } ? value.Length : 0;
        }

        Validity validity = Valid(storage);
        if (nulls)
        {
            VortexBuffer bits = arena.Allocate((entries.Length + 7) / 8, 1, out Span<byte> set);
            for (int i = 0; i < entries.Length; i++)
            {
                set[i >> 3] |= (constant ?? BytesOf(entries[i], place)) is null ? (byte)0 : (byte)(1 << (i & 7));
            }

            validity = Validity.Bitmap(arena.AddBool(_answer.Bool, entries.Length, Validity.NonNullable, bits, 0));
        }

        int node;
        if (storage.Kind == DTypeKind.Primitive)
        {
            int width = storage.PType.ByteWidth();
            VortexBuffer buffer = arena.Allocate(entries.Length * width, Math.Max(width, 1), out Span<byte> bytes);
            for (int i = 0; i < entries.Length; i++)
            {
                if ((constant ?? BytesOf(entries[i], place)) is { } value)
                {
                    value.AsSpan(0, Math.Min(width, value.Length)).CopyTo(bytes[(i * width)..]);
                }
            }

            node = arena.AddPrimitive(storage, entries.Length, validity, storage.PType, buffer);
        }
        else
        {
            VortexBuffer views = arena.Allocate(entries.Length * 16, 16, out Span<byte> view);
            VortexBuffer heap = arena.Allocate(Math.Max(data, 1), 1, out Span<byte> into);
            int offset = 0;
            for (int i = 0; i < entries.Length; i++)
            {
                byte[] value = constant ?? BytesOf(entries[i], place) ?? [];
                if (CanonicalSupport.WriteView(view.Slice(i * 16, 16), value, value.Length, 0, offset))
                {
                    value.CopyTo(into[offset..]);
                    offset += value.Length;
                }
            }

            node = arena.AddVarBinView(storage, entries.Length, validity, views, [heap]);
        }

        return Wrap(arena, dtype, node, entries.Length);
    }

    /// <summary>The bytes <see cref="Bytes"/> reads of entry <paramref name="entry"/>: its value for <paramref name="place"/> -1, else its sketch.</summary>
    private byte[]? BytesOf(int entry, int place) => place < 0 ? ValueOf(entry) : _sketches!.SketchOf(entry, place);

    /// <summary>The value of the group entry <paramref name="entry"/> belongs to; null for the nulls'.</summary>
    private byte[]? ValueOf(int entry)
    {
        CountGroup[] groups = _counts.Groups;
        int low = 0;
        int high = groups.Length - 1;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (groups[middle].FirstEntry <= entry)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return groups[low].Value;
    }

    /// <summary>The validity of a column of <paramref name="dtype"/> with no null: every row valid, which a nullable type says so.</summary>
    private static Validity Valid(DType dtype) => dtype.Nullability == Nullability.Nullable ? Validity.AllValid : Validity.NonNullable;

    /// <summary>The type <paramref name="dtype"/> stores its values as, under every extension.</summary>
    private static DType Storage(DType dtype)
    {
        while (dtype.Kind == DTypeKind.Extension)
        {
            dtype = dtype.StorageType;
        }

        return dtype;
    }

    /// <summary>The node <paramref name="storage"/> under every extension <paramref name="dtype"/> wraps it in.</summary>
    private static int Wrap(CanonicalArena arena, DType dtype, int storage, int rows)
    {
        if (dtype.Kind != DTypeKind.Extension)
        {
            return storage;
        }

        return arena.AddExtension(dtype, rows, Wrap(arena, dtype.StorageType, storage, rows));
    }
}
