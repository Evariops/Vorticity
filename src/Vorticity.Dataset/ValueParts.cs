using System;
using System.Collections.Generic;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Types;

namespace Vorticity.Dataset;

/// <summary>
/// A question's answer over one part of a range of a dataset's clustering key's first column, as
/// <see cref="VortexDataset.AskByValuesAsync{TValue, TAnswer}"/> delivers the parts: the values from <c>From</c> up to,
/// and not including, <c>To</c>, those of one clustering value, final for them.
/// </summary>
/// <typeparam name="TValue">The values of the key's first column.</typeparam>
/// <typeparam name="TAnswer">What the question answers.</typeparam>
/// <param name="From">The part's first value.</param>
/// <param name="To">The value past its last.</param>
/// <param name="Answer">The question's answer over the part.</param>
public sealed record ValuePart<TValue, TAnswer>(TValue From, TValue To, TAnswer Answer);

/// <summary>
/// A range of the key's first column cut where the dataset's clustering value changes: the parts a question is asked
/// over, one by one, so that a page shows the last of them while the rest is read.
/// </summary>
internal static class ValueParts
{
    /// <summary>
    /// The parts of the values from <paramref name="from"/> up to <paramref name="to"/>, the last first when
    /// <paramref name="descending"/>: the range cut at the starts of the clustering values it crosses, each part's bounds
    /// the values it starts and ends at, the range's own at its ends.
    /// </summary>
    /// <exception cref="InvalidOperationException">The dataset declares no clustering value, or one of a column whose next value no function tells.</exception>
    /// <exception cref="ArgumentException">The range holds no value the column stores.</exception>
    internal static IEnumerable<(TValue From, TValue To)> Of<TValue>(DatasetSchema schema, VortexExtensionRegistry? extensions, TValue from, TValue to, bool descending)
    {
        if (schema.Cut is not { } cut || schema.Key is not { } key)
        {
            throw new InvalidOperationException(
                "A range is asked part by part on the values a dataset cuts its objects by, and this dataset declares none (DatasetOptions.Clustering): ask its question over the whole range.");
        }

        if (cut.Integers is not { } storage)
        {
            throw new InvalidOperationException(
                $"'{key.InstantPath}' is a column whose next value no function tells, a text's or a float's: a part a value has no end to find.");
        }

        VortexType type = VortexTypes.FromDType(key.LeadingType);
        ClrShape shape = ClrShape.Of(typeof(TValue));
        Int128 low = Least(type, extensions, key.InstantPath, shape, from, storage);
        Int128 high = Least(type, extensions, key.InstantPath, shape, to, storage);
        if (high <= low)
        {
            throw new ArgumentException($"The range from {from} to {to} holds no value of '{key.InstantPath}'.", nameof(to));
        }

        return descending ? Descending(cut, type, storage, from, to, low, high) : Ascending(cut, type, storage, from, to, low, high);
    }

    private static IEnumerable<(TValue From, TValue To)> Descending<TValue>(
        ValueCut cut, VortexType type, PType storage, TValue from, TValue to, Int128 low, Int128 high)
    {
        Int128 end = high;
        TValue upper = to;
        while (end > low)
        {
            Int128 start = Int128.Max(cut.StartOf(end - 1), low);
            TValue lower = start == low ? from : ValueOf<TValue>(start, type, storage);
            yield return (lower, upper);
            (end, upper) = (start, lower);
        }
    }

    private static IEnumerable<(TValue From, TValue To)> Ascending<TValue>(
        ValueCut cut, VortexType type, PType storage, TValue from, TValue to, Int128 low, Int128 high)
    {
        Int128 start = low;
        TValue lower = from;
        while (start < high)
        {
            Int128 end = cut.TryNextStart(start, out Int128 next) ? Int128.Min(next, high) : high;
            TValue upper = end == high ? to : ValueOf<TValue>(end, type, storage);
            yield return (lower, upper);
            (start, lower) = (end, upper);
        }
    }

    /// <summary>The least stored value at or past <paramref name="value"/>: one past the storage's last when none is.</summary>
    private static Int128 Least<TValue>(VortexType type, VortexExtensionRegistry? extensions, string path, ClrShape shape, TValue value, PType storage)
    {
        (Int128 min, Int128 max) = ValueFunction.Integers(storage);
        SymLowering.Placement at = SymLowering.Place(type, extensions, path, shape, value);
        if (at.Beyond != 0)
        {
            return at.Beyond < 0 ? min : max + 1;
        }

        return ValueFunction.TryInteger(at.Floor, out Int128 floor)
            ? at.Exact ? floor : floor + 1
            : throw new ArgumentException($"{value} is not a value of '{path}', of {type}.", nameof(value));
    }

    /// <summary>A stored value as the .NET value of the column.</summary>
    private static TValue ValueOf<TValue>(Int128 value, VortexType type, PType storage) =>
        LiteralValues.ToValue<TValue>(storage.IsSignedInteger() ? FilterLiteral.From((long)value) : FilterLiteral.From((ulong)value), type)!;
}
