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

    /// <summary>The bytes an open of an object reads first, from its end.</summary>
    private const long OpenTailBytes = IO.ObjectStoreSource.TailBytes;

    /// <summary>A path's cost: its requests and its bytes.</summary>
    internal readonly record struct Cost(int Requests, long Bytes)
    {
        /// <summary>Its bytes and its requests' worth together, what the planner compares.</summary>
        internal long Weight => Bytes + (Requests * RequestBytes);
    }

    /// <summary>
    /// Reading the rows of <paramref name="fields"/> of the object <paramref name="entry"/> names: its open, a request for the
    /// bytes it reads from the object's end, then each column's chunks, a request each, as its contents count them or a
    /// mebibyte each; every column's bytes, the object's, when its contents say none.
    /// </summary>
    /// <remarks>
    /// A column's chunks but its last lie where its writer flushed them, each a read of its own. The last chunks of all the
    /// columns lie together at the end of the data, in the columns' order, so that a read joins two of them across the last
    /// chunks of the columns between when those take 256 KiB at most (<see cref="IO.SegmentReadOptions.Remote"/>), and
    /// reads those too: in an object of a few mebibytes, where every column is one chunk, what a read of two columns costs
    /// past their own bytes.
    /// </remarks>
    internal static Cost Rows(ObjectEntry entry, ReadOnlySpan<int> fields)
    {
        IReadOnlyList<long>? columns = entry.Contents?.ColumnBytes;
        if (columns is not { Count: > 0 })
        {
            return new Cost(1 + (int)Math.Min(int.MaxValue - 1, (entry.Bytes + ChunkBytes - 1) / ChunkBytes), entry.Bytes);
        }

        IReadOnlyList<long>? chunks = entry.Contents?.ColumnChunks;
        Span<int> read = fields.Length <= 64 ? stackalloc int[fields.Length] : new int[fields.Length];
        fields.CopyTo(read);
        read.Sort();
        long requests = 1;
        long bytes = OpenBytes(entry);
        int previous = -1;
        foreach (int field in read)
        {
            if (field == previous || field >= columns.Count)
            {
                continue;
            }

            bytes += columns[field];
            requests += ChunksOf(columns, chunks, field) - 1;

            // Its last chunk, a read of its own, or joined to the one before with the last chunks between.
            long gap = 0;
            for (int between = previous + 1; previous >= 0 && between < field && gap <= JoinedGap; between++)
            {
                gap += columns[between] / ChunksOf(columns, chunks, between);
            }

            if (previous >= 0 && gap <= JoinedGap)
            {
                bytes += gap;
            }
            else
            {
                requests++;
            }

            previous = field;
        }

        return new Cost((int)Math.Min(int.MaxValue, requests), bytes);
    }

    /// <summary>The chunks of column <paramref name="field"/>, as the contents count them or a mebibyte each, one at least.</summary>
    private static long ChunksOf(IReadOnlyList<long> columns, IReadOnlyList<long>? chunks, int field) =>
        Math.Max(1, Math.Min(int.MaxValue / 2, chunks is { } known && field < known.Count ? known[field] : (columns[field] + ChunkBytes - 1) / ChunkBytes));

    /// <summary>
    /// Reading the rows of <paramref name="fields"/> of the object <paramref name="entry"/> names in a run of
    /// <paramref name="share"/> of its rows: its open, then of each column the chunks the run lies in, as many as the share of
    /// its chunks and one more that it starts inside, all of them at most; nothing for no share.
    /// </summary>
    internal static Cost Share(ObjectEntry entry, ReadOnlySpan<int> fields, double share)
    {
        if (share <= 0)
        {
            return default;
        }

        Cost whole = Rows(entry, fields);
        IReadOnlyList<long>? columns = entry.Contents?.ColumnBytes;
        if (share >= 1 || columns is not { Count: > 0 })
        {
            return whole;
        }

        // Of each column, the share of its chunks the run lies in, and one it starts inside: the reads past the open, so
        // weighted by the columns' bytes.
        IReadOnlyList<long>? chunks = entry.Contents?.ColumnChunks;
        double total = 0;
        double touched = 0;
        foreach (int field in fields)
        {
            if (field < columns.Count)
            {
                double count = ChunksOf(columns, chunks, field);
                total += columns[field];
                touched += columns[field] * Math.Min(1, ((share * count) + 1) / count);
            }
        }

        double part = total > 0 ? touched / total : 1;
        long open = OpenBytes(entry);
        return new Cost(1 + (int)Math.Ceiling((whole.Requests - 1) * part), open + (long)((whole.Bytes - open) * part));
    }

    /// <summary>The bytes an open of the object <paramref name="entry"/> names reads from its end: the first read's, or its footer's when they pass it.</summary>
    internal static long OpenBytes(ObjectEntry entry) => Math.Min(entry.Bytes, Math.Max(OpenTailBytes, entry.FooterBytes));

    /// <summary>
    /// Whether reading <paramref name="rows"/> rows of <paramref name="fields"/> of the object <paramref name="entry"/> names,
    /// between two ranges a scan reads, costs less than reading the two ranges apart: a request more, and each column's
    /// chunk the gap ends inside, which both ranges would read. A column's chunks are its bytes over its chunks, each over an
    /// even share of the rows; with no contents, never.
    /// </summary>
    internal static bool Bridges(ObjectEntry entry, ReadOnlySpan<int> fields, long rows)
    {
        IReadOnlyList<long>? columns = entry.Contents?.ColumnBytes;
        long objectRows = entry.PhysicalRows;
        if (columns is not { Count: > 0 } || objectRows <= 0)
        {
            return false;
        }

        IReadOnlyList<long>? chunks = entry.Contents?.ColumnChunks;
        long read = 0;
        long apart = RequestBytes;
        foreach (int field in fields)
        {
            long column = field < columns.Count ? columns[field] : 0;
            long count = Math.Max(1, chunks is { } known && field < known.Count ? known[field] : (column + ChunkBytes - 1) / ChunkBytes);
            read += (long)((double)column * rows / objectRows);
            apart += rows < objectRows / count ? column / count : 0;
        }

        return read <= apart;
    }

    /// <summary>Reading <paramref name="regions"/> of an object with no open of it: their bytes, those within a gap of each other in one request that reads the gap too.</summary>
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
            bool joined = requests > 0 && offset <= end + JoinedGap;
            requests += joined ? 0 : 1;
            bytes += joined ? Math.Max(0, (long)(offset + length) - (long)end) : length;
            end = Math.Max(end, offset + length);
        }

        return new Cost(requests, bytes);
    }
}
