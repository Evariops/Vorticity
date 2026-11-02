using System;
using System.Collections.Generic;
using Vorticity.Aggregating;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.Keys;
using Vorticity.RowEncoding;
using Vorticity.Scanning;
using Vorticity.Types;

namespace Vorticity.Dataset;

/// <summary>
/// How the statistics of an object's columns over its granules (<see cref="GranuleStats"/>) answer an aggregation that asks for
/// states (<see cref="StateRequest"/>) granule by granule,
/// where the object's counts do not (<see cref="CountsAnswer"/>). A granule whose statistics prove the filter true over its
/// rows, and whose keys each hold one group through their functions, gives its rows and its measures' states: no column of
/// the question varies in it. A granule where one column of the question varies, under a question of counts alone, gives
/// the rows of each of its values, the filter judged value by value. Any other granule is read as rows: two columns varying,
/// a filter its statistics neither prove nor refute, or a measure its statistics do not hold. A quantile or a distribution
/// takes each granule's histogram of its measure (<see cref="GranuleHistograms"/>), when the error it asks allows their grid.
/// </summary>
/// <remarks>
/// A granule is judged by the summaries its statistics make of each column: one value, or the least and the greatest of
/// those it lists or of a measure, and its nulls; the columns the granule keeps none of are judged by the object's
/// summaries. Nothing here knows a time: a truncation of an instant is a function like any, which holds one group over a
/// granule when the instant's extremes there fall in one.
/// </remarks>
internal sealed class GranuleAnswer
{
    private readonly DatasetSchema _schema;
    private readonly PathCosts _costs;
    private readonly VortexExpr? _asked;

    // Each key of the request, its column and the key itself, a function of it or the column; the columns of the measures.
    private readonly (int Field, FieldExpr Key)[] _keys;
    private readonly int[] _measured;

    // The sort key's first column, which the object holds in order; and the columns of its indexed prefix, which a granule of
    // one value of the prefix holds one value of each (ObjectContents.PureGranuleRows).
    private readonly int _leading;
    private readonly int[] _prefixed;

    // The rows of a granule, which a window on the key's first column cuts at most two of.
    private readonly int _granuleRows;

    // Each key that is a function of a column of which a component of the sort key's indexed prefix is a function too, whose
    // values the object keeps by granule (GranuleKeyRuns): the key's place among the keys, the component's place in the
    // key, and the component's function. A granule takes the key's groups from those values where the component's class of
    // each falls in one group of the key's: a bucket of hours over hours.
    private readonly (int Key, int Component, ValueFunction Of)[] _runKeys;

    // For a question of counts two of whose keys the sort key's indexed prefix gives, the component each key reads its
    // groups from, by the key's place: a column of the prefix as it is, its function null, or the function of a column a key
    // that is a function of it too takes the groups of each value's class from; -1 for a key the prefix does not give. A
    // granule where several of them vary takes them run by run from the object's runs of the prefix (GranuleKeyRuns), at
    // _prefixAt among the components' values read. Null for another question.
    private readonly (int Component, ValueFunction? Of)[]? _prefixKeys;
    private readonly int _prefixAt = -1;

    // The columns of the fields a granule left reads whose chunks no read of granules slices, by the fields they were found
    // among; worked out at the first price.
    private (int[] Read, int[] Fields)? _unsliced;

    // The mantissa bits of the grid of the sketches a quantile of the request asks; 0 for none.
    private readonly int _sketchBits;

    // The filter, judged by summaries, and the columns it reads, top-level ones by their place; the columns whose statistics
    // a granule is judged by. Worked out at the first object whose keys may hold one group in a granule, which most questions
    // the counts answer never meet. Under a value left out, the filter less that conjunct; and the columns the whole filter
    // reads, the column of a value left out of one that is no identifier among them, which judge an object no index of it
    // places the value in.
    private SummaryPruner? _filter;
    private int[] _filtered = [];
    private int[] _filteredWhole = [];
    private int[]? _fields;

    // The shredded keys the filter reads, each a column of its own for the granules, past the version's columns: the variant
    // column, the key and the class its statistics list, the name its comparisons are judged under, and its type. A
    // comparison on such a key is judged on that column (OverPaths), which the key's statistics over the granules describe.
    // Null for a filter on none, which allocates nothing.
    private List<(int Variant, string Key, Types.Variants.PathClass Class, string Name, DType Type)>? _pathColumns;
    private DTypeArena? _pathTypes;

    // A value of a declared identifier the filter leaves out (CountsAnswer.Exclusion), or of another column, whose locate,
    // Bloom filters or postings in an object may place it: a granule they place no row of the value in is answered under
    // the rest of the filter, the others under the whole filter, or read as rows. Null for none, which most questions are:
    // made apart, it costs them nothing.
    private readonly Excluding? _excluding;

    // The columns of the sort key the filter only compares to a bound, a window: a granule holds their values in order, or
    // within one value of the columns before, so its extremes decide those whole, mostly, and they never make a granule vary
    // for the planner's count. A bound on another column, whose values a granule holds in any order, varies like any.
    private int[] _ranged = [];

    private GranuleAnswer(
        CountsAnswer counts, DatasetSchema version, PathCosts costs, VortexExpr? filter, (int Field, FieldExpr Key)[] keys, List<int> measured,
        List<VariantFieldExpr>? measuredPaths, int leading, int[] prefixed, int granuleRows, int sketchBits, (int Key, int Component, ValueFunction Of)[] runKeys,
        (int Component, ValueFunction? Of)[]? prefixKeys, ExcludedValue? excluded)
    {
        _prefixed = prefixed;
        _runKeys = runKeys;
        _prefixKeys = prefixKeys;
        _prefixAt = prefixKeys is null ? -1 : runKeys.Length;
        Components = [.. Array.ConvertAll(runKeys, static run => run.Component), .. prefixKeys is null ? (int[])[] : [GranuleKeyRuns.WholePrefix]];
        _sketchBits = sketchBits;
        _granuleRows = Math.Max(granuleRows, 1);
        _schema = version;
        _costs = costs;
        _asked = filter;
        _excluding = (counts.Exclusion ?? excluded) is { } value ? new Excluding(value, Less(filter!, value)) : null;

        // The shredded keys the keys, then the measures, read: each a column of the granules in that order, as the batches
        // number them.
        for (int k = 0; k < keys.Length; k++)
        {
            if (keys[k].Key is VariantFieldExpr path)
            {
                keys[k] = (PathField(path)!.Value, path);
            }
        }

        for (int m = 0; measuredPaths is not null && m < measuredPaths.Count; m++)
        {
            Add(measured, PathField(measuredPaths[m])!.Value);
        }

        _keys = keys;
        _measured = [.. measured];
        _leading = leading;
        (KeyFields, Measures, Type, Bool) = (counts.KeyFields, counts.Measures, counts.Type, counts.Bool);
        int width = 1;
        foreach ((_, _, bool floats, _) in Measures)
        {
            width += MeasureLayout.Words(floats);
        }

        Width = width;
        CountsOnly = Measures.Length == 0;
        List<int> histogrammed = [];
        foreach ((_, int field, _, bool sketch) in Measures)
        {
            if (sketch)
            {
                Add(histogrammed, field);
            }
        }

        Histogrammed = [.. histogrammed];
    }

    /// <summary>The columns of the measures whose histograms a quantile or a distribution asks, each once.</summary>
    internal int[] Histogrammed { get; }

    /// <summary>The components of the sort key whose values by granule the keys take their groups from, by their place in the key.</summary>
    internal int[] Components { get; }

    /// <summary>
    /// The value of a declared identifier the filter leaves out (<see cref="CountsAnswer.Exclusion"/>): its column, its path,
    /// and the equality its postings place the rows of; null for none. The granules are judged under the rest of the filter
    /// where the value is placed (<see cref="Settle"/>), and an object where it may not be is answered by none of them.
    /// </summary>
    internal ExcludedValue? Exclusion => _excluding?.Value;

    /// <summary>
    /// A value of a declared identifier the filter leaves out and the filter less its conjunct; and the whole filter as
    /// summaries judge it, which a granule is judged by where the value is not placed, worked out with the rest's.
    /// </summary>
    private sealed class Excluding(ExcludedValue value, VortexExpr? rest)
    {
        private int[]? _read;

        internal ExcludedValue Value { get; } = value;

        internal VortexExpr? Rest { get; } = rest;

        internal SummaryPruner? Whole { get; set; }

        /// <summary><paramref name="fields"/>, the columns a granule is judged by, and the identifier's, which a granule left is read for too; made once.</summary>
        internal int[] ReadOf(int[] fields) => _read ??= Array.IndexOf(fields, Value.Field) >= 0 ? fields : [.. fields, Value.Field];
    }

    /// <summary>
    /// <paramref name="filter"/> less its first conjunct that leaves out the values <paramref name="value"/> places, as
    /// <see cref="CountsAnswer.Exclusion"/> or <see cref="Excluded"/> took it: <c>x != v</c> of that value, <c>NOT</c> of
    /// that set, or <c>NOT</c> of that list's containing it; of a shredded key, the key's rows that hold a value in its
    /// place, and of a list, its rows that hold a list. Null when nothing else is left.
    /// </summary>
    private static VortexExpr? Less(VortexExpr filter, ExcludedValue value)
    {
        List<VortexExpr> conjuncts = [];
        GroupPredicates.Conjuncts(filter, conjuncts);
        int excluded = conjuncts.FindIndex(conjunct => (conjunct, value.Found) switch
        {
            (ComparisonExpr { Op: ComparisonOp.NotEqual, Field: ColumnFieldExpr column } other, ComparisonExpr found) =>
                string.Equals(column.Path, value.Path, StringComparison.Ordinal) && other.Value.Equals(found.Value),
            (NotExpr { Operand: InExpr set }, InExpr found) => ReferenceEquals(set, found),
            (NotExpr { Operand: ListContainsExpr contains }, ListContainsExpr found) => ReferenceEquals(contains, found),
            (VariantFilterExpr { Inner: ComparisonExpr { Op: ComparisonOp.NotEqual, Field: VariantFieldExpr path } other }, VariantFilterExpr { Inner: ComparisonExpr found }) =>
                string.Equals(path.Text, value.Path, StringComparison.Ordinal) && other.Value.Equals(found.Value),
            (NotExpr { Operand: VariantFilterExpr fence }, VariantFilterExpr found) => ReferenceEquals(fence, found),
            _ => false,
        });
        if (excluded >= 0)
        {
            conjuncts.RemoveAt(excluded);
        }

        // A key's rows that hold no value of it are left out too, as a column's nulls are; so are the rows of a null list,
        // which holds no element and of which a list that holds no such value says nothing.
        if (value.Key is { } key)
        {
            conjuncts.Add(VariantFilterExpr.Of(Expr.IsNotNull(key)));
        }
        else if (value.Found is ListContainsExpr { Field: { } list })
        {
            conjuncts.Add(Expr.IsNotNull(list));
        }

        return GroupPredicates.And(null, conjuncts);
    }

