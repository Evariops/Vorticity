using System;
using System.Collections.Generic;
using Vorticity.Aggregating;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.Keys;
using Vorticity.Scanning;
using Vorticity.Types;

namespace Vorticity.Dataset;

/// <summary>
/// How the statistics of an object's columns over its blocks (<see cref="ColumnBlocks"/>), with where their values live
/// (<see cref="ColumnWhere"/>), answer an aggregation that asks for states (<see cref="StateRequest"/>) block by block,
/// where the object's counts do not (<see cref="CountsAnswer"/>). A block whose statistics prove the filter true over its
/// rows, and whose keys each hold one group through their functions, gives its rows and its measures' states: no column of
/// the question varies in it. A block where one column of the question varies, under a question of counts alone, gives
/// the rows of each of its values, the filter judged value by value. Any other block is read as rows: two columns varying,
/// a filter its statistics neither prove nor refute, or a measure its statistics do not hold.
/// </summary>
/// <remarks>
/// A block is judged by the summaries its statistics make of each column: one value, or the least and the greatest of
/// those it lists or of a measure, and its nulls; the columns the block keeps none of are judged by the object's
/// summaries. Nothing here knows a time: a truncation of an instant is a function like any, which holds one group over a
/// block when the instant's extremes there fall in one.
/// </remarks>
internal sealed class BlockAnswer
{
    private readonly DatasetSchema _schema;
    private readonly VortexExpr? _asked;

    // Each key of the request, its column and the key itself, a function of it or the column; the columns of the measures.
    private readonly (int Field, FieldExpr Key)[] _keys;
    private readonly int[] _measured;

    // The key's first column, whose runs the blocks follow, which a block holds one value of but where they are short.
    private readonly int _leading;

    // The filter, judged by summaries, and the columns it reads, top-level ones by their place; the columns whose statistics
    // a block is judged by. Worked out at the first object whose keys may hold one group in a block, which most questions
    // the counts answer never meet.
    private SummaryPruner? _filter;
    private int[] _filtered = [];
    private int[]? _fields;

    private BlockAnswer(CountsAnswer counts, DatasetSchema version, VortexExpr? filter, (int Field, FieldExpr Key)[] keys, int[] measured, int leading)
    {
        _schema = version;
        _asked = filter;
        _keys = keys;
        _measured = measured;
        _leading = leading;
        (KeyFields, Measures, Type, Bool) = (counts.KeyFields, counts.Measures, counts.Type, counts.Bool);
        int width = 1;
        foreach ((_, _, bool floats, _) in Measures)
        {
            width += MeasureLayout.Words(floats);
        }

        Width = width;
        CountsOnly = Measures.Length == 0;
    }

    /// <summary>The columns whose statistics a block is judged by: those of the keys, the filter and the measures, each once.</summary>
    internal int[] Fields => _fields ??= Judged();

    /// <summary>The filter's columns and its pruner, then every column a block is judged by.</summary>
    private int[] Judged()
    {
        List<int> fields = [];
        foreach ((int field, _) in _keys)
        {
            Add(fields, field);
        }

        List<int> filtered = [];
        if (_asked is { } filter)
        {
            _filter = new SummaryPruner(filter);
            foreach (string path in _filter.Paths)
            {
                if (_schema.DType.IndexOfField(path) is int field and >= 0)
                {
                    Add(filtered, field);
                    Add(fields, field);
                }
            }
        }

        foreach (int field in _measured)
        {
            Add(fields, field);
        }

        _filtered = [.. filtered];
        return [.. fields];
    }

    /// <summary>The top-level column of each key's column of the batches, each once, in their order.</summary>
    internal int[] KeyFields { get; }

    /// <summary>Each measure the request reads, once, in its order: its name, its column, whether a float, and whether a quantile asks its sketch.</summary>
    internal (string Name, int Field, bool Floats, bool Sketch)[] Measures { get; }

    /// <summary>The type of the batches of states: a struct of their columns.</summary>
    internal DType Type { get; }

    /// <summary>The type of a validity's bits, in the answer's arena.</summary>
    internal DType Bool { get; }

    /// <summary>The words of a row of states: its rows, then each measure's.</summary>
    internal int Width { get; }

