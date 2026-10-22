using System;
using System.Collections.Generic;
using Vorticity.Expressions;

namespace Vorticity.Aggregating;

/// <summary>
/// A column of a group's results in a predicate or an order on groups: an aggregate, or a
/// component of the key, read from a column of the result the query adds for it and does not
/// deliver.
/// </summary>
internal sealed class ResultFieldExpr : FieldExpr
{
    internal ResultFieldExpr(string name, IResultNode? result, int component)
        : base([name])
    {
        Result = result;
        Component = component;
    }

    /// <summary>The aggregate, or null for a component of the key.</summary>
    internal IResultNode? Result { get; }

    /// <summary>The component of the key, or -1 for an aggregate.</summary>
    internal int Component { get; }

    /// <summary>The column of the result this field reads, of the result's own type.</summary>
    internal ResultColumn Column(ColumnShape[] keys) =>
        Result is not null ? Result.Column(Path, keys) : new KeyResultColumn(Path, keys[Component].Type, Component);
}

/// <summary>What follows a group by, before its <c>Select</c>, in the order written.</summary>
internal abstract class GroupOperator
{
}

/// <summary>A <c>Where</c> on groups: a predicate over their results, evaluated on columns of them.</summary>
internal sealed class GroupFilter : GroupOperator
{
    internal GroupFilter(VortexExpr predicate, ResultFieldExpr[] fields)
    {
        Predicate = predicate;
        Fields = fields;
    }

    internal VortexExpr Predicate { get; }

    /// <summary>The results the predicate reads, once each.</summary>
    internal ResultFieldExpr[] Fields { get; }
}

/// <summary>A <c>Skip</c> and a <c>Take</c> on groups, in the order they come at that point.</summary>
internal sealed class GroupWindow : GroupOperator
{
    internal GroupWindow(long skip, long take)
    {
        Skip = skip;
        Take = take;
    }

    internal long Skip { get; }

    /// <summary>The groups kept at most; <see cref="long.MaxValue"/> for every one.</summary>
    internal long Take { get; }
}

/// <summary>The predicates of the operators on groups: split into conjuncts, read, rewritten.</summary>
internal static class GroupPredicates
{
    /// <summary>The conjuncts of <paramref name="predicate"/>: its operands joined by AND, at any depth.</summary>
    internal static void Conjuncts(VortexExpr predicate, List<VortexExpr> into)
    {
        if (predicate is LogicalExpr { IsAnd: true } and)
        {
            Conjuncts(and.Left, into);
            Conjuncts(and.Right, into);
            return;
        }

        into.Add(predicate);
    }

    /// <summary>The conjunction of <paramref name="conjuncts"/>, or null for none.</summary>
    internal static VortexExpr? And(VortexExpr? first, List<VortexExpr> conjuncts)
    {
        VortexExpr? all = first;
        foreach (VortexExpr conjunct in conjuncts)
        {
            all = all is null ? conjunct : Expr.Logical(true, all, conjunct);
        }

        return all;
    }

    /// <summary>The fields <paramref name="predicate"/> reads.</summary>
    internal static void Fields(VortexExpr predicate, List<FieldExpr> into)
    {
        switch (predicate)
        {
            case FieldExpr field:
                into.Add(field);
                break;
            case ComparisonExpr comparison:
                into.Add(comparison.Field);
                break;
            case NullCheckExpr check:
                into.Add(check.Field);
                break;
            case InExpr membership:
                into.Add(membership.Field);
                break;
            case StringMatchExpr match:
                into.Add(match.Field);
                break;
            case ListContainsExpr contains:
                into.Add(contains.Field);
                break;
            case ColumnComparisonExpr columns:
                into.Add(columns.Left);
                into.Add(columns.Right);
                break;
            case LogicalExpr logical:
                Fields(logical.Left, into);
                Fields(logical.Right, into);
                break;
            case NotExpr not:
                Fields(not.Operand, into);
                break;
            case Compute.VariantFilterExpr fence:
                Fields(fence.Inner, into);
                break;
            default:
                break;
        }
    }

    /// <summary>Whether <paramref name="predicate"/> reads a result of a group: an aggregate compared in a filter on rows.</summary>
    internal static bool ReadsResults(VortexExpr predicate)
    {
        List<FieldExpr> fields = [];
        Fields(predicate, fields);
        return fields.Exists(field => field is ResultFieldExpr);
    }

    /// <summary>
    /// <paramref name="predicate"/> with each field replaced by <paramref name="map"/>'s: a part of it whose fields
    /// <paramref name="map"/> gives back as they are is kept as it is, the whole predicate when none changes.
    /// </summary>
    internal static VortexExpr Rewrite(VortexExpr predicate, Func<FieldExpr, FieldExpr> map)
    {
        switch (predicate)
        {
            case ComparisonExpr comparison:
                return Changed(comparison.Field, map, out FieldExpr field) ? new ComparisonExpr(field, comparison.Op, comparison.Value) : predicate;
            case NullCheckExpr check:
                return Changed(check.Field, map, out field) ? new NullCheckExpr(field, check.IsNull) : predicate;
            case InExpr membership:
                return Changed(membership.Field, map, out field) ? new InExpr(field, membership.Literals) : predicate;
            case StringMatchExpr match:
                return Changed(match.Field, map, out field) ? new StringMatchExpr(field, match.Op, match.Pattern, match.Escape) : predicate;
            case ListContainsExpr contains:
                return Changed(contains.Field, map, out field) ? new ListContainsExpr(field, contains.Value) : predicate;
            case ColumnComparisonExpr columns:
                bool left = Changed(columns.Left, map, out FieldExpr leftField);
                bool right = Changed(columns.Right, map, out FieldExpr rightField);
                return left || right ? new ColumnComparisonExpr(leftField, columns.Op, rightField) : predicate;
            case LogicalExpr logical:
                VortexExpr first = Rewrite(logical.Left, map);
                VortexExpr second = Rewrite(logical.Right, map);
                return ReferenceEquals(first, logical.Left) && ReferenceEquals(second, logical.Right) ? predicate : Expr.Logical(logical.IsAnd, first, second);
            case NotExpr not:
                VortexExpr operand = Rewrite(not.Operand, map);
                return ReferenceEquals(operand, not.Operand) ? predicate : new NotExpr(operand);
            default:
                return predicate;
        }
    }

    /// <summary>Whether <paramref name="map"/> gives <paramref name="field"/> another field, <paramref name="mapped"/>.</summary>
    private static bool Changed(FieldExpr field, Func<FieldExpr, FieldExpr> map, out FieldExpr mapped)
    {
        mapped = map(field);
        return !ReferenceEquals(mapped, field);
    }
}
