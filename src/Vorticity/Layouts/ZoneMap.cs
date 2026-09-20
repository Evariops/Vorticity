using System;

using Vorticity.Arrays.Metadata;

namespace Vorticity.Layouts;

/// <summary>
/// The shape of one <c>vortex.zoned</c> or <c>vortex.stats</c> zone map — the shape only, since the
/// reader exposes a map without ever pruning on it and therefore carries no zone values.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IsPruningAvailable"/> tells a caller whether the map is usable for pruning at all. It
/// is <see langword="false"/> when <see cref="ZoneLength"/> is zero, when an aggregate id is one
/// this build does not know, or when the layout is the legacy <c>vortex.stats</c>, whose metadata
/// is only parsed best-effort.
/// </para>
/// <para>
/// Zone <c>z</c> covers rows <c>[min(z * ZoneLength, RowCount), min((z + 1) * ZoneLength,
/// RowCount))</c>. A consistent file has <c>ZoneCount == ceil(RowCount / ZoneLength)</c> with only
/// the last zone short, but that is not enforced here: the zone count is simply the zones child's
/// own row count.
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

    /// <summary>
    /// Whether the map is complete enough to prune with. Always <see langword="false"/> for a
    /// legacy <c>vortex.stats</c> layout.
    /// </summary>
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
    /// know — which disables pruning and is never an error.
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
