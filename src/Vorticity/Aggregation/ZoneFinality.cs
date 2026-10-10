using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Types;

namespace Vorticity.Aggregating;

/// <summary>
/// The floors of a key column whose statistics do not say it is sorted, but whose zones nearly are
/// (16-queries.md §9.3): the smallest value a zone and every zone after it hold. A group whose key lies
/// below the floor of the first zone not read yet is final, since no row to come can hold its key: the
/// groups go out in key order as the floor rises, and the pass holds those the zones overlap.
/// </summary>
internal sealed class ZoneFinality
{
    /// <summary>The share of the zones a value may wait past its own, at most, for its groups to go out as they close rather than at the end.</summary>
    private const int LatenessShare = 8;

    private readonly long[] _floors;
    private readonly long _zoneLength;
    private readonly long _rowCount;

    private ZoneFinality(long[] floors, long zoneLength, long rowCount, int lateness)
    {
        _floors = floors;
        _zoneLength = zoneLength;
        _rowCount = rowCount;
        Lateness = lateness;
    }

    /// <summary>The zones a value waits past its own, at most, before every group it can be in is final.</summary>
    internal int Lateness { get; }

    /// <summary>The smallest key a row at or after <paramref name="row"/> can hold; past the last row, none.</summary>
    internal long Floor(long row) => row >= _rowCount ? long.MaxValue : _floors[(int)(row / _zoneLength)];

    /// <summary>
    /// Whether <paramref name="query"/> may have its groups proven final by its key's zones, told
    /// without a read: a file's column of integers read as they are stored, the key alone, and no order
    /// but the key's ascending.
    /// </summary>
    internal static bool Candidate(AggregationQuery query)
    {
        ColumnShape[] keys = query.Plan.Keys;
        if (query.Plan.Blocking || keys.Length != 1 || query.Host.Source is not FileScanSource
            || keys[0].Field is FunctionFieldExpr || keys[0].Column.FieldPath.Length != 1 || keys[0].Kind != StorageKind.Primitive
            || keys[0].PType is not (PType.I8 or PType.I16 or PType.I32 or PType.I64 or PType.U8 or PType.U16 or PType.U32))
        {
            return false;
        }

        bool windowed = query.Take != long.MaxValue;
        foreach (GroupOperator op in query.Operators)
        {
            if (op is GroupOrder order && (order.Keys[0].Field.Component != 0 || order.Keys[0].Descending))
            {
                return false;
            }

            windowed |= op is GroupWindow;
        }

        return query.Plan.ZonesAtEveryDegree || windowed || Degree(query) == 1 || Pressed(query);
    }

    /// <summary>
    /// The bytes a row of the source may come to hold in the lanes' tables of a blocking pass: past a budget
    /// of that many for each row, the stream, which holds the groups its zones overlap, takes ÷3 to ÷15 of
    /// the memory. On 4M rows of a key late by 2 500, the blocking pass held 80 to 110 MB at fourteen lanes.
    /// </summary>
    private const long BlockingRowBytes = 32;

    /// <summary>
    /// Whether the pass blocks on several lanes and leaves the zones to one lane, a window and a budget
    /// under pressure: measured on 2026-10-08 after the stream lets its ranges absorb what they leave open,
    /// the stream took 2.2 times the blocking pass's time at fourteen lanes on 4M rows of a key late by
    /// 2 500 (13.0 ms against 5.9), past the quarter more that decision F of the plan held the zones to;
    /// at one lane, 0.84 of it (30.7 against 36.4), its first batch 1.3 ms after the start against 34.
    /// </summary>
    private static int Degree(AggregationQuery query)
    {
        int degree = query.Host.Spec().Options.DegreeOfParallelism;
        return degree > 0 ? degree : query.Host.Source.Session.Options.MaxDegreeOfParallelism;
    }

    /// <summary>Whether the query's budget is short of what a blocking pass's tables may hold: <see cref="BlockingRowBytes"/> a row of the source.</summary>
    private static bool Pressed(AggregationQuery query) =>
        query.Host.Source.Session.Options.MemoryBudget is { } budget && query.Host.Source.RowBound is long rows and > 0
        && budget.CeilingBytes < rows * BlockingRowBytes;

