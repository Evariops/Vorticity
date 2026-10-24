using System;

namespace Vorticity;

/// <summary>How one scan runs: batch size, pruning, delivery form, parallelism, read-ahead.</summary>
public sealed record ScanOptions
{
    private readonly int _batchRows;
    private readonly int _degreeOfParallelism;
    private readonly int _prefetch = 1;

    /// <summary>The defaults.</summary>
    internal static ScanOptions Default { get; } = new ScanOptions();

    /// <summary>
    /// The most rows a batch holds; 0 to let the scan decide: the file's block size when it
    /// filters, orders or takes, and otherwise as many blocks as a window of the scan holds, never
    /// past the end of a chunk; a filter in file order reads the blocks its zone maps prove whole
    /// by the window too. A value above that decision changes nothing.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int BatchRows
    {
        get => _batchRows;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _batchRows = value;
        }
    }

    /// <summary>Whether statistics and zone maps may skip blocks; off only to check a file's statistics, never to change a result.</summary>
    public bool UseStatistics { get; init; } = true;

    /// <summary>Whether the file's indexes may skip blocks; off only to check an index, never to change a result.</summary>
    public bool UseIndexes { get; init; } = true;

    /// <summary>
    /// Whether an aggregation over a dataset may answer from the states its objects' synopses keep, in
    /// place of their rows; off only to check a synopsis, never to change a result.
    /// </summary>
    public bool UseSynopses { get; init; } = true;

    /// <summary>
    /// Whether the window on a dataset's instant that an aggregation answers from its synopses may grow so that
    /// its edges read no rows: each end that falls inside an object moves out, in the object of most rows it
    /// cuts, to the nearest bound its tables answer at, its time slice's, its hour's or the object's own. The
    /// answer is then the exact answer of the window applied, which <see cref="ScanMetrics.WindowFrom"/> and
    /// <see cref="ScanMetrics.WindowTo"/> give. False by default: the window asked, exactly.
    /// </summary>
    public bool AlignWindow { get; init; }

    /// <summary>
    /// Whether a filtered batch is compacted to the rows that passed; false delivers whole blocks
    /// with their <c>Selection</c>, which copies nothing.
    /// </summary>
    public bool Compact { get; init; } = true;

    /// <summary>How many chunks decode and aggregate at once; 0 for the session's <see cref="VortexSessionOptions.MaxDegreeOfParallelism"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int DegreeOfParallelism
    {
        get => _degreeOfParallelism;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _degreeOfParallelism = value;
        }
    }

    /// <summary>Batches decoded ahead of the consumer, so decoding overlaps the caller's work; 0 decodes on demand.</summary>
    /// <remarks>
    /// The batches ahead are decoded on the thread pool, beside the caller's thread, whatever the
    /// degree of parallelism: 1 by default, so a scan takes a thread of the pool even at a degree
    /// of 1. A source with latency to hide is where it pays.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int Prefetch
    {
        get => _prefetch;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _prefetch = value;
        }
    }
}
