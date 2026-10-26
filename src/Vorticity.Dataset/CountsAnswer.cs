using System;
using System.Collections.Generic;
using Vorticity.Aggregating;
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
/// (<see cref="FieldOf(in PositionedObject)"/>): an object whose summaries prove the filter's other conjuncts true over its every row and the
/// other keys constant, with no row a delete marked, which its counts still hold, or with the marked rows counted by the
/// delete under a question that subtracts them (<see cref="Subtracts"/>). Any other object is read as rows. The
/// summaries bound the key's first column by the object's keys where its file does not, a text's; and values of that
/// column, under a question keyed by another, are proven so rather than counted, each object holding the values asked
/// alone when the application clusters by them.
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

        // A count, a sum, a mean or an exact distinct count reads what the counts of the rows deletes took subtract;
        // extremes, sketches and the registers of approximate distinct counts are of every row.
        Subtracts = !AsksSketches;
        foreach (AggregateKind kind in request.Kinds)
        {
            Subtracts &= kind is AggregateKind.Count or AggregateKind.Sum or AggregateKind.Average or AggregateKind.CountDistinct;
        }

        for (int key = request.Grouped; key < request.Keys.Count; key++)
        {
            Subtracts &= request.DistinctColumn(key) is null;
        }

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

    /// <summary>
    /// Whether the counts of an object a delete marked answer, less those of its deleted rows (<see cref="DeletedCounts"/>):
    /// the request reads counts, sums, means and exact distinct counts alone.
    /// </summary>
    internal bool Subtracts { get; }

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
        // object's summaries prove true over its rows. Values of the key's first column, under a question keyed by another
        // column, are proven so too: the clustering holds a value's rows together, so that each object holds the values
        // asked alone, and the other column's counts answer it whatever that column holds.
        int? column = null;
        List<byte[]>? values = null;
        List<VortexExpr> rest = [];
        (int Field, string Path, VortexExpr Found)? exclusion = null;
        if (filter is not null)
        {
            int? leading = version.Schema.Key is { } clustering ? Top(Expr.Field(clustering.InstantPath), schema) : null;
            bool keyedElsewhere = false;
            foreach (FieldExpr key in request.Keys)
            {
                keyedElsewhere |= key is ColumnFieldExpr && Top(key, schema) is int keyField && keyField != leading;
            }

            List<VortexExpr> conjuncts = [];
            GroupPredicates.Conjuncts(filter, conjuncts);
            foreach (VortexExpr conjunct in conjuncts)
            {
                (FieldExpr? asked, FilterLiteral[]? literals) = Asked(conjunct);
                if (asked is not null && Top(asked, schema) is int field && (column is null || column == field)
                    && !(keyedElsewhere && field == leading)
                    && ValuesOf(literals!, schema.GetField(field)) is { } wanted)
                {
                    column = field;
                    values = values is null ? wanted : values.FindAll(value => wanted.Exists(other => value.AsSpan().SequenceEqual(other)));
                    continue;
                }

                // One value of a declared identifier left out, whose rows the identifier's postings find in each object.
                if (exclusion is null && conjunct is ComparisonExpr { Op: ComparisonOp.NotEqual, Field: ColumnFieldExpr excluded } other
                    && other.Value.Kind != FilterLiteralKind.Null && Top(excluded, schema) is int identifier
                    && Identifies(version.Schema, identifier))
                {
                    exclusion = (identifier, excluded.Path, new ComparisonExpr(excluded, ComparisonOp.Equal, other.Value));
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
            request, version.Schema, column, keyed, values?.ToArray(), GroupPredicates.And(null, rest), [.. constants])
        {
            Exclusion = exclusion,
        };
    }

    /// <summary>Whether a column of <paramref name="dtype"/> named <paramref name="path"/> holds no null: its type says so, or the summaries.</summary>
    private static bool NoNull(ObjectSummaries summaries, string path, DType dtype) =>
        !dtype.IsNullable || (summaries.TryGet(path, out ColumnSummary column) && column.HasNullCount && column.NullCount == 0);

    /// <summary>Whether column <paramref name="field"/> of <paramref name="version"/> is a declared identifier, which every job indexes.</summary>
    private static bool Identifies(DatasetSchema version, int field)
    {
        string name = version.Columns[field].Name;
        foreach (string identifier in version.Identifiers)
        {
            if (string.Equals(identifier, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The value of a declared identifier the filter leaves out, <c>id != v</c>, a conjunct of it: the identifier's column,
    /// its name, and <c>id == v</c>, which the identifier's postings find the rows of in each object
    /// (<see cref="VortexDataset.LookupAsync"/>), whose counts a question that subtracts takes from the object's
    /// (<see cref="DeletedCounts.Less"/>). A row whose identifier is null is left out too, which a count cannot tell: an
    /// object whose summaries do not prove the column holds none is read as rows. Null for none; a second is a conjunct
    /// like any, which summaries prove or not.
    /// </summary>
    internal (int Field, string Path, VortexExpr Found)? Exclusion { get; private init; }

    /// <summary>
    /// The summaries an object <paramref name="held"/> is judged by: its entry's, the key's first column bounded by its keys
    /// where they do not bound it (<see cref="PositionedObject.Summaries"/>).
    /// </summary>
    internal ObjectSummaries SummariesOf(in PositionedObject held) => held.Summaries(_schema.Key);

    /// <summary>
    /// The column whose counts answer for the object <paramref name="held"/>: the column asked, or for a question of no
    /// column the one whose counts' region is least; null when its rows are read: rows a delete marked, which its counts
    /// still hold, unless the delete counted them and the question subtracts them; a conjunct or a key its summaries do
    /// not prove true over every row or constant; no counts of the column; or columns of an earlier schema, which its
    /// counts name by their places.
    /// </summary>
    internal int? FieldOf(in PositionedObject held)
    {
        ObjectEntry entry = held.Entry;
        if ((entry.HasDeletions && !(entry.HasNegatives && Subtracts)) || entry.Contents is not { Counts.Count: > 0 } contents || !_schema.Holds(entry))
        {
            return null;
        }

        ObjectSummaries summaries = SummariesOf(held);
        if (_rest is { } rest && !rest.MustMatch(summaries.Columns, entry.PhysicalRows))
        {
            return null;
        }

        // A value left out: its rows found by the identifier's postings, the object's counts less theirs, for an object no
        // delete marked whose identifier holds no null.
        if (Exclusion is (int excluded, string excludedPath, _)
            && (!Subtracts || entry.HasDeletions || contents.DirectoryRegion is null || !NoNull(summaries, excludedPath, _schema.DType.GetField(excluded))))
        {
            return null;
        }

        foreach ((_, string path, FieldExpr key) in _constants)
        {
            if (ConstantOf(summaries, path, key) is null)
            {
                return null;
            }
        }

        int counted = Column ?? Least(contents.Counts);
        return contents.CountsOf(counted) is null ? null : counted;
    }

    /// <summary>
    /// The column whose totals answer for the rollup <paramref name="rollup"/> (<see cref="RollupEntry"/>), as an object's
    /// counts answer for it: the union of its objects' summaries proves every conjunct the counts do not hold true over
    /// their rows and every other key constant, and it keeps the column's totals; the column asked, or for a question of
    /// no column the one whose totals' region is least. Null when its objects are taken one by one.
    /// </summary>
    internal int? FieldOf(RollupEntry rollup)
    {
        if ((_rest is { } rest && !rest.MustMatch(rollup.Summaries.Columns, rollup.Rows)) || rollup.Columns.Count == 0 || Exclusion is not null)
        {
            return null;
        }

        if (!Constant(rollup.Summaries))
        {
            return null;
        }

        // The column asked, or the least whose totals answer; its totals carry every measure the request reads, sketched
        // when a quantile asks, and keep every value when a key or the values asked read them.
        RollupColumn? counted = null;
        foreach (RollupColumn column in rollup.Columns)
        {
            if ((Column is null || column.Field == Column) && Answers(column, buckets: false) && (counted is not { } least || column.Counts.Length < least.Counts.Length))
            {
                counted = column;
            }
        }

        return counted?.Field;
    }

    /// <summary>
    /// The column whose counts by bucket answer for the rollup <paramref name="rollup"/> bucket by bucket, where its totals
    /// do not (<see cref="FieldOf(RollupEntry)"/>), and the summaries of each bucket: the rollup's, the key's first column
    /// bounded by the bucket's (<see cref="ClusteringKey.Within"/>), which prove every conjunct the counts do not hold true
    /// over the bucket's rows and every other key constant; null for a bucket they refute the filter over
    /// (<paramref name="pruner"/>), which gives no row. Null when a bucket does neither, or the rollup keeps no buckets.
    /// </summary>
    internal (int Field, ObjectSummaries?[] Buckets)? BucketsOf(RollupEntry rollup, ClusteringKey key, SummaryPruner? pruner)
    {
        if (rollup.Buckets.Count < 2 || Exclusion is not null)
        {
            return null;
        }

        ObjectSummaries?[] buckets = new ObjectSummaries?[rollup.Buckets.Count];
        for (int b = 0; b < buckets.Length; b++)
        {
            RollupBucket bucket = rollup.Buckets[b];
            ObjectSummaries summaries = key.Within(rollup.Summaries, bucket.Low, bucket.High);
            if (pruner is not null && !pruner.MayMatch(summaries.Columns, bucket.Rows))
            {
                continue;
            }

            if ((_rest is { } rest && !rest.MustMatch(summaries.Columns, bucket.Rows)) || !Constant(summaries))
            {
                return null;
            }

            buckets[b] = summaries;
        }

        RollupColumn? counted = null;
        foreach (RollupColumn column in rollup.Columns)
        {
            if (column.BucketCounts is { } region && (Column is null || column.Field == Column) && Answers(column, buckets: true)
                && (counted is not { } least || region.Length < least.BucketCounts!.Value.Length))
            {
                counted = column;
            }
        }

        return counted is { } chosen ? (chosen.Field, buckets) : null;
    }

    /// <summary>
    /// Whether the totals of <paramref name="column"/>, or its counts by bucket under <paramref name="buckets"/>, carry
    /// every measure the request reads, sketched when a quantile asks, and keep every value when a key or the values asked
    /// read them.
    /// </summary>
    private bool Answers(RollupColumn column, bool buckets)
    {
        foreach ((_, int field, _, bool sketch) in Measures)
        {
            if (Array.IndexOf(column.MeasureFields, field) < 0
                || (sketch && (!column.Sketched || (buckets && column.BucketSketches is null) || _request.SketchBits > ColumnCountsBuilder.SketchBits)))
            {
                return false;
            }
        }

        return !column.LeavesOut || (!Keyed && Values is null);
    }

    /// <summary>Whether <paramref name="summaries"/> prove every key of another column, or of a function of one, constant.</summary>
    private bool Constant(ObjectSummaries summaries)
    {
        foreach ((_, string path, FieldExpr key) in _constants)
        {
            if (ConstantOf(summaries, path, key) is null)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The value an object's column <paramref name="path"/> holds that makes <paramref name="key"/> constant over its rows,
    /// as its summaries prove it: the least, when the key's image of it is its greatest's and the column holds no null;
    /// null when they prove no such thing. Bounds that are not exact lie outwards, which keeps the proof: a non-decreasing
    /// function sends every value between them between their images.
    /// </summary>
    internal static FilterLiteral? ConstantOf(ObjectSummaries summaries, string path, FieldExpr key)
    {
        if (!summaries.TryGet(path, out ColumnSummary column) || !column.HasMin || !column.HasMax || !column.HasNullCount || column.NullCount != 0
            || Image(key, column.Min) is not { } low || Image(key, column.Max) is not { } high || KeyCursor.Compare(low, high) != 0)
        {
            return null;
        }

        return column.Min;
    }

    /// <summary>The image of <paramref name="value"/> through the functions of <paramref name="key"/>, the innermost first; null when one does not read it.</summary>
    internal static FilterLiteral? Image(FieldExpr key, FilterLiteral value)
    {
        if (key is not FunctionFieldExpr function)
        {
            return value;
        }

        return Image(function.Column, value) is { } inner && function.Function.TryMap(inner, out FilterLiteral image) ? image : null;
    }

    /// <summary>The column <paramref name="key"/> reads, under its functions.</summary>
    internal static FieldExpr Inner(FieldExpr key)
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
    internal static (int[] KeyFields, (string Name, int Field, bool Floats, bool Sketch)[] Measures, DType Type) Layout(
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

    /// <summary>
    /// The column and the values <paramref name="conjunct"/> asks by equality, by a set, or by a disjunction of them over
    /// one column; (null, null) for any other.
    /// </summary>
    private static (FieldExpr? Field, FilterLiteral[]? Literals) Asked(VortexExpr conjunct) => conjunct switch
    {
        ComparisonExpr { Op: ComparisonOp.Equal } equal => (equal.Field, [equal.Value]),
        InExpr set => (set.Field, set.Literals),
        LogicalExpr { IsAnd: false } either
            when Asked(either.Left) is (ColumnFieldExpr left, { } some) && Asked(either.Right) is (ColumnFieldExpr right, { } others)
            && left.Path == right.Path => (left, [.. some, .. others]),
        _ => (null, null),
    };

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
    internal static List<byte[]>? ValuesOf(FilterLiteral[] literals, DType dtype)
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
internal sealed class StateRows : StateTable
{
    private readonly ColumnCounts _counts;
    private readonly ColumnSketches? _sketches;
    private readonly int[] _entries;
    private readonly int _count;

    // Each measure, its place among the counts', and its first word among an entry's.
    private readonly (string Name, int Field, bool Floats, bool Sketch)[] _measures;
    private readonly int[] _places;
    private readonly int[] _wordAt;

    // The value each key's column holds over the object, as the column stores it, for a column the object holds constant;
    // null for the counted column, whose entries give their own.
    private readonly byte[]?[] _constants;

    internal StateRows(CountsAnswer answer, ColumnCounts counts, ColumnSketches? sketches, int[] entries, int count, int[] places, byte[]?[] constants)
        : base(answer.KeyFields.Length, answer.Measures, answer.Type, answer.Bool)
    {
        _counts = counts;
        _sketches = sketches;
        _entries = entries;
        _count = count;
        _measures = answer.Measures;
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
    internal override int Count => _count;

    /// <inheritdoc/>
    protected override void FillWords(Span<long> into, int start, int word)
    {
        // The word among an entry's: its rows, or a measure's word where the measure's place among the counts' puts it.
        int at = word;
        for (int i = 0, rest = word - 1; rest >= 0 && i < _places.Length; i++)
        {
            int length = MeasureLayout.Words(_measures[i].Floats);
            if (rest < length)
            {
                at = _wordAt[i] + rest;
                break;
            }

            rest -= length;
        }

        for (int i = 0; i < into.Length; i++)
        {
            into[i] = _counts.WordsOf(_entries[start + i])[at];
        }
    }

    /// <inheritdoc/>
    protected override byte[]? KeyOf(int row, int key) => _constants[key] ?? ValueOf(_entries[row]);

    /// <inheritdoc/>
    protected override byte[]? SketchOf(int row, int measure) => _sketches!.SketchOf(_entries[row], _places[measure]);

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
}
