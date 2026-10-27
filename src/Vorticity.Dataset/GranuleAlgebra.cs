using System;
using System.Collections.Generic;
using System.Numerics;
using Vorticity.Expressions;
using Vorticity.Compute;
using Vorticity.Indexes;
using Vorticity.Types;
using Vorticity.Types.Variants;

namespace Vorticity.Dataset;

/// <summary>
/// The granules of an object where a row a filter selects may lie, by where the values of its columns lie
/// (<see cref="IValueGranules"/>: the object's own index or its partition's, spec D, 4 and 7): an equality, a set or a
/// prefix on a column the object locates gives the granules
/// of its values; <c>AND</c> intersects, <c>OR</c> unites; any other predicate, and a <c>NOT</c>, which an index of where
/// values lie does not prove, leaves every granule. A set is a map of a bit a granule, a word of 64 at a time; null for every
/// granule. The rows of the granules left are a superset of those the filter selects, which a scan judges again.
/// </summary>
internal static class GranuleAlgebra
{
    /// <summary>
    /// The granules of an object of <paramref name="granules"/> granules where a row <paramref name="filter"/> selects may
    /// lie, by the indexes <paramref name="locateOf"/> gives by column of <paramref name="schema"/>, and those
    /// <paramref name="pathOf"/> gives by shredded key read in its class (spec E, 3.3); null for every one.
    /// </summary>
    internal static ulong[]? May(
        VortexExpr filter, DType schema, Func<int, IValueGranules?> locateOf, int granules, Func<(int Field, string Key, PathClass Class), IValueGranules?>? pathOf = null) =>
        filter switch
        {
            ComparisonExpr { Op: ComparisonOp.Equal } equal when Located(equal.Field, schema, locateOf, pathOf, granules) is (DType dtype, IValueGranules locate) =>
                Values([equal.Value], dtype, locate),
            InExpr set when Located(set.Field, schema, locateOf, pathOf, granules) is (DType dtype, IValueGranules locate) =>
                Values(set.Literals, dtype, locate),
            StringMatchExpr { Op: StringMatchOp.StartsWith } prefix when Located(prefix.Field, schema, locateOf, pathOf, granules) is (_, IValueGranules locate) =>
                Prefixed(prefix.Pattern.BytesValue, locate),
            LogicalExpr { IsAnd: true } and => Both(May(and.Left, schema, locateOf, granules, pathOf), May(and.Right, schema, locateOf, granules, pathOf)),
            LogicalExpr or => Either(May(or.Left, schema, locateOf, granules, pathOf), May(or.Right, schema, locateOf, granules, pathOf)),
            _ => null,
        };

    /// <summary>
    /// The variant column, key and class of <paramref name="field"/>, one key of a top-level variant column read in a class
    /// whose values repeat, a text or an integer: what an index of where a shredded key's values lie answers; null for another.
    /// </summary>
    internal static (int Field, string Key, PathClass Class)? PathOf(FieldExpr field, DType schema) =>
        field is VariantFieldExpr { Steps: [{ Key: { } key }], Read: VariantRead.AsString or VariantRead.AsInt64, Column: ColumnFieldExpr column } path
        && column.Path.IndexOf('.') < 0 && schema.Kind == DTypeKind.Struct && schema.IndexOfField(column.Path) is int index and >= 0
            ? (index, key, path.Read == VariantRead.AsString ? PathClass.String : PathClass.Integer)
            : null;

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

    /// <summary>
    /// The dtype of the values <paramref name="field"/> reads and where they lie, when the object locates them over
    /// <paramref name="granules"/> granules: a top-level column's, or a shredded key's read in its class.
    /// </summary>
    private static (DType DType, IValueGranules Locate)? Located(
        FieldExpr field, DType schema, Func<int, IValueGranules?> locateOf, Func<(int Field, string Key, PathClass Class), IValueGranules?>? pathOf, int granules)
    {
        if (field is ColumnFieldExpr && schema.Kind == DTypeKind.Struct && field.Path.IndexOf('.') < 0
            && schema.IndexOfField(field.Path) is int index and >= 0 && locateOf(index) is { } locate && locate.Granules == granules)
        {
            return (schema.GetField(index), locate);
        }

        return pathOf is not null && PathOf(field, schema) is { } path && pathOf(path) is { } held && held.Granules == granules
            ? (path.Class == PathClass.String ? Texts : Integers, held)
            : null;
    }

    // The dtypes a shredded key's values take in a class whose values repeat, made once and read only after.
    private static readonly DTypeArena ClassTypes = new DTypeArena();
    private static readonly DType Texts = Writing.IndexWriter.ClassType(ClassTypes, PathClass.String);
    private static readonly DType Integers = Writing.IndexWriter.ClassType(ClassTypes, PathClass.Integer);

    /// <summary>The granules of the values <paramref name="literals"/> of a column of <paramref name="dtype"/>; null when one cannot be told.</summary>
    private static ulong[]? Values(FilterLiteral[] literals, DType dtype, IValueGranules locate)
    {
        if (CountsAnswer.ValuesOf(literals, dtype) is not { } values)
        {
            return null;
        }

        ulong[] granules = new ulong[(locate.Granules + 63) / 64];
        foreach (byte[] value in values)
        {
            locate.OrValue(value, granules);
        }

        return granules;
    }

    /// <summary>The granules of the values that start with <paramref name="prefix"/>; null when the index cannot tell.</summary>
    private static ulong[]? Prefixed(ReadOnlySpan<byte> prefix, IValueGranules locate)
    {
        ulong[] granules = new ulong[(locate.Granules + 63) / 64];
        return locate.TryOrPrefix(prefix, granules) ? granules : null;
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
