using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Expressions;

namespace Vorticity.Keys;

/// <summary>One column's entries in key order, and a position among them.</summary>
/// <remarks>
/// Each source both locates and walks itself, rather than a common merge cursor driving a narrower
/// source surface: a sorted column is one contiguous run, where a step is an addition and a rank a
/// subtraction, while sorted runs are a heap of positions, and the two have almost nothing to share.
/// What they do share -- argument checking and lifetime -- lives in <see cref="KeyCursor"/>.
/// </remarks>
internal abstract class KeySource : IAsyncDisposable
{
    /// <summary>The column's comparison domain, which every seek key is in.</summary>
    internal abstract FilterLiteralKind KeyKind { get; }

    /// <summary>The source's entries, when known without a walk.</summary>
    internal abstract long? EntryCount { get; }

    /// <summary>The runs the source merges: 1 for a sorted column.</summary>
    internal abstract int Runs { get; }

    /// <summary>Whether an entry can say which file row it came from.</summary>
    internal virtual bool HasRows => true;

    /// <summary>What a composite key's bytes follow; null for a single column.</summary>
    internal virtual string? KeyFormat => null;

    /// <summary>
    /// Makes the source read through what the scan that walks it holds: the segments it has read
    /// and the chunks it has decoded, so a column the scan also delivers is read and decoded once
    /// between the two. A source that reads its own structures rather than the file's columns has
    /// nothing to share, and ignores it.
    /// </summary>
    /// <param name="held">The scan's segments.</param>
    /// <param name="retained">The scan's decoded chunks.</param>
    internal virtual void Share(Scanning.ScanSegments held, Arrays.RetainedChunks retained)
    {
    }

    /// <summary>Whether the source is positioned on an entry.</summary>
    internal abstract bool IsValid { get; }

    /// <summary>The current entry's key; copies a byte key.</summary>
    internal abstract FilterLiteral Key { get; }

    /// <summary>The current entry's key bytes, borrowed; empty for a fixed-width key.</summary>
    internal abstract ReadOnlySpan<byte> KeyBytes { get; }

    /// <summary>The current entry's file row.</summary>
    internal abstract long Row { get; }

    /// <summary>Positions relative to a key, whose domain the caller has checked.</summary>
    internal abstract ValueTask<bool> SeekAsync(FilterLiteral key, SeekOp op, CancellationToken cancellationToken);

    /// <summary>Positions on the first entry.</summary>
    internal abstract ValueTask<bool> SeekFirstAsync(CancellationToken cancellationToken);

    /// <summary>Positions on the last entry.</summary>
    internal abstract ValueTask<bool> SeekLastAsync(CancellationToken cancellationToken);

    /// <summary>Steps forward; the caller has checked the source is positioned.</summary>
    internal abstract ValueTask<bool> NextAsync(CancellationToken cancellationToken);

    /// <summary>Steps backward; the caller has checked the source is positioned.</summary>
    internal abstract ValueTask<bool> PrevAsync(CancellationToken cancellationToken);

    /// <summary>The first entry of the next distinct key.</summary>
    internal abstract ValueTask<bool> NextKeyAsync(CancellationToken cancellationToken);

    /// <summary>The last entry of the previous distinct key.</summary>
    internal abstract ValueTask<bool> PrevKeyAsync(CancellationToken cancellationToken);

    /// <summary>The entries whose key is below <paramref name="key"/>; the position does not move.</summary>
    internal abstract ValueTask<long> RankAsync(FilterLiteral key, CancellationToken cancellationToken);

    /// <summary>The entries whose key is at or below <paramref name="key"/>; the position does not move.</summary>
    internal abstract ValueTask<long> UpperRankAsync(FilterLiteral key, CancellationToken cancellationToken);

    /// <summary>Positions on the entry of a rank.</summary>
    internal abstract ValueTask<bool> SeekRankAsync(long rank, CancellationToken cancellationToken);

    /// <summary>The entries sharing the current key; the position does not move.</summary>
    internal abstract ValueTask<long> KeyCountAsync(CancellationToken cancellationToken);

    /// <summary>The runs holding an entry of <paramref name="slices"/>; the position is lost.</summary>
    internal virtual ValueTask<int> RunsOverlappingAsync(
        System.Collections.Generic.List<(long Low, long High)> slices, CancellationToken cancellationToken) =>
        new ValueTask<int>(slices.Count > 0 ? Runs : 0);

    /// <summary>Leaves no entry current.</summary>
    internal abstract void Invalidate();

    public abstract ValueTask DisposeAsync();
}
