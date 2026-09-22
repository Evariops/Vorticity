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
    /// filters, orders or takes, and otherwise as many blocks as keep a batch of what it reads
    /// within half of a core's share of the L2 cache. A value above that decision changes nothing.
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
    public bool Pruning { get; init; } = true;

    /// <summary>Whether the file's indexes may skip blocks; off only to check an index, never to change a result.</summary>
    public bool UseIndexes { get; init; } = true;

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
