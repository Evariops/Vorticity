using System;
using System.Collections.Immutable;

namespace Vorticity;

/// <summary>What a group by will do, worked out before a row is read (docs/design/16-queries.md §10).</summary>
/// <param name="Keys">The key's components, in order.</param>
/// <param name="Streaming">The component the groups stream on, closed as its value changes; null when they wait for the end of the pass.</param>
/// <param name="NotStreaming">Why no component streams, when none does.</param>
/// <param name="Aggregates">The aggregates the pass computes, once the selection's duplicates are merged.</param>
/// <param name="RowFilter">The conditions on the key that filter the rows before they are grouped; null when there is none.</param>
/// <param name="Order">How the groups are ordered before they are delivered.</param>
/// <param name="Kept">The groups a top keeps, when <paramref name="Order"/> is <see cref="GroupOrdering.Top"/>.</param>
/// <param name="ChosenRows">The columns of chosen rows, a group's first or last row, the result reads.</param>
/// <param name="Degree">The lanes the pass may run on at most: ranges of rows aggregated side by side.</param>
/// <param name="Core">Whether the groups are held once in the core rather than on a table each lane, which the governor also turns to under pressure.</param>
/// <param name="CacheCapacity">The groups of a lane's cache when <paramref name="Core"/> is true.</param>
/// <param name="Alpha">The multiple of a part's groups its pending entries pass before it is applied, when <paramref name="Core"/> is true.</param>
/// <param name="MostGroups">The most groups the statistics allow, when they bound every component of the key.</param>
public sealed record GroupPlan(
    ImmutableArray<GroupKeyPlan> Keys,
    int? Streaming,
    string? NotStreaming,
    int Aggregates,
    string? RowFilter,
    GroupOrdering Order,
    long? Kept,
    int ChosenRows,
    int Degree,
    bool Core,
    int CacheCapacity,
    int Alpha,
    long? MostGroups);

/// <summary>A component of a group by's key, as the statistics describe it.</summary>
/// <param name="Column">The column it reads.</param>
/// <param name="Sorted">Whether the statistics say the column is sorted, so that its rows come in runs of one value.</param>
/// <param name="Values">The values between its smallest and its largest, when the statistics hold them for an integer column: what a table of its groups indexes directly.</param>
public sealed record GroupKeyPlan(string Column, bool Sorted, long? Values);

/// <summary>How the groups of a group by are ordered before they are delivered.</summary>
public enum GroupOrdering
{
    /// <summary>No order is asked for: the engine's own, which no code should rely on.</summary>
    None,

    /// <summary>The groups come in the order of the key they stream on, as they close.</summary>
    Streamed,

    /// <summary>A heap keeps the groups a window takes, the best of them only.</summary>
    Top,

    /// <summary>Every group is sorted once the pass is done.</summary>
    Sort,
}

/// <summary>What a group by did, counted as it ran (docs/design/16-queries.md §10).</summary>
/// <param name="Groups">The groups the pass found, before any filter, order or window on them.</param>
/// <param name="PeakGroups">The most groups held at once: every group of a pass that waits for its end, the open ones and a batch's closed ones of one that streams, the parts applied and not yet built of one delivered part by part.</param>
/// <param name="PeakBytes">The most bytes the query held of its memory budget at once.</param>
/// <param name="Lanes">The lanes the pass ran on.</param>
/// <param name="MergeParts">The parts of the key space the lanes' groups were merged in, side by side; none when they merged in series.</param>
/// <param name="Core">Whether the core held the groups at the end, chosen or turned to under pressure.</param>
/// <param name="CacheEvictions">The times a lane's cache was copied into the core's batches.</param>
/// <param name="BypassedRows">The rows lanes folded apart from their cache, which found too few of their keys.</param>
/// <param name="Bursts">The times a lane applied a part's batches during the pass.</param>
/// <param name="PendingBytes">The most bytes of batches waiting on the parts at once.</param>
/// <param name="ReloadedBytes">The bytes of sub-tables the bursts read again.</param>
/// <param name="Tables">The sub-tables holding the groups at the end.</param>
/// <param name="TableSplits">The times a sub-table split.</param>
/// <param name="SpilledParts">The parts written to local scratch, which no budget held.</param>
/// <param name="SpilledBytes">Their bytes.</param>
/// <param name="KeyBlocksByRange">Key blocks grouped by their runs: constant or run-end, one lookup a run.</param>
/// <param name="KeyBlocksByCode">Key blocks grouped by the codes of their dictionary, one lookup a distinct value.</param>
/// <param name="KeyBlocksHashed">Key blocks grouped row by row, each value hashed.</param>
/// <param name="TimeToFirstBatch">From the first move of the result to its first batch.</param>
public sealed record GroupStatistics(
    long Groups,
    long PeakGroups,
    long PeakBytes,
    int Lanes,
    int MergeParts,
    bool Core,
    long CacheEvictions,
    long BypassedRows,
    long Bursts,
    long PendingBytes,
    long ReloadedBytes,
    int Tables,
    int TableSplits,
    int SpilledParts,
    long SpilledBytes,
    long KeyBlocksByRange,
    long KeyBlocksByCode,
    long KeyBlocksHashed,
    TimeSpan TimeToFirstBatch)
{
    /// <summary>Why the core held the groups; <see cref="GroupCoreReason.None"/> when each lane held a table of its own.</summary>
    public GroupCoreReason CoreReason { get; init; }

    /// <summary>
    /// The rows the first lane to turn to the core had folded into its own table when it turned: 0 when
    /// it turned on its first batch, before folding a row; -1 when no lane turned, the core held from the
    /// start or not at all.
    /// </summary>
    public long TurnedAfterRows { get; init; } = -1;
}

/// <summary>Why a group by held its groups in the core, each group once, rather than on a table each lane.</summary>
public enum GroupCoreReason
{
    /// <summary>The core did not hold the groups: each lane held a table of the groups it met, merged at the end.</summary>
    None,

    /// <summary>The query asked for the core.</summary>
    Asked,

    /// <summary>A lane's first rows of a key hashed by its value were nearly all new groups: a key of half a million values or more.</summary>
    FirstRows,

    /// <summary>A lane's first batch of an integer key spread over the span the statistics bound it to, as a key in no order spreads.</summary>
    Spread,

    /// <summary>A lane's memory budget could not let its table grow.</summary>
    Pressure,
}
