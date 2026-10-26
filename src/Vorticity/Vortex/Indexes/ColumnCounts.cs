using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Vorticity.Aggregating;
using Vorticity.Serialization;

namespace Vorticity.Indexes;

/// <summary>What a group of a column's counts stands for: a value, the column's nulls, or the values past its budget together.</summary>
internal enum CountedValue : byte
{
    /// <summary>One value of the column.</summary>
    Value = 0,

    /// <summary>The rows whose value is null.</summary>
    Null = 1,

    /// <summary>The values a budget left out, their rows and states together, exact.</summary>
    Others = 2,
}

/// <summary>
/// A group of a column's counts: what it stands for, and its entries from the entry <paramref name="FirstEntry"/>, one over
/// the whole object, or one a bucket its rows lie in for counts kept by bucket.
/// </summary>
/// <param name="Kind">A value, the nulls, or the values left out.</param>
/// <param name="Value">The value's bytes as the column stores them, when <paramref name="Kind"/> is <see cref="CountedValue.Value"/>.</param>
/// <param name="FirstEntry">Its first entry.</param>
/// <param name="Entries">Its entries: one, or one a bucket (<see cref="ColumnCounts.Buckets"/>).</param>
internal readonly record struct CountGroup(CountedValue Kind, byte[]? Value, int FirstEntry, int Entries);

/// <summary>
/// What a reader of some values of a column's counts reads first (<see cref="ColumnCounts.ReadDirectory"/>): the counts'
/// header, their measures, and the directory of their chunks, the chunks starting at <paramref name="HeaderBytes"/>.
/// </summary>
/// <param name="HeaderBytes">The bytes before the first chunk.</param>
/// <param name="MeasureFields">The measures each entry holds.</param>
/// <param name="Kinds">How each is summed.</param>
/// <param name="Sketched">Whether the counts' sketches keep a quantile sketch of each measure.</param>
/// <param name="AttachedFields">The wide columns whose registers the sketches keep.</param>
/// <param name="Buckets">The buckets the counts keep apart, 0 for none.</param>
/// <param name="Entries">The counts' entries.</param>
/// <param name="Groups">Their groups.</param>
/// <param name="Chunks">The chunks, in their order.</param>
internal sealed record CountsDirectory(
    int HeaderBytes, int[] MeasureFields, MeasureKind[] Kinds, bool Sketched, int[] AttachedFields, int Buckets, int Entries, int Groups,
    IReadOnlyList<CountsChunk> Chunks);

/// <summary>One chunk of a column's counts, as their directory names it.</summary>
/// <param name="Offset">Where it starts past the header.</param>
/// <param name="Length">Its bytes.</param>
/// <param name="FirstGroup">Its first group.</param>
/// <param name="FirstEntry">Its first entry.</param>
/// <param name="Kind">What its first group stands for.</param>
/// <param name="Value">Its first group's value, for a value.</param>
/// <param name="Checksum">The XXH3-64 of its bytes, when the counts keep one a chunk.</param>
internal readonly record struct CountsChunk(int Offset, int Length, int FirstGroup, int FirstEntry, CountedValue Kind, byte[]? Value, ulong? Checksum);

