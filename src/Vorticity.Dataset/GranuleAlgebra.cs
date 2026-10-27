using System;
using System.Collections.Generic;
using System.Numerics;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.Types;

namespace Vorticity.Dataset;

/// <summary>
/// The granules of an object where a row a filter selects may lie, by where the values of its columns lie
/// (<see cref="ColumnLocate"/>, spec D, 4): an equality, a set or a prefix on a column the object locates gives the granules
/// of its values; <c>AND</c> intersects, <c>OR</c> unites; any other predicate, and a <c>NOT</c>, which an index of where
/// values lie does not prove, leaves every granule. A set is a map of a bit a granule, a word of 64 at a time; null for every
/// granule. The rows of the granules left are a superset of those the filter selects, which a scan judges again.
/// </summary>
internal static class GranuleAlgebra
{
    /// <summary>
    /// The granules of an object of <paramref name="granules"/> granules where a row <paramref name="filter"/> selects may
    /// lie, by the indexes <paramref name="locateOf"/> gives by column of <paramref name="schema"/>; null for every one.
    /// </summary>
    internal static ulong[]? May(VortexExpr filter, DType schema, Func<int, ColumnLocate?> locateOf, int granules) =>
        filter switch
        {
            ComparisonExpr { Op: ComparisonOp.Equal } equal when Located(equal.Field, schema, locateOf, granules) is (int field, ColumnLocate locate) =>
                Values([equal.Value], schema.GetField(field), locate),
            InExpr set when Located(set.Field, schema, locateOf, granules) is (int field, ColumnLocate locate) =>
                Values(set.Literals, schema.GetField(field), locate),
            StringMatchExpr { Op: StringMatchOp.StartsWith } prefix when Located(prefix.Field, schema, locateOf, granules) is (_, ColumnLocate locate) =>
                Prefixed(prefix.Pattern.BytesValue, locate),
            LogicalExpr { IsAnd: true } and => Both(May(and.Left, schema, locateOf, granules), May(and.Right, schema, locateOf, granules)),
            LogicalExpr or => Either(May(or.Left, schema, locateOf, granules), May(or.Right, schema, locateOf, granules)),
            _ => null,
        };

    /// <summary>Whether <paramref name="granules"/> holds no granule.</summary>
    internal static bool None(ReadOnlySpan<ulong> granules)
    {
        foreach (ulong word in granules)
        {
            if (word != 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The granules <paramref name="granules"/> holds.</summary>
    internal static int Count(ReadOnlySpan<ulong> granules)
    {
        int count = 0;
        foreach (ulong word in granules)
        {
            count += BitOperations.PopCount(word);
        }

        return count;
    }

    /// <summary>The top-level column <paramref name="field"/> names and its index, when the object locates it over <paramref name="granules"/> granules.</summary>
    private static (int Field, ColumnLocate Locate)? Located(FieldExpr field, DType schema, Func<int, ColumnLocate?> locateOf, int granules) =>
        field is ColumnFieldExpr && schema.Kind == DTypeKind.Struct && field.Path.IndexOf('.') < 0
        && schema.IndexOfField(field.Path) is int index and >= 0 && locateOf(index) is { } locate && locate.Granules == granules
            ? (index, locate)
            : null;

    /// <summary>The granules of the values <paramref name="literals"/> of a column of <paramref name="dtype"/>; null when one cannot be told.</summary>
    private static ulong[]? Values(FilterLiteral[] literals, DType dtype, ColumnLocate locate)
    {
        if (CountsAnswer.ValuesOf(literals, dtype) is not { } values)
        {
            return null;
        }

        ulong[] granules = new ulong[(locate.Granules + 63) / 64];
        foreach (byte[] value in values)
        {
            if (locate.Find(value) is int place and >= 0)
            {
                locate.Or(place, granules);
            }
        }

        return granules;
    }

    /// <summary>The granules of the values that start with <paramref name="prefix"/>.</summary>
    private static ulong[] Prefixed(ReadOnlySpan<byte> prefix, ColumnLocate locate)
    {
        ulong[] granules = new ulong[(locate.Granules + 63) / 64];
        (int first, int end) = locate.Prefixed(prefix);
        for (int place = first; place < end; place++)
        {
            locate.Or(place, granules);
        }

        return granules;
    }

    /// <summary>The granules in both, null for every one.</summary>
    private static ulong[]? Both(ulong[]? left, ulong[]? right)
    {
        if (left is null || right is null)
        {
            return left ?? right;
        }

        for (int i = 0; i < left.Length && i < right.Length; i++)
        {
            left[i] &= right[i];
        }

        return left;
    }

    /// <summary>The granules in either, null for every one.</summary>
    private static ulong[]? Either(ulong[]? left, ulong[]? right)
    {
        if (left is null || right is null)
        {
            return null;
        }

        for (int i = 0; i < left.Length && i < right.Length; i++)
        {
            left[i] |= right[i];
        }

        return left;
    }
}
