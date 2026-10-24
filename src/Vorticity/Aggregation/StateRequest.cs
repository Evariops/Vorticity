using System;
using System.Collections.Generic;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Types;

namespace Vorticity.Aggregating;

/// <summary>
/// What an aggregation asks of a source that keeps states of its rows, as a dataset's synopsis does: the
/// fields its keys read, through their functions, and per aggregate the column it folds, none for a count
/// of rows. A source answers the rows it keeps states of with batches of states
/// (<see cref="RecordBatch.CarriesStates"/>) whose columns carry the names below, and its other rows as
/// rows. The plan folds both to the bits the rows alone would give.
/// </summary>
/// <remarks>
/// Asked only of a pass whose every aggregate a state answers whole: a count of rows, a sum, a least, a
/// greatest or a mean of a number column or of a path of integers, a distribution or a quantile of one, which a
/// table of sketches answers, or a distinct count of a column, exact or approximate, which the states are then
/// keyed by too; none filtered, nor a key's top, nor a chosen row's
/// column, and whose every key is a column, a function of one, or a path of a variant a synopsis counts
/// (<see cref="VariantFieldExpr.CountedClass"/>). A path's states carry its text as their name, a key's column the
/// name <see cref="KeyColumn"/> gives it.
/// </remarks>
internal sealed class StateRequest
{
    /// <summary>The column of the rows of the data a row of states stands for.</summary>
    internal const string RowsColumn = "count";

    /// <summary>The column of a table of states' buckets, which a batch of states carries under the instant's name.</summary>
    internal const string BucketColumn = "bucket";

    /// <summary>What ends the name of a key's column whose own name a column of states could take (<see cref="KeyColumn"/>).</summary>
    private const string KeyMark = "#key";

    // The words that end the name of a column of states, after a dot and the name of its measure or key.
    private const string ValuesWord = "values";
    private const string LeastWord = "min";
    private const string GreatestWord = "max";
    private const string SketchWord = "sketch";
    private const string DistinctWord = "distinct";

    /// <summary>Every word that ends the name of a column of states: those above, and those of a measure's sum (<see cref="MeasureLayout.SumWords"/>).</summary>
    private static readonly string[] StateWords =
        [ValuesWord, .. MeasureLayout.SumWords(floats: false), .. MeasureLayout.SumWords(floats: true), LeastWord, GreatestWord, SketchWord, DistinctWord];

    /// <summary>The keys <see cref="KeyField"/> renamed, each renamed once and let go with its key.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FieldExpr, FieldExpr> Renamed = [];

    /// <summary>
    /// The name a key's column takes in a table of states and in a batch of them: its own, unless a column of states could
    /// take it too, the bucket's, the rows', or one that ends in a dot and a state's word, or it ends in the mark the others
    /// take; then its own followed by that mark. No key takes the name of another column, nor of another key, and a table
    /// none of whose keys is named so keeps the names it always had.
    /// </summary>
    internal static string KeyColumn(string name) => Reserved(name) ? name + KeyMark : name;

    /// <summary>
    /// The key <paramref name="key"/> as a batch of states carries it: its column under the name <see cref="KeyColumn"/>
    /// gives it, through the same functions; a path as it is, under its text. A key whose column keeps its name is
    /// itself, and one renamed is made once for the key.
    /// </summary>
    internal static FieldExpr KeyField(FieldExpr key)
    {
        FieldExpr column = ColumnOf(key);
        return column is ColumnFieldExpr && Reserved(column.Path) ? Renamed.GetValue(key, Rename) : key;
    }

    /// <summary>The key <paramref name="key"/> over its column renamed by <see cref="KeyColumn"/>.</summary>
    private static FieldExpr Rename(FieldExpr key)
    {
        FieldExpr named = new ColumnFieldExpr([KeyColumn(ColumnOf(key).Path)]);
        return key is FunctionFieldExpr functions ? functions.Over(named) : named;
    }

    /// <summary>The column <paramref name="key"/> reads, under its functions.</summary>
    private static FieldExpr ColumnOf(FieldExpr key)
    {
        while (key is FunctionFieldExpr function)
        {
            key = function.Column;
        }

        return key;
    }

    /// <summary>Whether a column of states could take the name <paramref name="name"/> (<see cref="KeyColumn"/>).</summary>
    private static bool Reserved(string name)
    {
        if (name is RowsColumn or BucketColumn || name.EndsWith(KeyMark, StringComparison.Ordinal))
        {
            return true;
        }

        ReadOnlySpan<char> word = name.AsSpan(name.LastIndexOf('.') + 1);
        if (word.Length == name.Length)
        {
            return false;
        }

        foreach (string state in StateWords)
        {
            if (word.SequenceEqual(state))
            {
                return true;
            }
        }

        return false;
    }