/// <summary>
/// A column's counts in an object (<see cref="IndexKinds.ColumnCounts"/>): for each of its values, its nulls, and past an
/// absolute budget the values left out together, an entry over the whole object that holds the rows with that value and
/// the states of every measure column over them, its values, their exact sum, their least and their greatest, in the
/// words <see cref="StateWords"/> lays out. A region a column, which the object's table of contents names, so that a
/// reader fetches it with no open of the object; the entries' quantile sketches and distinct registers lie in a region of
/// their own right after it (<see cref="ColumnSketches"/>), which a question of counts, sums and extremes does not read.
/// </summary>
/// <remarks>
/// <para>
/// The grain is the object, whatever its rows: the application cuts its objects by the values it chooses
/// (<c>ClusteringOptions</c>), and the counts describe what each holds. The groups go the nulls first, then the values
/// left out, then the values in the order of their bytes. Entries of the same value from two objects merge word for word.
/// </para>
/// <para>
/// The region is a header, the measures, a directory of chunks of about <see cref="ChunkBytes"/>, each naming its first
/// group, then the chunks: a group's value, its entry's rows, and each measure's values as what they lack of the rows,
/// its sum and its extremes as varints, a float's extremes as their eight bytes. A reader of one value can read the
/// header and the chunk that holds it alone.
/// </para>
/// <para>
/// Counts kept by bucket, a rollup's by clustering value, give each group an entry for each bucket its value has rows in,
/// the buckets ascending, each entry its bucket's number before its words: a reader of one value finds all its buckets'
/// entries in the chunk that holds it.
/// </para>
/// <para>
/// A rollup's counts, which weigh megabytes, are kept with a checksum a chunk (<see cref="ChunkSums"/>, a flag of the
/// header): the directory gives each chunk its first entry and the XXH3-64 of its bytes, and the header ends with its
/// own. A reader of some values reads the header, of the size the rollup's entry records, then their chunks alone
/// (<see cref="ReadDirectory"/>, <see cref="ChunksOf"/>, <see cref="FromChunks"/>). An object's counts keep none: they
/// are read whole, the region's checksum checking them.
/// </para>
/// </remarks>
internal sealed class ColumnCounts
{
    /// <summary>
    /// The version this library writes and reads. Version 1 counted each value by the hours of an instant, which an earlier
    /// writer of this branch kept; it is read as no counts.
    /// </summary>
    internal const byte Version = 2;

    /// <summary>The bytes a chunk of groups takes about: a group is never cut.</summary>
    internal const int ChunkBytes = 16 << 10;

    /// <summary>The header's flag of counts whose sketches keep a quantile sketch of each measure.</summary>
    private const byte SketchesFlag = 1;

    /// <summary>The header's flag of counts whose sketches keep the registers of the distinct values of wide columns.</summary>
    private const byte DistinctFlag = 2;

    /// <summary>The header's flag of counts kept by bucket (<see cref="Buckets"/>).</summary>
    private const byte BucketsFlag = 4;

    /// <summary>The header's flag of counts whose directory gives each chunk's first entry and checksum (<see cref="ChunkSums"/>).</summary>
    private const byte ChunkSumsFlag = 8;

    /// <param name="measureFields">The top-level columns whose states each entry holds, in their order.</param>
    /// <param name="kinds">How each is summed.</param>
    /// <param name="groups">The groups, in their order, an entry each.</param>
    /// <param name="words">Each entry's words, <see cref="StateWords.Count"/> of them: its rows, then each measure's states.</param>
    internal ColumnCounts(int[] measureFields, MeasureKind[] kinds, CountGroup[] groups, long[] words)
    {
        MeasureFields = measureFields;
        Kinds = kinds;
        Groups = groups;
        Words = words;
        WordsPerEntry = StateWords.Count(kinds);
    }

    /// <summary>
    /// Whether the column's sketches (<see cref="ColumnSketches"/>) keep a quantile sketch of each measure over each entry's
    /// rows: a column of at most <see cref="Writing.ColumnCountsBuilder.SketchValues"/> values.
    /// </summary>
    internal bool Sketched { get; init; }

    /// <summary>
    /// The wide columns whose distinct values the column's sketches keep the registers of over each entry's rows: the text
    /// columns of many values of the object, the counts of a column of few values keeping their registers. Empty for none.
    /// </summary>
    internal int[] AttachedFields { get; init; } = [];

    /// <summary>
    /// The buckets the counts keep apart, a rollup's clustering values: 0 for an entry a group; past 0, a group holds an
    /// entry for each bucket its value has rows in, <see cref="EntryBuckets"/> numbering each entry's.
    /// </summary>
    internal int Buckets { get; init; }

    /// <summary>The bucket of each entry, ascending within a group, when <see cref="Buckets"/> is past 0; null otherwise.</summary>
    internal int[]? EntryBuckets { get; init; }

