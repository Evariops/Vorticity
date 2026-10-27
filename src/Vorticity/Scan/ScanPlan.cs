using System;
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
/// How one unit of a question over a dataset is answered (spec B, 7): a rollup's run or an object, the alternative its weight
/// chose, what that reads and weighs, and why each other alternative lost.
/// </summary>
/// <param name="Unit">The unit, by its key in the dataset: a rollup's, or an object's.</param>
/// <param name="Level"><c>run</c> for a rollup standing for the objects it covers, <c>object</c> for an object.</param>
/// <param name="Alternative">
/// The alternative taken: <c>rollup</c>, <c>counts</c> (the object's counts of a column), <c>partial</c> (the partial of the
/// question's family it learned), <c>blocks</c> (its columns' statistics over its blocks, the rows of the others read
/// besides), <c>zones</c> (its rows, the blocks its zone maps settle taken from them) or <c>rows</c>.
/// </param>
/// <param name="Detail">What it reads.</param>
/// <param name="Requests">Its requests to the store.</param>
/// <param name="Bytes">Its bytes from the store.</param>
/// <param name="Weight">
/// What it weighs at the dataset's cost profile, in bytes of the store's link: its bytes, a request's worth a request, its
/// reads from a cache and its processing.
/// </param>
/// <param name="Rejected">Each other alternative and why it lost: it does not cover the unit, or what it weighs.</param>
public sealed record UnitStep(string Unit, string Level, string Alternative, string Detail, long Requests, long Bytes, double Weight, ImmutableArray<string> Rejected);

/// <summary>What a plan costs at the source's cost profile, before reading anything (spec B, 7).</summary>
/// <param name="Requests">Its requests to the store.</param>
/// <param name="Bytes">Its bytes from the store.</param>
/// <param name="Nanoseconds">What the processor spends on its values, in nanoseconds of one core.</param>
/// <param name="Predicted">
/// The time it is predicted to take cold: its requests in rounds of those in flight, a round trip each, its bytes over the
/// link, its processing over the cores.
/// </param>
public sealed record PlanCost(long Requests, long Bytes, double Nanoseconds, TimeSpan Predicted);

/// <summary>
/// What a scan will do, worked out from statistics, zone maps and indexes before a data segment is
/// read: its blocks, the segments and bytes the live ones need, what each structure pruned.
/// </summary>
/// <param name="Rows">Rows the scan covers: the file's, those of the range <c>Rows</c> gave, or the rows it takes.</param>
/// <param name="Blocks">Blocks those rows touch.</param>
/// <param name="LiveBlocks">Of those, the blocks no structure could prove empty: the ones the scan decodes.</param>
/// <param name="Segments">
/// Segments the scan asks its source for: each data segment of the live blocks once, and what
/// consulting each structure reads. The execution's <see cref="ScanMetrics.Requests"/> is the same count.
/// </param>
/// <param name="BytesToRead">
/// Their bytes; the execution's <see cref="ScanMetrics.BytesRequested"/> is the same sum, and what
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
    /// <summary>
    /// The question the scan answers, in words: the shape of its answer (rows, an aggregate, a count), its source, the columns
    /// it reads, its filter, its options; what it asks, never how.
    /// </summary>
    public string Logical { get; init; } = string.Empty;

    /// <summary>What a group by will do with the rows; null without a group by.</summary>
    public GroupPlan? Grouping { get; init; }

    /// <summary>
    /// Over a dataset, how each unit is answered: each rollup and object the scan covers, the alternative its weight chose at
    /// the dataset's cost profile, and why the others lost; empty over a file.
    /// </summary>
    public ImmutableArray<UnitStep> Units { get; init; } = [];

    /// <summary>What the plan costs at the source's cost profile, before reading anything; null where the source does not say.</summary>
    public PlanCost? Cost { get; init; }

    /// <summary>
    /// The range an aggregation over a dataset would answer under <see cref="ScanOptions.AlignWindow"/>, when it grows the
    /// one asked (<see cref="ScanMetrics.Window"/>); null when it applies its filter as asked.
    /// </summary>
    public VortexExpr? Window { get; init; }

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
public readonly record struct ScanMetrics(
    long Rows, long Batches, long Requests, long BytesRequested, long BlocksDecoded, long BlocksPruned, long CacheHits)
{
    /// <summary>What a group by did with the rows; null without a group by, or before it ran.</summary>
    public GroupMetrics? Grouping { get; init; }

    /// <summary>
    /// The range on the first column of a dataset's clustering key its aggregation answered under
    /// <see cref="ScanOptions.AlignWindow"/>, when it grew the one asked: the conjuncts on that column the answer applies,
    /// whose bounds lie at or outside those asked. Null for a scan that applied its filter as asked.
    /// </summary>
    public VortexExpr? Window { get; init; }

    internal static ScanMetrics From(ScanCounters counters) => new ScanMetrics(
        counters.Rows, counters.Batches, counters.SegmentRequests, counters.BytesRequested, counters.BlocksDecoded, counters.BlocksPruned, counters.CacheHits)
    {
        Window = counters.Window,
    };
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