    /// <summary>
    /// The floors of a candidate's key, its zone maps read as a filter on it would read them, or null:
    /// a zone without exact bounds, or values that wait past their zone more than an eighth of the
    /// zones, which a stream would hold nearly whole.
    /// </summary>
    internal static async ValueTask<ZoneFinality?> PlanAsync(AggregationQuery query, CancellationToken cancellationToken)
    {
        VortexFile file = ((FileScanSource)query.Host.Source).File;
        string path = query.Plan.Keys[0].Path;
        ZoneColumn? zones = (await ZonePruningPlan
            .PlanAsync(file, file.LayoutTree, Expr.IsNotNull(Expr.Field(path)), cancellationToken, steps: null, query.Host.Counters, indexes: false)
            .ConfigureAwait(false)).Zones?.Column(path);
        if (zones is not { HasStatistics: true } || zones.RowCount != file.RowCount)
        {
            return null;
        }

        // From the last zone back, the smallest value of each and every one after it; a zone of
        // nulls alone holds no value and leaves the floor where it is.
        int count = zones.ZoneCount;
        long[] floors = new long[count];
        long[] tops = new long[count];
        long floor = long.MaxValue;
        for (int z = count - 1; z >= 0; z--)
        {
            ZoneBounds bounds = zones.Bounds(z);
            tops[z] = long.MinValue;
            if (!bounds.HasNullCount || bounds.NullCount < zones.RowsInZone(z))
            {
                if (!bounds.IsExact || !bounds.HasMin || !bounds.HasMax || !TryLong(bounds.Min, out long min) || !TryLong(bounds.Max, out long max))
                {
                    return null;
                }

                floor = Math.Min(floor, min);
                tops[z] = max;
            }

            floors[z] = floor;
        }

        // A zone's values are all final once the floor of the zones after the read passes its
        // greatest: the floors only rise, so the first zone that does is found by halving.
        int most = Math.Max(1, count / LatenessShare);
        int lateness = 0;
        for (int z = 0; z < count; z++)
        {
            int final = FirstAbove(floors, z + 1, tops[z]);
            if (final - z > most)
            {
                return null;
            }

            lateness = Math.Max(lateness, final - z);
        }

        return new ZoneFinality(floors, zones.ZoneLength, zones.RowCount, lateness);
    }

    /// <summary>The first zone at or after <paramref name="from"/> whose floor lies above <paramref name="value"/>, or the count past the last.</summary>
    private static int FirstAbove(long[] floors, int from, long value)
    {
        int low = from;
        int high = floors.Length;
        while (low < high)
        {
            int middle = (int)((uint)(low + high) >> 1);
            if (floors[middle] > value)
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }

        return low;
    }

    /// <summary>A zone's bound as a signed integer, which every candidate's values fit.</summary>
    private static bool TryLong(FilterLiteral bound, out long value)
    {
        value = bound.Kind switch
        {
            FilterLiteralKind.Signed => bound.SignedValue,
            FilterLiteralKind.Unsigned when bound.UnsignedValue <= long.MaxValue => (long)bound.UnsignedValue,
            _ => 0,
        };

        return bound.Kind is FilterLiteralKind.Signed || (bound.Kind is FilterLiteralKind.Unsigned && bound.UnsignedValue <= long.MaxValue);
    }
}

/// <summary>
/// The batches of a group by whose key its zones may prove final as the read goes: the zones are read
/// first, and the groups stream when they nearly sort, or go out once the pass has run otherwise. The
/// pass begins once, whichever runs.
/// </summary>
internal sealed class ZoneDecidedGroups(AggregationQuery query, CancellationToken cancellationToken) : IAsyncEnumerator<RecordBatch>
{
    private IAsyncEnumerator<RecordBatch>? _inner;

    public RecordBatch Current => _inner is null ? throw new InvalidOperationException("The stream has no current batch.") : _inner.Current;

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<bool> MoveNextAsync()
    {
        _inner ??= await ZoneFinality.PlanAsync(query, cancellationToken).ConfigureAwait(false) is { } zones
            ? new StreamingGroupBatches(query, cancellationToken, zones)
            : new GroupBatches(query, cancellationToken);
        return await _inner.MoveNextAsync().ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => _inner?.DisposeAsync() ?? default;
}
