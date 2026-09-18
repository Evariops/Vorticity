// Reading the regions of several index runs at once - the shape of every pruner's load: gather the
// regions of the runs it needs, read them in one request, decode them in one context.
//
// ONE BATCH PER ORIGIN, since docs/13-dataset.md §6.4. A run's offsets count from the start of its
// origin -- the file, or the fragment it came from -- and its payloads name the arrays of
// that origin's encoding table. So regions of two origins can be neither read by one request nor
// decoded by one context, and a load that used to be one read per file is one read per origin: still
// one for a file indexed by itself, one more per fragment a scan actually needs.
using System;
using System.Collections.Generic;
using Vorticity.IO;

namespace Vorticity.Indexes;

/// <summary>Regions of index runs to read, grouped by the origin their offsets count in.</summary>
/// <typeparam name="T">What the caller keeps to decode each region once it is read.</typeparam>
internal sealed class IndexBatches<T> : IDisposable
{
    private readonly SortedDictionary<int, Batch> _batches = [];

    /// <summary>The batches, by origin, in origin order.</summary>
    internal IEnumerable<KeyValuePair<int, Batch>> ByOrigin => _batches;

    /// <summary>The batch of the origin <paramref name="run"/> is read from.</summary>
    /// <param name="run">A run of the file's directory.</param>
    /// <returns>Its batch, created on first use.</returns>
    internal Batch For(IndexRun run)
    {
        if (!_batches.TryGetValue(run.Origin, out Batch? batch))
        {
            batch = new Batch();
            _batches[run.Origin] = batch;
        }

        return batch;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (Batch batch in _batches.Values)
        {
            batch.Requests.Dispose();
        }

        _batches.Clear();
    }

    /// <summary>The regions of one origin, and what the caller keeps to decode them.</summary>
    internal sealed class Batch
    {
        /// <summary>The regions, read together from the origin's source.</summary>
        internal SegmentRequestSet Requests { get; } = new SegmentRequestSet();

        /// <summary>What each region is for.</summary>
        internal List<T> Wanted { get; } = [];
    }
}