    /// <summary>Whether the request asks counts alone, which the values of a column varying in a block answer.</summary>
    internal bool CountsOnly { get; }

    /// <summary>
    /// How the blocks of the objects answer <paramref name="request"/> under <paramref name="filter"/> over the columns of
    /// <paramref name="version"/>, in the batches of states <paramref name="counts"/> lays out for the same request; null
    /// when they answer no block of any: a quantile or a distribution, whose sketches no block keeps, a distinct count kept
    /// as a sketch, or a key or a measure on a path.
    /// </summary>
    internal static BlockAnswer? Of(StateRequest request, VortexExpr? filter, DatasetSnapshot version, CountsAnswer counts)
    {
        DType schema = version.Schema.DType;
        if (schema.IsDefault || schema.Kind != DTypeKind.Struct)
        {
            return null;
        }

        foreach (AggregateKind kind in request.Kinds)
        {
            if (kind is AggregateKind.Distribution or AggregateKind.Quantile)
            {
                return null;
            }
        }

        (int Field, FieldExpr Key)[] keys = new (int Field, FieldExpr Key)[request.Keys.Count];
        for (int k = 0; k < keys.Length; k++)
        {
            FieldExpr key = request.Keys[k];
            if (request.DistinctColumn(k) is not null || Top(CountsAnswer.Inner(key), schema) is not int field)
            {
                return null;
            }

            keys[k] = (field, key);
        }

        List<int> measured = [];
        foreach (FieldExpr? input in request.InputFields)
        {
            if (input is not null)
            {
                if (Top(input, schema) is not int field)
                {
                    return null;
                }

                Add(measured, field);
            }
        }

        int leading = version.Schema.Key is { } clustering && schema.IndexOfField(clustering.InstantPath) is int first and >= 0 ? first : -1;
        return new BlockAnswer(counts, version.Schema, filter, keys, [.. measured], leading);
    }

    private static void Add(List<int> fields, int field)
    {
        if (!fields.Contains(field))
        {
            fields.Add(field);
        }
    }

    /// <summary>The top-level column <paramref name="field"/> reads as it is, or null for a path, a function or a column the schema lacks.</summary>
    private static int? Top(FieldExpr field, DType schema) =>
        field is ColumnFieldExpr && !field.Path.Contains('.', StringComparison.Ordinal) && schema.IndexOfField(field.Path) is int index and >= 0
            ? index
            : null;

    /// <summary>
    /// Whether the blocks of the object <paramref name="held"/> may answer: no row a delete marked, which its statistics
    /// still count, the version's columns in their places, and statistics of every column a key or a measure reads. And
    /// whether a block of it may hold one group, as the blocks follow the key's first column: of the columns of the keys,
    /// themselves and not a function of them, and of a filter the object's summaries do not prove true, those other than
    /// the first, and than a key's column they prove constant, may vary in a block: at most one under a question of
    /// counts, none under one of measures. Past that, every block would be read as rows after its statistics, a request
    /// for nothing. A function of a column, a truncation, holds one group over a block whose extremes it sends to one.
    /// </summary>
    internal bool MayAnswer(in PositionedObject held)
    {
        ObjectEntry entry = held.Entry;
        if (entry.HasDeletions || entry.Contents is not { BlockStats.Count: > 0 } contents || !_schema.Holds(entry))
        {
            return false;
        }

        // The keys first, which decide most questions with no look at the filter.
        ObjectSummaries summaries = held.Summaries(_schema.Key);
        int free = 0;
        foreach ((int field, FieldExpr key) in _keys)
        {
            if (field != _leading && key is ColumnFieldExpr && CountsAnswer.ConstantOf(summaries, _schema.Columns[field].Name, key) is null)
            {
                free++;
            }
        }

        int allowed = CountsOnly ? 1 : 0;
        if (free > allowed)
        {
            return false;
        }

        int[] fields = Fields;
        if (_filter is { } filter && !filter.MustMatch(summaries.Columns, entry.PhysicalRows))
        {
            foreach (int field in _filtered)
            {
                free += field != _leading && !Keyed(field) ? 1 : 0;
            }
        }

        if (free > allowed)
        {
            return false;
        }

        // A column the filter alone reads may keep none: the object's summaries judge it.
        foreach (int field in fields)
        {
            if (contents.BlockStatsOf(field) is null && (Keyed(field) || Measured(field)))
            {
                return false;
            }
        }

        return true;
    }

