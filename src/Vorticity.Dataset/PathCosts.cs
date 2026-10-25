using System;
using System.Collections.Generic;
using Vorticity.Indexes;

namespace Vorticity.Dataset;

/// <summary>
/// What a path costs an object, as a planner weighs it before reading anything: its bytes, and its requests at the price of
/// the bytes a request's round trip would have carried. The inputs are the object's table of contents: each column's
/// bytes, and the regions of its synopses.
/// </summary>
/// <remarks>
/// A request costs what 293 KB of transfer cost under the cost model of an object store (<see cref="Scanning.ReadCost"/>):
/// the rule that joins two ranges across a gap of 256 KiB at most (<see cref="IO.SegmentReadOptions.Remote"/>), which joins
/// the regions of one read here too.
/// </remarks>
internal static class PathCosts
{
    /// <summary>The bytes a request's round trip is worth.</summary>
    internal const long RequestBytes = Scanning.ReadCost.RequestBytes;

    /// <summary>The gap two regions are read across as one, as an object store's source joins them.</summary>
    private const long JoinedGap = 256 << 10;

    /// <summary>The bytes of a column's chunk in a dataset's objects, each a request when a scan reads it.</summary>
    private const long ChunkBytes = 1 << 20;

    /// <summary>A path's cost: its requests and its bytes.</summary>
    internal readonly record struct Cost(int Requests, long Bytes)
    {
        /// <summary>Its bytes and its requests' worth together, what the planner compares.</summary>
        internal long Weight => Bytes + (Requests * RequestBytes);
    }

    /// <summary>
    /// Reading the rows of <paramref name="fields"/> of the object <paramref name="entry"/> names: its open, then each
    /// column's chunks, a request each; every column's bytes, the object's, when its contents say none.
    /// </summary>
    internal static Cost Rows(ObjectEntry entry, ReadOnlySpan<int> fields)
    {
        IReadOnlyList<long>? columns = entry.Contents?.ColumnBytes;
        if (columns is not { Count: > 0 })
        {
            return new Cost(1 + (int)Math.Min(int.MaxValue - 1, (entry.Bytes + ChunkBytes - 1) / ChunkBytes), entry.Bytes);
        }

        int requests = 1;
        long bytes = 0;
        foreach (int field in fields)
        {
            long column = field < columns.Count ? columns[field] : 0;
            bytes += column;
            requests += (int)Math.Min(int.MaxValue / 2, (column + ChunkBytes - 1) / ChunkBytes);
        }

        return new Cost(requests, bytes);
    }

    /// <summary>Reading <paramref name="regions"/> of an object with no open of it: their bytes, those within a gap of each other in one request.</summary>
    internal static Cost Regions(IEnumerable<IndexSegment?> regions)
    {
        List<(ulong Offset, uint Length)> held = [];
        foreach (IndexSegment? region in regions)
        {
            if (region is { } some)
            {
                held.Add((some.Offset, some.Length));
            }
        }

        held.Sort();
        int requests = 0;
        long bytes = 0;
        ulong end = 0;
        foreach ((ulong offset, uint length) in held)
        {
            requests += requests == 0 || offset > end + JoinedGap ? 1 : 0;
            bytes += length;
            end = Math.Max(end, offset + length);
        }

        return new Cost(requests, bytes);
    }
}
