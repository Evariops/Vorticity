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
/// <param name="granuleRows">The rows of a granule, the writer's block, which an object that keeps its marks is read a part of by.</param>
internal sealed class PathCosts(CostProfile profile, long chunkBytes, Func<UInt128, IndexSegment, CostTier?>? residence = null, int granuleRows = 8_192)
{
    /// <summary>The bytes an open of an object reads first, from its end.</summary>
    private const long OpenTailBytes = ObjectStoreSource.TailBytes;

    private readonly long _chunkBytes = Math.Max(chunkBytes, 1);

    private readonly long _granuleRows = Math.Max(granuleRows, 1);

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
    /// An object a read of rows opens whole (<see cref="OpensWhole"/>) costs that one request and its bytes, whatever the
    /// columns.
    /// </remarks>
    internal Cost Rows(ObjectEntry entry, ReadOnlySpan<int> fields) => Read(entry, fields, dictionaries: true);

    /// <summary>
    /// Whether a read of rows opens the object <paramref name="entry"/> names whole: one that goes past the tail an open
    /// reads by no more than the gap two ranges are read across as one (<see cref="ObjectCache.WholeBytesUnder"/>).
    /// </summary>
    internal bool OpensWhole(ObjectEntry entry) => entry.Bytes > OpenTailBytes && entry.Bytes <= ObjectCache.WholeBytesUnder(profile);

    /// <summary>What <see cref="Rows"/> says, but for the columns' object dictionaries unless <paramref name="dictionaries"/>.</summary>
    private Cost Read(ObjectEntry entry, ReadOnlySpan<int> fields, bool dictionaries)
    {
        IReadOnlyList<long>? columns = entry.Contents?.ColumnBytes;
        double decoded = Processing(entry, fields, default, 0);
        // An object a read of rows opens whole, or one whose own file holds its table of contents, which any open brings
        // whole: that one request and its bytes.
        if (OpensWhole(entry) || entry.TableInFile)
        {
            return new Cost(1, entry.Bytes, 0, decoded);
        }

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
    /// <paramref name="share"/> of its rows: its open and its columns' object dictionaries, then of each column the units the
    /// run lies in, as many as the share of its units and one more that it starts inside, all of them at most; nothing for
    /// no share. A column's unit is its granule when the object keeps its marks and is worth more than a request, as a
    /// scan reads a part of a chunk (spec C, 2), the chunk read whole past three quarters of it; its chunk otherwise. A
    /// column of <paramref name="unsliced"/>, whose chunks no read of granules slices, a list's, is read whole. What a read
    /// of granules reads whole of each chunk it touches, as the object says (<see cref="ObjectContents.ColumnWholeBytes"/>),
    /// is priced by the chunks the run touches: the dictionary values of a key of 150 000 values, 517 KB of its column's
    /// 1.1 MB, were priced at the run's share of them.
    /// </summary>
    internal Cost Share(ObjectEntry entry, ReadOnlySpan<int> fields, double share, ReadOnlySpan<int> unsliced = default)
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

        // An object opened whole brings every row with its open: a part of them spares the decoding of the others alone.
        if (OpensWhole(entry) || entry.TableInFile)
        {
            Cost opened = Rows(entry, fields);
            return opened with { Nanoseconds = opened.Nanoseconds * share };
        }

        // Of each column, the share of its units the run lies in, and one it starts inside: the reads past the open, so
        // weighted by the columns' bytes.
        IReadOnlyList<long>? chunks = entry.Contents?.ColumnChunks;
        IReadOnlyList<long> wholes = entry.Contents?.ColumnWholeBytes ?? [];
        long granules = Granules(entry);
        double total = 0;
        double touched = 0;
        int read = 0;
        foreach (int field in fields)
        {
            if (field < columns.Count)
            {
                read++;
                double count = granules > 0 ? granules : ChunksOf(columns, chunks, field);
                double run = Math.Min(1, ((share * count) + 1) / count);
                double cut = (granules > 0 && run > GranulePlans.WholeShare) || unsliced.Contains(field) ? 1 : run;

                // What each chunk the run touches reads whole whatever its granules, a dictionary's values among them, once a
                // chunk: as many chunks as the share of them and one more it starts inside.
                long once = granules > 0 && field < wholes.Count ? Math.Min(wholes[field], columns[field]) : 0;
                double held = ChunksOf(columns, chunks, field);
                double spanned = Math.Max(cut, Math.Min(1, ((share * held) + 1) / held));
                total += columns[field];
                touched += ((columns[field] - once) * cut) + (once * spanned);
            }
        }

        // The columns' reads past the open at that share, a request a column at least when it reads granules, and their
        // dictionaries whole, apart.
        double part = total > 0 ? touched / total : 1;
        Cost whole = Read(entry, fields, dictionaries: false);
        Span<int> sorted = fields.Length <= 64 ? stackalloc int[fields.Length] : new int[fields.Length];
        fields.CopyTo(sorted);
        sorted.Sort();
        Cost dictionaries = Dictionaries(entry, sorted);
        long open = OpenBytes(entry);
        int requests = 1 + (int)Math.Ceiling((whole.Requests - 1) * part);
        return new Cost(granules > 0 ? Math.Max(requests, 1 + read) : requests, open + (long)((whole.Bytes - open) * part), 0, whole.Nanoseconds * share) + dictionaries;
    }

    /// <summary>
    /// The granules a part of each column of the object <paramref name="entry"/> names is read by: its rows over a granule's,
    /// when it keeps the marks of its columns and is larger than what a request is worth, as a scan reads the geometry of no
    /// smaller one; 0 when its chunks are read whole.
    /// </summary>
    private long Granules(ObjectEntry entry) =>
        entry.Contents is { Marks.Count: > 0 } && entry.Bytes > profile.RequestBytes ? (entry.PhysicalRows + _granuleRows - 1) / _granuleRows : 0;

    /// <summary>
    /// The bytes an open of the object <paramref name="entry"/> names reads from its end: the first read's, or its footer's
    /// when they pass it, from a 64-byte boundary as the open reads them.
    /// </summary>
    internal static long OpenBytes(ObjectEntry entry)
    {
        long window = Math.Max(OpenTailBytes, entry.FooterBytes);
        return entry.Bytes <= window ? entry.Bytes : window + ((entry.Bytes - window) & (VortexLimits.MaxAlignment - 1));
    }

    /// <summary>Whether <paramref name="region"/> of the object <paramref name="entry"/> names lies in the bytes its open reads (<see cref="OpenBytes"/>): the open serves it.</summary>
    internal static bool InOpen(ObjectEntry entry, IndexSegment region) => (long)region.Offset >= entry.Bytes - OpenBytes(entry);


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