    private bool Keyed(int field)
    {
        foreach ((int keyed, _) in _keys)
        {
            if (keyed == field)
            {
                return true;
            }
        }

        return false;
    }

    private bool Measured(int field) => Array.IndexOf(_measured, field) >= 0;

    /// <summary>
    /// The states the blocks of the object <paramref name="held"/> give, from the statistics and the indexes of
    /// <see cref="Fields"/>, by their places in it, and the ranges of its rows, from its first, that its blocks do not
    /// answer, which are read as rows; every row when the statistics do not describe the object's blocks alike.
    /// </summary>
    internal (BlockStateRows? States, List<RowRange> Unsettled, int Settled) Settle(in PositionedObject held, (ColumnWhere? Where, ColumnBlocks? Blocks)[] columns)
    {
        long objectRows = held.Entry.PhysicalRows;
        ColumnBlocks? any = null;
        foreach ((_, ColumnBlocks? blocks) in columns)
        {
            if (blocks is null)
            {
                continue;
            }

            if ((any is not null && (blocks.Count != any.Count || !blocks.Rows.AsSpan().SequenceEqual(any.Rows))) || blocks.TotalRows != objectRows)
            {
                return (null, [new RowRange(0, objectRows)], 0);
            }

            any = blocks;
        }

        if (any is null)
        {
            return (null, [new RowRange(0, objectRows)], 0);
        }

        ObjectSummaries summaries = held.Summaries(_schema.Key);
        BlockColumn[] described = new BlockColumn[Fields.Length];
        for (int i = 0; i < Fields.Length; i++)
        {
            described[i] = new BlockColumn(_schema.Columns[Fields[i]].Name, _schema.DType.GetField(Fields[i]), columns[i].Where, columns[i].Blocks);
        }

        BlockStateRows states = new BlockStateRows(this);
        List<RowRange> unsettled = [];
        int settled = 0;
        List<ColumnSummary> judged = [];
        for (int b = 0; b < any.Count; b++)
        {
            long first = any.FirstRow(b);
            long rows = any.Rows[b];
            int before = states.Count;
            if (SettleBlock(b, rows, described, summaries, judged, states))
            {
                settled++;
                continue;
            }

            states.Truncate(before);
            if (unsettled.Count > 0 && unsettled[^1].End == first)
            {
                unsettled[^1] = new RowRange(unsettled[^1].Start, first + rows);
            }
            else
            {
                unsettled.Add(new RowRange(first, first + rows));
            }
        }

        return (states.Count > 0 ? states : null, unsettled, settled);
    }