    private StateRequest(FieldExpr[] keys, string?[] inputs, FieldExpr?[] inputFields, AggregateKind[] kinds)
    {
        Keys = keys;
        Inputs = inputs;
        InputFields = inputFields;
        Kinds = kinds;
    }

    /// <summary>The fields the keys read, functions included, in the plan's order.</summary>
    internal IReadOnlyList<FieldExpr> Keys { get; }

    /// <summary>
    /// Per aggregate of the plan, the name of the column it folds (<see cref="NameOf"/>); null for a count of rows,
    /// or for one the statistics settled.
    /// </summary>
    internal IReadOnlyList<string?> Inputs { get; }

    /// <summary>Per aggregate of the plan, the field it folds, a column or a path; null where <see cref="Inputs"/> is.</summary>
    internal IReadOnlyList<FieldExpr?> InputFields { get; }

    /// <summary>The name a key or a measure's states carry in a batch of states: a column's own, or a path's text.</summary>
    internal static string NameOf(FieldExpr field) => field is VariantFieldExpr path ? path.Text : field.Path;

    /// <summary>Per aggregate of the plan, what it is: which of a column's states it reads, its sum or one of its extremes.</summary>
    internal IReadOnlyList<AggregateKind> Kinds { get; }

    /// <summary>Whether an aggregate reads <paramref name="column"/>'s sum (a sum or a mean), its least (a minimum), its greatest (a maximum).</summary>
    internal (bool Sum, bool Least, bool Greatest) Reads(string column)
    {
        (bool sum, bool least, bool greatest) = (false, false, false);
        for (int i = 0; i < Inputs.Count; i++)
        {
            if (string.Equals(Inputs[i], column, System.StringComparison.Ordinal))
            {
                sum |= Kinds[i] is AggregateKind.Sum or AggregateKind.Average;
                least |= Kinds[i] == AggregateKind.Min;
                greatest |= Kinds[i] == AggregateKind.Max;
            }
        }

        return (sum, least, greatest);
    }

    /// <summary>The column of how many values of <paramref name="column"/> a row of states folds.</summary>
    internal static string ValuesOf(string column) => column + "." + ValuesWord;

    /// <summary>The column of a word of the sum of <paramref name="column"/>: <c>low</c> and <c>high</c> for integers, <c>m0</c> to <c>m2</c>, <c>meta</c> and <c>high</c> for floats.</summary>
    internal static string SumWord(string column, string word) => column + ".sum." + word;

    /// <summary>The column of the least value of <paramref name="column"/>.</summary>
    internal static string LeastOf(string column) => column + "." + LeastWord;

    /// <summary>The column of the greatest value of <paramref name="column"/>.</summary>
    internal static string GreatestOf(string column) => column + "." + GreatestWord;

    /// <summary>The column of the sketch of <paramref name="column"/>'s values (<see cref="QuantileSketch.Encode"/>), which a table of sketches holds.</summary>
    internal static string SketchOf(string column) => column + "." + SketchWord;

    /// <summary>
    /// The column of the distinct sketch of <paramref name="column"/>'s values (<see cref="CardinalitySketch.Encode"/>),
    /// which a table of sketches holds for each key deeper than its own.
    /// </summary>
    internal static string DistinctOf(string column) => column + "." + DistinctWord;

    /// <summary>
    /// The mantissa bits of the finest grid a distribution or a quantile asks for, 0 when none does: a row of states
    /// answers them with a sketch of as fine a grid or finer.
    /// </summary>
    internal int SketchBits { get; private init; }

