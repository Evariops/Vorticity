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
/// greatest or a mean of a number column, none filtered, nor a key's top, nor a chosen row's column, and
/// whose every key is a column or a function of one, never a path of a variant.
/// </remarks>
internal sealed class StateRequest
{
    /// <summary>The column of the rows of the data a row of states stands for.</summary>
    internal const string RowsColumn = "count";

    private StateRequest(FieldExpr[] keys, string?[] inputs)
    {
        Keys = keys;
        Inputs = inputs;
    }

    /// <summary>The fields the keys read, functions included, in the plan's order.</summary>
    internal IReadOnlyList<FieldExpr> Keys { get; }

    /// <summary>Per aggregate of the plan, the column it folds; null for a count of rows, or for one the statistics settled.</summary>
    internal IReadOnlyList<string?> Inputs { get; }

    /// <summary>The column of how many values of <paramref name="column"/> a row of states folds.</summary>
    internal static string ValuesOf(string column) => column + ".values";

    /// <summary>The column of a word of the sum of <paramref name="column"/>: <c>low</c> and <c>high</c> for integers, <c>m0</c> to <c>m2</c>, <c>meta</c> and <c>high</c> for floats.</summary>
    internal static string SumWord(string column, string word) => column + ".sum." + word;

    /// <summary>The column of the least value of <paramref name="column"/>.</summary>
    internal static string LeastOf(string column) => column + ".min";

    /// <summary>The column of the greatest value of <paramref name="column"/>.</summary>
    internal static string GreatestOf(string column) => column + ".max";

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

        FieldExpr[] keys = new FieldExpr[plan.Keys.Length];
        for (int k = 0; k < keys.Length; k++)
        {
            if (!Keyable(plan.Keys[k].Field))
            {
                return null;
            }

            keys[k] = plan.Keys[k].Field;
        }

        string?[] inputs = new string?[plan.Aggregates.Length];
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
                    when aggregate.Input is { Kind: StorageKind.Primitive } input && Column(input.Field) && Numeric(input.PType):
                    inputs[i] = input.Path;
                    break;
                default:
                    return null;
            }
        }

        return new StateRequest(keys, inputs);
    }

    /// <summary>A key a batch of states carries: a top-level column, or functions of one.</summary>
    private static bool Keyable(FieldExpr field) => field switch
    {
        VariantFieldExpr => false,
        FunctionFieldExpr function => Keyable(function.Column),
        _ => Column(field),
    };

    /// <summary>A top-level column, read as it is.</summary>
    private static bool Column(FieldExpr field) =>
        field.GetType() == typeof(FieldExpr) && !field.Path.Contains('.', System.StringComparison.Ordinal);

    /// <summary>The numbers a synopsis keeps states of: integers, and floats of 32 and 64 bits.</summary>
    private static bool Numeric(PType ptype) =>
        ptype is PType.I8 or PType.I16 or PType.I32 or PType.I64 or PType.U8 or PType.U16 or PType.U32 or PType.U64 or PType.F32 or PType.F64;
}
