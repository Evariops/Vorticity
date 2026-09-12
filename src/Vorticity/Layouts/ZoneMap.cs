// PHASE1-CONTRACTS.md §11.3. Phase 1 parses a zone map and exposes its shape; it prunes with
// nothing (docs/01-scope.md §3 defers pruning to Phase 2), so this type carries no zone values.
using System;

using Vorticity.Arrays.Metadata;

namespace Vorticity.Layouts;

/// <summary>
/// The shape of one <c>vortex.zoned</c> or <c>vortex.stats</c> zone map.
/// </summary>
/// <remarks>
/// <para>
/// Phase 1 never prunes: <see cref="IsPruningAvailable"/> reports whether a Phase 2 pruner
/// <em>could</em>, and is <see langword="false"/> when <see cref="ZoneLength"/> is zero, when an
/// aggregate id is one this build does not know, or when the layout is the legacy
/// <c>vortex.stats</c> (contract §2.7).
/// </para>
/// <para>
/// Zone <c>z</c> covers rows <c>[min(z * ZoneLength, RowCount), min((z + 1) * ZoneLength,
/// RowCount))</c>. A consistent file has <c>ZoneCount == ceil(RowCount / ZoneLength)</c> with only
/// the last zone short, but the reference does not enforce it and neither do we: the zone count is
/// the zones child's own row count.
/// </para>
/// </remarks>
public readonly struct ZoneMap
{
    private readonly AggregateId[]? _aggregates;
    private readonly int[]? _columnIndices;

    internal ZoneMap(
        bool pruningAvailable,
        int zoneCount,
        long zoneLength,
        AggregateId[]? aggregates,
        int[]? columnIndices)
    {
        IsPruningAvailable = pruningAvailable;
        ZoneCount = zoneCount;
        ZoneLength = zoneLength;
        _aggregates = aggregates;
        _columnIndices = columnIndices;
    }

    /// <summary>Whether the map is complete enough to prune with. Always <see langword="false"/> in Phase 1 for stats.</summary>
    public bool IsPruningAvailable { get; }

    /// <summary>The zones child's row count.</summary>
    public int ZoneCount { get; }

    /// <summary>Rows per zone. Zero means "no zone map": the reader behaves as the data child.</summary>
    public long ZoneLength { get; }

    /// <summary>How many aggregate specs the metadata declared, in wire order.</summary>
    public int AggregateCount => _aggregates?.Length ?? 0;

    /// <summary>The aggregate at <paramref name="index"/>, in the metadata's own order.</summary>
    /// <param name="index">0-based, below <see cref="AggregateCount"/>.</param>
    /// <returns>
    /// The resolved id, or <see cref="AggregateId.Unknown"/> for an aggregate this build does not
    /// know — which disables pruning and is never an error (docs/08-semantics.md §4).
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    public AggregateId GetAggregate(int index)
    {
        AggregateId[]? aggregates = _aggregates;
        if (aggregates is null || (uint)index >= (uint)aggregates.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "Outside the aggregate list.");
        }

        return aggregates[index];
    }

    /// <summary>
    /// The column of the zones struct that carries aggregate <paramref name="aggregateIndex"/>, or
    /// <c>-1</c> when the aggregate has no state dtype for this column and therefore contributes
    /// no column at all.
    /// </summary>
    /// <param name="aggregateIndex">0-based, below <see cref="AggregateCount"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="aggregateIndex"/> is out of range.</exception>
    public int GetColumnIndex(int aggregateIndex)
    {
        int[]? columns = _columnIndices;
        if (columns is null || (uint)aggregateIndex >= (uint)columns.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(aggregateIndex), aggregateIndex, "Outside the aggregate list.");
        }

        return columns[aggregateIndex];
    }
}
