using System;
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
    private long _cacheHits;
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

    /// <summary>Of the segments asked for, those the session's cache served without a read.</summary>
    public long CacheHits => Interlocked.Read(ref _cacheHits);

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

    /// <summary>
    /// Adds to the sink the segments of a read just made that the session's cache served: the
    /// scan's own hits, where the cache's counter is the session's.
    /// </summary>
    /// <param name="metrics">The scan's sink, or null.</param>
    /// <param name="segments">The request set, read.</param>
    internal static void Served(ScanMetrics? metrics, IO.SegmentRequestSet segments)
    {
        int hits = segments.CacheHits;
        if (hits > 0 && metrics is not null)
        {
            Interlocked.Add(ref metrics._cacheHits, hits);
        }
    }

    internal void AddDecoded(long values) => Interlocked.Add(ref _valuesDecoded, values);

    internal void AddBatch(long rows)
    {
        Interlocked.Increment(ref _batches);
        Interlocked.Add(ref _rows, rows);
    }

    /// <summary>
    /// Blocks whose data the scan decoded to canonical form: every block an enumeration delivers,
    /// since its consumer reads the columns; for a sink that reads encoded forms itself, only the
    /// blocks a column of which reached canonical form; for a terminal, the blocks it decoded to
    /// evaluate its filter.
    /// </summary>
    public long BlocksDecoded => Interlocked.Read(ref _blocksDecoded);

    /// <summary>Blocks skipped because a structure proved them empty.</summary>
    public long BlocksPruned => Interlocked.Read(ref _blocksPruned);

    private long _blocksDecoded;
    private long _blocksPruned;

    internal void AddBlocksDecoded(long blocks)
    {
        if (blocks > 0)
        {
            Interlocked.Add(ref _blocksDecoded, blocks);
        }
    }

    internal void AddBlocksPruned(long blocks)
    {
        if (blocks > 0)
        {
            Interlocked.Add(ref _blocksPruned, blocks);
        }
    }

    /// <summary>
    /// Whether any column under <paramref name="node"/> holds values in canonical form: decoded so
    /// by the reader, or expanded from a dictionary, a run-end or a constant form by whoever read it.
    /// </summary>
    /// <param name="arena">The batch's arena.</param>
    /// <param name="node">A decoded node of the batch.</param>
    internal static bool Decoded(Arrays.CanonicalArena arena, int node)
    {
        ref readonly Arrays.CanonicalRecord record = ref arena.RecordRef(node);
        switch (record.Kind)
        {
            case Arrays.CanonicalKind.Dictionary:
            case Arrays.CanonicalKind.RunEnd:
            case Arrays.CanonicalKind.Constant:
                return record.Materialized >= 0;
            case Arrays.CanonicalKind.Null:
                return false;
            case Arrays.CanonicalKind.Struct:
                Arrays.CanonicalNode fields = arena.GetNode(node);
                for (int i = 0; i < fields.FieldCount; i++)
                {
                    if (Decoded(arena, fields.GetFieldIndex(i)))
                    {
                        return true;
                    }
                }

                return false;
            case Arrays.CanonicalKind.Extension:
                return Decoded(arena, arena.GetNode(node).StorageIndex);
            default:
                return true;
        }
    }
}

/// <summary>
/// Counts the blocks a walk of splits touches, each once, for a walk in row order either way: two
/// splits of one block, when a batch is smaller than a block, count it once.
/// </summary>
internal struct BlockTally
{
    private readonly long _blockRows;
    private long _first;
    private long _last;

    /// <param name="blockRows">The rows of a block: the zone length, or the natural batch size of a file without zone maps.</param>
    internal BlockTally(long blockRows)
    {
        _blockRows = Math.Max(blockRows, 1);
        _first = -1;
        _last = -1;
    }

    /// <summary>The blocks of <paramref name="split"/> the split walked before it did not already touch.</summary>
    /// <param name="split">The next split of the walk.</param>
    /// <returns>How many.</returns>
    internal long Add(RowRange split)
    {
        if (split.IsEmpty)
        {
            return 0;
        }

        long first = split.Start / _blockRows;
        long last = (split.End - 1) / _blockRows;
        long shared = Math.Max(0, Math.Min(last, _last) - Math.Max(first, _first) + 1);
        _first = first;
        _last = last;
        return last - first + 1 - shared;
    }

    /// <summary>
    /// The blocks of <paramref name="split"/> that hold a row of <paramref name="wanted"/>, every
    /// block of it when every row is wanted, whose own liveness is <paramref name="alive"/>, and that
    /// were not already counted.
    /// </summary>
    /// <param name="split">The next split of the walk.</param>
    /// <param name="wanted">The rows a take asks for, or null.</param>
    /// <param name="live">The mask of live blocks, over blocks of this tally's size, or null when every block is live.</param>
    /// <param name="proven">The rows an exact index proved, or null: a block holding none of them is not live.</param>
    /// <param name="alive">Whether to count the live blocks, or the dead ones.</param>
    /// <returns>How many.</returns>
    /// <remarks>
    /// A split follows the chunks rather than the blocks, so it can run into a block the scan does not
    /// want or a structure proved empty. That block's rows are decoded in passing; it is not one the
    /// rows touch, nor a live one, and each block is counted by its own verdict, not its split's.
    /// </remarks>
    internal long Add(RowRange split, RowSelection? wanted, Compute.BlockMask? live = null, RowSelection? proven = null, bool alive = true)
    {
        if (split.IsEmpty)
        {
            return 0;
        }

        long counted = 0;
        for (long block = split.Start / _blockRows; block <= (split.End - 1) / _blockRows; block++)
        {
            RowRange part = new RowRange(
                Math.Max(split.Start, block * _blockRows), Math.Min(split.End, (block + 1) * _blockRows));
            if (wanted is not null && !wanted.Touches(part))
            {
                continue;
            }

            bool isLive = (live is null || live.IsLive((int)(part.Start / live.BlockRows)))
                && (proven is null || proven.Touches(part));
            if (isLive == alive)
            {
                counted += Add(part);
            }
        }

        return counted;
    }
}
