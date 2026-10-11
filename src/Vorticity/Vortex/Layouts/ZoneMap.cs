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
internal readonly struct ZoneMap
{
    /// <summary>
    /// The aggregates of every zone map of the tree, one int an aggregate -- its id in the low byte,
    /// its column in the zones struct plus one above it, zero for none -- of which this map's are
    /// the run from <see cref="_start"/>: one table a tree rather than two arrays a zone map.
    /// </summary>
    private readonly int[]? _aggregates;
    private readonly int _start;

    /// <summary>The most aggregates a zone map resolves: its columns plus one fit the bits above the id.</summary>
    internal const int MaxResolvedAggregates = (1 << 23) - 1;

    /// <summary>An aggregate and its column, packed as <see cref="_aggregates"/> holds them.</summary>
    internal static int Pack(AggregateId aggregate, int column) => (int)aggregate | ((column + 1) << 8);

    internal ZoneMap(
        bool pruningAvailable,
        int zoneCount,
        long zoneLength,
        int[]? aggregates,
        int start,
        int count)
    {
        IsPruningAvailable = pruningAvailable;
        ZoneCount = zoneCount;
        ZoneLength = zoneLength;
        _aggregates = aggregates;
        _start = start;
        AggregateCount = count;
    }

    /// <summary>This map over <paramref name="aggregates"/>, the tree's table once its parse is done.</summary>
    internal ZoneMap WithAggregates(int[] aggregates) =>
        new ZoneMap(IsPruningAvailable, ZoneCount, ZoneLength, aggregates, _start, AggregateCount);

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
    public int AggregateCount { get; }

    /// <summary>The aggregate at <paramref name="index"/>, in the metadata's own order.</summary>
    /// <param name="index">0-based, below <see cref="AggregateCount"/>.</param>
    /// <returns>
    /// The resolved id, or <see cref="AggregateId.Unknown"/> for an aggregate this build does not
    /// know — which disables pruning and is never an error.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    public AggregateId GetAggregate(int index)
    {
        if ((uint)index >= (uint)AggregateCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "Outside the aggregate list.");
        }

        return (AggregateId)(byte)_aggregates![_start + index];
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
        if ((uint)aggregateIndex >= (uint)AggregateCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(aggregateIndex), aggregateIndex, "Outside the aggregate list.");
        }

        return (_aggregates![_start + aggregateIndex] >> 8) - 1;
    }
}
