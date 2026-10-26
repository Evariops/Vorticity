using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Vorticity.Dataset;

/// <summary>
/// A column a clustering by which would have spared the questions a handle asked some bytes, as
/// <see cref="VortexDataset.RecommendClustering"/> ranks it. A diagnostic: the library never clusters by it on its own.
/// </summary>
/// <param name="Column">The column, a top-level one the questions' filters read.</param>
/// <param name="BytesAvoided">
/// The bytes the objects read as rows under a filter on the column would have been spared, had the dataset's rows been
/// clustered by it: each object's bytes of the columns read, by the cost formula, times the share of its rows the filter
/// left out, which a clustering would have left in other objects.
/// </param>
/// <param name="Objects">The objects read as rows under a filter on the column.</param>
public sealed record ClusteringAdvice(string Column, long BytesAvoided, long Objects);

/// <summary>
/// What the questions a handle asked would have been spared by a clustering of each column their filters read: bounded
/// counters, a column each, which <see cref="VortexDataset.RecommendClustering"/> ranks and the telemetry counts.
/// </summary>
/// <remarks>
/// An object read as rows under a filter, whole, its rows not narrowed by an index, credits each top-level column the
/// filter reads but the key's first, which the dataset is clustered by already, with its bytes of the columns read times
/// the share of its rows the filter left out. A filter on several columns credits each of them the whole: the ranking says
/// which column the questions lean on most, an upper bound of what each would spare.
/// </remarks>
internal sealed class ClusteringAdvisor
{
    /// <summary>The columns counted at most; a column past them is not counted.</summary>
    internal const int MaxColumns = 64;

    private readonly ConcurrentDictionary<string, Counted> _columns = new ConcurrentDictionary<string, Counted>(StringComparer.Ordinal);

    /// <summary>Credits <paramref name="columns"/> with an object of <paramref name="bytes"/> read as rows, <paramref name="kept"/> of its <paramref name="rows"/> rows kept.</summary>
    internal void Read(IReadOnlyList<string> columns, long bytes, long kept, long rows)
    {
        if (rows <= 0 || kept >= rows)
        {
            return;
        }

        long avoided = (long)(bytes * (1 - ((double)kept / rows)));
        foreach (string column in columns)
        {
            if (_columns.Count >= MaxColumns && !_columns.ContainsKey(column))
            {
                continue;
            }

            Counted counted = _columns.GetOrAdd(column, static _ => new Counted());
            Interlocked.Add(ref counted.Bytes, avoided);
            Interlocked.Increment(ref counted.Objects);
            DatasetTelemetry.ClusteringAvoidable(column, avoided);
        }
    }

    /// <summary>The columns credited, the most bytes avoided first.</summary>
    internal List<ClusteringAdvice> Ranked()
    {
        List<ClusteringAdvice> ranked = [];
        foreach (KeyValuePair<string, Counted> column in _columns)
        {
            ranked.Add(new ClusteringAdvice(column.Key, Volatile.Read(ref column.Value.Bytes), Volatile.Read(ref column.Value.Objects)));
        }

        ranked.Sort(static (left, right) => right.BytesAvoided != left.BytesAvoided
            ? right.BytesAvoided.CompareTo(left.BytesAvoided)
            : string.CompareOrdinal(left.Column, right.Column));
        return ranked;
    }

    private sealed class Counted
    {
        internal long Bytes;
        internal long Objects;
    }
}
