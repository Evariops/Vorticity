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
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Dataset;

/// <summary>
/// How the objects' counts by hour (<see cref="ColumnCounts"/>) answer an aggregation that asks for states
/// (<see cref="StateRequest"/>) in place of its rows: a question on one column, the time and the measures. Its keys read
/// the column, functions of it, and the instant truncated to the hour or coarser on the stored calendar; its aggregates
/// count rows, sum, take the extremes or the mean of a measure the counts carry, take a quantile or a distribution of one
/// they sketch, or count the column's distinct values; its filter bounds the instant and asks the column for values by
/// equality or a set. The hours the filter's window holds whole come as states; those it cuts, the edges, are read as
/// rows. Worked out once a question, with the columns of its batches of states; object by object, it names the column
/// whose counts answer (<see cref="FieldOf"/>), and an object with rows a delete marked, which its counts still hold, is
/// read as rows.
/// </summary>
internal sealed class CountsAnswer
{
    // The batches' columns, the same for every object: each key's column once, the rows, each measure's states, then the
    // sketches a quantile asks, in a type arena of the answer's own.
    private readonly DTypeArena _types = new DTypeArena();

    private readonly StateRequest _request;

    private CountsAnswer(
        StateRequest request, VortexSchema columns, DType schema, string instant, long unitsPerHour, int? column, bool keyed, long low,
        long high, bool windowed, byte[][]? values, VortexExpr? edges)
    {
        _request = request;
        UnitsPerHour = unitsPerHour;
        InstantField = columns.IndexOf(instant);
        Column = column;
        Keyed = keyed;
        Low = low;
        High = high;
        Windowed = windowed;
        Values = values;
        Edges = edges;
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

    /// <summary>
    /// The period's instant of a dataset in periods and an hour in its units, by which its sealed objects count their
    /// columns; null for a dataset of no periods, whose objects keep no counts.
    /// </summary>
    internal static (string Instant, long UnitsPerHour)? Counting(DatasetSnapshot version)
    {
        if (version.Header.PeriodLength is null || version.Schema.Key is not { } key || key.InstantPath.Contains('.'))
        {
            return null;
        }

        return (key.InstantPath, VortexTypes.FromDType(key.InstantType).Unit switch
        {
            TimeUnit.Seconds => 3_600L,
            TimeUnit.Milliseconds => 3_600_000L,
            TimeUnit.Nanoseconds => 3_600_000_000_000L,
            _ => 3_600_000_000L,
        });
    }

    /// <summary>The column a key or the filter reads; null for a question of time alone, which any column's counts answer.</summary>
    internal int? Column { get; }

    /// <summary>Whether a key or a distinct count reads the column's values, which the entries of the values a budget left out do not give.</summary>
    internal bool Keyed { get; }

    /// <summary>The instant's first value the filter keeps, in its units; <see cref="long.MinValue"/> for none.</summary>
    internal long Low { get; }

    /// <summary>The instant's last value the filter keeps, in its units; <see cref="long.MaxValue"/> for none.</summary>
    internal long High { get; }

    /// <summary>Whether the filter bounds the instant: a row whose instant is null is then kept by none.</summary>
    internal bool Windowed { get; }

    /// <summary>The values an equality or a set asks of the column, each by its bytes as the column stores them, each once; null for every value.</summary>
    internal byte[][]? Values { get; }

    /// <summary>The filter of the object's rows the states leave to the rows, those of the hours the window cuts; null when it cuts none.</summary>
    internal VortexExpr? Edges { get; }

    /// <summary>Whether the request asks a quantile or a distribution, which the counts' sketches answer: they are read beside them.</summary>
    internal bool AsksSketches { get; }

    /// <summary>An hour in the instant's units.</summary>
    internal long UnitsPerHour { get; }

    /// <summary>The instant's top-level column.</summary>
    internal int InstantField { get; }

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
    /// <paramref name="version"/>; null when they answer no object of it, a dataset of no periods, a key or a filter they
    /// do not hold, or a measure no column is, and every object is read as rows.
    /// </summary>
    internal static CountsAnswer? Of(StateRequest request, VortexExpr? filter, DatasetSnapshot version)
    {
        if (Counting(version) is not (string instant, long unitsPerHour))
        {
            return null;
        }

        DType schema = version.Schema.DType;

        // The keys: the column, functions of it, and the instant truncated to the hour or coarser.
        int? column = null;
        foreach (FieldExpr key in request.Keys)
        {
            FieldExpr inner = key;
            ValueFunction? first = null;
            while (inner is FunctionFieldExpr function)
            {
                first = function.Function;
                inner = function.Column;
            }

            if (Top(inner, schema) is not int field)
            {
                return null;
            }

            if (string.Equals(inner.Path, instant, StringComparison.Ordinal))
            {
                if (first is not InstantTruncate { Unit: >= CalendarUnit.Hour, Zoned: false })
                {
                    return null;
                }

                continue;
            }

            if (column is { } held && held != field)
            {
                return null;
            }

            column = field;
        }

        // The measures: top-level columns, which the counts carry as numbers.
        foreach (FieldExpr? input in request.InputFields)
        {
            if (input is not null && Top(input, schema) is null)
            {
                return null;
            }
        }

        // The filter: bounds of the instant, and values of the column by equality or a set.
        bool keyed = column is not null;
        long low = long.MinValue;
        long high = long.MaxValue;
        bool windowed = false;
        List<byte[]>? values = null;
        if (filter is not null)
        {
            List<VortexExpr> conjuncts = [];
            GroupPredicates.Conjuncts(filter, conjuncts);
            foreach (VortexExpr conjunct in conjuncts)
            {
                if (DatasetScanBuilder.Bound(conjunct, instant, ref low, ref high))
                {
                    windowed = true;
                    continue;
                }

                (FieldExpr? asked, FilterLiteral[]? literals) = conjunct switch
                {
                    ComparisonExpr { Op: ComparisonOp.Equal } equal => (equal.Field, [equal.Value]),
                    InExpr set => (set.Field, set.Literals),
                    _ => (null, null),
                };
                if (asked is null || Top(asked, schema) is not int field || (column is { } held && held != field)
                    || string.Equals(asked.Path, instant, StringComparison.Ordinal) || ValuesOf(literals!, schema.GetField(field)) is not { } wanted)
                {
                    return null;
                }

                column = field;
                values = values is null ? wanted : values.FindAll(value => wanted.Exists(other => value.AsSpan().SequenceEqual(other)));
            }
        }

        return new CountsAnswer(
            request, version.Schema.Columns, schema, instant, unitsPerHour, column, keyed, low, high, windowed, values?.ToArray(),
            EdgesOf(filter, instant, low, high, windowed, unitsPerHour));
    }

    /// <summary>
    /// The column whose counts answer for the object <paramref name="entry"/> names: the column asked, or for a question of
    /// time alone the one whose counts' region is least; null when its rows are read, rows a delete marked, which its
    /// counts still hold, or no counts of the column.
    /// </summary>
    internal int? FieldOf(ObjectEntry entry)
    {
        if (entry.HasDeletions || entry.Contents is not { Counts.Count: > 0 } contents)
        {
            return null;
        }

        int counted = Column ?? Least(contents.Counts);
        return contents.CountsOf(counted) is null ? null : counted;
    }

    /// <summary>
    /// The states the counts <paramref name="counts"/> give, with <paramref name="sketches"/> their sketches when the
    /// request asks them: an entry a row of states, of the hours the window holds whole and the values asked; null when
    /// the counts lack a measure the request reads, the sketches a quantile asks, or a value a key asks the budget left out.
    /// </summary>
    internal StateRows? Rows(ColumnCounts counts, ColumnSketches? sketches)
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

        // The entries: those of the values asked, in the hours the window holds whole; the hours it cuts are read as rows.
        int[] entries = new int[counts.Entries];
        int taken = 0;
        bool cuts = false;
        if (Values is { } asked)
        {
            foreach (byte[] value in asked)
            {
                if (GroupOf(counts.Groups, value) is { } group)
                {
                    cuts |= Take(counts, group, entries, ref taken);
                }
            }
        }
        else
        {
            foreach (CountGroup group in counts.Groups)
            {
                cuts |= Take(counts, group, entries, ref taken);
            }
        }

        return new StateRows(this, counts, sketches, entries, taken, places, cuts);
    }