    /// <summary>The columns whose statistics a granule is judged by: those of the keys, the filter and the measures, each once.</summary>
    internal int[] Fields => _fields ??= Judged();

    /// <summary>The filter's columns and its pruner, then every column a granule is judged by.</summary>
    private int[] Judged()
    {
        List<int> fields = [];
        foreach ((int field, _) in _keys)
        {
            Add(fields, field);
        }

        List<int> filtered = [];
        List<int> ranged = [];
        if (_excluding is { } excluding && _asked is { } whole)
        {
            excluding.Whole = new SummaryPruner(OverPaths(whole));
        }

        if ((_excluding is { } less ? less.Rest : _asked) is { } asked)
        {
            VortexExpr filter = OverPaths(asked);
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
                    if (bounded.Contains(path) && !other.Contains(path) && Sorted(_schema, field))
                    {
                        Add(ranged, field);
                    }
                }
                else if (PathIndex(path) is int at and >= 0)
                {
                    Add(filtered, _schema.DType.FieldCount + at);
                    Add(fields, _schema.DType.FieldCount + at);
                }
            }
        }

        _ranged = [.. ranged];

        foreach (int field in _measured)
        {
            Add(fields, field);
        }

        _filtered = [.. filtered];

        // The column of a value left out of one that is no identifier: its statistics judge, under the whole filter, the objects
        // no index of it places the value in, and the granules one places it in.
        if (_excluding is { Value: { Identifier: false } own })
        {
            Add(fields, JudgedOf(own));
            Add(filtered, JudgedOf(own));
        }

        _filteredWhole = [.. filtered];
        return [.. fields];
    }

    /// <summary>The column whose statistics judge the value <paramref name="value"/> leaves out: its column's, or its key's.</summary>
    private int JudgedOf(ExcludedValue value) => value.Key is { } key && PathField(key) is int path ? path : value.Field;

    /// <summary>
    /// <paramref name="filter"/> with each comparison on a shredded key read in a class its statistics list, an integer or a
    /// text, made one on a column of the key's own (<see cref="_pathColumns"/>), out of its fence; the rest as it is, a
    /// predicate on any other path in its fence, which no summary of the variant column judges.
    /// </summary>
    private VortexExpr OverPaths(VortexExpr filter)
    {
        switch (filter)
        {
            case VariantFilterExpr fenced:
                VortexExpr inner = OverPaths(fenced.Inner);
                return ReferenceEquals(inner, fenced.Inner) ? fenced : inner;
            case LogicalExpr logical:
                VortexExpr left = OverPaths(logical.Left);
                VortexExpr right = OverPaths(logical.Right);
                return ReferenceEquals(left, logical.Left) && ReferenceEquals(right, logical.Right) ? logical
                    : logical.IsAnd ? Expr.And(left, right) : Expr.Or(left, right);
            case ComparisonExpr { Field: VariantFieldExpr path } comparison when PathColumn(path) is { } column:
                return new ComparisonExpr(column, comparison.Op, comparison.Value);
            case InExpr { Field: VariantFieldExpr path } set when PathColumn(path) is { } column:
                return new InExpr(column, [.. set.Values]);
            case NullCheckExpr { Field: VariantFieldExpr path } check when PathColumn(path) is { } column:
                return new NullCheckExpr(column, check.IsNull);
            default:
                return filter;
        }
    }

    /// <summary>The column of the granules the shredded key <paramref name="path"/> reads, made the first time; null for a path no statistics describe.</summary>
    private ColumnFieldExpr? PathColumn(VariantFieldExpr path) =>
        PathField(path) is int field ? new ColumnFieldExpr([PathOf(field)!.Value.Name]) : null;

    /// <summary>
    /// The column the shredded key <paramref name="path"/> stands for, past the version's columns (<see cref="_pathColumns"/>),
    /// made the first time; null for a path no statistics describe.
    /// </summary>
    private int? PathField(VariantFieldExpr path)
    {
        if (ClassOf(path, _schema.DType) is not { } held)
        {
            return null;
        }

        int at = PathIndex(path.Text);
        if (at < 0)
        {
            _pathTypes ??= new DTypeArena();
            (_pathColumns ??= []).Add((_schema.DType.IndexOfField(path.Column.Path), path.KeyName!, held, path.Text, Writing.IndexWriter.ClassType(_pathTypes, held)));
            at = _pathColumns.Count - 1;
        }

        return _schema.DType.FieldCount + at;
    }

    /// <summary>The place among <see cref="_pathColumns"/> of the shredded key judged under <paramref name="name"/>; -1 for none.</summary>
    private int PathIndex(string name)
    {
        for (int i = 0; _pathColumns is { } columns && i < columns.Count; i++)
        {
            if (string.Equals(columns[i].Name, name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The shredded key column <paramref name="field"/> stands for, past the version's columns; null for a column.</summary>
    private (int Variant, string Key, Types.Variants.PathClass Class, string Name, DType Type)? PathOf(int field) =>
        _pathColumns is { } columns && field >= _schema.DType.FieldCount && field - _schema.DType.FieldCount < columns.Count ? columns[field - _schema.DType.FieldCount] : null;

    /// <summary>
    /// The shredded key each of <see cref="Fields"/> stands for, by the same places, its variant column, key and class, which
    /// a read of the statistics takes the key's of; null for a column, and null whole when the filter reads no key.
    /// </summary>
    internal (int Field, string Key, Types.Variants.PathClass Class)?[]? PathsOfFields
    {
        get
        {
            int[] fields = Fields;
            return _pathColumns is null ? null
                : _pathsOfFields ??= Array.ConvertAll(fields, column => PathOf(column) is { } path ? ((int, string, Types.Variants.PathClass)?)(path.Variant, path.Key, path.Class) : null);
        }
    }

    private (int Field, string Key, Types.Variants.PathClass Class)?[]? _pathsOfFields;

    /// <summary><see cref="Fields"/> as a scan reads their rows: a shredded key's column the variant column it lies in, each once.</summary>
    private int[] ReadFields
    {
        get
        {
            if (_readFields is null)
            {
                int[] fields = Fields;
                List<int>? read = _pathColumns is null ? null : [];
                foreach (int column in read is null ? [] : fields)
                {
                    Add(read!, PathOf(column)?.Variant ?? column);
                }

                _readFields = read is null ? fields : [.. read];
            }

            return _readFields;
        }
    }

    private int[]? _readFields;

    /// <summary>The name column <paramref name="field"/> is judged under: a column's own, a shredded key's text.</summary>
    private string NameOf(int field) => PathOf(field)?.Name ?? _schema.Columns[field].Name;

    /// <summary>The type of column <paramref name="field"/>: a column's own, the class a shredded key's statistics list.</summary>
    private DType TypeOf(int field) => PathOf(field)?.Type ?? _schema.DType.GetField(field);

    /// <summary>The region of the statistics over the granules of column <paramref name="field"/> of an object of <paramref name="contents"/>; null for none.</summary>
    private IndexSegment? StatisticsOf(ObjectContents contents, int field) =>
        PathOf(field) is { } path ? contents.PathGranuleStatsOf(path.Variant, path.Key, path.Class)?.Region : contents.GranuleStatsOf(field);

    /// <summary>The rows of the granules where column <paramref name="field"/> of an object of <paramref name="contents"/> holds one value.</summary>
    private long SingleRowsOf(ObjectContents contents, int field) =>
        PathOf(field) is { } path ? contents.PathGranuleStatsOf(path.Variant, path.Key, path.Class)?.SingleRows ?? 0 : contents.SingleRowsOf(field);

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

    /// <summary>Whether the request asks counts alone, which the values of a column varying in a granule answer.</summary>
    internal bool CountsOnly { get; }

    /// <summary>
    /// How the granules of the objects answer <paramref name="request"/> under <paramref name="filter"/> over the columns of
    /// <paramref name="version"/>, in the batches of states <paramref name="counts"/> lays out for the same request; null
    /// when they answer no granule of any: a quantile or a distribution finer than the granules' histograms, a distinct count
    /// kept as a sketch, or a key or a measure on a path.
    /// </summary>
    internal static GranuleAnswer? Of(StateRequest request, VortexExpr? filter, DatasetSnapshot version, CountsAnswer counts, int granuleRows) =>
        Of(request, filter, version, counts, granuleRows, out _);

    /// <summary>
    /// <see cref="Of(StateRequest, VortexExpr?, DatasetSnapshot, CountsAnswer, int)"/>, and why it declines, for an
    /// explanation; null when it does not.
    /// </summary>
    internal static GranuleAnswer? Of(StateRequest request, VortexExpr? filter, DatasetSnapshot version, CountsAnswer counts, int granuleRows, out string? declined)
    {
        DType schema = version.Schema.DType;
        if (schema.IsDefault || schema.Kind != DTypeKind.Struct)
        {
            declined = "the version's columns are no struct";
            return null;
        }

        foreach (AggregateKind kind in request.Kinds)
        {
            if (kind is AggregateKind.Distribution or AggregateKind.Quantile && request.SketchBits is <= 0 or > GranuleHistograms.WrittenBits)
            {
                declined = "a quantile or a distribution asks a finer grid than the granules' histograms";
                return null;
            }
        }

        // A key or a measure on a shredded key is numbered past the version's columns when the answer is made, as the batches
        // lay it out (CountsAnswer.Layout); -1 until then.
        (int Field, FieldExpr Key)[] keys = new (int Field, FieldExpr Key)[request.Keys.Count];
        for (int k = 0; k < keys.Length; k++)
        {
            FieldExpr key = request.Keys[k];
            if (key is VariantFieldExpr path && ClassOf(path, schema) is not null)
            {
                keys[k] = (-1, key);
                continue;
            }

            if (Top(CountsAnswer.Inner(key), schema) is not int field)
            {
                declined = key is VariantFieldExpr
                    ? "a key reads a path no statistics over the granules describe: a nested one, or one read in a class they do not list"
                    : "a key reads no top-level column";
                return null;
            }

            keys[k] = (field, key);
        }

        List<int> measured = [];
        List<VariantFieldExpr>? measuredPaths = null;
        for (int i = 0; i < request.InputFields.Count; i++)
        {
            if (request.InputFields[i] is VariantFieldExpr path)
            {
                if (ClassOf(path, schema) is null || request.Kinds[i] is AggregateKind.Distribution or AggregateKind.Quantile)
                {
                    declined = "a measure reads a path whose granules keep no statistics of it, or no histograms a quantile asks";
                    return null;
                }

                (measuredPaths ??= []).Add(path);
                continue;
            }

            if (request.InputFields[i] is { } input)
            {
                if (Top(input, schema) is not int field)
                {
                    declined = "a measure reads no top-level column";
                    return null;
                }

                Add(measured, field);
            }
        }

        // The sort key's columns as they are: its first, and those of its indexed prefix.
        int leading = -1;
        List<int> prefixed = [];
        if (version.Schema.Layout is { } layout)
        {
            for (int c = 0; c < layout.Components.Count && c < layout.Prefix; c++)
            {
                if (layout.Components[c] is { Function: null, Read: null } component)
                {
                    leading = c == 0 ? component.Field : leading;
                    prefixed.Add(component.Field);
                }
            }
        }

        // The keys that are functions of a column a component of the indexed prefix is a function of too.
        List<(int Key, int Component, ValueFunction Of)> runKeys = [];
        for (int k = 0; k < keys.Length && version.Schema.Layout is { } prefix; k++)
        {
            for (int c = 0; keys[k].Key is FunctionFieldExpr && c < prefix.Components.Count && c < prefix.Prefix; c++)
            {
                if (prefix.Components[c] is { Function: { } function, Read: null } component && component.Field == keys[k].Field)
                {
                    runKeys.Add((k, c, function));
                    break;
                }
            }
        }

        declined = null;
        return new GranuleAnswer(
            counts, version.Schema, version.Costs, filter, keys, measured, measuredPaths, leading, [.. prefixed], granuleRows, request.SketchBits, [.. runKeys],
            counts.Measures.Length == 0 ? PrefixKeys(keys, version.Schema.Layout) : null, counts.Exclusion is null && filter is not null ? Excluded(filter, version.Schema) : null);
    }

    /// <summary>
    /// The component of the indexed prefix of <paramref name="layout"/> each of <paramref name="keys"/> takes its groups from,
    /// by the key's place: a column of the prefix as it is, its function null, or a function of a column a key that is a
    /// function of it too reads; -1 for a key the prefix does not give. Null when it gives fewer than two, which a granule's
    /// runs of the prefix would not answer more of than its components' values and listed columns do.
    /// </summary>
    private static (int Component, ValueFunction? Of)[]? PrefixKeys((int Field, FieldExpr Key)[] keys, LayoutPlan? layout)
    {
        if (layout is not { Prefix: >= 2 } || keys.Length < 2)
        {
            return null;
        }

        (int Component, ValueFunction? Of)[] given = new (int, ValueFunction?)[keys.Length];
        int count = 0;
        for (int k = 0; k < keys.Length; k++)
        {
            given[k] = (-1, null);
            for (int c = 0; keys[k].Field >= 0 && c < layout.Components.Count && c < layout.Prefix; c++)
            {
                if (layout.Components[c] is { Read: null } component && component.Field == keys[k].Field
                    && (component.Function is null || keys[k].Key is FunctionFieldExpr))
                {
                    given[k] = (c, component.Function);
                    count++;
                    break;
                }
            }
        }

        return count >= 2 ? given : null;
    }

    /// <summary>
    /// The class the statistics of the shredded key <paramref name="path"/> reads list its values in, an integer or a text, for
    /// a key of a top-level column of <paramref name="schema"/>, top-level or nested in its objects; null for a path no
    /// statistics over the granules describe.
    /// </summary>
    private static Types.Variants.PathClass? ClassOf(VariantFieldExpr path, DType schema) =>
        path.Column is not ColumnFieldExpr || path.Column.Path.Contains('.', StringComparison.Ordinal) || schema.IndexOfField(path.Column.Path) < 0
            || path.KeyName is null
            ? null
            : path.Read switch
            {
                VariantRead.AsInt64 => Types.Variants.PathClass.Integer,
                VariantRead.AsString => Types.Variants.PathClass.String,
                _ => null,
            };

    /// <summary>
    /// The first values <paramref name="filter"/> leaves out of a top-level column of <paramref name="version"/> by a
    /// conjunct, <c>x != v</c> of a value that is no null, <c>NOT x IN (…)</c> of such values or <c>NOT list_contains(x, v)</c>,
    /// which an object's indexes of the column may place (spec D, 4), their equality, set or containing the found rows; of a
    /// shredded key, as its postings when the dataset declares it an identifier, or its Bloom filter, place them. Null for
    /// none.
    /// </summary>
    private static ExcludedValue? Excluded(VortexExpr filter, DatasetSchema version) => filter switch
    {
        LogicalExpr { IsAnd: true } and => Excluded(and.Left, version) ?? Excluded(and.Right, version),
        ComparisonExpr { Op: ComparisonOp.NotEqual, Field: ColumnFieldExpr column } other when other.Value.Kind != FilterLiteralKind.Null && Top(column, version.DType) is int field =>
            new ExcludedValue(field, column.Path, new ComparisonExpr(column, ComparisonOp.Equal, other.Value), identifier: false),
        NotExpr { Operand: InExpr { Field: ColumnFieldExpr column } set } when Present(set) && Top(column, version.DType) is int field =>
            new ExcludedValue(field, column.Path, set, identifier: false),
        NotExpr { Operand: ListContainsExpr { Field: ColumnFieldExpr column } contains } when contains.Value.Kind != FilterLiteralKind.Null && Top(column, version.DType) is int field =>
            new ExcludedValue(field, column.Path, contains, identifier: false),
        VariantFilterExpr { Inner: ComparisonExpr { Op: ComparisonOp.NotEqual, Field: VariantFieldExpr path } other } when other.Value.Kind != FilterLiteralKind.Null && ClassOf(path, version.DType) is not null =>
            new ExcludedValue(version.DType.IndexOfField(path.Column.Path), path.Text, VariantFilterExpr.Of(new ComparisonExpr(path, ComparisonOp.Equal, other.Value)), Identifies(version, path)) { Key = path },
        NotExpr { Operand: VariantFilterExpr { Inner: InExpr { Field: VariantFieldExpr path } set } fence } when Present(set) && ClassOf(path, version.DType) is not null =>
            new ExcludedValue(version.DType.IndexOfField(path.Column.Path), path.Text, fence, Identifies(version, path)) { Key = path },
        _ => null,
    };

    /// <summary>Whether <paramref name="version"/> declares the shredded key <paramref name="path"/> an identifier, whose postings every job writes.</summary>
    private static bool Identifies(DatasetSchema version, VariantFieldExpr path)
    {
        foreach (string identifier in version.Identifiers)
        {
            if (VortexDataset.IdentifierPath(identifier, version.DType) is { } key && string.Equals(key.Text, path.Text, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether <paramref name="set"/> holds values and no null, which <c>NOT IN</c> leaves no row of in its column's nulls.</summary>
    private static bool Present(InExpr set)
    {
        foreach (FilterLiteral value in set.Values)
        {
            if (value.Kind == FilterLiteralKind.Null)
            {
                return false;
            }
        }

        return set.Values.Count > 0;
    }

    /// <summary>
    /// The filter the granules of the object <paramref name="held"/> are judged by and the columns it reads: under a value left
    /// out of a column that is no identifier, which no index of the object places, the whole filter; the rest otherwise.
    /// </summary>
    private (SummaryPruner? Judge, int[] Filtered) JudgeOf(in PositionedObject held) =>
        _excluding is { Value: { Identifier: false } value } excluding && !value.Placed(held.Entry.Uid) ? (excluding.Whole, _filteredWhole) : (_filter, _filtered);

    /// <summary>Whether column <paramref name="field"/> is one of the sort key's, read as it is: one whose values a granule holds in order.</summary>
    private static bool Sorted(DatasetSchema schema, int field)
    {
        foreach (LayoutColumn component in schema.Layout?.Components ?? [])
        {
            if (component.Field == field && component.Function is null && component.Read is null)
            {
                return true;
            }
        }

        return false;
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
    /// Whether the granules of the object <paramref name="held"/> may answer: the version's columns in their places, and
    /// statistics of every column a key or a measure reads; a granule that holds a row a delete marked, which its statistics
    /// still count, is read as rows (<see cref="Settle"/>). And whether some granule of it may hold one group, at most one
    /// of the question's columns varying there under a question of counts, none under one of measures: judged granule by
    /// granule from what the object says each column holds (<see cref="ObjectContents.SingleRows"/>), or, for an object
    /// that does not say, from its summaries, the columns of the keys, themselves and not a function of them, and of a filter
    /// they do not prove true, other than the key's first and than those they prove constant, taken to vary in every
    /// granule. Past that, every granule would be read as rows after its statistics, a request for nothing. A function of a
    /// column, a truncation, holds one group over a granule whose extremes it sends to one. What they cost against the rows,
    /// the scan's choice weighs (<see cref="Costs"/>).
    /// </summary>
    internal bool Covers(in PositionedObject held) => Declines(held) is null;

    /// <summary>Why the granules of the object <paramref name="held"/> may not answer (<see cref="Covers"/>); null when they may.</summary>
    internal string? Declines(in PositionedObject held)
    {
        ObjectEntry entry = held.Entry;
        if (entry.Contents is not { GranuleStats.Count: > 0 } contents || !_schema.Holds(entry))
        {
            return entry.Contents is { GranuleStats.Count: > 0 } ? "its columns are an earlier schema's" : "it keeps no statistics over its granules";
        }

        // An object that does not say what each column holds granule by granule is judged by its summaries: the keys first,
        // which decide most questions with no look at the filter.
        ObjectSummaries summaries = held.Summaries(_schema);
        bool judged = contents.CountsSingleRows;
        int free = 0;
        foreach ((int field, FieldExpr key) in judged ? [] : _keys)
        {
            if (Array.IndexOf(_prefixed, field) < 0 && key is ColumnFieldExpr && CountsAnswer.ConstantOf(summaries, NameOf(field), key) is null)
            {
                free++;
            }
        }

        int allowed = CountsOnly ? 1 : 0;
        if (free > allowed)
        {
            return CountsOnly ? "two of its keys' columns may vary in a granule" : "a key's column may vary in a granule under a measure";
        }

        _ = Fields;
        (SummaryPruner? judge, int[] filtered) = JudgeOf(held);
        if (!judged && judge is { } filter && !filter.MustMatch(summaries.Columns, entry.PhysicalRows))
        {
            foreach (int field in filtered)
            {
                free += Array.IndexOf(_prefixed, field) < 0 && !Keyed(field) && Array.IndexOf(_ranged, field) < 0 ? 1 : 0;
            }
        }

        if (free > allowed)
        {
            return "the filter's columns and its keys' may vary in a granule, more than one";
        }

        // A column the filter alone reads may keep none: the object's summaries judge it. A shredded key an object keeps none
        // of, a residual one there, is read as rows.
        foreach ((int field, _) in _keys)
        {
            if (StatisticsOf(contents, field) is null)
            {
                return "a key's or a measure's column keeps no statistics over its granules";
            }
        }

        foreach (int field in _measured)
        {
            if (StatisticsOf(contents, field) is null)
            {
                return "a key's or a measure's column keeps no statistics over its granules";
            }
        }

        foreach (int field in Histogrammed)
        {
            if (contents.GranuleHistogramsOf(field) is null)
            {
                return "a quantile's column keeps no histograms over its granules";
            }
        }

        // An object whose every granule holds several values that the question needs one of reads its rows, the statistics a
        // request for nothing.
        return Unanswered(held) < 1 ? null : "every granule holds several values that the question needs one of";
    }

    /// <summary>
    /// What the object <paramref name="held"/> costs through its granules' statistics and their indexes, then through the rows of
    /// the granules they would not answer, as their share of the object's rows estimates them (<see cref="Unanswered"/>), and
    /// through the rows of the question's columns alone (<see cref="PathCosts"/>). Under a value of an identifier left out, the
    /// rows of a granule left are read under the whole filter, the identifier's column with the others. Of the
    /// <paramref name="kept"/> share of its rows the key's sparse index keeps, when it is known: the granules it refutes, their
    /// statistics refute too, and those it keeps leave as many unanswered as the object's do. A route's pods, under a key of
    /// the service and the route, were priced at the eighth of the object whose routes vary in a granule, where the five
    /// granules of the route leave two.
    /// </summary>
    internal (PathCosts.Cost Statistics, PathCosts.Cost Unanswered, PathCosts.Cost Rows) Costs(in PositionedObject held, double kept = 1)
    {
        ObjectContents contents = held.Entry.Contents!;
        int[] fields = Fields;
        int count = fields.Length + Histogrammed.Length + Components.Length;
        Span<IndexSegment?> regions = count <= 16 ? stackalloc IndexSegment?[count] : new IndexSegment?[count];
        int unread = Unread(held);
        for (int i = 0; i < fields.Length; i++)
        {
            regions[i] = fields[i] == unread ? null : StatisticsOf(contents, fields[i]);
        }

        for (int i = 0; i < Histogrammed.Length; i++)
        {
            regions[fields.Length + i] = contents.GranuleHistogramsOf(Histogrammed[i]);
        }

        for (int i = 0; i < Components.Length; i++)
        {
            regions[fields.Length + Histogrammed.Length + i] = contents.KeyRunsOf(Components[i])?.Region;
        }

        int[] read = _excluding is { } excluding ? excluding.ReadOf(ReadFields) : ReadFields;
        double unanswered = Unanswered(held, edges: true) * Math.Clamp(kept, 0, 1);
        return (_costs.Regions(held.Entry.Uid, regions), _costs.Share(held.Entry, read, unanswered, Unsliced(read)), _costs.Rows(held.Entry, read));
    }

    /// <summary>
    /// The columns of <paramref name="read"/> whose chunks a read of granules does not slice, read whole: a list's or a map's,
    /// whose elements no marks place granule by granule. Empty for most questions, which allocates nothing.
    /// </summary>
    private int[] Unsliced(int[] read)
    {
        if (_unsliced is { } known && ReferenceEquals(known.Read, read))
        {
            return known.Fields;
        }

        int[] fields = [];
        foreach (int field in read)
        {
            DType type = field < _schema.DType.FieldCount ? _schema.DType.GetField(field) : default;
            while (!type.IsDefault && type.Kind == DTypeKind.Extension)
            {
                type = type.StorageType;
            }

            if (!type.IsDefault && type.Kind is DTypeKind.List or DTypeKind.FixedSizeList or DTypeKind.Map)
            {
                fields = [.. fields, field];
            }
        }

        _unsliced = (read, fields);
        return fields;
    }

    /// <summary>
    /// The column of the value the filter leaves out whose statistics a read of the object <paramref name="held"/> leaves
    /// aside, -1 for none: one that is no identifier, whose indexes place the value in the object, and whose statistics weigh
    /// more than the rows of a granule the question reads, which the granules the value is placed in, about one, are read
    /// as when its statistics judge them not. A session among a thousand by service kept 100 KB of statistics, against 6 KB
    /// of rows a granule.
    /// </summary>
    internal int Unread(in PositionedObject held)
    {
        if (_excluding?.Value is not { Identifier: false } value || !value.Placed(held.Entry.Uid) || held.Entry.Contents is not { } contents
            || StatisticsOf(contents, JudgedOf(value)) is not { } region || held.Entry.PhysicalRows <= 0)
        {
            return -1;
        }

        double rows = 0;
        foreach (int field in ReadFields)
        {
            rows += field < contents.ColumnBytes.Count ? contents.ColumnBytes[field] : 0;
        }

        return region.Length > rows * Math.Min(1, (double)_granuleRows / held.Entry.PhysicalRows) ? JudgedOf(value) : -1;
    }

    /// <summary>
    /// What placing the value the filter leaves out in the object <paramref name="held"/> costs (<see cref="PlacingSource"/>):
    /// its locate's region, read beside the statistics, where the column is no identifier and the object keeps one; the
    /// regions of the Bloom filters, runs or postings of its column the table of contents names, every level of them, a bound,
    /// and a round trip for their directory, for another column or a key that is no identifier; two round trips cold for an
    /// identifier's postings, its partition's page then its runs; nothing where the value is not placed. A user's key under a
    /// Bloom filter of 786 KB in an object whose rows weighed 1.1 MB was priced at two round trips.
    /// </summary>
    internal PathCosts.Cost Placing(in PositionedObject held)
    {
        if (_excluding?.Value is not { } value || !value.Placed(held.Entry.Uid))
        {
            return default;
        }

        if (value.Identifier || held.Entry.Contents is not { } contents)
        {
            return new PathCosts.Cost(2, 0);
        }

        if (value.Key is null && contents.LocateOf(value.Field) is { } locate)
        {
            return _costs.Regions(held.Entry.Uid, [locate]);
        }

        // The entries named, read in one request, then each index's segments; an object whose table of contents does not say
        // their bytes is priced at the round trips alone.
        int count = 0;
        long bytes = 0;
        bool known = true;
        foreach (LocatingEntry entry in contents.Locating ?? [])
        {
            if (Places(entry, value))
            {
                count++;
                bytes += entry.Region.Length + entry.Bytes;
                known &= entry.Bytes > 0;
            }
        }

        return count == 0 || !known ? new PathCosts.Cost(2, 0) : new PathCosts.Cost(1 + count, bytes);
    }

    /// <summary>What places the value the filter leaves out in the object <paramref name="held"/>, for an explanation; null for nothing.</summary>
    internal string? PlacingSource(in PositionedObject held) =>
        _excluding?.Value is not { } value || !value.Placed(held.Entry.Uid) ? null
        : value.Identifier ? "its identifier's postings"
        : value.Key is null && held.Entry.Contents?.LocateOf(value.Field) is not null ? "its locate"
        : "the Bloom filters, runs or postings of its column";

    /// <summary>
    /// Whether <paramref name="entry"/> is an index of the column of <paramref name="value"/>, of its key on a variant's, that
    /// places a value by its blocks with no open; an entry that does not say its key is taken to be the key's.
    /// </summary>
    internal static bool Places(LocatingEntry entry, ExcludedValue value) =>
        entry.Column == (uint)value.Field && entry.Kind is IndexKinds.SortedRuns or IndexKinds.PostingsBlocks or IndexKinds.PostingsHashed or IndexKinds.BloomSbbf
        && (value.Key is not { } key || entry.Key is null || string.Equals(entry.Key, key.KeyName, StringComparison.Ordinal));

    /// <summary>
    /// The share of the rows of the object <paramref name="held"/> whose granules its statistics are not expected to answer:
    /// those where more of the question's columns vary than a granule's statistics answer, from what the object says each
    /// column holds granule by granule (<see cref="Varying"/>); for an object that does not say,
    /// those of the granules that hold several values of the key's first column or of the granule value
    /// (<see cref="ObjectContents.PureGranuleRows"/>), short runs gathered, unless the question would answer such a granule too,
    /// every column it keys or filters but one, under counts alone, constant over the object, and then those of the granules
    /// that do not list that column's values (<see cref="ObjectContents.ListedRows"/>), or, for a function of a column, those
    /// where its groups change (<see cref="Changing"/>); none when the object does not say,
    /// the granules then taken to answer, as before it said. And those of the granules that hold a row a delete marked,
    /// which are read as rows (<see cref="Settle"/>). With <paramref name="edges"/>, for a price that bounds what is
    /// read, those a window on a column of the key cuts too, which the choice of the path need not weigh: a window on the
    /// granule value cuts no granule of one value.
    /// </summary>
    internal double Unanswered(in PositionedObject held, bool edges = false)
    {
        ObjectEntry entry = held.Entry;
        if (entry.PhysicalRows <= 0)
        {
            return 0;
        }

        ObjectSummaries summaries = held.Summaries(_schema);
        _ = Fields;
        (SummaryPruner? judge, int[] filtered) = JudgeOf(held);
        bool proven = judge is not { } filter || filter.MustMatch(summaries.Columns, entry.PhysicalRows);
        double share = 0;
        if (entry.Contents is { CountsSingleRows: true } columns)
        {
            share = Varying(held, summaries, columns, proven, filtered);
        }
        else if (entry.Contents is { PureGranuleRows: >= 0 } contents)
        {
            // In a granule of several runs, the first column varies, and so does every key and filtered column the object's
            // summaries do not hold constant. One column that varies, under counts alone, answers where its values are
            // listed; a function of one, a truncation, where the granule's extremes fall in one group: all but the granules
            // where its groups change.
            int varying = 0;
            int listing = -1;
            double changing = 0;
            foreach ((int field, FieldExpr key) in _keys)
            {
                if (CountsAnswer.ConstantOf(summaries, NameOf(field), key) is null)
                {
                    varying++;
                    listing = key is ColumnFieldExpr && PathOf(field) is null ? field : -1;
                    changing = key is FunctionFieldExpr function ? Changing(summaries, field, function, entry.PhysicalRows) : 0;
                }
            }

            // A filtered column outside the sort key lies spread over every granule, however pure its runs of the key's first
            // columns, and a granule's statistics bound it by its extremes alone: a granule answers only where it lists the
            // column's values. A filter leaving one request of S1 out, its ids drawn at random, read an object's rows at every
            // asking, priced at a sixth of them.
            double spread = 0;
            foreach (int field in proven ? [] : filtered)
            {
                if (!Keyed(field) && Array.IndexOf(_ranged, field) < 0)
                {
                    varying++;
                    listing = field;
                    spread = Sorted(_schema, field) ? spread
                        : Math.Max(spread, 1 - Math.Clamp(contents.ListedRowsOf(field) / (double)entry.PhysicalRows, 0, 1));
                }
            }

            share = varying > (CountsOnly ? 1 : 0) ? 1 - Math.Clamp(contents.PureGranuleRows / (double)entry.PhysicalRows, 0, 1)
                : varying == 1 && listing >= 0 ? 1 - Math.Clamp(contents.ListedRowsOf(listing) / (double)entry.PhysicalRows, 0, 1)
                : varying == 1 ? changing
                : 0;
            share = Math.Max(share, spread);
        }

        // A granule that holds a row a delete marked is read as rows: the deleted rows taken to lie apart, as an erasure's do,
        // the share of the others' granules that none of them falls in e^(-deleted / granules).
        if (entry.DeletedRows > 0)
        {
            double granules = Math.Max(1, entry.PhysicalRows / (double)_granuleRows);
            share += (1 - share) * (1 - Math.Exp(-entry.DeletedRows / granules));
        }

        // A window the object's summaries do not prove cuts the granules at its bounds, read as rows: two at most on the first
        // column, whose values the object holds in order; on another column of the key, a granule of each run of the first,
        // every granule at most.
        foreach (int field in edges && !proven ? _ranged : [])
        {
            share += field == _leading ? Math.Min(1, 2.0 * _granuleRows / entry.PhysicalRows) : 1;
        }

        // The value of an identifier left out lies in a granule or so of an object, which its postings place: read as rows; a
        // value of another column, in the granules its indexes place it in, taken to be as few.
        if (edges && _excluding is not null)
        {
            share += Math.Min(1, (double)_granuleRows / entry.PhysicalRows);
        }

        return Math.Min(1, share);
    }

    /// <summary>
    /// The share of the rows of the object <paramref name="held"/> in the granules where more of the question's columns vary
    /// than their statistics answer, from the rows of the granules where each column holds one value
    /// (<see cref="ObjectContents.SingleRows"/>): a column of a key, or of a filter its summaries do not prove true, varies in
    /// the others, unless the summaries hold it constant over the object; a function of a column, a truncation, varies where
    /// the column does and its groups change (<see cref="FunctionVaries"/>). Under counts alone, one column may vary in a
    /// granule that lists its values, its rows counted value by value: the largest share a listed column varies in is taken
    /// off. The columns are taken to vary in granules apart, their shares added: a bound, which a sort key that holds them
    /// together makes loose, never short. Where the object keeps the runs of its prefix and the question's keys are read
    /// from them (<see cref="AddPrefixRuns"/>), the keys the prefix gives vary in the granules the runs do not describe
    /// alone, whatever they hold: a bound too, the lesser of the two taken.
    /// </summary>
    private double Varying(in PositionedObject held, ObjectSummaries summaries, ObjectContents contents, bool proven, int[] filtered)
    {
        double rows = held.Entry.PhysicalRows;
        double total = 0;
        double listed = 0;

        // The shares of the keys the prefix does not give, and of the filter's columns.
        double others = 0;
        for (int k = 0; k < _keys.Length; k++)
        {
            (int field, FieldExpr key) = _keys[k];
            if (CountsAnswer.ConstantOf(summaries, NameOf(field), key) is not null)
            {
                continue;
            }

            double varies = 1 - Math.Clamp(SingleRowsOf(contents, field) / rows, 0, 1);
            double share;
            if (key is FunctionFieldExpr function && RunsOf(k, contents) is { } runs)
            {
                // A key that takes its groups from a component's values by granule varies where the component does, and may
                // vary alone as a listed column does.
                share = 1 - Math.Clamp(runs.SingleRows / rows, 0, 1);
                listed = Math.Max(listed, share);
            }
            else if (key is FunctionFieldExpr other)
            {
                share = Math.Min(varies, FunctionVaries(summaries, field, other, contents, rows));
            }
            else
            {
                share = varies;
                listed = Math.Max(listed, Math.Min(varies, ListedShare(contents, field, rows)));
            }

            total += share;
            others += _prefixKeys is { } given && given[k].Component >= 0 ? 0 : share;
        }

        foreach (int field in proven ? [] : filtered)
        {
            if (!Keyed(field) && Array.IndexOf(_ranged, field) < 0)
            {
                double varies = 1 - Math.Clamp(SingleRowsOf(contents, field) / rows, 0, 1);
                total += varies;
                others += varies;
                listed = Math.Max(listed, Math.Min(varies, ListedShare(contents, field, rows)));
            }
        }

        double varying = Math.Clamp(total - (CountsOnly ? listed : 0), 0, 1);
        return _prefixKeys is not null && contents.KeyRunsOf(GranuleKeyRuns.WholePrefix) is { } prefix
            ? Math.Min(varying, Math.Clamp(others + 1 - (prefix.SingleRows / rows), 0, 1))
            : varying;
    }

    /// <summary>
    /// The share of the <paramref name="rows"/> rows of an object of <paramref name="contents"/> in the granules that list the
    /// values of column <paramref name="field"/>; all of them for a shredded key, whose statistics do not say: taken to list them.
    /// </summary>
    private double ListedShare(ObjectContents contents, int field, double rows) =>
        PathOf(field) is null ? Math.Clamp(contents.ListedRowsOf(field) / rows, 0, 1) : 1;

    /// <summary>The values by granule of the component key <paramref name="key"/> takes its groups from, as the object <paramref name="contents"/> describe it keeps them; null for none.</summary>
    private (IndexSegment Region, long SingleRows)? RunsOf(int key, ObjectContents contents)
    {
        foreach ((int held, int component, _) in _runKeys)
        {
            if (held == key)
            {
                return contents.KeyRunsOf(component);
            }
        }

        return null;
    }

    /// <summary>
    /// The share of the rows of an object of <paramref name="rows"/> rows in the granules where the groups of
    /// <paramref name="key"/>, a function of column <paramref name="field"/>, change: counted between the column's extremes
    /// for a column the object holds in order (<see cref="Changing"/>); under a key whose indexed prefix holds a function of
    /// the column, those outside the granules of one run of the prefix (<see cref="ObjectContents.PureGranuleRows"/>), whose
    /// values of the column lie within one group of that function; every granule otherwise.
    /// </summary>
    private double FunctionVaries(ObjectSummaries summaries, int field, FunctionFieldExpr key, ObjectContents contents, double rows)
    {
        if (InOrder(summaries, field))
        {
            return Changing(summaries, field, key, (long)rows);
        }

        if (contents.PureGranuleRows >= 0 && _schema.Layout is { } layout)
        {
            for (int c = 0; c < layout.Components.Count && c < layout.Prefix; c++)
            {
                if (layout.Components[c] is { Function: not null, Read: null } component && component.Field == field)
                {
                    return 1 - Math.Clamp(contents.PureGranuleRows / rows, 0, 1);
                }
            }
        }

        return 1;
    }

    /// <summary>
    /// The share of the rows of an object of <paramref name="rows"/> rows, whose summaries are <paramref name="summaries"/>,
    /// in the granules where the groups of <paramref name="key"/>, a function of column <paramref name="field"/> that varies
    /// over it, change: a granule a change, the changes counted between the column's extremes, of an integer storage, one
    /// of another, for a column the object holds in order (<see cref="InOrder"/>); every granule for another.
    /// </summary>
    private double Changing(ObjectSummaries summaries, int field, FunctionFieldExpr key, long rows)
    {
        if (!InOrder(summaries, field))
        {
            return 1;
        }

        // Each group past the first starts at the least value whose image passes the one before's; beyond a change a
        // granule, every granule holds one.
        long granules = (rows + _granuleRows - 1) / _granuleRows;
        long changes = 0;
        ValueFunction function = key.Function;
        if (key.Column is ColumnFieldExpr && summaries.TryGet(_schema.Columns[field].Name, out ColumnSummary column) && column.HasMin && column.HasMax
            && ValueFunction.TryInteger(column.Min, out Int128 at) && ValueFunction.TryInteger(column.Max, out Int128 high))
        {
            while (changes < granules && function.TryMap(function.Literal(at), out FilterLiteral image) && ValueFunction.TryInteger(image, out Int128 group)
                && function.TryLeast(group + 1, out Int128 next) && next <= high)
            {
                changes++;
                at = next;
            }
        }

        return Math.Min(1, Math.Max(1, changes) * (double)_granuleRows / rows);
    }

    /// <summary>
    /// Whether the object whose summaries are <paramref name="summaries"/> holds column <paramref name="field"/> in order: a
    /// column of the sort key whose components before it, of other columns, each hold one value over the object; a
    /// function of the column itself orders it as the column does.
    /// </summary>
    private bool InOrder(ObjectSummaries summaries, int field)
    {
        foreach (LayoutColumn component in _schema.Layout?.Components ?? [])
        {
            if (component.Read is not null)
            {
                return false;
            }

            if (component.Field == field)
            {
                if (component.IsColumn)
                {
                    return true;
                }
            }
            else if (!summaries.TryGet(_schema.Columns[component.Field].Name, out ColumnSummary column) || !column.HasMin || !column.HasMax
                || !column.HasNullCount || column.NullCount != 0 || !Same(component.Function, column.Min, column.Max))
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>Whether <paramref name="low"/> and <paramref name="high"/> are one value through <paramref name="function"/>, or themselves.</summary>
    private static bool Same(ValueFunction? function, FilterLiteral low, FilterLiteral high) =>
        function is null
            ? KeyCursor.Compare(low, high) == 0
            : function.TryMap(low, out FilterLiteral first) && function.TryMap(high, out FilterLiteral last) && KeyCursor.Compare(first, last) == 0;

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
    /// The states the granules of the object <paramref name="held"/> give, from the statistics and the indexes of
    /// <see cref="Fields"/>, by their places in it, and the ranges of its live rows, from its first, that its granules do not
    /// answer, which are read as rows; every row when the statistics do not describe the object's granules alike. A granule
    /// that holds a row of <paramref name="deletions"/> is not answered: its statistics count that row. A quantile takes the
    /// histograms of <paramref name="histograms"/>, by the same places. Of the granules within <paramref name="part"/> alone,
    /// the object's own positions a lane's piece of it covers, deleted rows included: the rows it holds of a granule it cuts
    /// are read as rows. Under a value of an identifier left out (<see cref="Exclusion"/>), the granules are judged under the
    /// rest of the filter, and those that <paramref name="holding"/>, the runs of the object's rows its postings place the
    /// value in, touches are read as rows; with none known, every granule is judged under the whole filter. A key whose
    /// groups a component of the sort key gives takes them from <paramref name="runs"/>, by <see cref="Components"/>' places.
    /// </summary>
    internal (GranuleStateRows? States, List<RowRange> Unsettled, int Settled) Settle(
        in PositionedObject held, GranuleStats?[] columns, GranuleHistograms?[] histograms, GranuleKeyRuns?[] runs, DeletionVector deletions, RowRange part,
        List<RowRange>? holding = null)
    {
        _ = Fields;
        bool placed = _excluding is null || holding is not null;
        (GranuleStateRows? states, List<RowRange> unsettled, int settled) =
            SettlePhysical(held, columns, histograms, runs, deletions, part, placed ? _filter : _excluding!.Whole, _excluding is null ? null : holding);
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

    /// <summary>
    /// <see cref="Settle"/>, its ranges the object's own row positions, deleted rows included: each granule judged by
    /// <paramref name="judge"/>, and one that <paramref name="holding"/> touches read as rows.
    /// </summary>
    private (GranuleStateRows? States, List<RowRange> Unsettled, int Settled) SettlePhysical(
        in PositionedObject held, GranuleStats?[] columns, GranuleHistograms?[] histograms, GranuleKeyRuns?[] runs, DeletionVector deletions, RowRange part,
        SummaryPruner? judge, List<RowRange>? holding)
    {
        long objectRows = held.Entry.PhysicalRows;
        GranuleStats? any = null;
        foreach (GranuleStats? stats in columns)
        {
            if (stats is null)
            {
                continue;
            }

            if ((any is not null && (stats.Count != any.Count || !stats.Rows.AsSpan().SequenceEqual(any.Rows))) || stats.TotalRows != objectRows)
            {
                return (null, [part], 0);
            }

            any = stats;
        }

        if (any is null || !Alike(histograms, any.Count))
        {
            return (null, [part], 0);
        }

        // The values of a component that do not describe the same granules are not taken.
        GranuleKeyRuns?[] alike = runs;
        for (int r = 0; r < runs.Length; r++)
        {
            if (runs[r] is { } values && values.Count != any.Count)
            {
                alike = alike == runs ? (GranuleKeyRuns?[])runs.Clone() : alike;
                alike[r] = null;
            }
        }

        ObjectSummaries summaries = held.Summaries(_schema);
        GranuleColumn[] described = new GranuleColumn[Fields.Length];
        for (int i = 0; i < Fields.Length; i++)
        {
            described[i] = new GranuleColumn(NameOf(Fields[i]), TypeOf(Fields[i]), columns[i], histograms[i]);
        }

        // The granules the part holds a row of, from lo to hi; those it holds whole alone may be answered.
        int lo = 0;
        while (lo < any.Count && any.FirstRow(lo) + any.Rows[lo] <= part.Start)
        {
            lo++;
        }

        int hi = lo;
        while (hi < any.Count && any.FirstRow(hi) < part.End)
        {
            hi++;
        }

        // Which granules their statistics answer, then, for an object with rows marked, whose granules left a part of the scan
        // reads a run each, those answered between two read as rows that cost less read as rows too than a part of the scan
        // more, which would read again the chunks the two parts share. Any other object's granules left are read in one part,
        // by a mask of them (DatasetScanBuilder.UnsettledPart), a run between two no part more: bridged, two hours of four
        // services by service and hour read 2.37 MB, where the granules left and the statistics hold 0.51, the rows 3.04.
        GranuleStateRows states = new GranuleStateRows(this);
        List<ColumnSummary> judged = new List<ColumnSummary>(Fields.Length + summaries.Columns.Count);
        PrefixValues? prefixRuns = _prefixAt >= 0 && _prefixAt < alike.Length && alike[_prefixAt] is { IsPrefix: true } prefix && _schema.Tree is { } tree
            ? new PrefixValues(prefix, tree, _schema.Partitioned ? 1 : 0, KeyFields.Length, _prefixKeys)
            : null;

        // A granule the places of a value left out touch is judged under the whole filter, where the value's column is no
        // identifier: its statistics may list the value's rows apart, or refute the rest.
        bool[] answers = new bool[any.Count];
        bool[]? wholly = holding is null ? null : new bool[any.Count];
        SummaryPruner? whole = _excluding?.Whole;
        int touched = 0;
        for (int b = lo; b < hi; b++)
        {
            long first = any.FirstRow(b);
            bool placed = Touches(holding, ref touched, first, first + any.Rows[b]);
            wholly?[b] = placed;
            answers[b] = first >= part.Start && first + any.Rows[b] <= part.End && deletions.DeletedIn(first, first + any.Rows[b]) == 0
                && (placed ? whole is not null && SettleGranule(b, any.Rows[b], described, alike, summaries, judged, states, whole, prefixRuns)
                    : SettleGranule(b, any.Rows[b], described, alike, summaries, judged, states, judge, prefixRuns));
            states.Truncate(0);
        }

        if (!deletions.IsEmpty)
        {
            Bridge(held.Entry, any, answers, lo, hi);
        }

        List<RowRange> unsettled = [];
        int settled = 0;
        for (int b = lo; b < hi; b++)
        {
            long first = any.FirstRow(b);
            long rows = any.Rows[b];
            if (answers[b])
            {
                SettleGranule(b, rows, described, alike, summaries, judged, states, wholly?[b] == true ? whole : judge, prefixRuns);
                settled++;
                continue;
            }

            // The rows the part holds of it.
            long start = Math.Max(first, part.Start);
            long end = Math.Min(first + rows, part.End);
            if (unsettled.Count > 0 && unsettled[^1].End == start)
            {
                unsettled[^1] = new RowRange(unsettled[^1].Start, end);
            }
            else
            {
                unsettled.Add(new RowRange(start, end));
            }
        }

        if (states.Count == 0)
        {
            states.Release();
            return (null, unsettled, settled);
        }

        return (states, unsettled, settled);
    }

    /// <summary>
    /// Whether <paramref name="holding"/>, ascending runs of rows, touches rows <c>[first, end)</c>, asked of granules in their
    /// order: <paramref name="at"/> the first run that may, moved on as they pass.
    /// </summary>
    private static bool Touches(List<RowRange>? holding, ref int at, long first, long end)
    {
        if (holding is null)
        {
            return false;
        }

        while (at < holding.Count && holding[at].End <= first)
        {
            at++;
        }

        return at < holding.Count && holding[at].Start < end;
    }

    /// <summary>Whether each of <paramref name="histograms"/> describes <paramref name="granules"/> granules, as the statistics do.</summary>
    private static bool Alike(GranuleHistograms?[] histograms, int granules)
    {
        foreach (GranuleHistograms? held in histograms)
        {
            if (held is not null && held.Count != granules)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Reads as rows each run of granules <paramref name="answers"/> answers between two it does not, of those from
    /// <paramref name="lo"/> to <paramref name="hi"/>, when reading its rows of the question's columns costs less than a part
    /// of the scan more (<see cref="PathCosts.Bridges"/>).
    /// </summary>
    private void Bridge(ObjectEntry entry, GranuleStats stats, bool[] answers, int lo, int hi)
    {
        int b = lo;
        while (b < hi && answers[b])
        {
            b++;
        }

        while (b < hi)
        {
            // From an unanswered granule, the run of answered ones after it, and whether an unanswered one ends it.
            while (b < hi && !answers[b])
            {
                b++;
            }

            int start = b;
            long rows = 0;
            while (b < hi && answers[b])
            {
                rows += stats.Rows[b];
                b++;
            }

            if (b < hi && _costs.Bridges(entry, ReadFields, rows))
            {
                Array.Fill(answers, false, start, b - start);
            }
        }
    }

    /// <summary>
    /// Settles granule <paramref name="granule"/> of <paramref name="rows"/> rows into <paramref name="states"/>: true when its
    /// statistics answer it under <paramref name="judge"/>, its rows of states added, none when they prove the filter selects
    /// no row of it. Where more of the keys' columns vary than its statistics answer, the runs of the prefix
    /// <paramref name="values"/> reads may answer it run by run (<see cref="AddPrefixRuns"/>).
    /// </summary>
    private bool SettleGranule(
        int granule, long rows, GranuleColumn[] columns, GranuleKeyRuns?[] runs, ObjectSummaries summaries, List<ColumnSummary> judged, GranuleStateRows states,
        SummaryPruner? judge, PrefixValues? values)
    {
        // The granule's summaries: each column's own, the object's for the columns the granule keeps none of.
        judged.Clear();
        foreach (GranuleColumn column in columns)
        {
            if (column.SummaryOf(granule) is { } own)
            {
                judged.Add(own);
            }
        }

        // By place rather than through the list's enumerator, which an interface boxes: a granule asks it of each column.
        int kept = judged.Count;
        IReadOnlyList<ColumnSummary> held = summaries.Columns;
        for (int c = 0; c < held.Count; c++)
        {
            if (IndexOf(judged, kept, held[c].Path) < 0)
            {
                judged.Add(held[c]);
            }
        }

        if (judge is { } filter && !filter.MayMatch(judged, rows))
        {
            return true;
        }

        bool whole = judge is not { } proving || proving.MustMatch(judged, rows);
        int[] filtered = _excluding is { } excluding && ReferenceEquals(judge, excluding.Whole) ? _filteredWhole : _filtered;

        // The column that varies: a key's column that holds more than one group, or, under a filter the granule's summaries
        // do not prove, a column it reads that holds more than one value; at most one, which lists its values, or whose key
        // takes its groups from a component's values over the granule, under a filter proven.
        int varying = -1;
        int byRuns = -1;
        for (int i = 0; i < Fields.Length; i++)
        {
            bool varies = !KeysHoldOneGroup(i, columns[i], granule, rows)
                || (!whole && Array.IndexOf(filtered, Fields[i]) >= 0 && !columns[i].HoldsOneValue(granule, rows));
            if (!varies)
            {
                continue;
            }

            int run = whole && !columns[i].Lists(granule) ? RunOf(Fields[i], runs) : -1;
            if (varying >= 0 || !CountsOnly || !(columns[i].Lists(granule) || run >= 0))
            {
                return whole && values is not null && AddPrefixRuns(granule, rows, columns, values, states);
            }

            varying = i;
            byRuns = run;
        }

        if (varying < 0)
        {
            return whole && AddWhole(granule, rows, columns, states);
        }

        if (byRuns >= 0)
        {
            return AddRuns(granule, rows, varying, columns, runs[byRuns]!, _runKeys[byRuns], states);
        }

        // Each of its values, and its nulls, judged apart: the column's summary replaced by the value's.
        GranuleColumn x = columns[varying];
        int at = IndexOf(judged, judged.Count, x.Path);
        (ReadOnlyMemory<int> ids, ReadOnlyMemory<long> counted) = x.Stats!.ValuesOf(granule);
        for (int v = 0; v <= ids.Length; v++)
        {
            long taken = v < ids.Length ? counted.Span[v] : x.Stats.Nulls[granule];
            if (taken == 0)
            {
                continue;
            }

            byte[]? value = v < ids.Length ? x.ValueOf(ids.Span[v]) : null;
            if (v < ids.Length && value is null)
            {
                return false;
            }

            judged[at] = x.SummaryOfValue(v < ids.Length ? x.LiteralOf(ids.Span[v]) : null, taken);
            if (judge is { } each && !each.MayMatch(judged, taken))
            {
                continue;
            }

            if (judge is { } proven && !proven.MustMatch(judged, taken))
            {
                return false;
            }

            int row = states.Add(taken);
            for (int k = 0; k < KeyFields.Length; k++)
            {
                int place = Array.IndexOf(Fields, KeyFields[k]);
                states.SetKey(row, k, place == varying ? value : columns[place].ConstantOf(granule, rows));
            }
        }

        return true;
    }

    /// <summary>The place among <see cref="Components"/> of the values a key of column <paramref name="field"/> takes its groups from; -1 for none, or none read.</summary>
    private int RunOf(int field, GranuleKeyRuns?[] runs)
    {
        for (int r = 0; r < _runKeys.Length && r < runs.Length; r++)
        {
            if (_keys[_runKeys[r].Key].Field == field && runs[r] is not null)
            {
                return r;
            }
        }

        return -1;
    }

    /// <summary>
    /// The rows of states of granule <paramref name="granule"/> of <paramref name="rows"/> rows, whose column at
    /// <paramref name="varying"/> holds several groups of a key that <paramref name="run"/> gives, from the values of its
    /// component over the granule <paramref name="runs"/> holds: each value's class, from the least value of the column
    /// whose image is it to the least whose image is past it, falls in one group of the key, its rows that group's, the
    /// group stood for by the class's least value as the column stores it; the other keys' columns hold one value each.
    /// False when a class straddles two groups, or the values do not count the granule's rows.
    /// </summary>
    private bool AddRuns(int granule, long rows, int varying, GranuleColumn[] columns, GranuleKeyRuns runs, (int Key, int Component, ValueFunction Of) run, GranuleStateRows states)
    {
        FieldExpr key = _keys[run.Key].Key;
        if (key is not FunctionFieldExpr function)
        {
            return false;
        }

        (ReadOnlyMemory<long> values, ReadOnlyMemory<long> counted) = runs.Of(granule);
        long total = 0;
        foreach (long count in counted.Span)
        {
            total += count;
        }

        if (total != rows)
        {
            return false;
        }

        int first = states.Count;
        for (int v = 0; v < values.Length; v++)
        {
            if (StandsFor(function, run.Of, values.Span[v], columns[varying]) is not { } stands)
            {
                states.Truncate(first);
                return false;
            }

            int row = states.Add(counted.Span[v]);
            for (int k = 0; k < KeyFields.Length; k++)
            {
                int place = Array.IndexOf(Fields, KeyFields[k]);
                states.SetKey(row, k, place == varying ? stands : columns[place].ConstantOf(granule, rows));
            }
        }

        return true;
    }

    /// <summary>
    /// The value of <paramref name="column"/> that stands for the group of <paramref name="key"/>, a function of the column,
    /// holding the class of <paramref name="value"/> under <paramref name="of"/>, a function of the column too: the class,
    /// from the least value of the column whose image is it to the least whose image is past it, falls in one group of the
    /// key, stood for by the class's least value as the column stores it. Null when it straddles two.
    /// </summary>
    private static byte[]? StandsFor(FunctionFieldExpr key, ValueFunction of, long value, GranuleColumn column) =>
        of.TryLeast(value, out Int128 least) && of.TryLeast((Int128)value + 1, out Int128 next) && next > least
            && CountsAnswer.Image(key, key.Function.Literal(least)) is { } low
            && CountsAnswer.Image(key, key.Function.Literal(next - 1)) is { } high && KeyCursor.Compare(low, high) == 0
            ? column.KeyOf(key.Function.Literal(least))
            : null;

    /// <summary>
    /// The rows of states of granule <paramref name="granule"/> of <paramref name="rows"/> rows, where more of the keys'
    /// columns vary than its statistics answer, from the runs of the whole indexed prefix <paramref name="values"/> reads:
    /// each run holds one value of every component, so one group of each key the prefix gives (<see cref="_prefixKeys"/>), a
    /// column of it as the run holds it, a function of a column a component is a function of by the component's class
    /// (<see cref="StandsFor"/>); every other key's column holds one group over the granule. Runs of the same groups, one
    /// after the other, make one row. False for a question of measures, a granule the runs do not describe, a key's column
    /// the prefix does not give that varies, or a class that straddles two groups.
    /// </summary>
    private bool AddPrefixRuns(int granule, long rows, GranuleColumn[] columns, PrefixValues values, GranuleStateRows states)
    {
        (int First, int Count) runs = values.Runs.RunsOf(granule);
        long total = 0;
        for (int r = runs.First; r < runs.First + runs.Count; r++)
        {
            total += values.Runs.RowsOf(r);
        }

        if (!CountsOnly || _prefixKeys is not { } given || runs.Count == 0 || total != rows)
        {
            return false;
        }

        // The keys the prefix does not give, one group each over the granule.
        for (int k = 0; k < given.Length; k++)
        {
            int place = Array.IndexOf(Fields, KeyFields[k]);
            if (given[k].Component < 0 && !columns[place].HoldsOneGroup(granule, rows, _keys[k].Key))
            {
                return false;
            }

            values.Current[k] = given[k].Component < 0 ? columns[place].ConstantOf(granule, rows) : null;
        }

        int first = states.Count;
        for (int r = runs.First; r < runs.First + runs.Count; r++)
        {
            ReadOnlySpan<byte> key = values.Runs.KeyOf(r);
            if (!values.Bound(key))
            {
                states.Truncate(first);
                return false;
            }

            bool same = states.Count > first;
            for (int k = 0; k < given.Length; k++)
            {
                if (given[k].Component >= 0)
                {
                    if (!values.TryStand(k, key, given[k], _keys[k].Key, columns[Array.IndexOf(Fields, KeyFields[k])]))
                    {
                        states.Truncate(first);
                        return false;
                    }
                }

                same &= ReferenceEquals(values.Current[k], values.Previous[k]);
            }

            if (same)
            {
                states.Words(states.Count - 1)[0] += values.Runs.RowsOf(r);
                continue;
            }

            int row = states.Add(values.Runs.RowsOf(r));
            for (int k = 0; k < given.Length; k++)
            {
                states.SetKey(row, k, values.Current[k]);
                values.Previous[k] = values.Current[k];
            }
        }

        return true;
    }

    /// <summary>
    /// The runs of the prefix an object keeps by granule, as a granule of a question its prefix gives the keys of reads them
    /// (<see cref="AddPrefixRuns"/>): each key's group in the run read, and in the run before it read; and, by key, the bytes
    /// of its component in the last run decoded and the value that stood for it, which the runs of one service and other
    /// hosts take again with no decoding.
    /// </summary>
    private sealed class PrefixValues(GranuleKeyRuns runs, SortKey tree, int skip, int keys, (int Component, ValueFunction? Of)[]? given)
    {
        private readonly byte[]?[] _bytes = new byte[]?[keys];
        private readonly int[] _lengths = new int[keys];
        private readonly byte[]?[] _stands = new byte[]?[keys];

        // The end of each value of the run's key up to the last a key takes, found once a run (Bound): the keys' values
        // are each found from the key's start, which walked it twice a key a run, 1.35 million values a question of the
        // distinct hosts by region over 97,000 runs.
        private readonly int[] _ends = new int[Needed(skip, given)];

        private static int Needed(int skip, (int Component, ValueFunction? Of)[]? given)
        {
            int last = -1;
            foreach ((int component, _) in given ?? [])
            {
                last = Math.Max(last, component);
            }

            return last < 0 ? 0 : skip + last + 1;
        }

        /// <summary>The runs.</summary>
        internal GranuleKeyRuns Runs { get; } = runs;

        /// <summary>Each key's group in the run read, as its column stores the value that stands for it.</summary>
        internal byte[]?[] Current { get; } = new byte[]?[keys];

        /// <summary>Each key's group in the run before it, of the same granule or the one before.</summary>
        internal byte[]?[] Previous { get; } = new byte[]?[keys];

        /// <summary>The ends of the values of the run's key <paramref name="run"/> that the keys take; false when one's width is unknown.</summary>
        internal bool Bound(ReadOnlySpan<byte> run) => tree.Ends(run, _ends);

        /// <summary>
        /// Sets <see cref="Current"/>'s <paramref name="k"/>th to the group of <paramref name="key"/> in the run of key
        /// <paramref name="run"/>, from the component <paramref name="given"/> names: its value as <paramref name="column"/>
        /// stores it, or the one standing for its class; false when it reads as none, or a class straddles two groups. The
        /// run's ends are <see cref="Bound"/>'s.
        /// </summary>
        internal bool TryStand(int k, ReadOnlySpan<byte> run, (int Component, ValueFunction? Of) given, FieldExpr key, GranuleColumn column)
        {
            int position = skip + given.Component;
            int at = position == 0 ? 0 : _ends[position - 1];
            ReadOnlySpan<byte> bytes = run[at.._ends[position]];
            if (_bytes[k] is { } held && bytes.SequenceEqual(held.AsSpan(0, _lengths[k])))
            {
                Current[k] = _stands[k];
                return true;
            }

            FilterLiteral literal = RowEncoder.ReadFirstValue(run[at..], tree.TypeAt(position));
            byte[]? stands;
            if (given.Of is not { } function)
            {
                stands = literal.Kind == FilterLiteralKind.Null ? null : column.KeyOf(literal);
                if (stands is null && literal.Kind != FilterLiteralKind.Null)
                {
                    return false;
                }
            }
            else if (key is not FunctionFieldExpr coarser || !ValueFunction.TryInteger(literal, out Int128 value) || value < long.MinValue || value > long.MaxValue
                || (stands = StandsFor(coarser, function, (long)value, column)) is null)
            {
                return false;
            }

            byte[] kept = _bytes[k] ?? new byte[16];
            _lengths[k] = SortedRows.Keep(ref kept, bytes);
            _bytes[k] = kept;
            Current[k] = _stands[k] = stands;
            return true;
        }
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

    /// <summary>Whether every key on the column at <see cref="Fields"/>' <paramref name="place"/> holds one group over the granule: true for a column no key reads.</summary>
    private bool KeysHoldOneGroup(int place, GranuleColumn column, int granule, long rows)
    {
        foreach ((int field, FieldExpr key) in _keys)
        {
            if (field == Fields[place] && !column.HoldsOneGroup(granule, rows, key))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A granule no column of the question varies in, the filter proven over its rows: its rows, each measure's states and the
    /// histograms a quantile asks, a row of states; false when its statistics lack one.
    /// </summary>
    private bool AddWhole(int granule, long rows, GranuleColumn[] columns, GranuleStateRows states)
    {
        int row = states.Add(rows);
        for (int k = 0; k < KeyFields.Length; k++)
        {
            int place = Array.IndexOf(Fields, KeyFields[k]);
            states.SetKey(row, k, columns[place].ConstantOf(granule, rows));
        }

        int word = 1;
        for (int m = 0; m < Measures.Length; m++)
        {
            (_, int field, bool floats, bool sketch) = Measures[m];
            GranuleColumn column = columns[Array.IndexOf(Fields, field)];
            if (!column.MeasureInto(granule, floats, states.Words(row).Slice(word, MeasureLayout.Words(floats))))
            {
                return false;
            }

            if (sketch)
            {
                if (column.Histograms is not { } histograms || histograms.Bits < _sketchBits)
                {
                    return false;
                }

                states.SetSketch(row, m, histograms.HistogramOf(granule));
            }

            word += MeasureLayout.Words(floats);
        }

        return true;
    }
}

/// <summary>
/// One column's statistics over an object's granules and where its values live, as a granule of a question reads them: the
/// summary it makes of the column, whether the column holds one value or one group of a key there, and that value.
/// </summary>
internal sealed class GranuleColumn(string path, DType dtype, GranuleStats? stats, GranuleHistograms? histograms = null)
{
    private readonly KeyLayout _layout = KeyLayout.TryOf(dtype, out KeyLayout layout) ? layout : default;
    private readonly bool _keyed = KeyLayout.TryOf(dtype, out _);

    /// <summary>The column, as a filter names it.</summary>
    internal string Path { get; } = path;

    /// <summary>Its statistics over the granules; null when the object keeps none of it.</summary>
    internal GranuleStats? Stats { get; } = stats;

    /// <summary>A measure's histograms over the granules, when a quantile asks them; null otherwise.</summary>
    internal GranuleHistograms? Histograms { get; } = histograms;

    /// <summary>Whether granule <paramref name="granule"/> lists the column's values, each named in the index.</summary>
    internal bool Lists(int granule) =>
        Stats is { HoldsValueLists: true } held && held.Distinct![granule] <= GranuleStats.ListedValues;

    /// <summary>The value numbered <paramref name="id"/>, as the column stores it; null for a number the dictionary lacks.</summary>
    internal byte[]? ValueOf(int id) => Stats?.ValueOf(id);

    /// <summary>The literal of the value numbered <paramref name="id"/>, decoded once with the statistics; null for none.</summary>
    internal FilterLiteral? LiteralOf(int id) =>
        Stats is { } stats && _keyed && (uint)id < (uint)stats.Named ? stats.Literals(_layout)[id] : null;

    /// <summary>
    /// The summary granule <paramref name="granule"/> makes of the column: the least and the greatest of the values it lists
    /// or of its measure, exact, and its nulls; null when it keeps no statistics.
    /// </summary>
    internal ColumnSummary? SummaryOf(int granule)
    {
        if (Stats is not { } stats)
        {
            return null;
        }

        long nulls = stats.Nulls[granule];
        if (Bounds(granule) is ({ } least, { } greatest))
        {
            return new ColumnSummary(Path, least, true, greatest, true, true, nulls, true);
        }

        return new ColumnSummary(Path, default, false, default, false, false, nulls, true);
    }

    /// <summary>The summary of the column holding the value <paramref name="value"/> stands for alone over <paramref name="rows"/> rows, or nothing but nulls for null.</summary>
    internal ColumnSummary SummaryOfValue(FilterLiteral? value, long rows) =>
        value is { } literal
            ? new ColumnSummary(Path, literal, true, literal, true, true, 0, true)
            : new ColumnSummary(Path, default, false, default, false, false, rows, true);

    /// <summary>The least and the greatest value of granule <paramref name="granule"/>: of those it lists, else of its measure; null for none known.</summary>
    private (FilterLiteral? Least, FilterLiteral? Greatest) Bounds(int granule)
    {
        GranuleStats stats = Stats!;
        if (!Lists(granule))
        {
            return stats.MeasureBounds(granule);
        }

        if (!_keyed)
        {
            return (null, null);
        }

        (int least, int greatest) = stats.ListedExtremes(granule, _layout);
        return least < 0 ? (null, null) : (LiteralOf(least), LiteralOf(greatest));
    }

    /// <summary>Whether granule <paramref name="granule"/> of <paramref name="rows"/> rows holds one value of the column, or nulls alone.</summary>
    internal bool HoldsOneValue(int granule, long rows)
    {
        if (Stats is not { } stats)
        {
            return false;
        }

        long nulls = stats.Nulls[granule];
        if (nulls == rows)
        {
            return true;
        }

        return nulls == 0 && Bounds(granule) is ({ } least, { } greatest) && KeyCursor.Compare(least, greatest) == 0;
    }

    /// <summary>
    /// Whether granule <paramref name="granule"/> of <paramref name="rows"/> rows holds one group of <paramref name="key"/>: one
    /// value or nulls alone, or no null and extremes the key's functions send to one group.
    /// </summary>
    internal bool HoldsOneGroup(int granule, long rows, FieldExpr key)
    {
        if (HoldsOneValue(granule, rows))
        {
            return true;
        }

        return key is FunctionFieldExpr && Stats is { } stats && stats.Nulls[granule] == 0 && Bounds(granule) is ({ } least, { } greatest)
            && CountsAnswer.Image(key, least) is { } low && CountsAnswer.Image(key, greatest) is { } high && KeyCursor.Compare(low, high) == 0;
    }

    /// <summary>The bytes the column stores <paramref name="value"/> as, a key's; null for a column no key holds, or a value it does not.</summary>
    internal byte[]? KeyOf(FilterLiteral value)
    {
        Span<byte> scratch = stackalloc byte[sizeof(ulong)];
        Span<byte> zero = stackalloc byte[sizeof(ulong)];
        return _keyed && _layout.TryEncode(value, scratch, out ReadOnlySpan<byte> key, zero, out _) ? key.ToArray() : null;
    }

    /// <summary>
    /// The value of the column that stands for the one group of granule <paramref name="granule"/> of <paramref name="rows"/>
    /// rows, as the column stores it: its value, or its least under a function that sends its extremes to one group; null
    /// for nulls.
    /// </summary>
    internal byte[]? ConstantOf(int granule, long rows)
    {
        GranuleStats stats = Stats!;
        if (stats.Nulls[granule] == rows)
        {
            return null;
        }

        if (Lists(granule))
        {
            return stats.ValuesOf(granule).Ids.Span is [int id, ..] ? ValueOf(id) : null;
        }

        return _keyed ? stats.MeasureLeast(granule, _layout) : null;
    }

    /// <summary>
    /// Writes into <paramref name="into"/> the states of the column as a measure over granule <paramref name="granule"/>, in
    /// <see cref="MeasureLayout"/>'s words: its own, or those its listed values give; false when it holds neither.
    /// </summary>
    internal bool MeasureInto(int granule, bool floats, Span<long> into)
    {
        GranuleStats stats = Stats!;
        if (stats.Measure is { } kind && (kind == MeasureKind.Float) == floats)
        {
            stats.MeasureOf(granule).CopyTo(into);
            return true;
        }

        if (!Lists(granule))
        {
            return false;
        }

        // A number of few values, which lists them: each value its rows' worth.
        into.Clear();
        (ReadOnlyMemory<int> ids, ReadOnlyMemory<long> counted) = stats.ValuesOf(granule);
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