    /// <summary>
    /// The request of <paramref name="plan"/>, or null when a state cannot answer one of its aggregates or
    /// keys, or when <paramref name="settled"/> or <paramref name="top"/> answer part of it apart.
    /// </summary>
    internal static StateRequest? Of(AggregationPlan plan, AggregateSlot?[] settled, KeyTop? top)
    {
        if (top is not null || plan.Chosen.Length > 0)
        {
            return null;
        }

        List<FieldExpr> keys = new List<FieldExpr>(plan.Keys.Length);
        for (int k = 0; k < plan.Keys.Length; k++)
        {
            if (!Keyable(plan.Keys[k].Field))
            {
                return null;
            }

            keys.Add(plan.Keys[k].Field);
        }

        string?[] inputs = new string?[plan.Aggregates.Length];
        FieldExpr?[] fields = new FieldExpr?[plan.Aggregates.Length];
        AggregateKind[] kinds = new AggregateKind[plan.Aggregates.Length];
        List<bool>? approximate = null;
        int sketchBits = 0;
        for (int i = 0; i < inputs.Length; i++)
        {
            // An aggregate the statistics settled is folded by no batch, of rows or of states.
            IAggregateNode aggregate = plan.Aggregates[i];
            if (settled[i] is not null)
            {
                continue;
            }

            if (aggregate.Filter is not null)
            {
                return null;
            }

            switch (aggregate.Kind)
            {
                case AggregateKind.Count when aggregate.Input is null:
                    break;
                case AggregateKind.Sum or AggregateKind.Min or AggregateKind.Max or AggregateKind.Average
                    when aggregate.Input is { Kind: StorageKind.Primitive } input && Measurable(input.Field) && Numeric(input.PType):
                    inputs[i] = NameOf(input.Field);
                    fields[i] = input.Field;
                    kinds[i] = aggregate.Kind;
                    break;
                case AggregateKind.Distribution or AggregateKind.Quantile
                    when aggregate.Input is { Kind: StorageKind.Primitive } input && Measurable(input.Field) && Numeric(input.PType)
                        && Aggregators.SketchBits(aggregate.Identity) is > 0 and var bits:
                    // A distribution merges the sketches of the rows of states, each of the values a row stands for.
                    inputs[i] = NameOf(input.Field);
                    fields[i] = input.Field;
                    kinds[i] = aggregate.Kind;
                    sketchBits = System.Math.Max(sketchBits, bits);
                    break;
                case AggregateKind.CountDistinct or AggregateKind.ApproximateCountDistinct
                    when aggregate.Input is { } distinct && Column(distinct.Field):
                    // A distinct count of a column the states may be keyed by: they come at its grain too, a row of states
                    // for each of its values in a group, and the count folds those values, the rows' own. A column only
                    // approximate counts fold may come instead as the sketch of its values a table of sketches holds.
                    bool approximately = aggregate.Kind == AggregateKind.ApproximateCountDistinct;
                    int at = keys.FindIndex(key => key is ColumnFieldExpr && string.Equals(key.Path, distinct.Field.Path, System.StringComparison.Ordinal));
                    if (at < 0)
                    {
                        keys.Add(distinct.Field);
                        (approximate ??= []).Add(approximately);
                    }
                    else if (at >= plan.Keys.Length)
                    {
                        approximate![at - plan.Keys.Length] &= approximately;
                    }

                    kinds[i] = aggregate.Kind;
                    break;
                default:
                    return null;
            }
        }

        // A question of no distinct count makes neither the list nor the names.
        string?[] sketches = approximate is null ? [] : new string?[approximate.Count];
        for (int k = 0; k < sketches.Length; k++)
        {
            sketches[k] = approximate![k] ? DistinctOf(NameOf(keys[plan.Keys.Length + k])) : null;
        }

        return new StateRequest([.. keys], inputs, fields, kinds) { Grouped = plan.Keys.Length, SketchBits = sketchBits, Distincts = sketches };
    }

    /// <summary>
    /// How many of <see cref="Keys"/> are the plan's own, which group its rows; past them, the columns its distinct counts
    /// fold, which a batch of states is keyed by too.
    /// </summary>
    internal int Grouped { get; private init; }

    /// <summary>For each key past <see cref="Grouped"/>, the column of its distinct sketches when only approximate distinct counts fold it.</summary>
    private string?[] Distincts { get; init; } = [];

    /// <summary>
    /// The column of the distinct sketches of key <paramref name="key"/> of <see cref="Keys"/> (<see cref="DistinctOf"/>),
    /// which a table of sketches holds for it in place of the key, when only approximate distinct counts fold it, which
    /// merge a sketch as they would have added its values; null otherwise.
    /// </summary>
    internal string? DistinctColumn(int key) => key >= Grouped ? Distincts[key - Grouped] : null;

    /// <summary>Whether an aggregate of <paramref name="kind"/> folds a column of a batch of states, the values it is keyed by, rather than states.</summary>
    internal static bool FoldsValues(AggregateKind kind) => kind is AggregateKind.CountDistinct or AggregateKind.ApproximateCountDistinct;

    /// <summary>A key a batch of states carries: a top-level column, functions of one, or a path a synopsis counts.</summary>
    private static bool Keyable(FieldExpr field) => field switch
    {
        VariantFieldExpr path => path.CountedClass is not null,
        FunctionFieldExpr function => function.Column is not VariantFieldExpr && Keyable(function.Column),
        _ => Column(field),
    };

    /// <summary>A measure a batch of states carries: a top-level column, or a path of integers or doubles a synopsis sums.</summary>
    private static bool Measurable(FieldExpr field) =>
        field is VariantFieldExpr path
            ? path.CountedClass is Types.Variants.VariantClass.Integer or Types.Variants.VariantClass.Double
            : Column(field);

    /// <summary>A top-level column, read as it is.</summary>
    private static bool Column(FieldExpr field) =>
        field is ColumnFieldExpr && !field.Path.Contains('.', System.StringComparison.Ordinal);

    /// <summary>The numbers a synopsis keeps states of: integers, and floats of 32 and 64 bits.</summary>
    private static bool Numeric(PType ptype) =>
        ptype is PType.I8 or PType.I16 or PType.I32 or PType.I64 or PType.U8 or PType.U16 or PType.U32 or PType.U64 or PType.F32 or PType.F64;
}