    /// <summary>Takes into <paramref name="entries"/> those of <paramref name="group"/> in the hours the window holds whole: whether it cuts one.</summary>
    private bool Take(ColumnCounts counts, CountGroup group, int[] entries, ref int taken)
    {
        bool cuts = false;
        for (int entry = group.FirstEntry; entry < group.FirstEntry + group.Entries; entry++)
        {
            long hour = counts.Hours[entry];
            if (hour == CountsPlan.NullHour)
            {
                if (!Windowed)
                {
                    entries[taken++] = entry;
                }

                continue;
            }

            long start = hour * UnitsPerHour;
            long end = start + UnitsPerHour - 1;
            if (start >= Low && end <= High)
            {
                entries[taken++] = entry;
            }
            else if (end >= Low && start <= High)
            {
                cuts = true;
            }
        }

        return cuts;
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
            FieldExpr inner = key;
            while (inner is FunctionFieldExpr function)
            {
                inner = function.Column;
            }

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

    /// <summary>
    /// The filter of the rows the states of a window <c>[low, high]</c> leave, those of the hours it cuts: the
    /// <paramref name="filter"/> and an instant before the end of its first hour or from the start of its last; null when
    /// the window holds its hours whole.
    /// </summary>
    private static VortexExpr? EdgesOf(VortexExpr? filter, string instant, long low, long high, bool windowed, long unitsPerHour)
    {
        if (!windowed || filter is null || low > high)
        {
            return null;
        }

        long firstStart = Floor(low, unitsPerHour);
        long lastStart = Floor(high, unitsPerHour);
        bool cutsFirst = low != long.MinValue && low != firstStart;
        bool cutsLast = high != long.MaxValue && high != lastStart + unitsPerHour - 1;
        VortexExpr? first = cutsFirst ? Expr.Lt(Expr.Field(instant), Expr.Literal(FilterLiteral.From(firstStart + unitsPerHour))) : null;
        VortexExpr? last = cutsLast ? Expr.Ge(Expr.Field(instant), Expr.Literal(FilterLiteral.From(lastStart))) : null;
        return (first, last) switch
        {
            (null, null) => null,
            ({ } before, null) => Expr.And(filter, before),
            (null, { } after) => Expr.And(filter, after),
            ({ } before, { } after) => Expr.And(filter, Expr.Or(before, after)),
        };
    }

    /// <summary>The start of the hour <paramref name="instant"/> falls in.</summary>
    internal static long Floor(long instant, long unitsPerHour) =>
        (instant >= 0 ? instant / unitsPerHour : ((instant + 1) / unitsPerHour) - 1) * unitsPerHour;
}

/// <summary>
/// An object's entries of the counts that answer, as batches of states (<see cref="RecordBatch.CarriesStates"/>): a row an
/// entry, its keys' columns, the column's value and the hour's start, its rows, and each measure's states and sketch
/// under the names <see cref="StateRequest"/> gives them, in the columns the answer lays out.
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

