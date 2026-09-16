// What a scan DID - docs/11-write-strategy.md §6.4: "ScanMetrics reports the same after
// execution" as `Explain` planned, so that whether a structure earns its bytes is a number and not
// an impression. docs/12-index-reads.md §12.8 adds the EventSource counters on top.
//
// A SINK THE CALLER OWNS. The builder is handed one (`WithMetrics`) and every enumerator started
// from it adds to it; a caller that runs the same scan twice sees the sum, and one that wants a
// fresh count hands over a fresh object. The additions are interlocked because a scan with a
// degree above one runs its lanes on the pool.
using System.Threading;

namespace Vorticity.Scan;

/// <summary>Counters a scan adds to as it runs.</summary>
public sealed class ScanMetrics
{
    private long _segmentRequests;
    private long _bytesRequested;
    private long _valuesDecoded;
    private long _batches;
    private long _rows;
    private long _windows;
    private long _windowSplits;

    /// <summary>
    /// Segments the scan asked its source for, one per batch and per distinct segment the batch
    /// registered. A source that caches may serve some without a read; this counts the asking.
    /// </summary>
    public long SegmentRequests => Interlocked.Read(ref _segmentRequests);

    /// <summary>The bytes those requests named.</summary>
    public long BytesRequested => Interlocked.Read(ref _bytesRequested);

    /// <summary>
    /// Values the flat layout reader materialized: whole nodes, retained chunks, and the rows a
    /// positional take or a live-block decode selected.
    /// </summary>
    public long ValuesDecoded => Interlocked.Read(ref _valuesDecoded);

    /// <summary>
    /// Batches the scan's enumerator produced, empty ones included -- a filtered scan's wrapper
    /// drops the empty ones before the caller sees them, so a caller may count fewer.
    /// </summary>
    public long Batches => Interlocked.Read(ref _batches);

    /// <summary>Rows those batches held, after the filter: what the caller received.</summary>
    public long Rows => Interlocked.Read(ref _rows);

    /// <summary>
    /// Windows a key-ordered scan walked (docs/12-index-reads.md §6); zero for a scan in file order.
    /// </summary>
    public long Windows => Interlocked.Read(ref _windows);

    /// <summary>
    /// Splits those windows touched, summed: <see cref="Windows"/> when the key follows file order,
    /// up to one per row when it does not -- the number that says what a key order cost.
    /// </summary>
    public long WindowSplits => Interlocked.Read(ref _windowSplits);

    internal void AddWindow(int splits)
    {
        Interlocked.Increment(ref _windows);
        Interlocked.Add(ref _windowSplits, splits);
    }

    internal void AddRequests(long segments, long bytes)
    {
        Interlocked.Add(ref _segmentRequests, segments);
        Interlocked.Add(ref _bytesRequested, bytes);
    }

    /// <summary>
    /// Adds what a split just registered: the distinct segments and their bytes, counted at the
    /// asking (docs/11 §6.4) -- a caching source may serve some without a read.
    /// </summary>
    /// <param name="segments">The request set, after registration and before the read.</param>
    internal void AddRequests(IO.SegmentRequestSet segments)
    {
        long bytes = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            bytes += segments.GetSpec(i).Length;
        }

        AddRequests(segments.Count, bytes);
    }

    /// <summary>
    /// Adds what a split registered to the scan's sink, when there is one, and to the process's
    /// counters, when a listener is attached (docs/09 §5); nothing is walked when neither asks.
    /// </summary>
    /// <param name="metrics">The scan's sink, or null.</param>
    /// <param name="segments">The request set, after registration.</param>
    internal static void Note(ScanMetrics? metrics, IO.SegmentRequestSet segments)
    {
        if (metrics is null && !Diagnostics.VortexEventSource.On)
        {
            return;
        }

        long bytes = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            bytes += segments.GetSpec(i).Length;
        }

        Note(metrics, segments.Count, bytes);
    }

    /// <summary>Adds counted requests to the sink and to the process's counters.</summary>
    /// <param name="metrics">The scan's sink, or null.</param>
    /// <param name="segments">Segments asked for.</param>
    /// <param name="bytes">Their bytes.</param>
    internal static void Note(ScanMetrics? metrics, long segments, long bytes)
    {
        metrics?.AddRequests(segments, bytes);
        Diagnostics.VortexEventSource.Requested(segments, bytes);
    }

    internal void AddDecoded(long values) => Interlocked.Add(ref _valuesDecoded, values);

    internal void AddBatch(long rows)
    {
        Interlocked.Increment(ref _batches);
        Interlocked.Add(ref _rows, rows);
    }
}