    /// <summary>
    /// Whether the directory gives each chunk's first entry and checksum, and the header its own, so that a reader of some
    /// values reads the header, then their chunks alone, and checks each (<see cref="FromChunks"/>): a rollup's, whose
    /// counts weigh megabytes. Set before <see cref="ToBytes"/>.
    /// </summary>
    internal bool ChunkSums { get; set; }

    /// <summary>The bytes the counts hold in memory, about: their words and their groups.</summary>
    internal long Bytes => (Words.Length * (long)sizeof(long)) + (Groups.Length * 64L) + ((EntryBuckets?.Length ?? 0) * (long)sizeof(int));

    /// <summary>The top-level columns whose states each entry holds.</summary>
    internal int[] MeasureFields { get; }

    /// <summary>How each measure is summed.</summary>
    internal MeasureKind[] Kinds { get; }

    /// <summary>The groups: the nulls, the values left out, then the values in the order of their bytes.</summary>
    internal CountGroup[] Groups { get; }

    /// <summary>Each entry's words, <see cref="WordsPerEntry"/> of them.</summary>
    internal long[] Words { get; }

    /// <summary>The words of an entry: its rows, then each measure's states.</summary>
    internal int WordsPerEntry { get; }

    /// <summary>The entries: a group's each, or one a bucket of each group.</summary>
    internal int Entries => Words.Length / WordsPerEntry;

    /// <summary>The words of entry <paramref name="entry"/>.</summary>
    internal ReadOnlySpan<long> WordsOf(int entry) => Words.AsSpan(entry * WordsPerEntry, WordsPerEntry);

    /// <summary>The counts' bytes, which <see cref="From"/> reads.</summary>
    internal byte[] ToBytes()
    {
        Output chunks = new Output(ChunkBytes);
        List<(int Offset, int Length, int FirstGroup, int FirstEntry, ulong Checksum)> directory = [];
        int chunkStart = 0;
        int chunkGroup = 0;
        for (int group = 0; group < Groups.Length; group++)
        {
            WriteGroup(ref chunks, Groups[group]);
            if (chunks.Length - chunkStart >= ChunkBytes || group == Groups.Length - 1)
            {
                int length = chunks.Length - chunkStart;
                ulong checksum = ChunkSums ? System.IO.Hashing.XxHash3.HashToUInt64(chunks.Span.Slice(chunkStart, length)) : 0;
                directory.Add((chunkStart, length, chunkGroup, Groups[chunkGroup].FirstEntry, checksum));
                chunkStart = chunks.Length;
                chunkGroup = group + 1;
            }
        }

        Output head = new Output(256);
        head.Byte(Version);
        head.Byte((byte)((Sketched ? SketchesFlag : 0) | (AttachedFields.Length == 0 ? 0 : DistinctFlag) | (Buckets == 0 ? 0 : BucketsFlag)
            | (ChunkSums ? ChunkSumsFlag : 0)));
        head.Varint((ulong)MeasureFields.Length);
        for (int i = 0; i < MeasureFields.Length; i++)
        {
            head.Varint((ulong)MeasureFields[i]);
            head.Byte((byte)Kinds[i]);
        }

        if (AttachedFields.Length > 0)
        {
            head.Varint((ulong)AttachedFields.Length);
            foreach (int field in AttachedFields)
            {
                head.Varint((ulong)field);
            }
        }

        if (Buckets > 0)
        {
            head.Varint((ulong)Buckets);
            head.Varint((ulong)Entries);
        }

        head.Varint((ulong)Groups.Length);
        head.Varint((ulong)directory.Count);
        foreach ((int offset, int length, int firstGroup, int firstEntry, ulong checksum) in directory)
        {
            head.Varint((ulong)offset);
            head.Varint((ulong)length);
            head.Varint((ulong)firstGroup);
            if (ChunkSums)
            {
                head.Varint((ulong)firstEntry);
                head.Long((long)checksum);
            }

            Key(ref head, Groups[firstGroup]);
        }

        if (ChunkSums)
        {
            // The header's own checksum closes it, which a reader of the header alone checks.
            head.Long((long)System.IO.Hashing.XxHash3.HashToUInt64(head.Span));
        }

        byte[] bytes = new byte[head.Length + chunks.Length];
        head.Span.CopyTo(bytes);
        chunks.Span.CopyTo(bytes.AsSpan(head.Length));
        return bytes;
    }

