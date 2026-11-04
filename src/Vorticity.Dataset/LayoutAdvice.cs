using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Vorticity.Dataset;

/// <summary>
/// A value a layout's key could take as a component, as <see cref="VortexDataset.RecommendLayoutAsync"/> ranks it: a column
/// or a variant key's read the filters of a handle's questions read. A diagnostic: the library never lays a dataset out by
/// it on its own, a layout being declared when the dataset is created.
/// </summary>
/// <param name="Path">The column, or the variant key's read, <c>Attributes['tenant'].AsString()</c>, as a filter names it.</param>
/// <param name="BytesAvoided">
/// The bytes the objects read as rows under a filter on it would have been spared, had the dataset's rows been ordered by
/// it: each object's bytes of the columns read, by the cost formula, times the share of its rows the filter left out, which
/// an order by it would have left in other granules.
/// </param>
/// <param name="Objects">The objects read as rows under a filter on it.</param>
/// <param name="DistinctPerPartition">
/// The distinct values a partition holds of it, about, by the column sheets of its objects merged; NaN when no sheet counts
/// it, as for a variant key.
/// </param>
/// <param name="BytesPerRow">
/// The bytes a row would take of it in runs at its place in the order <see cref="LayoutAdvice.Components"/> proposes, about:
/// its runs, the distinct values of the components up to it multiplied, at a value and a length each, over a partition's
/// rows; NaN when a component up to it has no count.
/// </param>
public sealed record ComponentAdvice(string Path, long BytesAvoided, long Objects, double DistinctPerPartition, double BytesPerRow);

/// <summary>
/// The key a handle's questions call for, as <see cref="VortexDataset.RecommendLayoutAsync"/> works it out: the values its
/// filters read, ordered as a key would take them, and where its sparse index would stop. The partition stays the
/// application's choice, the library not knowing which of its values close.
/// </summary>
/// <param name="Components">
/// The candidates, in the order a key would take them: the fewest distinct values a partition first, which group the
/// longest runs, those no sheet counts last; at equal counts, the most bytes avoided first.
/// </param>
/// <param name="IndexedPrefix">
/// The components the sparse index would hold: up to the last whose values, with those before it, still group a granule's
/// rows or more within a partition, the following ones varying inside a granule, whose zone maps bound them; 0 for none.
/// </param>
public sealed record LayoutAdvice(IReadOnlyList<ComponentAdvice> Components, int IndexedPrefix);

/// <summary>
/// What the questions a handle asked would have been spared by an order of the rows by each value their filters read:
/// bounded counters, a value each, which <see cref="VortexDataset.RecommendLayoutAsync"/> ranks and the telemetry counts.
/// </summary>
/// <remarks>
/// An object read as rows under a filter, whole, its rows not narrowed by an index, credits each top-level column and each
/// variant key's read the filter reads but the layout key's first column, which the dataset is ordered by already, with
/// its bytes of the columns read times the share of its rows the filter left out. A filter on several values credits each
/// of them the whole: the ranking says which value the questions lean on most, an upper bound of what each would spare.
/// </remarks>
internal sealed class LayoutAdvisor
{
    /// <summary>The values counted at most; a value past them is not counted.</summary>
    internal const int MaxValues = 64;

    private readonly ConcurrentDictionary<string, Counted> _values = new ConcurrentDictionary<string, Counted>(StringComparer.Ordinal);

    /// <summary>Credits <paramref name="values"/> with an object of <paramref name="bytes"/> read as rows, <paramref name="kept"/> of its <paramref name="rows"/> rows kept.</summary>
    internal void Read(IReadOnlyList<string> values, long bytes, long kept, long rows)
    {
        if (rows <= 0 || kept >= rows)
        {
            return;
        }

        long avoided = (long)(bytes * (1 - ((double)kept / rows)));
        foreach (string value in values)
        {
            if (_values.Count >= MaxValues && !_values.ContainsKey(value))
            {
                continue;
            }

            Counted counted = _values.GetOrAdd(value, static _ => new Counted());
            Interlocked.Add(ref counted.Bytes, avoided);
            Interlocked.Increment(ref counted.Objects);
            DatasetTelemetry.LayoutAvoidable(value, avoided);
        }
    }

    /// <summary>The values credited, the most bytes avoided first.</summary>
    internal List<(string Path, long Bytes, long Objects)> Ranked()
    {
        List<(string Path, long Bytes, long Objects)> ranked = [];
        foreach (KeyValuePair<string, Counted> value in _values)
        {
            ranked.Add((value.Key, Volatile.Read(ref value.Value.Bytes), Volatile.Read(ref value.Value.Objects)));
        }

        ranked.Sort(static (left, right) => right.Bytes != left.Bytes
            ? right.Bytes.CompareTo(left.Bytes)
            : string.CompareOrdinal(left.Path, right.Path));
        return ranked;
    }

    private sealed class Counted
    {
        internal long Bytes;
        internal long Objects;
    }
}
