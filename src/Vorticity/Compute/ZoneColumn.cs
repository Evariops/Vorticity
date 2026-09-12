// One filtered column's zone map, decoded once per scan and detached from the arena it came out of.
//
// Zone z covers rows [z * ZoneLength, min((z + 1) * ZoneLength, RowCount)) -- the same arithmetic
// ZoneMap documents, and the reason the LAST zone is short rather than padded. RowsInZone spells
// that out because the null-count rule ("a zone whose nulls fill it cannot satisfy a comparison")
// is wrong by one row for the last zone if it does not.
using System;
using System.Collections.Generic;
using Vorticity.Expressions;
using Vorticity.File;

namespace Vorticity.Compute;

/// <summary>The per-zone bounds of one column a filter reads.</summary>
internal sealed class ZoneColumn
{
    private readonly ZoneBounds[] _zones;

    internal ZoneColumn(FieldExpr field, long zoneLength, long rowCount, ZoneBounds[] zones)
    {
        Field = field;
        ZoneLength = zoneLength;
        RowCount = rowCount;
        _zones = zones;
    }

    /// <summary>The column, as the filter names it.</summary>
    internal FieldExpr Field { get; }

    /// <summary>Rows per zone; zero means the map is unusable.</summary>
    internal long ZoneLength { get; }

    /// <summary>The column's own row count, which bounds the last zone.</summary>
    internal long RowCount { get; }

    /// <summary>Whether this column can prune anything at all.</summary>
    internal bool HasStatistics => ZoneLength > 0 && _zones.Length > 0;

    /// <summary>Zone <paramref name="index"/>'s summary.</summary>
    /// <param name="index">A zone index.</param>
    internal ZoneBounds Bounds(int index) =>
        (uint)index < (uint)_zones.Length ? _zones[index] : ZoneBounds.Unknown;

    /// <summary>How many rows zone <paramref name="index"/> actually covers.</summary>
    /// <param name="index">A zone index.</param>
    internal long RowsInZone(int index)
    {
        long start = Math.Min((long)index * ZoneLength, RowCount);
        long end = Math.Min(start + ZoneLength, RowCount);
        return end - start;
    }

    /// <summary>The zones overlapping <paramref name="rows"/>.</summary>
    /// <param name="rows">A row range in the column's own coordinates.</param>
    internal IEnumerable<int> Zones(RowRange rows)
    {
        if (ZoneLength <= 0 || rows.Length <= 0)
        {
            yield break;
        }

        long first = rows.Start / ZoneLength;
        long last = (rows.End - 1) / ZoneLength;
        long limit = Math.Min(last, _zones.Length - 1);

        for (long z = first; z <= limit; z++)
        {
            yield return (int)z;
        }
    }
}