    /// <summary>
    /// Settles block <paramref name="block"/> of <paramref name="rows"/> rows into <paramref name="states"/>: true when its
    /// statistics answer it, its rows of states added, none when they prove the filter selects no row of it.
    /// </summary>
    private bool SettleBlock(int block, long rows, BlockColumn[] columns, ObjectSummaries summaries, List<ColumnSummary> judged, BlockStateRows states)
    {
        // The block's summaries: each column's own, the object's for the columns the block keeps none of.
        judged.Clear();
        foreach (BlockColumn column in columns)
        {
            if (column.SummaryOf(block, rows) is { } own)
            {
                judged.Add(own);
            }
        }

        int kept = judged.Count;
        foreach (ColumnSummary column in summaries.Columns)
        {
            if (IndexOf(judged, kept, column.Path) < 0)
            {
                judged.Add(column);
            }
        }

        if (_filter is { } filter && !filter.MayMatch(judged, rows))
        {
            return true;
        }

        bool whole = _filter is not { } proving || proving.MustMatch(judged, rows);

        // The column that varies: a key's column that holds more than one group, or, under a filter the block's summaries
        // do not prove, a column it reads that holds more than one value; at most one, which lists its values.
        int varying = -1;
        for (int i = 0; i < Fields.Length; i++)
        {
            bool varies = !KeysHoldOneGroup(i, columns[i], block, rows)
                || (!whole && Array.IndexOf(_filtered, Fields[i]) >= 0 && !columns[i].HoldsOneValue(block, rows));
            if (!varies)
            {
                continue;
            }

            if (varying >= 0 || !CountsOnly || !columns[i].Lists(block))
            {
                return false;
            }

            varying = i;
        }

        if (varying < 0)
        {
            return whole && AddWhole(block, rows, columns, states);
        }

        // Each of its values, and its nulls, judged apart: the column's summary replaced by the value's.
        BlockColumn x = columns[varying];
        int at = IndexOf(judged, judged.Count, x.Path);
        (ReadOnlyMemory<int> ids, ReadOnlyMemory<long> counted) = x.Blocks!.ValuesOf(block);
        for (int v = 0; v <= ids.Length; v++)
        {
            long taken = v < ids.Length ? counted.Span[v] : x.Blocks.Nulls[block];
            if (taken == 0)
            {
                continue;
            }

            byte[]? value = v < ids.Length ? x.ValueOf(ids.Span[v]) : null;
            if (v < ids.Length && value is null)
            {
                return false;
            }

            judged[at] = x.SummaryOfValue(value, taken);
            if (_filter is { } each && !each.MayMatch(judged, taken))
            {
                continue;
            }

            if (_filter is { } proven && !proven.MustMatch(judged, taken))
            {
                return false;
            }

            int row = states.Add(taken);
            for (int k = 0; k < KeyFields.Length; k++)
            {
                int place = Array.IndexOf(Fields, KeyFields[k]);
                states.SetKey(row, k, place == varying ? value : columns[place].ConstantOf(block, rows, KeyOf(place)!));
            }
        }

        return true;
    }

