using System.Threading;

namespace Vorticity.Scanning;

/// <summary>
/// Counters a scan adds to as it runs, reporting after execution what <c>Explain</c> planned, so
/// that whether a structure earns its bytes is a number rather than an impression. The object is a
/// sink the caller owns: every enumerator started from the builder it was handed to adds to the
/// same instance, so running a scan twice sums, and a fresh count means a fresh object. The
/// additions are interlocked because a scan above degree one runs its lanes on the thread pool.
/// </summary>
internal sealed class ScanMetrics
{
    private long _segmentRequests;
    private long _bytesRequested;
    private long _valuesDecoded;
    private long _batches;
    private long _rows;
    private long _windows;
    private long _windowSplits;

    /// <summary>
    /// Segments the scan asked its source for: those a batch registered and the scan did not
    /// already hold. A session cache may serve some without a read; this counts the asking.
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
    /// Windows a key-ordered scan walked; zero for a scan in file order.
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
    /// The slots of a set the source is about to be asked for: registered, not filled by what the
    /// scan holds, and not empty, since an empty segment is filled when it is registered.
    /// </summary>
    /// <param name="segments">The request set, registered and not yet read.</param>
    /// <param name="bytes">Their bytes.</param>
    /// <returns>How many.</returns>
    internal static int Unread(IO.SegmentRequestSet segments, out long bytes)
    {
        int count = 0;
        bytes = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            if (!segments.IsFilled(i))
            {
                count++;
                bytes += segments.GetSpec(i).Length;
            }
        }

        return count;
    }

    /// <summary>
    /// Adds what a split is about to ask of the source to the scan's sink, when there is one, and to
    /// the process's counters, when a listener is attached.
    /// </summary>
    /// <param name="metrics">The scan's sink, or null.</param>
    /// <param name="segments">The request set, registered, filled with what the scan holds, and not yet read.</param>
    /// <returns>Whether there is anything to ask: when not, the set is complete already and the source is not called.</returns>
    internal static bool Note(ScanMetrics? metrics, IO.SegmentRequestSet segments)
    {
        int count = Unread(segments, out long bytes);
        if (count == 0)
        {
            return false;
        }

        Note(metrics, count, bytes);
        return true;
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

    /// <summary>Blocks decoded: every split read and executed counts its blocks.</summary>
    public long BlocksDecoded => Interlocked.Read(ref _blocksDecoded);

    /// <summary>Blocks skipped because a structure proved them empty.</summary>
    public long BlocksPruned => Interlocked.Read(ref _blocksPruned);

    private long _blocksDecoded;
    private long _blocksPruned;

    internal void AddBlocksDecoded(long blocks) => Interlocked.Add(ref _blocksDecoded, blocks);

    internal void AddBlocksPruned(long blocks) => Interlocked.Add(ref _blocksPruned, blocks);
}
