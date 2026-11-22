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
    /// Whether an aggregation over a dataset may answer from the states its objects' synopses keep, their
    /// columns' counts, in place of their rows; off only to check a synopsis, never to change a result.
    /// </summary>
    public bool UseSynopses { get; init; } = true;

    /// <summary>
    /// Whether an aggregation over a dataset may grow its filter's range on the first column of the dataset's
    /// sort key, of any type, so that the objects it cuts are answered whole by their synopses rather than
    /// read as rows: each bound that falls inside such objects moves out to the furthest of their own bounds on
    /// the column, unless another such object would then hold values on both sides of it, where the bound stays.
    /// Objects cut by a partition value hold none in common. The answer is then the exact answer of the range applied, which
    /// <see cref="ScanMetrics.Window"/> and <see cref="ScanPlan.Window"/> give. False by default: the range asked,
    /// exactly.
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
    /// The batches ahead are decoded within the degree of parallelism, of which the caller's thread is one: at a degree of
    /// <c>N</c>, on <c>N − 1</c> threads of the pool at most beside it, each on a lane of the session's free at the time,
    /// and at a degree of 1 on the caller's thread alone, a batch when it asks for it. A source with latency to hide is
    /// where it pays; the reads it issues ahead go on whatever the degree.
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
