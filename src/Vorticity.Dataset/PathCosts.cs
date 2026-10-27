using System;
using System.Collections.Generic;
using Vorticity.Indexes;
using Vorticity.IO;

namespace Vorticity.Dataset;

/// <summary>
/// What a path costs an object, as a planner weighs it before reading anything: its bytes, its requests at the price of the
/// bytes a request's round trip would have carried, and what the processor spends on its values, all as the dataset's cost
/// profile prices them (<see cref="CostProfile"/>). The inputs are the object's table of contents: each column's bytes,
/// chunks and encoding classes, and the regions of its synopses, a region a cache holds priced at the cache's tier.
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

    /// <summary>
    /// A path's cost: its requests and its bytes, at the store's tier, the store-tier bytes its cached regions are worth, and
    /// the nanoseconds of one core its values cost the processor.
    /// </summary>
    internal readonly record struct Cost(int Requests, long Bytes, double Cached = 0, double Nanoseconds = 0)
    {
        /// <summary>Both costs, paid one after the other.</summary>
        public static Cost operator +(Cost left, Cost right) =>
            new Cost((int)Math.Min((long)left.Requests + right.Requests, int.MaxValue), left.Bytes + right.Bytes, left.Cached + right.Cached, left.Nanoseconds + right.Nanoseconds);
    }

    /// <summary>A path's weight, what the planner compares: its bytes, its requests' worth and its processing, at the profile's prices.</summary>
    internal double Weight(Cost cost) => profile.Weight(cost.Requests, cost.Bytes, cost.Nanoseconds) + cost.Cached;

    /// <summary>A read of <paramref name="bytes"/> bytes at <paramref name="tier"/>, a cache's: its round trip and its bytes there, weighed in the store's bytes.</summary>
    internal Cost At(CostTier tier, long bytes) =>
        new Cost(0, 0, ((tier.Latency.TotalSeconds / Math.Max(tier.InFlight, 1)) + (bytes / tier.BytesPerSecond)) * profile.Store.BytesPerSecond);

    /// <summary>
    /// What the processor spends reading the rows of <paramref name="fields"/> of the object <paramref name="entry"/> names,
    /// in nanoseconds of one core (spec B, 2.1): each column's values decoded at the price of the classes its chunks hold
    /// (<see cref="Indexes.ObjectContents.ClassesOf"/>), those of <paramref name="judged"/> judged by a pushed predicate
    /// besides, and <paramref name="folded"/> values a row folded, as the profile's node folds them. A column whose contents
    /// name no class is priced at the medium class.
    /// </summary>
    internal double Processing(ObjectEntry entry, ReadOnlySpan<int> fields, ReadOnlySpan<int> judged, int folded)
    {
        long rows = entry.PhysicalRows;
        double nanoseconds = 0;
        foreach (int field in fields)
        {
            nanoseconds += Values(entry, field, rows, judge: false);
        }

        foreach (int field in judged)
        {
            nanoseconds += Values(entry, field, rows, judge: true);
        }

        // A node folds FoldValuesPerSecond over its cores: a value is that many cores' worth of a core's nanoseconds.
        return nanoseconds + ((double)rows * folded * 1e9 * Math.Max(profile.Cores, 1) / profile.FoldValuesPerSecond);
    }

    /// <summary>What decoding, or judging, the values of column <paramref name="field"/> over <paramref name="rows"/> rows costs, by its classes.</summary>
    private double Values(ObjectEntry entry, int field, long rows, bool judge)
    {
        ReadOnlySpan<uint> classes = entry.Contents is { } contents ? contents.ClassesOf(field) : default;
        if (classes.IsEmpty)
        {
            ProcessorPrice medium = profile.PriceOf(EncodingClass.Medium);
            return rows * (judge ? medium.Judge : medium.Decode);
        }

        double nanoseconds = 0;
        foreach (uint held in classes)
        {
            ProcessorPrice price = profile.PriceOf((EncodingClass)(held & 7));
            nanoseconds += (held >> 3) / 256.0 * rows * (judge ? price.Judge : price.Decode);
        }

        return nanoseconds;
    }

    /// <summary>
    /// Reading the rows of <paramref name="fields"/> of the object <paramref name="entry"/> names: its open, a request for the
    /// bytes it reads from the object's end, then each column's chunks, a request each, as its contents count them or a
    /// chunk's bytes each, every column's bytes, the object's, when its contents say none, with the object dictionary of a
    /// column written against one; and each value decoded (<see cref="Processing"/>).
    /// </summary>
    /// <remarks>
    /// A column's chunks but its last lie where its writer flushed them, each a read of its own. The last chunks of all the
    /// columns lie together at the end of the data, in the columns' order, so that a read joins two of them across the last
    /// chunks of the columns between when those take the joined gap at most, and reads those too: in an object of a few
    /// mebibytes, where every column is one chunk, what a read of two columns costs past their own bytes. A column's object
    /// dictionary lies right before its first chunk and is read with it: among the last chunks for a column of one chunk.
    /// </remarks>
    internal Cost Rows(ObjectEntry entry, ReadOnlySpan<int> fields) => Read(entry, fields, dictionaries: true);

    /// <summary>What <see cref="Rows"/> says, but for the columns' object dictionaries unless <paramref name="dictionaries"/>.</summary>
    private Cost Read(ObjectEntry entry, ReadOnlySpan<int> fields, bool dictionaries)
    {
        IReadOnlyList<long>? columns = entry.Contents?.ColumnBytes;
        double decoded = Processing(entry, fields, default, 0);
        if (columns is not { Count: > 0 })
        {
            return new Cost(1 + (int)Math.Min(int.MaxValue - 1, (entry.Bytes + _chunkBytes - 1) / _chunkBytes), entry.Bytes, 0, decoded);
        }

        IReadOnlyList<long>? chunks = entry.Contents?.ColumnChunks;
        IReadOnlyList<ColumnDictionary?> held = entry.Contents!.Dictionaries;
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

            bytes += columns[field] + (dictionaries ? DictionaryBytes(held, field) : 0);
            requests += ChunksOf(columns, chunks, field) - 1;

            // Its last chunk, a read of its own, or joined to the one before with the last chunks between, each with the
            // dictionary before it when it is its column's only one.
            long gap = 0;
            for (int between = previous + 1; previous >= 0 && between < field && gap <= joined; between++)
            {
                long count = ChunksOf(columns, chunks, between);
                gap += (columns[between] / count) + (count == 1 ? DictionaryBytes(held, between) : 0);
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

        return new Cost((int)Math.Min(int.MaxValue, requests), bytes, 0, decoded);
    }

    /// <summary>The bytes of column <paramref name="field"/>'s object dictionary among <paramref name="held"/>, 0 for none.</summary>
    private static long DictionaryBytes(IReadOnlyList<ColumnDictionary?> held, int field) =>
        field < held.Count && held[field] is { } dictionary ? dictionary.Length : 0;

    /// <summary>
    /// Reading the object dictionaries of the columns of <paramref name="sorted"/> written against one apart from their
    /// chunks, whatever share of their rows is read: a request each.
    /// </summary>
    private static Cost Dictionaries(ObjectEntry entry, ReadOnlySpan<int> sorted)
    {
        IReadOnlyList<ColumnDictionary?> held = entry.Contents?.Dictionaries ?? [];
        int requests = 0;
        long bytes = 0;
        int previous = -1;
        foreach (int field in sorted)
        {
            if (field != previous && DictionaryBytes(held, field) is > 0 and long length)
            {
                bytes += length;
                requests++;
            }

            previous = field;
        }

        return new Cost(requests, bytes);
    }

    /// <summary>The chunks of column <paramref name="field"/>, as the contents count them or a chunk's bytes each, one at least.</summary>
    private long ChunksOf(IReadOnlyList<long> columns, IReadOnlyList<long>? chunks, int field) =>
        Math.Max(1, Math.Min(int.MaxValue / 2, chunks is { } known && field < known.Count ? known[field] : (columns[field] + _chunkBytes - 1) / _chunkBytes));

    /// <summary>
    /// Reading the rows of <paramref name="fields"/> of the object <paramref name="entry"/> names in a run of
    /// <paramref name="share"/> of its rows: its open and its columns' object dictionaries, then of each column the chunks the
    /// run lies in, as many as the share of its chunks and one more that it starts inside, all of them at most; nothing for
    /// no share.
    /// </summary>
    internal Cost Share(ObjectEntry entry, ReadOnlySpan<int> fields, double share)
    {
        if (share <= 0)
        {
            return default;
        }

        IReadOnlyList<long>? columns = entry.Contents?.ColumnBytes;
        if (share >= 1 || columns is not { Count: > 0 })
        {
            return Rows(entry, fields);
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

        // The columns' reads past the open at that share, and their dictionaries whole, apart.
        double part = total > 0 ? touched / total : 1;
        Cost whole = Read(entry, fields, dictionaries: false);
        Span<int> sorted = fields.Length <= 64 ? stackalloc int[fields.Length] : new int[fields.Length];
        fields.CopyTo(sorted);
        sorted.Sort();
        Cost dictionaries = Dictionaries(entry, sorted);
        long open = OpenBytes(entry);
        return new Cost(1 + (int)Math.Ceiling((whole.Requests - 1) * part), open + (long)((whole.Bytes - open) * part), 0, whole.Nanoseconds * share) + dictionaries;
    }

    /// <summary>
    /// The bytes an open of the object <paramref name="entry"/> names reads from its end: the first read's, or its footer's
    /// when they pass it, from a 64-byte boundary as the open reads them.
    /// </summary>
    internal static long OpenBytes(ObjectEntry entry)
    {
        long window = Math.Max(OpenTailBytes, entry.FooterBytes);
        return entry.Bytes <= window ? entry.Bytes : window + ((entry.Bytes - window) & (VortexLimits.MaxAlignment - 1));
    }


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
    internal Cost Regions(UInt128 uid, ReadOnlySpan<IndexSegment?> regions)
    {
        // The regions the store reads, on the stack for the few a synopsis takes.
        Span<(ulong Offset, uint Length)> held = regions.Length <= 32 ? stackalloc (ulong, uint)[regions.Length] : new (ulong, uint)[regions.Length];
        int count = 0;
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
                cached += At(tier, some.Length).Cached;
                continue;
            }

            held[count++] = (some.Offset, some.Length);
        }

        held = held[..count];
        held.Sort();
        int requests = 0;
        long bytes = 0;
        ulong end = 0;
        ulong joined = (ulong)profile.JoinedGap;
        foreach ((ulong offset, uint length) in held)
        {
            // As the reader joins them (SegmentCoalescer): a run starts on a 64-byte boundary below its first region.
            bool merged = requests > 0 && offset <= end + joined;
            requests += merged ? 0 : 1;
            bytes += merged ? Math.Max(0, (long)(offset + length) - (long)end) : length + (long)(offset & ((ulong)VortexLimits.MaxAlignment - 1));
            end = Math.Max(end, offset + length);
        }

        return new Cost(requests, bytes, cached);
    }
}