    /// <summary>The place among the first <paramref name="count"/> summaries of the one of column <paramref name="path"/>, or -1.</summary>
    private static int IndexOf(List<ColumnSummary> summaries, int count, string path)
    {
        for (int i = 0; i < count; i++)
        {
            if (string.Equals(summaries[i].Path, path, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Whether every key on the column at <see cref="Fields"/>' <paramref name="place"/> holds one group over the block: true for a column no key reads.</summary>
    private bool KeysHoldOneGroup(int place, BlockColumn column, int block, long rows)
    {
        foreach ((int field, FieldExpr key) in _keys)
        {
            if (field == Fields[place] && !column.HoldsOneGroup(block, rows, key))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The key whose column is <see cref="Fields"/>' <paramref name="place"/>, the first such; null for a column no key reads.</summary>
    private FieldExpr? KeyOf(int place)
    {
        foreach ((int field, FieldExpr key) in _keys)
        {
            if (field == Fields[place])
            {
                return key;
            }
        }

        return null;
    }

    /// <summary>A block no column of the question varies in, the filter proven over its rows: its rows and each measure's states, a row of states.</summary>
    private bool AddWhole(int block, long rows, BlockColumn[] columns, BlockStateRows states)
    {
        int row = states.Add(rows);
        for (int k = 0; k < KeyFields.Length; k++)
        {
            int place = Array.IndexOf(Fields, KeyFields[k]);
            states.SetKey(row, k, columns[place].ConstantOf(block, rows, KeyOf(place)!));
        }

        int word = 1;
        foreach ((_, int field, bool floats, _) in Measures)
        {
            int place = Array.IndexOf(Fields, field);
            if (!columns[place].MeasureInto(block, floats, states.Words(row).Slice(word, MeasureLayout.Words(floats))))
            {
                return false;
            }

            word += MeasureLayout.Words(floats);
        }

        return true;
    }
}

/// <summary>
/// One column's statistics over an object's blocks and where its values live, as a block of a question reads them: the
/// summary it makes of the column, whether the column holds one value or one group of a key there, and that value.
/// </summary>
internal sealed class BlockColumn(string path, DType dtype, ColumnWhere? where, ColumnBlocks? blocks)
{
    private readonly KeyLayout _layout = KeyLayout.TryOf(dtype, out KeyLayout layout) ? layout : default;
    private readonly bool _keyed = KeyLayout.TryOf(dtype, out _);
    private FilterLiteral?[]? _literals;

    /// <summary>The column, as a filter names it.</summary>
    internal string Path { get; } = path;

    /// <summary>Its statistics over the blocks; null when the object keeps none of it.</summary>
    internal ColumnBlocks? Blocks { get; } = blocks;

    /// <summary>Whether block <paramref name="block"/> lists the column's values, each named in the index.</summary>
    internal bool Lists(int block) =>
        Blocks is { HoldsValueLists: true } stats && where is not null && stats.Distinct![block] <= ColumnBlocks.ListedValues;

    /// <summary>The value numbered <paramref name="id"/>, as the column stores it; null for a number the index lacks.</summary>
    internal byte[]? ValueOf(int id) => where is { } index && index.PlaceOf(id) is int place and >= 0 ? index.Values[place] : null;

    /// <summary>The literal of the value numbered <paramref name="id"/>, decoded once; null for none.</summary>
    private FilterLiteral? LiteralOf(int id)
    {
        if (where is not { } index || !_keyed || index.PlaceOf(id) is not (int place and >= 0))
        {
            return null;
        }

        _literals ??= new FilterLiteral?[index.Values.Length];
        return _literals[place] ??= _layout.ToLiteral(index.Values[place]);
    }

    /// <summary>
    /// The summary block <paramref name="block"/> of <paramref name="rows"/> rows makes of the column: the least and the
    /// greatest of the values it lists or of its measure, exact, and its nulls; null when it keeps no statistics.
    /// </summary>
    internal ColumnSummary? SummaryOf(int block, long rows)
    {
        if (Blocks is not { } stats)
        {
            return null;
        }

        long nulls = stats.Nulls[block];
        if (Bounds(block) is ({ } least, { } greatest))
        {
            return new ColumnSummary(Path, least, true, greatest, true, true, nulls, true);
        }

        return new ColumnSummary(Path, default, false, default, false, false, nulls, true);
    }

    /// <summary>The summary of the column holding <paramref name="value"/> alone over <paramref name="rows"/> rows, or nothing but nulls for null.</summary>
    internal ColumnSummary SummaryOfValue(byte[]? value, long rows) =>
        value is null || !_keyed
            ? new ColumnSummary(Path, default, false, default, false, false, rows, true)
            : new ColumnSummary(Path, _layout.ToLiteral(value), true, _layout.ToLiteral(value), true, true, 0, true);

    /// <summary>The least and the greatest value of block <paramref name="block"/>: of those it lists, else of its measure; null for none known.</summary>
    private (FilterLiteral? Least, FilterLiteral? Greatest) Bounds(int block)
    {
        ColumnBlocks stats = Blocks!;
        if (Lists(block))
        {
            FilterLiteral? least = null;
            FilterLiteral? greatest = null;
            foreach (int id in stats.ValuesOf(block).Ids.Span)
            {
                if (LiteralOf(id) is not { } value)
                {
                    return (null, null);
                }

                least = least is { } low && KeyCursor.Compare(low, value) <= 0 ? low : value;
                greatest = greatest is { } high && KeyCursor.Compare(high, value) >= 0 ? high : value;
            }

            return (least, greatest);
        }

        if (stats.Measure is { } kind && stats.MeasureOf(block) is { } words && words[MeasureLayout.Values] > 0)
        {
            bool floats = kind == MeasureKind.Float;
            long least = words[MeasureLayout.Least(floats)];
            long greatest = words[MeasureLayout.Greatest(floats)];
            return kind switch
            {
                MeasureKind.Float when !double.IsNaN(BitConverter.Int64BitsToDouble(least)) =>
                    (FilterLiteral.From(BitConverter.Int64BitsToDouble(least)), FilterLiteral.From(BitConverter.Int64BitsToDouble(greatest))),
                MeasureKind.Float => (null, null),
                MeasureKind.Signed => (FilterLiteral.From(least), FilterLiteral.From(greatest)),
                _ => (FilterLiteral.From((ulong)least), FilterLiteral.From((ulong)greatest)),
            };
        }

        return (null, null);
    }

    /// <summary>Whether block <paramref name="block"/> of <paramref name="rows"/> rows holds one value of the column, or nulls alone.</summary>
    internal bool HoldsOneValue(int block, long rows)
    {
        if (Blocks is not { } stats)
        {
            return false;
        }

        long nulls = stats.Nulls[block];
        if (nulls == rows)
        {
            return true;
        }

        return nulls == 0 && Bounds(block) is ({ } least, { } greatest) && KeyCursor.Compare(least, greatest) == 0;
    }

    /// <summary>
    /// Whether block <paramref name="block"/> of <paramref name="rows"/> rows holds one group of <paramref name="key"/>: one
    /// value or nulls alone, or no null and extremes the key's functions send to one group.
    /// </summary>
    internal bool HoldsOneGroup(int block, long rows, FieldExpr key)
    {
        if (HoldsOneValue(block, rows))
        {
            return true;
        }

        return key is FunctionFieldExpr && Blocks is { } stats && stats.Nulls[block] == 0 && Bounds(block) is ({ } least, { } greatest)
            && CountsAnswer.Image(key, least) is { } low && CountsAnswer.Image(key, greatest) is { } high && KeyCursor.Compare(low, high) == 0;
    }

    /// <summary>
    /// The value of the column that stands for block <paramref name="block"/>'s one group of <paramref name="key"/>, as the
    /// column stores it: its value, or its least under a function that sends its extremes to one group; null for nulls.
    /// </summary>
    internal byte[]? ConstantOf(int block, long rows, FieldExpr key)
    {
        ColumnBlocks stats = Blocks!;
        if (stats.Nulls[block] == rows)
        {
            return null;
        }

        if (Lists(block) && stats.ValuesOf(block).Ids.Span is [int id, ..])
        {
            return ValueOf(id);
        }

        return Bounds(block).Least is { } least && CountsAnswer.ValuesOf([least], dtype) is [{ } bytes, ..] ? bytes : null;
    }

    /// <summary>
    /// Writes into <paramref name="into"/> the states of the column as a measure over block <paramref name="block"/>, in
    /// <see cref="MeasureLayout"/>'s words: its own, or those its listed values give; false when it holds neither.
    /// </summary>
    internal bool MeasureInto(int block, bool floats, Span<long> into)
    {
        ColumnBlocks stats = Blocks!;
        if (stats.Measure is { } kind && (kind == MeasureKind.Float) == floats)
        {
            stats.MeasureOf(block).CopyTo(into);
            return true;
        }

        if (!Lists(block))
        {
            return false;
        }

        // A number of few values, which lists them: each value its rows' worth.
        into.Clear();
        (ReadOnlyMemory<int> ids, ReadOnlyMemory<long> counted) = stats.ValuesOf(block);
        if (floats)
        {
            WideIndexedSum sum = default;
            double least = double.PositiveInfinity;
            double greatest = double.NegativeInfinity;
            for (int i = 0; i < ids.Length; i++)
            {
                if (LiteralOf(ids.Span[i]) is not { Kind: FilterLiteralKind.Float } value)
                {
                    return false;
                }

                sum.AddWeighted(value.FloatValue, counted.Span[i]);
                into[MeasureLayout.Values] += counted.Span[i];
                least = Math.Min(least, value.FloatValue);
                greatest = Math.Max(greatest, value.FloatValue);
            }

            StateWords.Write(sum, into, MeasureLayout.Sum);
            into[MeasureLayout.FloatLeast] = BitConverter.DoubleToInt64Bits(least);
            into[MeasureLayout.FloatGreatest] = BitConverter.DoubleToInt64Bits(greatest);
            Writing.ColumnCountsBuilder.CloseState(into, MeasureKind.Float);
            return true;
        }

        Int128 total = 0;
        long low = long.MaxValue;
        long high = long.MinValue;
        for (int i = 0; i < ids.Length; i++)
        {
            if (LiteralOf(ids.Span[i]) is not { Kind: FilterLiteralKind.Signed } value)
            {
                return false;
            }

            total += (Int128)value.SignedValue * counted.Span[i];
            into[MeasureLayout.Values] += counted.Span[i];
            low = Math.Min(low, value.SignedValue);
            high = Math.Max(high, value.SignedValue);
        }

        StateWords.Write(total, into, MeasureLayout.Sum);
        into[MeasureLayout.IntegerLeast] = low;
        into[MeasureLayout.IntegerGreatest] = high;
        Writing.ColumnCountsBuilder.CloseState(into, MeasureKind.Signed);
        return true;
    }
}
