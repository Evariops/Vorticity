using System;
using System.Collections.Generic;
using Vorticity.Indexes;
using Vorticity.IO;

namespace Vorticity.Dataset;

/// <summary>
/// What a path costs an object, as a planner weighs it before reading anything: its bytes, and its requests at the price of
/// the bytes a request's round trip would have carried, both as the dataset's cost profile prices them
/// (<see cref="CostProfile"/>). The inputs are the object's table of contents: each column's bytes and chunks, and the
/// regions of its synopses, a region a cache holds priced at the cache's tier.
/// </summary>
/// <remarks>
/// Two regions of one read are joined across a gap of the profile's joined gap at most, as the store's source joins them:
/// 256 KiB at the remote tier.
/// </remarks>
/// <param name="profile">The dataset's cost profile.</param>
/// <param name="chunkBytes">The bytes of a column's chunk in an object whose contents count none.</param>
/// <param name="residence">The tier a cache holds an object's region at, by the object's uid; null when none does.</param>
internal sealed class PathCosts(CostProfile profile, long chunkBytes, Func<UInt128, IndexSegment, CostTier?>? residence = null)
{
    /// <summary>The bytes an open of an object reads first, from its end.</summary>
    private const long OpenTailBytes = ObjectStoreSource.TailBytes;

    private readonly long _chunkBytes = Math.Max(chunkBytes, 1);

    /// <summary>The profile the paths are priced at.</summary>
    internal CostProfile Profile => profile;

    /// <summary>A path's cost: its requests and its bytes, at the store's tier, and the store-tier bytes its cached regions are worth.</summary>
    internal readonly record struct Cost(int Requests, long Bytes, double Cached = 0);

    /// <summary>A path's weight, what the planner compares: its bytes and its requests' worth, at the profile's prices.</summary>
    internal double Weight(Cost cost) => profile.Weight(cost.Requests, cost.Bytes) + cost.Cached;

    /// <summary>
    /// Reading the rows of <paramref name="fields"/> of the object <paramref name="entry"/> names: its open, a request for the
    /// bytes it reads from the object's end, then each column's chunks, a request each, as its contents count them or a
    /// chunk's bytes each; every column's bytes, the object's, when its contents say none.
    /// </summary>
    /// <remarks>
    /// A column's chunks but its last lie where its writer flushed them, each a read of its own. The last chunks of all the
    /// columns lie together at the end of the data, in the columns' order, so that a read joins two of them across the last
    /// chunks of the columns between when those take the joined gap at most, and reads those too: in an object of a few
    /// mebibytes, where every column is one chunk, what a read of two columns costs past their own bytes.
    /// </remarks>
    internal Cost Rows(ObjectEntry entry, ReadOnlySpan<int> fields)
    {
        IReadOnlyList<long>? columns = entry.Contents?.ColumnBytes;
        if (columns is not { Count: > 0 })
        {
            return new Cost(1 + (int)Math.Min(int.MaxValue - 1, (entry.Bytes + _chunkBytes - 1) / _chunkBytes), entry.Bytes);
        }

        IReadOnlyList<long>? chunks = entry.Contents?.ColumnChunks;
        Span<int> read = fields.Length <= 64 ? stackalloc int[fields.Length] : new int[fields.Length];
        fields.CopyTo(read);
        read.Sort();
        long requests = 1;
        long bytes = OpenBytes(entry);
        long joined = profile.JoinedGap;
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
            for (int between = previous + 1; previous >= 0 && between < field && gap <= joined; between++)
            {
                gap += columns[between] / ChunksOf(columns, chunks, between);
            }

            if (previous >= 0 && gap <= joined)
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

    /// <summary>The chunks of column <paramref name="field"/>, as the contents count them or a chunk's bytes each, one at least.</summary>
    private long ChunksOf(IReadOnlyList<long> columns, IReadOnlyList<long>? chunks, int field) =>
        Math.Max(1, Math.Min(int.MaxValue / 2, chunks is { } known && field < known.Count ? known[field] : (columns[field] + _chunkBytes - 1) / _chunkBytes));

    /// <summary>
    /// Reading the rows of <paramref name="fields"/> of the object <paramref name="entry"/> names in a run of
    /// <paramref name="share"/> of its rows: its open, then of each column the chunks the run lies in, as many as the share of
    /// its chunks and one more that it starts inside, all of them at most; nothing for no share.
    /// </summary>
    internal Cost Share(ObjectEntry entry, ReadOnlySpan<int> fields, double share)
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
    internal bool Bridges(ObjectEntry entry, ReadOnlySpan<int> fields, long rows)
    {
        IReadOnlyList<long>? columns = entry.Contents?.ColumnBytes;
        long objectRows = entry.PhysicalRows;
        if (columns is not { Count: > 0 } || objectRows <= 0)
        {
            return false;
        }

        IReadOnlyList<long>? chunks = entry.Contents?.ColumnChunks;
        long read = 0;
        long apart = profile.RequestBytes;
        foreach (int field in fields)
        {
            long column = field < columns.Count ? columns[field] : 0;
            long count = Math.Max(1, chunks is { } known && field < known.Count ? known[field] : (column + _chunkBytes - 1) / _chunkBytes);
            read += (long)((double)column * rows / objectRows);
            apart += rows < objectRows / count ? column / count : 0;
        }

        return read <= apart;
    }

    /// <summary>
    /// Reading <paramref name="regions"/> of the object of uid <paramref name="uid"/> with no open of it: their bytes, those
    /// within the joined gap of each other in one request that reads the gap too; a region a cache holds at the cache's tier,
    /// its round trip and bytes there weighed in the store's bytes, nothing in memory.
    /// </summary>
    internal Cost Regions(UInt128 uid, IEnumerable<IndexSegment?> regions)
    {
        List<(ulong Offset, uint Length)> held = [];
        double cached = 0;
        foreach (IndexSegment? region in regions)
        {
            if (region is not { } some)
            {
                continue;
            }

            if (residence?.Invoke(uid, some) is { } tier)
            {
                // Its read at the cache's tier, as many bytes of the store's link as that time carries there.
                cached += ((tier.Latency.TotalSeconds / Math.Max(tier.InFlight, 1)) + (some.Length / tier.BytesPerSecond)) * profile.Store.BytesPerSecond;
                continue;
            }

            held.Add((some.Offset, some.Length));
        }

        held.Sort();
        int requests = 0;
        long bytes = 0;
        ulong end = 0;
        ulong joined = (ulong)profile.JoinedGap;
        foreach ((ulong offset, uint length) in held)
        {
            bool merged = requests > 0 && offset <= end + joined;
            requests += merged ? 0 : 1;
            bytes += merged ? Math.Max(0, (long)(offset + length) - (long)end) : length;
            end = Math.Max(end, offset + length);
        }

        return new Cost(requests, bytes, cached);
    }
}
