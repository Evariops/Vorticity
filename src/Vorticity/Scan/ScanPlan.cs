using System.Collections.Generic;
using System.Collections.Immutable;
using Vorticity.Scanning;

namespace Vorticity;

/// <summary>
/// One pruning structure's contribution to a scan's live blocks, and what consulting it cost: the
/// two numbers that say whether a structure earns its bytes.
/// </summary>
/// <param name="Structure">The structure, such as <c>zone map</c> or <c>bloom filter</c>.</param>
/// <param name="BlocksPruned">Blocks it proved empty that were still live when it ran.</param>
/// <param name="SegmentsRead">Segments read to consult it.</param>
/// <param name="BytesRead">Their bytes.</param>
public sealed record PruningStep(string Structure, int BlocksPruned, int SegmentsRead, long BytesRead);

/// <summary>
/// What a scan will do, worked out from statistics, zone maps and indexes before a data segment is
/// read: its blocks, the segments and bytes the live ones need, what each structure pruned.
/// </summary>
/// <param name="Rows">Rows the scan covers: the file's, those of the range <c>Rows</c> gave, or the rows it takes.</param>
/// <param name="Blocks">Blocks those rows touch.</param>
/// <param name="LiveBlocks">Of those, the blocks no structure could prove empty: the ones the scan decodes.</param>
/// <param name="Segments">
/// Segments the scan asks its source for: each data segment of the live blocks once, and what
/// consulting each structure reads. The execution's <see cref="ScanStatistics.Requests"/> is the same count.
/// </param>
/// <param name="BytesToRead">
/// Their bytes; the execution's <see cref="ScanStatistics.BytesRequested"/> is the same sum, and what
/// a source transfers exceeds it only by the gaps it bridges to coalesce neighbouring segments.
/// </param>
/// <param name="MayMatch">False when the file's statistics or filters prove the scan empty.</param>
/// <param name="Pruning">What each structure pruned, cheapest first.</param>
/// <param name="Count">How a count of the scan is answered.</param>
/// <param name="Order">What drives a key-ordered scan; null in file order.</param>
public sealed record ScanPlan(
    long Rows, int Blocks, int LiveBlocks, int Segments, long BytesToRead, bool MayMatch,
    ImmutableArray<PruningStep> Pruning, CountPlan Count, OrderPlan? Order)
{
    /// <summary>The engine's own explanation, with its splits, for the tests that hold the plan to the execution.</summary>
    internal ScanExplanation? Detail { get; init; }

    internal static ScanPlan From(ScanExplanation plan) => new ScanPlan(
        plan.RowCount,
        plan.Blocks,
        plan.LiveBlocks,
        plan.SegmentsToRead,
        plan.BytesToRead,
        plan.FileMayMatch,
        [.. plan.Pruning],
        plan.Count is { } count
            ? new CountPlan(count.ExactCover, count.ExactCount, count.SplitsPruned, count.SplitsProven, count.SplitsDecoded)
            : new CountPlan(true, plan.RowCount, 0, 0, 0),
        plan.Order is { } order
            ? new OrderPlan(order.Source.ToString(), order.RunsInRange, order.EntriesInRange, order.Descending)
            : null)
    {
        Detail = plan,
    };
}

/// <summary>How a count of the scan is answered, from the cheapest proof up.</summary>
/// <param name="Exact">Whether the statistics or an exact index answer the whole predicate, with no block decoded.</param>
/// <param name="Rows">The count they give, when they do.</param>
/// <param name="Pruned">Batches of blocks the pruning rules out: counted as nothing.</param>
/// <param name="Proven">Batches the zone maps decide whole: counted from bounds in memory.</param>
/// <param name="Decoded">Batches left to decode and evaluate.</param>
public sealed record CountPlan(bool Exact, long Rows, int Pruned, int Proven, int Decoded);

/// <summary>What a key-ordered scan walks.</summary>
/// <param name="Source">The key source: a column the statistics say is sorted, or a sorted-runs index.</param>
/// <param name="Runs">The runs holding an entry the filter admits.</param>
/// <param name="Entries">The entries the filter admits, when the source counts them.</param>
/// <param name="Descending">Whether the order is reversed.</param>
public sealed record OrderPlan(string Source, int Runs, long? Entries, bool Descending);

/// <summary>What one scan did, counted as it ran.</summary>
/// <param name="Rows">Rows delivered.</param>
/// <param name="Batches">Batches delivered.</param>
/// <param name="Requests">Segments asked of the source, each at most once per scan; the session's cache may serve some.</param>
/// <param name="BytesRequested">Their bytes.</param>
/// <param name="BlocksDecoded">
/// Blocks whose data was decoded to canonical form: every block an enumeration delivers; for an
/// aggregate, only the blocks it could not read in their dictionary, run-end or constant form.
/// </param>
/// <param name="BlocksPruned">Blocks skipped by statistics, zone maps or indexes.</param>
/// <param name="CacheHits">Segments served by the session's cache.</param>
public readonly record struct ScanStatistics(
    long Rows, long Batches, long Requests, long BytesRequested, long BlocksDecoded, long BlocksPruned, long CacheHits)
{
    internal static ScanStatistics From(ScanMetrics metrics, long cacheHits) => new ScanStatistics(
        metrics.Rows, metrics.Batches, metrics.SegmentRequests, metrics.BytesRequested, metrics.BlocksDecoded, metrics.BlocksPruned, cacheHits);
}

/// <summary>
/// The plan of a scan as the engine works it out: the layout's splits, the live blocks, the
/// segments a live split registers, stopped before the first batch.
/// </summary>
internal sealed record ScanExplanation(
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
    /// <summary>How <c>CountAsync</c> would answer the scan; null without a filter, where the count is arithmetic.</summary>
    public CountExplanation? Count { get; init; }

    /// <summary>What drives the scan under <c>InKeyOrder</c>; null for a scan in file order.</summary>
    public OrderExplanation? Order { get; init; }
}

/// <summary>The tier a count takes, split by split.</summary>
internal sealed record CountExplanation(bool ExactCover, long ExactCount, int SplitsPruned, int SplitsProven, int SplitsDecoded);

/// <summary>The key source a key-ordered scan walks, before it walks it.</summary>
internal sealed record OrderExplanation(
    string Path, KeySourceKind Source, int Runs, int RunsInRange, long? EntryCount, long EntriesInRange, bool Descending);
