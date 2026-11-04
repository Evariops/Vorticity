using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Expressions;

namespace Vorticity.Keys;

/// <summary>
/// Entries that are a file's rows, contiguous and already in key order: a sorted column's non-null rows, or the key tuples of
/// a dataset's object, whose rows its writer sorted. A step is an addition and a rank a subtraction; a bound bisects what
/// separates the rows, then the rows of one part, decoded. <see cref="SortedColumnWalker"/> walks them.
/// </summary>
internal interface ISortedEntries : IAsyncDisposable
{
    /// <summary>The keys' comparison domain, which every seek key must be in.</summary>
    FilterLiteralKind KeyKind { get; }

    /// <summary>What a composite key's bytes follow; null for a single column.</summary>
    string? KeyFormat { get; }

    /// <summary>The entries, exactly.</summary>
    long EntryCount { get; }

    /// <summary>The file row an entry came from.</summary>
    long RowOf(long entry);

    /// <summary>Orders two keys as the entries are ordered.</summary>
    int Compare(FilterLiteral left, FilterLiteral right);

    /// <summary>Makes the entries read through what the scan that walks them holds; nothing to share for some.</summary>
    void Share(Scanning.ScanSegments held, Arrays.RetainedChunks retained);

    /// <summary>Decodes the part holding <paramref name="entry"/>, unless it already is; allocates nothing when it is.</summary>
    ValueTask EnsureEntryAsync(long entry, CancellationToken cancellationToken);

    /// <summary>The key of an entry whose part is loaded; copies a byte key.</summary>
    FilterLiteral LoadedKey(long entry);

    /// <summary>The bytes of an entry whose part is loaded, borrowed until the next positioning; empty for a fixed-width key.</summary>
    ReadOnlySpan<byte> BytesAt(long entry);

    /// <summary>
    /// The entry just past <paramref name="entry"/>'s key, forward or back, found in the loaded part; -1 when the key reaches
    /// the part's edge that way.
    /// </summary>
    long PastKeyInZone(long entry, bool forward);

    /// <summary>The first entry at or after <paramref name="key"/>: its rank.</summary>
    ValueTask<long> LowerBoundAsync(FilterLiteral key, CancellationToken cancellationToken);

    /// <summary>The first entry strictly after <paramref name="key"/>.</summary>
    ValueTask<long> UpperBoundAsync(FilterLiteral key, CancellationToken cancellationToken);

    /// <summary>A bound of the byte key <paramref name="lender"/> is on, compared where it lends it.</summary>
    ValueTask<long> BoundOfAsync(KeySource lender, bool strict, CancellationToken cancellationToken);
}