    internal StateRows(CountsAnswer answer, ColumnCounts counts, ColumnSketches? sketches, int[] entries, int count, int[] places, bool cuts)
    {
        _answer = answer;
        _counts = counts;
        _sketches = sketches;
        _entries = entries;
        Count = count;
        _places = places;
        CutsHours = cuts;
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

    /// <summary>Whether the window cuts one of the object's hours, whose rows are then read.</summary>
    internal bool CutsHours { get; }

    /// <summary>The batch of states of entries <c>[start, start + count)</c> of the answer, built in <paramref name="arena"/>, its rows counted from <paramref name="firstRow"/>.</summary>
    internal RecordBatch Batch(CanonicalArena arena, int start, int count, long firstRow)
    {
        ReadOnlySpan<int> entries = _entries.AsSpan(start, count);
        DType type = _answer.Type;
        Span<int> nodes = stackalloc int[type.FieldCount];
        int at = 0;
        foreach (int field in _answer.KeyFields)
        {
            nodes[at] = field == _answer.InstantField ? Hours(arena, type.GetField(at), entries) : Bytes(arena, type.GetField(at), entries, -1);
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
                nodes[at] = Bytes(arena, type.GetField(at), entries, _places[i]);
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

    /// <summary>The instant's column: each entry's hour's start, null for the hour of a null instant.</summary>
    private int Hours(CanonicalArena arena, DType dtype, ReadOnlySpan<int> entries)
    {
        VortexBuffer buffer = arena.AllocateUninitialized(entries.Length * sizeof(long), sizeof(long), out Span<byte> bytes);
        Span<long> starts = MemoryMarshal.Cast<byte, long>(bytes);
        bool nulls = false;
        for (int i = 0; i < entries.Length; i++)
        {
            long hour = _counts.Hours[entries[i]];
            nulls |= hour == CountsPlan.NullHour;
            starts[i] = hour == CountsPlan.NullHour ? 0 : hour * _answer.UnitsPerHour;
        }

        DType storage = Storage(dtype);
        Validity validity = Valid(storage);
        if (nulls)
        {
            VortexBuffer bits = arena.Allocate((entries.Length + 7) / 8, 1, out Span<byte> set);
            for (int i = 0; i < entries.Length; i++)
            {
                set[i >> 3] |= _counts.Hours[entries[i]] == CountsPlan.NullHour ? (byte)0 : (byte)(1 << (i & 7));
            }

            validity = Validity.Bitmap(arena.AddBool(_answer.Bool, entries.Length, Validity.NonNullable, bits, 0));
        }

        return Wrap(arena, dtype, arena.AddPrimitive(storage, entries.Length, validity, PType.I64, buffer), entries.Length);
    }

    /// <summary>
    /// A column of each entry's bytes, null for none: the counted column's value as it stores it for <paramref name="place"/>
    /// -1, the sketch of the measure at <paramref name="place"/> among the counts' otherwise; fixed-width values in a buffer,
    /// texts and binaries as views, the long ones in one data buffer.
    /// </summary>
    private int Bytes(CanonicalArena arena, DType dtype, ReadOnlySpan<int> entries, int place)
    {
        DType storage = Storage(dtype);
        bool nulls = false;
        int data = 0;
        foreach (int entry in entries)
        {
            byte[]? value = BytesOf(entry, place);
            nulls |= value is null;
            data += value is { Length: > CanonicalSupport.MaxInlineViewLength } ? value.Length : 0;
        }

        Validity validity = Valid(storage);
        if (nulls)
        {
            VortexBuffer bits = arena.Allocate((entries.Length + 7) / 8, 1, out Span<byte> set);
            for (int i = 0; i < entries.Length; i++)
            {
                set[i >> 3] |= BytesOf(entries[i], place) is null ? (byte)0 : (byte)(1 << (i & 7));
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
                if (BytesOf(entries[i], place) is { } value)
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
                byte[] value = BytesOf(entries[i], place) ?? [];
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
