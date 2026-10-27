using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Vorticity.Dataset;

/// <summary>
/// The keys of variant columns this handle's questions ask (spec E, 5.1): each key a question reads by its key, to filter,
/// group or show, counted once a question; a key asked <see cref="Asks"/> times is promoted, and the next job that writes
/// its column shreds it in its dominant class however rare, with its index of where its values lie and its zone maps, as
/// the column a question names. In the handle's memory, as the layout's counters are; the keys an application declares
/// shredded (<see cref="DatasetOptions.ShreddedPaths"/>) stay so.
/// </summary>
internal sealed class VariantPromotions
{
    /// <summary>The questions after which a key is promoted.</summary>
    internal const int Asks = 3;

    /// <summary>The keys counted at most; a key past them is not counted.</summary>
    internal const int MaxKeys = 4_096;

    private readonly ConcurrentDictionary<(string Column, string Key), Counter> _asked = new();

    /// <summary>Counts a question that reads <paramref name="keys"/>, each variant column by name with the keys it reads by key.</summary>
    internal void Asked(IReadOnlyDictionary<string, string[]>? keys)
    {
        if (keys is null)
        {
            return;
        }

        foreach (KeyValuePair<string, string[]> column in keys)
        {
            foreach (string key in column.Value)
            {
                if (_asked.Count >= MaxKeys && !_asked.ContainsKey((column.Key, key)))
                {
                    continue;
                }

                Interlocked.Increment(ref _asked.GetOrAdd((column.Key, key), static _ => new Counter()).Count);
            }
        }
    }

    /// <summary>The keys of the variant column <paramref name="column"/> promoted; null for none.</summary>
    internal HashSet<string>? Of(string column)
    {
        HashSet<string>? promoted = null;
        foreach (KeyValuePair<(string Column, string Key), Counter> asked in _asked)
        {
            if (string.Equals(asked.Key.Column, column, StringComparison.Ordinal) && Volatile.Read(ref asked.Value.Count) >= Asks)
            {
                (promoted ??= new HashSet<string>(StringComparer.Ordinal)).Add(asked.Key.Key);
            }
        }

        return promoted;
    }

    private sealed class Counter
    {
        internal int Count;
    }
}
