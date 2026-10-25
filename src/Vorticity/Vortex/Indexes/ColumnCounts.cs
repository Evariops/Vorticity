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
        List<(int Offset, int Length, int FirstGroup)> directory = [];
        int chunkStart = 0;
        int chunkGroup = 0;
        for (int group = 0; group < Groups.Length; group++)
        {
            WriteGroup(ref chunks, Groups[group]);
            if (chunks.Length - chunkStart >= ChunkBytes || group == Groups.Length - 1)
            {
                directory.Add((chunkStart, chunks.Length - chunkStart, chunkGroup));
                chunkStart = chunks.Length;
                chunkGroup = group + 1;
            }
        }

        Output head = new Output(256);
        head.Byte(Version);
        head.Byte((byte)((Sketched ? SketchesFlag : 0) | (AttachedFields.Length == 0 ? 0 : DistinctFlag) | (Buckets == 0 ? 0 : BucketsFlag)));
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
        foreach ((int offset, int length, int firstGroup) in directory)
        {
            head.Varint((ulong)offset);
            head.Varint((ulong)length);
            head.Varint((ulong)firstGroup);
            Key(ref head, Groups[firstGroup]);
        }

        byte[] bytes = new byte[head.Length + chunks.Length];
        head.Span.CopyTo(bytes);
        chunks.Span.CopyTo(bytes.AsSpan(head.Length));
        return bytes;
    }

    /// <summary>The counts <see cref="ToBytes"/> wrote.</summary>
    /// <exception cref="VortexFormatException">The bytes are no counts this library reads.</exception>
    internal static ColumnCounts From(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 2 || bytes[0] != Version)
        {
            throw new VortexFormatException(bytes.Length < 2 ? "A column's counts end before their header." : $"A column's counts are of version {bytes[0]}, which this library does not read.");
        }

        int at = 2;
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

        int groups = Count(bytes, ref at);
        int chunks = Count(bytes, ref at);
        for (int chunk = 0; chunk < chunks; chunk++)
        {
            // The directory serves a reader of one value; a reader of them all reads the chunks in their order.
            Count(bytes, ref at);
            Count(bytes, ref at);
            Count(bytes, ref at);
            SkipKey(bytes, ref at);
        }

        int words = StateWords.Count(kinds);
        entries = buckets == 0 ? groups : entries;
        if (entries < groups || entries > bytes.Length)
        {
            throw new VortexFormatException($"A column's counts claim {entries} entries for {groups} groups in {bytes.Length} bytes.");
        }

        ColumnCounts counts = new ColumnCounts(fields, kinds, new CountGroup[groups], new long[entries * words])
        {
            Sketched = (bytes[1] & SketchesFlag) != 0,
            AttachedFields = attached,
            Buckets = buckets,
            EntryBuckets = buckets == 0 ? null : new int[entries],
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

    private static void SkipKey(ReadOnlySpan<byte> bytes, ref int at) => ReadKey(bytes, ref at);

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
