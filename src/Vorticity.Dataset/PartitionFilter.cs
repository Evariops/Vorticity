using System;
using System.Collections.Generic;
using Vorticity.Expressions;

namespace Vorticity.Dataset;

/// <summary>
/// A filter on the partition columns of a dataset, decided for a whole object by the one value of them it holds:
/// what lets a question skip or keep an object's rows without reading them, and an expiry drop a partition's objects.
/// </summary>
internal static class PartitionFilter
{
    /// <summary>
    /// What <paramref name="expr"/> is for every row of an object whose partition columns hold <paramref name="values"/>:
    /// true or false when it reads them alone through comparisons, sets, conjunctions, disjunctions and negations;
    /// null when it reads another column or anything else, or compares across kinds.
    /// </summary>
    internal static bool? Decided(VortexExpr expr, IReadOnlyDictionary<string, FilterLiteral> values)
    {
        switch (expr)
        {
            case ComparisonExpr comparison when comparison.Field.GetType() == typeof(FieldExpr)
                && values.TryGetValue(comparison.Field.Path, out FilterLiteral value) && comparison.Value.Kind == value.Kind:
                int order = Keys.KeyCursor.Compare(value, comparison.Value);
                return comparison.Op switch
                {
                    ComparisonOp.Equal => order == 0,
                    ComparisonOp.NotEqual => order != 0,
                    ComparisonOp.Less => order < 0,
                    ComparisonOp.LessOrEqual => order <= 0,
                    ComparisonOp.Greater => order > 0,
                    ComparisonOp.GreaterOrEqual => order >= 0,
                    _ => null,
                };
            case InExpr set when set.Field.GetType() == typeof(FieldExpr) && values.TryGetValue(set.Field.Path, out FilterLiteral member):
                bool found = false;
                foreach (FilterLiteral candidate in set.Literals)
                {
                    if (candidate.Kind != member.Kind && candidate.Kind != FilterLiteralKind.Null)
                    {
                        return null;
                    }

                    found |= candidate.Kind == member.Kind && Keys.KeyCursor.Compare(member, candidate) == 0;
                }

                return found;
            case LogicalExpr logical:
                bool? left = Decided(logical.Left, values);
                bool? right = Decided(logical.Right, values);
                return logical.IsAnd
                    ? left == false || right == false ? false : left == true && right == true ? true : null
                    : left == true || right == true ? true : left == false && right == false ? false : null;
            case NotExpr not:
                return Decided(not.Operand, values) is { } operand ? !operand : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// Refuses a filter an object's partition value cannot decide: one that reads a column other than the partition
    /// columns, or reads them otherwise than through comparisons with a literal, sets, conjunctions, disjunctions and
    /// negations.
    /// </summary>
    /// <exception cref="ArgumentException">The filter is not one of the partition columns alone.</exception>
    internal static void RequireDecidable(VortexExpr expr, ClusteringKey key, string parameter)
    {
        switch (expr)
        {
            case ComparisonExpr comparison when comparison.Field.GetType() == typeof(FieldExpr) && key.PartitionPaths.Contains(comparison.Field.Path):
                return;
            case InExpr set when set.Field.GetType() == typeof(FieldExpr) && key.PartitionPaths.Contains(set.Field.Path):
                return;
            case LogicalExpr logical:
                RequireDecidable(logical.Left, key, parameter);
                RequireDecidable(logical.Right, key, parameter);
                return;
            case NotExpr not:
                RequireDecidable(not.Operand, key, parameter);
                return;
            default:
                throw new ArgumentException(
                    $"An expiry drops whole partitions, and its filter compares the partition columns ({string.Join(", ", key.PartitionPaths.ToArray())}) " +
                    $"with values alone: '{expr}' reads otherwise, which a delete does (DeleteAsync).",
                    parameter);
        }
    }

    /// <summary>The one value of each partition column an object holds, by column; null when its summaries do not say it.</summary>
    internal static Dictionary<string, FilterLiteral>? ValuesOf(ObjectEntry entry, ClusteringKey key)
    {
        if (key.PartitionOf(entry.Summaries) is not { } values)
        {
            return null;
        }

        Dictionary<string, FilterLiteral> named = new Dictionary<string, FilterLiteral>(values.Length, System.StringComparer.Ordinal);
        for (int i = 0; i < values.Length; i++)
        {
            named[key.PartitionPaths[i]] = values[i];
        }

        return named;
    }
}
