// What a cursor is served by, and why the other sources were not chosen - docs/12-index-reads.md
// §3 and §8.1. The cheapest source the file offers wins, and `Explain` says so without opening a
// cursor, the way `ScanPlan` says what a scan would read without running it.
using System.Collections.Generic;

namespace Vorticity.Keys;

/// <summary>The kinds of key source of docs/12-index-reads.md §3.</summary>
public enum KeySourceKind : byte
{
    /// <summary>No source: the column is neither sorted nor indexed.</summary>
    None = 0,

    /// <summary>
    /// The file statistics say the column is sorted and it carries a zone map, so its rows are its
    /// entries in key order.
    /// </summary>
    SortedColumn = 1,

    /// <summary>A <c>vorticity.sorted.runs.v1</c> index (docs/10-indexes.md §6.2).</summary>
    SortedRuns = 2,

    /// <summary>A <c>vorticity.postings.blocks.v1</c> index: keys without rows.</summary>
    Postings = 3,

    /// <summary>A dictionary's values child: keys without rows.</summary>
    Dictionary = 4,
}

/// <summary>What a cursor over one column would be served by.</summary>
/// <param name="Path">The column.</param>
/// <param name="Source">The source chosen, or <see cref="KeySourceKind.None"/> when there is none.</param>
/// <param name="Runs">How many runs the source merges; 1 for a sorted column.</param>
/// <param name="EntryCount">The entries the source holds, or <see langword="null"/> when not known without a walk.</param>
/// <param name="HasRows">Whether an entry can say which file row it came from.</param>
/// <param name="Rejected">Why each source that exists in principle was not chosen.</param>
public sealed record KeyPlan(
    string Path,
    KeySourceKind Source,
    int Runs,
    long? EntryCount,
    bool HasRows,
    IReadOnlyList<KeySourceRejection> Rejected);

/// <summary>One source that was not chosen, and the reason.</summary>
/// <param name="Source">The source.</param>
/// <param name="Reason">Why it does not serve this column of this file.</param>
public sealed record KeySourceRejection(KeySourceKind Source, string Reason);
