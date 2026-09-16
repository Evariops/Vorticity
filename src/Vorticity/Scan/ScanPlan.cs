// What a scan WOULD do - docs/11-write-strategy.md §6.4, `ScanBuilder.Explain()`.
//
// "Returns the plan without executing it: splits in the file, blocks pruned by each structure,
// rows selected by exact indexes, bytes to read against the file's size." Nothing here is a
// measurement: it is the same planning the scan does before its first batch -- the layout tree,
// the split plan, the mask of live blocks, the segments a live split registers -- stopped before
// any data segment is read. What the scan then did is `ScanMetrics`, the same quantities counted.
using System.Collections.Generic;

namespace Vorticity.Scan;

/// <summary>
/// One pruning structure's contribution to a scan's mask of live blocks, and what consulting it
/// cost -- the two numbers that say whether a structure earns its bytes.
/// </summary>
/// <param name="Structure">The structure, as the reader names it: <c>"zone map"</c> today.</param>
/// <param name="BlocksPruned">Blocks it proved empty that were still live when it ran.</param>
/// <param name="SegmentsRead">Segments read to consult it.</param>
/// <param name="BytesRead">Their bytes.</param>
public sealed record PruningStep(string Structure, int BlocksPruned, int SegmentsRead, long BytesRead);

/// <summary>The plan of a scan, from its builder, without a data segment read.</summary>
/// <param name="RowCount">Rows the scan covers, after <c>Rows</c> and <c>Take</c> narrowed it.</param>
/// <param name="BlockRows">Rows per block -- the zone map's zone, the writer's row block.</param>
/// <param name="Blocks">Blocks over the file's rows.</param>
/// <param name="LiveBlocks">Blocks no structure could prove empty.</param>
/// <param name="Pruning">What each structure pruned, in the order they ran: cheapest first.</param>
/// <param name="Splits">Splits the scan would visit, before any of them is skipped.</param>
/// <param name="LiveSplits">Splits it would read: at least one wanted row, at least one live block.</param>
/// <param name="RowsSelectedByIndex">
/// Rows an exact index selected outright: the scan reads them as a take and evaluates nothing
/// (docs/10-indexes.md §6.6); zero when no exact source covers the filter, or it covers more than a
/// batch, or the scan is narrowed by <c>Rows</c> or <c>Take</c>.
/// </param>
/// <param name="SegmentsToRead">Distinct segments the live splits register.</param>
/// <param name="BytesToRead">Their bytes.</param>
/// <param name="FileBytes">The file's length, for the ratio.</param>
/// <param name="FileMayMatch">
/// The file-level answer of <c>VortexFile.MayMatch</c>: <see langword="false"/> means the file
/// statistics already prove the scan empty and every count above is what it would cost to find
/// that out the long way.
/// </param>
public sealed record ScanPlan(
    long RowCount,
    long BlockRows,
    int Blocks,
    int LiveBlocks,
    IReadOnlyList<PruningStep> Pruning,
    int Splits,
    int LiveSplits,
    long RowsSelectedByIndex,
    int SegmentsToRead,
    long BytesToRead,
    long FileBytes,
    bool FileMayMatch)
{
    /// <summary>
    /// How <c>CountAsync</c> would answer the scan (docs/12-index-reads.md §5.2); null without a
    /// filter, where the count is arithmetic.
    /// </summary>
    public CountPlan? Count { get; init; }

    /// <summary>What drives the scan under <c>InKeyOrder</c> (§6); null for a scan in file order.</summary>
    public OrderPlan? Order { get; init; }
}

/// <summary>The tier a count takes, split by split.</summary>
/// <param name="ExactCover">Whether an exact index answers the whole predicate, with no block read.</param>
/// <param name="ExactCount">The count it gives, when it does.</param>
/// <param name="SplitsPruned">Splits the mask rules out: counted as nothing.</param>
/// <param name="SplitsProven">Splits the zone maps decide whole: counted from bounds in memory.</param>
/// <param name="SplitsDecoded">Splits left to decode and evaluate.</param>
public sealed record CountPlan(bool ExactCover, long ExactCount, int SplitsPruned, int SplitsProven, int SplitsDecoded);

/// <summary>The key source a key-ordered scan walks, before it walks it.</summary>
/// <param name="Path">The key column.</param>
/// <param name="Source">The source.</param>
/// <param name="Runs">The runs it merges.</param>
/// <param name="RunsInRange">The runs holding an entry the filter's range admits.</param>
/// <param name="EntryCount">Its entries, when known.</param>
/// <param name="EntriesInRange">The entries the range admits: the bound on the rows the windows gather.</param>
/// <param name="Descending">Whether the order is reversed.</param>
public sealed record OrderPlan(
    string Path, Keys.KeySourceKind Source, int Runs, int RunsInRange, long? EntryCount, long EntriesInRange, bool Descending);
