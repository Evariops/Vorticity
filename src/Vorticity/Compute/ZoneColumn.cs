using System;
using System.Collections.Generic;
using System.Threading;
using Vorticity.Expressions;
using Vorticity.File;

namespace Vorticity.Compute;

/// <summary>
/// The per-zone bounds of one column a filter reads, decoded once per file and detached from the
/// arena they came out of. Zone <c>z</c> covers rows
/// <c>[z * ZoneLength, min((z + 1) * ZoneLength, RowCount))</c>, so the final zone is short rather
/// than padded.
/// </summary>
internal sealed class ZoneColumn
{
    /// <summary>The zones one by one, or null when <see cref="_table"/> holds them.</summary>
    private readonly ZoneBounds[]? _zones;

    internal ZoneColumn(FieldExpr field, long zoneLength, long rowCount, ZoneBounds[] zones, bool isDecimal = false)
    {
        Field = field;
        ZoneLength = zoneLength;
        RowCount = rowCount;
        _zones = zones;
        IsDecimal = isDecimal;
    }

    /// <summary>A numeric column's zones, held as columns.</summary>
    internal ZoneColumn(FieldExpr field, long zoneLength, long rowCount, ZoneTable table)
    {
        Field = field;
        ZoneLength = zoneLength;
        RowCount = rowCount;
        _table = table;
    }

    /// <summary>The column, as the filter names it.</summary>
    internal FieldExpr Field { get; }

    /// <summary>
    /// Whether the bounds are unscaled decimals -- signed integers, or sixteen or thirty-two
    /// little-endian bytes -- which order as the numbers they are, never bytewise.
    /// </summary>
    internal bool IsDecimal { get; }

    /// <summary>Rows per zone; zero means the map is unusable.</summary>
    internal long ZoneLength { get; }

    /// <summary>The column's own row count, which bounds the last zone.</summary>
    internal long RowCount { get; }

    /// <summary>Whether this column can prune anything at all.</summary>
    internal bool HasStatistics => ZoneLength > 0 && ZoneCount > 0;

    /// <summary>How many zones the map describes.</summary>
    internal int ZoneCount => _zones?.Length ?? _table!.ZoneCount;

    private ZoneTable? _table;

    /// <summary>
    /// The zones as columns: the column's own storage when it is numeric, else built at the first
    /// question and kept, or <see cref="ZoneTable.None"/> when the bounds are not all of one
    /// numeric kind.
    /// </summary>
    /// <remarks>Two scans asking at once build it twice and keep one: both are the same.</remarks>
    internal ZoneTable Table
    {
        get
        {
            ZoneTable? table = Volatile.Read(ref _table);
            if (table is null)
            {
                Interlocked.CompareExchange(ref _table, ZoneTable.Build(this), null);
                table = _table!;
            }

            return table;
        }
    }

    /// <summary>Zone <paramref name="index"/>'s summary.</summary>
    /// <param name="index">A zone index.</param>
    internal ZoneBounds Bounds(int index) =>
        _zones is not { } zones ? _table!.Bounds(index)
        : (uint)index < (uint)zones.Length ? zones[index] : ZoneBounds.Unknown;

    /// <summary>
    /// How many rows zone <paramref name="index"/> actually covers. The final zone is short, so a
    /// rule of the form "a zone whose nulls fill it cannot satisfy a comparison" is wrong by a row
    /// for it unless it asks this rather than assuming <see cref="ZoneLength"/>.
    /// </summary>
    /// <param name="index">A zone index.</param>
    internal long RowsInZone(int index) => RowsInZone(index, ZoneLength, RowCount);

    /// <summary>How many rows zone <paramref name="index"/> of zones of <paramref name="zoneLength"/> over <paramref name="rowCount"/> rows covers.</summary>
    internal static long RowsInZone(int index, long zoneLength, long rowCount)
    {
        long start = Math.Min((long)index * zoneLength, rowCount);
        long end = Math.Min(start + zoneLength, rowCount);
        return end - start;
    }

    /// <summary>The zones overlapping <paramref name="rows"/>, as a half-open index range.</summary>
    /// <param name="rows">A row range in the column's own coordinates.</param>
    /// <returns>The first zone and the one past the last; empty when nothing overlaps.</returns>
    /// <remarks>
    /// A range and not a sequence: the indices are consecutive, so two <c>int</c>s say everything
    /// an <c>IEnumerable&lt;int&gt;</c> would, and this is asked once per split and per predicate,
    /// which is often enough that an iterator per call would be felt.
    /// </remarks>
    internal ZoneRange Zones(RowRange rows)
    {
        if (ZoneLength <= 0 || rows.Length <= 0)
        {
            return default;
        }

        long first = rows.Start / ZoneLength;
        long last = Math.Min((rows.End - 1) / ZoneLength, ZoneCount - 1);
        return last < first ? default : new ZoneRange((int)first, (int)last + 1);
    }
}

/// <summary>A half-open range of zone indices.</summary>
/// <param name="Start">The first zone.</param>
/// <param name="End">One past the last; equal to <paramref name="Start"/> when empty.</param>
internal readonly record struct ZoneRange(int Start, int End);
