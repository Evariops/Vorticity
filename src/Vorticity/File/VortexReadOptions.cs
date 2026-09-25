using System;
using System.Collections.Generic;

namespace Vorticity.File;

/// <summary>
/// Read-time policy, carried on the open file and copied into every scan context. It is immutable
/// and shared because one open file may serve several concurrent scans.
/// </summary>
internal sealed class VortexReadOptions
{
    /// <summary>
    /// The per-decode ceiling, kept as an <see cref="int"/>: a decode produces buffers of an
    /// <see cref="int"/>'s length at most, which a larger ceiling bounds no further.
    /// </summary>
    private readonly int _maxDecompressedSize = (int)VortexLimits.DefaultMaxDecompressedSize;
    private readonly long _maxBatchDecompressedSize = long.MaxValue;
    private readonly long _indexCacheBytes = DefaultIndexCacheBytes;

    /// <summary>The default of <see cref="IndexCacheBytes"/>: 64 MiB.</summary>
    public const long DefaultIndexCacheBytes = 64L << 20;

    /// <summary>The defaults: 256 MiB decompression ceiling, no statistics verification.</summary>
    public static VortexReadOptions Default { get; } = new VortexReadOptions();

    /// <summary>
    /// Ceiling on the bytes one decompression step may produce. Defaults to
    /// <see cref="VortexLimits.DefaultMaxDecompressedSize"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public long MaxDecompressedSize
    {
        get => _maxDecompressedSize;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _maxDecompressedSize = (int)Math.Min(value, int.MaxValue);
        }
    }

    /// <summary>
    /// Ceiling on the bytes the decodes of one batch may produce together, across its columns;
    /// <see cref="long.MaxValue"/>, none, by default.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public long MaxBatchDecompressedSize
    {
        get => _maxBatchDecompressedSize;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _maxBatchDecompressedSize = value;
        }
    }

    /// <summary>
    /// The bytes of decoded index runs one open file keeps for its cursors, least recently used
    /// first out. Default <see cref="DefaultIndexCacheBytes"/>; <c>0</c> keeps nothing, and every
    /// seek then reads what it needs.
    /// </summary>
    /// <remarks>
    /// A ceiling, not a reservation. A single run's keys can be as many as a chunk's rows, so an
    /// uncapped cache would be bounded only by the file. A run larger than the whole cap is
    /// decoded, used and not kept.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public long IndexCacheBytes
    {
        get => _indexCacheBytes;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _indexCacheBytes = value;
        }
    }

    /// <summary>
    /// Verify the statistics a decode can recompute — monotonic run ends, <c>is_sorted</c>, zone
    /// bounds — instead of trusting what the file claims. Costs one pass over the values at first
    /// decode. Default <see langword="false"/>.
    /// </summary>
    public bool VerifyStatistics { get; init; }

    /// <summary>
    /// Index fragments built for this file, each one a whole container: the runs and the directory
    /// an indexer wrote for a block range of the file, bound to it by its identity. Empty by
    /// default.
    /// </summary>
    /// <remarks>
    /// They are merged into the file's own index directory, entry by entry, rather than replacing
    /// it. An entry the file already has, of the same kind, column, block length and options,
    /// stays the file's; the same entry across fragments joins its runs when their blocks are
    /// disjoint; an entry of other options is another entry. A fragment that is not this file's, or
    /// an entry that overlaps blocks another fragment covers, is left out with the reason in
    /// <see cref="VortexFile.IndexFragmentRefusals"/>, and never fails the open, because an index
    /// is only a hint. A fragment is decoded against its own encoding table, never the
    /// file's footer, so the bytes are the same wherever they are stored. Binding reads no byte of
    /// the file: the length and the identity come from the tail the open read, and a file written
    /// without an identity is bound by its store token -- its length and modification time, taken
    /// when it is opened from a path -- which is a heuristic.
    /// </remarks>
    public IReadOnlyList<ReadOnlyMemory<byte>> IndexFragments { get; init; } = [];
}
