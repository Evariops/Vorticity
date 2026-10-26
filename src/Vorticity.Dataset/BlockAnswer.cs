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

    // The columns of the clustering key the filter only compares to a bound, a window: a block holds their values in order,
    // or within one value of the first column, so its extremes decide those whole, mostly, and they never make a block vary
    // for the planner's count. A bound on another column, whose values a block holds in any order, varies like any.
    private int[] _ranged = [];

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
        List<int> ranged = [];
        if (_asked is { } filter)
        {
            _filter = new SummaryPruner(filter);
            HashSet<string> bounded = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> other = new HashSet<string>(StringComparer.Ordinal);
            Bounded(filter, bounded, other);
            foreach (string path in _filter.Paths)
            {
                if (_schema.DType.IndexOfField(path) is int field and >= 0)
                {
                    Add(filtered, field);
                    Add(fields, field);
                    if (bounded.Contains(path) && !other.Contains(path) && _schema.Key is { } key && System.Linq.Enumerable.Contains(key.Paths, path, StringComparer.Ordinal))
                    {
                        Add(ranged, field);
                    }
                }
            }
        }

        _ranged = [.. ranged];

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

    /// <summary>
    /// Sorts the columns <paramref name="filter"/> reads: those a comparison to a bound reads, through <c>AND</c> and
    /// <c>OR</c>, into <paramref name="bounded"/>, and those any other predicate reads into <paramref name="other"/>.
    /// </summary>
    private static void Bounded(VortexExpr filter, HashSet<string> bounded, HashSet<string> other)
    {
        switch (filter)
        {
            case LogicalExpr logical:
                Bounded(logical.Left, bounded, other);
                Bounded(logical.Right, bounded, other);
                break;
            case ComparisonExpr { Op: ComparisonOp.Less or ComparisonOp.LessOrEqual or ComparisonOp.Greater or ComparisonOp.GreaterOrEqual, Field: ColumnFieldExpr column }:
                bounded.Add(column.Path);
                break;
            default:
                other.UnionWith(new SummaryPruner(filter).Paths);
                break;
        }
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
    /// Whether the blocks of the object <paramref name="held"/> may answer: the version's columns in their places, and
    /// statistics of every column a key or a measure reads; a block that holds a row a delete marked, which its statistics
    /// still count, is read as rows (<see cref="Settle"/>). And
    /// whether a block of it may hold one group, as the blocks follow the key's first column: of the columns of the keys,
    /// themselves and not a function of them, and of a filter the object's summaries do not prove true, those other than
    /// the first, and than a key's column they prove constant, may vary in a block: at most one under a question of
    /// counts, none under one of measures. Past that, every block would be read as rows after its statistics, a request
    /// for nothing. A function of a column, a truncation, holds one group over a block whose extremes it sends to one.
    /// </summary>
    internal bool MayAnswer(in PositionedObject held)
    {
        ObjectEntry entry = held.Entry;
        if (entry.Contents is not { BlockStats.Count: > 0 } contents || !_schema.Holds(entry))
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
                free += field != _leading && !Keyed(field) && Array.IndexOf(_ranged, field) < 0 ? 1 : 0;
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

        // And the statistics cost less than the rows they would spare, both by the formula; an object whose every block holds
        // several values that the question needs one of reads its rows, the statistics a request for nothing.
        (PathCosts.Cost statistics, _, PathCosts.Cost rows) = Costs(held);
        return Unanswered(held) < 1 && statistics.Weight < rows.Weight;
    }

    /// <summary>
    /// What the object <paramref name="held"/> costs through its blocks' statistics and their indexes, then through the rows of
    /// the blocks they would not answer, as their share of the object's rows estimates them (<see cref="Unanswered"/>), and
    /// through the rows of the question's columns alone (<see cref="PathCosts"/>).
    /// </summary>
    internal (PathCosts.Cost Statistics, PathCosts.Cost Unanswered, PathCosts.Cost Rows) Costs(in PositionedObject held)
    {
        ObjectContents contents = held.Entry.Contents!;
        int[] fields = Fields;
        IndexSegment?[] regions = new IndexSegment?[2 * fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            (regions[2 * i], regions[(2 * i) + 1]) = (contents.WhereOf(fields[i]), contents.BlockStatsOf(fields[i]));
        }

        return (PathCosts.Regions(regions), PathCosts.Share(held.Entry, fields, Unanswered(held, edges: true)), PathCosts.Rows(held.Entry, fields));
    }

    /// <summary>
    /// The share of the rows of the object <paramref name="held"/> whose blocks its statistics are not expected to answer:
    /// those of the blocks that hold several values of the key's first column or of the block value
    /// (<see cref="ObjectContents.PureBlockRows"/>), short runs gathered, unless the question would answer such a block too,
    /// every column it keys or filters but one, under counts alone, constant over the object; none when the object does not
    /// say, the blocks then taken to answer, as before it said. With <paramref name="edges"/>, for a price that bounds what
    /// is read, those a window on a column of the key cuts too, which the choice of the path need not weigh: a window on
    /// the block value cuts no block of one value.
    /// </summary>
    internal double Unanswered(in PositionedObject held, bool edges = false)
    {
        ObjectEntry entry = held.Entry;
        if (entry.PhysicalRows <= 0)
        {
            return 0;
        }

        ObjectSummaries summaries = held.Summaries(_schema.Key);
        _ = Fields;
        bool proven = _filter is not { } filter || filter.MustMatch(summaries.Columns, entry.PhysicalRows);
        double share = 0;
        if (entry.Contents is { PureBlockRows: >= 0 } contents)
        {
            // In a block of several runs, the first column varies, and so does every key and filtered column the object's
            // summaries do not hold constant.
            int varying = 0;
            foreach ((int field, FieldExpr key) in _keys)
            {
                varying += CountsAnswer.ConstantOf(summaries, _schema.Columns[field].Name, key) is null ? 1 : 0;
            }

            foreach (int field in proven ? [] : _filtered)
            {
                varying += !Keyed(field) && Array.IndexOf(_ranged, field) < 0 ? 1 : 0;
            }

            share = varying <= (CountsOnly ? 1 : 0) ? 0 : 1 - Math.Clamp(contents.PureBlockRows / (double)entry.PhysicalRows, 0, 1);
        }

        // A window the object's summaries do not prove cuts the blocks at its bounds, read as rows: two at most on the first
        // column, whose values the object holds in order; on another column of the key, a block of each run of the first,
        // every block at most.
        foreach (int field in edges && !proven ? _ranged : [])
        {
            share += field == _leading ? Math.Min(1, 2.0 * BlockTable.MostRows / entry.PhysicalRows) : 1;
        }

        return Math.Min(1, share);
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
    /// <see cref="Fields"/>, by their places in it, and the ranges of its live rows, from its first, that its blocks do not
    /// answer, which are read as rows; every row when the statistics do not describe the object's blocks alike. A block
    /// that holds a row of <paramref name="deletions"/> is not answered: its statistics count that row.
    /// </summary>
    internal (BlockStateRows? States, List<RowRange> Unsettled, int Settled) Settle(
        in PositionedObject held, (ColumnWhere? Where, ColumnBlocks? Blocks)[] columns, DeletionVector deletions)
    {
        (BlockStateRows? states, List<RowRange> unsettled, int settled) = SettlePhysical(held, columns, deletions);
        if (deletions.IsEmpty)
        {
            return (states, unsettled, settled);
        }

        // The ranges as the object's live rows number them, which a part of the scan reads.
        for (int i = 0; i < unsettled.Count; i++)
        {
            unsettled[i] = new RowRange(deletions.Logical(unsettled[i].Start), deletions.Logical(unsettled[i].End));
        }

        return (states, unsettled, settled);
    }

    /// <summary><see cref="Settle"/>, its ranges the object's own row positions, deleted rows included.</summary>
    private (BlockStateRows? States, List<RowRange> Unsettled, int Settled) SettlePhysical(
        in PositionedObject held, (ColumnWhere? Where, ColumnBlocks? Blocks)[] columns, DeletionVector deletions)
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

        // Which blocks their statistics answer, then those answered between two read as rows that cost less read as rows
        // too than a part of the scan more, which would read again the chunks the two parts share.
        BlockStateRows states = new BlockStateRows(this);
        List<ColumnSummary> judged = [];
        bool[] answers = new bool[any.Count];
        for (int b = 0; b < any.Count; b++)
        {
            long first = any.FirstRow(b);
            answers[b] = deletions.DeletedIn(first, first + any.Rows[b]) == 0 && SettleBlock(b, any.Rows[b], described, summaries, judged, states);
            states.Truncate(0);
        }

        Bridge(held.Entry, any, answers);
        List<RowRange> unsettled = [];
        int settled = 0;
        for (int b = 0; b < any.Count; b++)
        {
            long first = any.FirstRow(b);
            long rows = any.Rows[b];
            if (answers[b])
            {
                SettleBlock(b, rows, described, summaries, judged, states);
                settled++;
                continue;
            }

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
    /// Reads as rows each run of blocks <paramref name="answers"/> answers between two it does not, when reading its rows of
    /// the question's columns costs less than a part of the scan more (<see cref="PathCosts.Bridges"/>).
    /// </summary>
    private void Bridge(ObjectEntry entry, ColumnBlocks blocks, bool[] answers)
    {
        int b = 0;
        while (b < answers.Length && answers[b])
        {
            b++;
        }

        while (b < answers.Length)
        {
            // From an unanswered block, the run of answered ones after it, and whether an unanswered one ends it.
            while (b < answers.Length && !answers[b])
            {
                b++;
            }

            int start = b;
            long rows = 0;
            while (b < answers.Length && answers[b])
            {
                rows += blocks.Rows[b];
                b++;
            }

            if (b < answers.Length && PathCosts.Bridges(entry, Fields, rows))
            {
                Array.Fill(answers, false, start, b - start);
            }
        }
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
            if (column.SummaryOf(block) is { } own)
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
                states.SetKey(row, k, place == varying ? value : columns[place].ConstantOf(block, rows));
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

    /// <summary>A block no column of the question varies in, the filter proven over its rows: its rows and each measure's states, a row of states.</summary>
    private bool AddWhole(int block, long rows, BlockColumn[] columns, BlockStateRows states)
    {
        int row = states.Add(rows);
        for (int k = 0; k < KeyFields.Length; k++)
        {
            int place = Array.IndexOf(Fields, KeyFields[k]);
            states.SetKey(row, k, columns[place].ConstantOf(block, rows));
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
    /// The summary block <paramref name="block"/> makes of the column: the least and the greatest of the values it lists
    /// or of its measure, exact, and its nulls; null when it keeps no statistics.
    /// </summary>
    internal ColumnSummary? SummaryOf(int block)
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
    /// The value of the column that stands for the one group of block <paramref name="block"/> of <paramref name="rows"/>
    /// rows, as the column stores it: its value, or its least under a function that sends its extremes to one group; null
    /// for nulls.
    /// </summary>
    internal byte[]? ConstantOf(int block, long rows)
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