    /// <summary>
    /// The header and the directory of the counts whose first bytes are <paramref name="head"/>, which a reader of some of
    /// their values reads before the chunks that hold them: the counts' header whole, or more of their bytes.
    /// </summary>
    /// <exception cref="VortexFormatException">The bytes are no counts' header this library reads, or fail its checksum.</exception>
    internal static CountsDirectory ReadDirectory(ReadOnlySpan<byte> head)
    {
        int at = 0;
        return Head(head, ref at);
    }

    /// <summary>
    /// The chunks of <paramref name="directory"/> that hold the groups of <paramref name="values"/>, ascending, and the first
    /// when <paramref name="leading"/> asks the nulls' and the values left out's groups, which lead the counts.
    /// </summary>
    internal static List<int> ChunksOf(CountsDirectory directory, IReadOnlyList<byte[]> values, bool leading)
    {
        SortedSet<int> chunks = [];
        if (leading && directory.Chunks.Count > 0)
        {
            chunks.Add(0);
        }

        foreach (byte[] value in values)
        {
            // The last chunk whose first group comes at or before the value's.
            CountGroup asked = new CountGroup(CountedValue.Value, value, 0, 1);
            int low = 0;
            int high = directory.Chunks.Count - 1;
            int found = -1;
            while (low <= high)
            {
                int middle = low + ((high - low) >> 1);
                CountsChunk chunk = directory.Chunks[middle];
                CountGroup first = new CountGroup(chunk.Kind, chunk.Value, 0, 1);
                if (!Precedes(asked, first))
                {
                    found = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            if (found >= 0)
            {
                chunks.Add(found);
            }
        }

        return [.. chunks];
    }

    /// <summary>
    /// The counts of chunks <paramref name="chunks"/> of <paramref name="directory"/>, ascending, whose bytes are
    /// <paramref name="bytes"/>, each checked against its checksum: their groups alone, their entries from 0,
    /// <paramref name="entries"/> each one's place among all the counts' entries, which their sketches take.
    /// </summary>
    /// <exception cref="VortexFormatException">A chunk is not the length or the checksum its directory says, or no counts.</exception>
    internal static ColumnCounts FromChunks(CountsDirectory directory, IReadOnlyList<int> chunks, IReadOnlyList<ReadOnlyMemory<byte>> bytes, out int[] entries)
    {
        int groups = 0;
        int held = 0;
        foreach (int chunk in chunks)
        {
            (int chunkGroups, int chunkEntries) = Sizes(directory, chunk);
            groups += chunkGroups;
            held += chunkEntries;
        }

        ColumnCounts counts = new ColumnCounts(directory.MeasureFields, directory.Kinds, new CountGroup[groups], new long[held * StateWords.Count(directory.Kinds)])
        {
            Sketched = directory.Sketched,
            AttachedFields = directory.AttachedFields,
            Buckets = directory.Buckets,
            EntryBuckets = directory.Buckets == 0 ? null : new int[held],
        };
        entries = new int[held];
        int group = 0;
        int entry = 0;
        for (int i = 0; i < chunks.Count; i++)
        {
            CountsChunk chunk = directory.Chunks[chunks[i]];
            ReadOnlySpan<byte> span = bytes[i].Span;
            if (span.Length != chunk.Length || (chunk.Checksum is { } checksum && System.IO.Hashing.XxHash3.HashToUInt64(span) != checksum))
            {
                throw new VortexFormatException($"A chunk of a column's counts is not the {chunk.Length} bytes its directory checks.");
            }

            (int chunkGroups, int _) = Sizes(directory, chunks[i]);
            int at = 0;
            int start = entry;
            for (int g = 0; g < chunkGroups; g++)
            {
                CountGroup read = counts.ReadGroup(span, ref at, entry);
                counts.Groups[group++] = read;
                entry += read.Entries;
            }

            for (int e = start; e < entry; e++)
            {
                entries[e] = chunk.FirstEntry + (e - start);
            }

            if (at != span.Length)
            {
                throw new VortexFormatException($"A chunk of a column's counts ends at {at} where its directory says {span.Length}.");
            }
        }

        return counts;
    }

    /// <summary>The entries chunks <paramref name="chunks"/> of <paramref name="directory"/> hold, ascending: those whose sketches a reader of them reads.</summary>
    internal static List<int> EntriesOf(CountsDirectory directory, IReadOnlyList<int> chunks)
    {
        List<int> entries = [];
        foreach (int chunk in chunks)
        {
            int first = directory.Chunks[chunk].FirstEntry;
            (_, int count) = Sizes(directory, chunk);
            for (int entry = first; entry < first + count; entry++)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    /// <summary>The groups and the entries chunk <paramref name="chunk"/> of <paramref name="directory"/> holds.</summary>
    private static (int Groups, int Entries) Sizes(CountsDirectory directory, int chunk)
    {
        CountsChunk at = directory.Chunks[chunk];
        bool last = chunk + 1 == directory.Chunks.Count;
        return (
            (last ? directory.Groups : directory.Chunks[chunk + 1].FirstGroup) - at.FirstGroup,
            (last ? directory.Entries : directory.Chunks[chunk + 1].FirstEntry) - at.FirstEntry);
    }

    /// <summary>The counts' header and directory, from <paramref name="at"/>, past which the groups start.</summary>
    private static CountsDirectory Head(ReadOnlySpan<byte> bytes, ref int at)
    {
        if (bytes.Length < 2 || bytes[0] != Version)
        {
            throw new VortexFormatException(bytes.Length < 2 ? "A column's counts end before their header." : $"A column's counts are of version {bytes[0]}, which this library does not read.");
        }

        at = 2;
        int measures = Count(bytes, ref at);
        int[] fields = new int[measures];
        MeasureKind[] kinds = new MeasureKind[measures];
        for (int i = 0; i < measures; i++)
        {
            fields[i] = Count(bytes, ref at);
            kinds[i] = at < bytes.Length && bytes[at] <= (byte)MeasureKind.Float
                ? (MeasureKind)bytes[at++]
                : throw new VortexFormatException($"A column's counts name a measure of no kind this library reads at {at}.");
        }

        int[] attached = [];
        if ((bytes[1] & DistinctFlag) != 0)
        {
            attached = new int[Count(bytes, ref at)];
            for (int a = 0; a < attached.Length; a++)
            {
                attached[a] = Count(bytes, ref at);
            }
        }

        int buckets = 0;
        int entries = -1;
        if ((bytes[1] & BucketsFlag) != 0)
        {
            buckets = Count(bytes, ref at);
            entries = Count(bytes, ref at);
        }

        bool sums = (bytes[1] & ChunkSumsFlag) != 0;
        int groups = Count(bytes, ref at);
        int count = Count(bytes, ref at);
        if (count > bytes.Length)
        {
            throw new VortexFormatException($"A column's counts claim {count} chunks in {bytes.Length} bytes.");
        }

        CountsChunk[] chunks = new CountsChunk[count];
        for (int chunk = 0; chunk < count; chunk++)
        {
            int offset = Count(bytes, ref at);
            int length = Count(bytes, ref at);
            int firstGroup = Count(bytes, ref at);
            int firstEntry = sums ? Count(bytes, ref at) : firstGroup;
            ulong? checksum = sums ? (ulong)Long(bytes, ref at) : null;
            (CountedValue kind, byte[]? value) = ReadKey(bytes, ref at);
            chunks[chunk] = new CountsChunk(offset, length, firstGroup, firstEntry, kind, value, checksum);
        }

        if (sums && (ulong)Long(bytes, ref at) != System.IO.Hashing.XxHash3.HashToUInt64(bytes[..(at - sizeof(long))]))
        {
            throw new VortexFormatException("A column's counts' header fails its checksum.");
        }

        // Without a first entry a chunk, the counts of buckets are read whole.
        if (buckets > 0 && !sums && count > 0)
        {
            chunks = [];
        }

        return new CountsDirectory(at, fields, kinds, (bytes[1] & SketchesFlag) != 0, attached, buckets, buckets == 0 ? groups : entries, groups, chunks);
    }

    /// <summary>The counts <see cref="ToBytes"/> wrote.</summary>
    /// <exception cref="VortexFormatException">The bytes are no counts this library reads.</exception>
    internal static ColumnCounts From(ReadOnlySpan<byte> bytes)
    {
        // The directory serves a reader of some values; a reader of them all reads the chunks in their order.
        int at = 0;
        CountsDirectory head = Head(bytes, ref at);
        int groups = head.Groups;
        int entries = head.Entries;
        if (entries < groups || entries > bytes.Length)
        {
            throw new VortexFormatException($"A column's counts claim {entries} entries for {groups} groups in {bytes.Length} bytes.");
        }

        ColumnCounts counts = new ColumnCounts(head.MeasureFields, head.Kinds, new CountGroup[groups], new long[entries * StateWords.Count(head.Kinds)])
        {
            Sketched = head.Sketched,
            AttachedFields = head.AttachedFields,
            Buckets = head.Buckets,
            EntryBuckets = head.Buckets == 0 ? null : new int[entries],
            ChunkSums = (bytes[1] & ChunkSumsFlag) != 0,
        };

        // The groups in their order, which a reader of a value searches: the nulls, the values left out, then the values by
        // their bytes, each once.
        int entry = 0;
        for (int group = 0; group < groups; group++)
        {
            CountGroup read = counts.ReadGroup(bytes, ref at, entry);
            if (group > 0 && !Precedes(counts.Groups[group - 1], read))
            {
                throw new VortexFormatException($"A column's counts hold their group {group} out of its order, at {at}.");
            }

            counts.Groups[group] = read;
            entry += read.Entries;
        }

        if (entry != entries)
        {
            throw new VortexFormatException($"A column's counts hold {entry} entries where their header says {entries}.");
        }

        if (at != bytes.Length)
        {
            throw new VortexFormatException($"A column's counts end at {at} where their header says {bytes.Length}.");
        }

        return counts;
    }

    /// <summary>Whether group <paramref name="before"/> comes before <paramref name="after"/>: the nulls, the values left out, then the values by their bytes.</summary>
    private static bool Precedes(CountGroup before, CountGroup after)
    {
        static int Rank(CountedValue kind) => kind switch { CountedValue.Null => 0, CountedValue.Others => 1, _ => 2 };
        int ranks = Rank(before.Kind).CompareTo(Rank(after.Kind));
        return ranks < 0 || (ranks == 0 && before.Kind == CountedValue.Value && before.Value.AsSpan().SequenceCompareTo(after.Value) < 0);
    }

    private void WriteGroup(ref Output into, CountGroup group)
    {
        Key(ref into, group);
        if (Buckets == 0)
        {
            WriteEntry(ref into, group.FirstEntry);
            return;
        }

        into.Varint((ulong)group.Entries);
        for (int entry = group.FirstEntry; entry < group.FirstEntry + group.Entries; entry++)
        {
            into.Varint((ulong)EntryBuckets![entry]);
            WriteEntry(ref into, entry);
        }
    }

    private void WriteEntry(ref Output into, int entry)
    {
        int words = WordsPerEntry;
        ReadOnlySpan<long> state = Words.AsSpan(entry * words, words);
        long rows = state[0];
        into.Varint((ulong)rows);
        int at = 1;
        foreach (MeasureKind kind in Kinds)
        {
            bool floats = kind == MeasureKind.Float;
            long values = state[at + MeasureLayout.Values];
            into.Varint((ulong)(rows - values));
            if (values > 0)
            {
                int sum = at + MeasureLayout.Sum;
                if (floats)
                {
                    // The three bins, the meta word as the values it skipped, its top and its infinities, then the high part.
                    for (int bin = 0; bin < 3; bin++)
                    {
                        into.Varint(Varint.ZigZagEncode64(state[sum + bin]));
                    }

                    (long counted, int top, int infinities) = IndexedSum.PartsOf(state[sum + 3]);
                    into.Varint((ulong)(values - counted));
                    into.Varint((ulong)(top | (infinities << 7)));
                    into.Varint(Varint.ZigZagEncode64(state[sum + 4]));
                    into.Long(state[at + MeasureLayout.FloatLeast]);
                    into.Long(state[at + MeasureLayout.FloatGreatest]);
                }
                else
                {
                    Int128 total = StateWords.IntegerOf(state, sum);
                    UInt128 zigzag = (UInt128)((total << 1) ^ (total >> 127));
                    into.Varint((ulong)zigzag);
                    into.Varint((ulong)(zigzag >> 64));
                    long least = state[at + MeasureLayout.IntegerLeast];
                    long greatest = state[at + MeasureLayout.IntegerGreatest];
                    into.Varint(kind == MeasureKind.Signed ? Varint.ZigZagEncode64(least) : (ulong)least);
                    into.Varint(kind == MeasureKind.Signed ? Varint.ZigZagEncode64(greatest) : (ulong)greatest);
                }
            }

            at += MeasureLayout.Words(floats);
        }
    }

    private CountGroup ReadGroup(ReadOnlySpan<byte> bytes, ref int at, int first)
    {
        (CountedValue counted, byte[]? value) = ReadKey(bytes, ref at);
        if (Buckets == 0)
        {
            ReadEntry(bytes, ref at, first);
            return new CountGroup(counted, value, first, 1);
        }

        // An entry a bucket the value has rows in, the buckets ascending.
        int entries = Count(bytes, ref at);
        if (entries == 0 || entries > Entries - first)
        {
            throw new VortexFormatException($"A column's counts give a group {entries} entries where {Entries - first} are left, at {at}.");
        }

        for (int entry = first; entry < first + entries; entry++)
        {
            int bucket = Count(bytes, ref at);
            if (bucket >= Buckets || (entry > first && bucket <= EntryBuckets![entry - 1]))
            {
                throw new VortexFormatException($"A column's counts hold an entry of bucket {bucket} out of its order, at {at}.");
            }

            EntryBuckets![entry] = bucket;
            ReadEntry(bytes, ref at, entry);
        }

        return new CountGroup(counted, value, first, entries);
    }

    private void ReadEntry(ReadOnlySpan<byte> bytes, ref int at, int entry)
    {
        int words = WordsPerEntry;
        Span<long> state = Words.AsSpan(entry * words, words);
        StateWords.Clear(state, Kinds);
        long rows = (long)Varint.Read64<Errors>(bytes, ref at);
        state[0] = rows;
        int m = 1;
        foreach (MeasureKind kind in Kinds)
        {
            bool floats = kind == MeasureKind.Float;
            long values = rows - (long)Varint.Read64<Errors>(bytes, ref at);
            state[m + MeasureLayout.Values] = values;
            if (values > 0)
            {
                int sum = m + MeasureLayout.Sum;
                if (floats)
                {
                    for (int bin = 0; bin < 3; bin++)
                    {
                        state[sum + bin] = Varint.ZigZagDecode64(Varint.Read64<Errors>(bytes, ref at));
                    }

                    ulong skipped = Varint.Read64<Errors>(bytes, ref at);
                    ulong top = Varint.Read64<Errors>(bytes, ref at);
                    if (skipped > (ulong)values || top >= 1 << 9)
                    {
                        throw new VortexFormatException($"A column's counts hold a float sum they cannot read at {at}.");
                    }

                    state[sum + 3] = IndexedSum.MetaOf(values - (long)skipped, (int)(top & 0x7F), (int)(top >> 7));
                    state[sum + 4] = Varint.ZigZagDecode64(Varint.Read64<Errors>(bytes, ref at));
                    state[m + MeasureLayout.FloatLeast] = Long(bytes, ref at);
                    state[m + MeasureLayout.FloatGreatest] = Long(bytes, ref at);
                }
                else
                {
                    ulong low = Varint.Read64<Errors>(bytes, ref at);
                    ulong high = Varint.Read64<Errors>(bytes, ref at);
                    UInt128 zigzag = new UInt128(high, low);
                    Int128 total = (Int128)(zigzag >> 1) ^ -(Int128)(zigzag & 1);
                    StateWords.Write(total, state, sum);
                    ulong least = Varint.Read64<Errors>(bytes, ref at);
                    ulong greatest = Varint.Read64<Errors>(bytes, ref at);
                    state[m + MeasureLayout.IntegerLeast] = kind == MeasureKind.Signed ? Varint.ZigZagDecode64(least) : (long)least;
                    state[m + MeasureLayout.IntegerGreatest] = kind == MeasureKind.Signed ? Varint.ZigZagDecode64(greatest) : (long)greatest;
                }
            }

            m += MeasureLayout.Words(floats);
        }
    }

    private static void Key(ref Output into, CountGroup group)
    {
        into.Byte((byte)group.Kind);
        if (group.Kind == CountedValue.Value)
        {
            byte[] value = group.Value!;
            into.Varint((ulong)value.Length);
            into.Bytes(value);
        }
    }

    private static (CountedValue Kind, byte[]? Value) ReadKey(ReadOnlySpan<byte> bytes, ref int at)
    {
        if (at >= bytes.Length || bytes[at] > (byte)CountedValue.Others)
        {
            throw new VortexFormatException($"A column's counts name a group of no kind this library reads at {at}.");
        }

        CountedValue kind = (CountedValue)bytes[at++];
        if (kind != CountedValue.Value)
        {
            return (kind, null);
        }

        int length = Count(bytes, ref at);
        if (length > bytes.Length - at)
        {
            throw new VortexFormatException($"A column's counts hold a value past their end at {at}.");
        }

        byte[] value = bytes.Slice(at, length).ToArray();
        at += length;
        return (kind, value);
    }

    private static int Count(ReadOnlySpan<byte> bytes, ref int at)
    {
        ulong count = Varint.Read64<Errors>(bytes, ref at);
        return count <= int.MaxValue ? (int)count : throw new VortexFormatException($"A column's counts hold a count too large at {at}.");
    }

    private static long Long(ReadOnlySpan<byte> bytes, ref int at)
    {
        if (bytes.Length - at < sizeof(long))
        {
            throw new VortexFormatException($"A column's counts end inside a float at {at}.");
        }

        long value = BinaryPrimitives.ReadInt64LittleEndian(bytes[at..]);
        at += sizeof(long);
        return value;
    }

    /// <summary>A growing buffer the counts and their sketches are written into.</summary>
    internal struct Output(int capacity)
    {
        private byte[] _bytes = new byte[capacity];

        internal int Length { get; private set; }

        internal readonly ReadOnlySpan<byte> Span => _bytes.AsSpan(0, Length);

        internal void Byte(byte value) => Room(1)[0] = value;

        internal void Bytes(ReadOnlySpan<byte> value) => value.CopyTo(Room(value.Length));

        internal void Varint(ulong value)
        {
            Span<byte> into = Room(Serialization.Varint.MaxLength64);
            Length -= Serialization.Varint.MaxLength64 - Serialization.Varint.Write(into, value);
        }

        internal void Long(long value) => BinaryPrimitives.WriteInt64LittleEndian(Room(sizeof(long)), value);

        private Span<byte> Room(int bytes)
        {
            if (Length + bytes > _bytes.Length)
            {
                Array.Resize(ref _bytes, Math.Max(_bytes.Length * 2, Length + bytes));
            }

            Span<byte> span = _bytes.AsSpan(Length, bytes);
            Length += bytes;
            return span;
        }
    }

    private readonly struct Errors : IVarintErrors
    {
        public static ulong Truncated(int position) => throw new VortexFormatException($"A column's counts end inside a varint at {position}.");

        public static ulong Malformed(int position, byte value) => throw new VortexFormatException($"A column's counts hold a varint too wide at {position}.");
    }
}
