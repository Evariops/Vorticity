using System;
using System.Collections.Generic;
using Vorticity.Aggregating;
using Vorticity.Serialization;

namespace Vorticity.Indexes;

/// <summary>
/// What a reader of some entries' sketches reads first (<see cref="ColumnSketches.ReadDirectory"/>): the sketches' header
/// and the directory of their chunks, the chunks starting at <paramref name="HeaderBytes"/>.
/// </summary>
/// <param name="HeaderBytes">The bytes before the first chunk.</param>
/// <param name="Entries">The entries of the counts.</param>
/// <param name="Measures">The sketches of an entry.</param>
/// <param name="Attached">The registers of an entry.</param>
/// <param name="Chunks">Each chunk: where it starts past the header, its bytes, its first entry, and its checksum when the sketches keep one.</param>
internal sealed record SketchesDirectory(
    int HeaderBytes, int Entries, int Measures, int Attached, IReadOnlyList<(int Offset, int Length, int FirstEntry, ulong? Checksum)> Chunks);

/// <summary>
/// The sketches of a column's counts (<see cref="IndexKinds.ColumnSketches"/>): for each entry of the counts
/// (<see cref="ColumnCounts"/>), in their order, a quantile sketch of each measure they carry, and the registers of the
/// distinct values of each wide column they attach. A region a column, right after its counts, which the object's table
/// of contents names: a question of counts, sums and extremes reads the counts alone, and one of quantiles reads both,
/// side by side, in one request.
/// </summary>
/// <remarks>
/// The region is a header, the entries and what each keeps, a directory of chunks of about
/// <see cref="ColumnCounts.ChunkBytes"/>, each naming its first entry, then the chunks: each entry's sketches, then its
/// registers, each its length first, 0 for none. A reader of one value's entries, which the counts' groups number, can
/// read the header and the chunks that hold them alone. A rollup's sketches, version 2, give each chunk the XXH3-64 of its
/// bytes and end their header with its own, so that a reader of the header, then of some chunks, checks each
/// (<see cref="FromEntries"/>).
/// </remarks>
internal sealed class ColumnSketches
{
    /// <summary>The version of the sketches an object keeps, with no checksum a chunk, which this library writes and reads.</summary>
    internal const byte Version = 1;

    /// <summary>The version of sketches whose directory gives each chunk's checksum (<see cref="ChunkSums"/>).</summary>
    internal const byte SummedVersion = 2;

    /// <param name="entries">The entries of the counts.</param>
    /// <param name="measures">The sketches of an entry: the measures of the counts, or 0 when they keep none.</param>
    /// <param name="sketches">
    /// Each entry's quantile sketch of each measure, as <see cref="QuantileSketch.Encode"/> writes it, entry by entry, null
    /// for a measure that met no value; null when the counts keep none.
    /// </param>
    /// <param name="attached">The registers of an entry: the wide columns the counts attach.</param>
    /// <param name="distinct">
    /// Each entry's registers of each wide column, a HyperLogLog of 2^11 registers as <see cref="HyperLogLog.Encode"/>
    /// writes them, entry by entry, null for none; null when no column is attached.
    /// </param>
    internal ColumnSketches(int entries, int measures, byte[]?[]? sketches, int attached, byte[]?[]? distinct)
    {
        Entries = entries;
        Measures = sketches is null ? 0 : measures;
        Sketches = sketches;
        Attached = distinct is null ? 0 : attached;
        Distinct = distinct;
    }

    /// <summary>The entries of the counts, in their order.</summary>
    internal int Entries { get; }

    /// <summary>The quantile sketches of an entry, one a measure of the counts; 0 when they keep none.</summary>
    internal int Measures { get; }

    /// <summary>The registers of an entry, one a wide column the counts attach; 0 when they attach none.</summary>
    internal int Attached { get; }

    /// <summary>
    /// Each entry's quantile sketch of each measure, encoded, <see cref="Measures"/> of them an entry, null for one that
    /// met no value; null when the counts keep none.
    /// </summary>
    internal byte[]?[]? Sketches { get; }

    /// <summary>
    /// Each entry's registers of each wide column, encoded, <see cref="Attached"/> of them an entry, null for none; null
    /// when the counts attach no column.
    /// </summary>
    internal byte[]?[]? Distinct { get; }

    /// <summary>The bytes the sketches hold in memory, about: their own, and a reference and an array's header each.</summary>
    internal long Bytes { get; private init; }

    /// <summary>
    /// Whether the directory gives each chunk's checksum, and the header its own, so that a reader of some entries reads
    /// the header, then their chunks alone, and checks each (<see cref="FromEntries"/>): a rollup's, written in version 2.
    /// Set before <see cref="ToBytes"/>.
    /// </summary>
    internal bool ChunkSums { get; set; }

    /// <summary>The quantile sketch of measure <paramref name="measure"/> over entry <paramref name="entry"/>'s rows, encoded; null for none.</summary>
    internal byte[]? SketchOf(int entry, int measure) => Sketches?[(entry * Measures) + measure];

    /// <summary>The registers of the wide column <paramref name="attached"/> over entry <paramref name="entry"/>'s rows, encoded; null for none.</summary>
    internal byte[]? DistinctOf(int entry, int attached) => Distinct?[(entry * Attached) + attached];

    /// <summary>The sketches' bytes, which <see cref="From"/> reads.</summary>
    internal byte[] ToBytes()
    {
        ColumnCounts.Output chunks = new ColumnCounts.Output(ColumnCounts.ChunkBytes);
        List<(int Offset, int Length, int FirstEntry, ulong Checksum)> directory = [];
        int chunkStart = 0;
        int chunkEntry = 0;
        for (int entry = 0; entry < Entries; entry++)
        {
            for (int m = 0; m < Measures; m++)
            {
                byte[]? sketch = Sketches![(entry * Measures) + m];
                chunks.Varint((ulong)(sketch?.Length ?? 0));
                chunks.Bytes(sketch);
            }

            for (int a = 0; a < Attached; a++)
            {
                byte[]? registers = Distinct![(entry * Attached) + a];
                chunks.Varint((ulong)(registers?.Length ?? 0));
                chunks.Bytes(registers);
            }

            if (chunks.Length - chunkStart >= ColumnCounts.ChunkBytes || entry == Entries - 1)
            {
                int length = chunks.Length - chunkStart;
                directory.Add((chunkStart, length, chunkEntry, ChunkSums ? System.IO.Hashing.XxHash3.HashToUInt64(chunks.Span.Slice(chunkStart, length)) : 0));
                chunkStart = chunks.Length;
                chunkEntry = entry + 1;
            }
        }

        ColumnCounts.Output head = new ColumnCounts.Output(64);
        head.Byte(ChunkSums ? SummedVersion : Version);
        head.Varint((ulong)Entries);
        head.Varint((ulong)Measures);
        head.Varint((ulong)Attached);
        head.Varint((ulong)directory.Count);
        foreach ((int offset, int length, int firstEntry, ulong checksum) in directory)
        {
            head.Varint((ulong)offset);
            head.Varint((ulong)length);
            head.Varint((ulong)firstEntry);
            if (ChunkSums)
            {
                head.Long((long)checksum);
            }
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

    /// <summary>The sketches <see cref="ToBytes"/> wrote.</summary>
    /// <exception cref="VortexFormatException">The bytes are no sketches this library reads.</exception>
    internal static ColumnSketches From(ReadOnlySpan<byte> bytes)
    {
        // The directory serves a reader of some entries; a reader of them all reads the chunks in their order.
        int at = 0;
        SketchesDirectory head = Head(bytes, ref at);
        int entries = head.Entries;
        int measures = head.Measures;
        int attached = head.Attached;

        // Each sketch and each register takes a byte at least, its length.
        long held = (long)entries * (measures + (long)attached);
        if (held > bytes.Length - at)
        {
            throw new VortexFormatException($"A column's sketches say they hold {held} sketches and registers in {bytes.Length - at} bytes.");
        }

        byte[]?[]? sketches = measures > 0 ? new byte[]?[entries * measures] : null;
        byte[]?[]? distinct = attached > 0 ? new byte[]?[entries * attached] : null;
        for (int entry = 0; entry < entries; entry++)
        {
            for (int m = 0; m < measures; m++)
            {
                sketches![(entry * measures) + m] = Held(bytes, ref at);
            }

            for (int a = 0; a < attached; a++)
            {
                distinct![(entry * attached) + a] = Held(bytes, ref at);
            }
        }

        if (at != bytes.Length)
        {
            throw new VortexFormatException($"A column's sketches end at {at} where their region ends at {bytes.Length}.");
        }

        return new ColumnSketches(entries, measures, sketches, attached, distinct)
        {
            Bytes = bytes.Length + (held * (IntPtr.Size + 24)),
            ChunkSums = bytes[0] == SummedVersion,
        };
    }

    /// <summary>
    /// The header and the directory of the sketches whose first bytes are <paramref name="head"/>, which a reader of some
    /// entries reads before the chunks that hold them: the sketches' header whole, or more of their bytes.
    /// </summary>
    /// <exception cref="VortexFormatException">The bytes are no sketches' header this library reads, or fail its checksum.</exception>
    internal static SketchesDirectory ReadDirectory(ReadOnlySpan<byte> head)
    {
        int at = 0;
        return Head(head, ref at);
    }

    /// <summary>The chunks of <paramref name="directory"/> that hold <paramref name="entries"/>, ascending.</summary>
    internal static List<int> ChunksOf(SketchesDirectory directory, IReadOnlyList<int> entries)
    {
        SortedSet<int> chunks = [];
        foreach (int entry in entries)
        {
            int low = 0;
            int high = directory.Chunks.Count - 1;
            int found = -1;
            while (low <= high)
            {
                int middle = low + ((high - low) >> 1);
                if (directory.Chunks[middle].FirstEntry <= entry)
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
    /// The sketches of entries <paramref name="entries"/>, in their order, from chunks <paramref name="chunks"/> of
    /// <paramref name="directory"/>, ascending, whose bytes are <paramref name="bytes"/>, each checked against its checksum:
    /// the entries' sketches as those of entries 0 on, which the counts read with them number.
    /// </summary>
    /// <exception cref="VortexFormatException">A chunk is not the length or the checksum its directory says, or lacks an entry asked.</exception>
    internal static ColumnSketches FromEntries(
        SketchesDirectory directory, IReadOnlyList<int> chunks, IReadOnlyList<ReadOnlyMemory<byte>> bytes, IReadOnlyList<int> entries)
    {
        int measures = directory.Measures;
        int attached = directory.Attached;
        Dictionary<int, (int Chunk, int At)> places = [];
        for (int i = 0; i < chunks.Count; i++)
        {
            (_, int length, int first, ulong? checksum) = directory.Chunks[chunks[i]];
            ReadOnlySpan<byte> span = bytes[i].Span;
            if (span.Length != length || (checksum is { } sum && System.IO.Hashing.XxHash3.HashToUInt64(span) != sum))
            {
                throw new VortexFormatException($"A chunk of a column's sketches is not the {length} bytes its directory checks.");
            }

            int last = chunks[i] + 1 < directory.Chunks.Count ? directory.Chunks[chunks[i] + 1].FirstEntry : directory.Entries;
            int at = 0;
            for (int entry = first; entry < last; entry++)
            {
                places[entry] = (i, at);
                for (int held = 0; held < measures + attached; held++)
                {
                    int skipped = Count(span, ref at);
                    if (skipped > span.Length - at)
                    {
                        throw new VortexFormatException($"A chunk of a column's sketches holds one past its end at {at}.");
                    }

                    at += skipped;
                }
            }
        }

        byte[]?[]? sketches = measures > 0 ? new byte[]?[entries.Count * measures] : null;
        byte[]?[]? distinct = attached > 0 ? new byte[]?[entries.Count * attached] : null;
        for (int e = 0; e < entries.Count; e++)
        {
            if (!places.TryGetValue(entries[e], out (int Chunk, int At) place))
            {
                throw new VortexFormatException($"The chunks of a column's sketches read do not hold entry {entries[e]}.");
            }

            ReadOnlySpan<byte> span = bytes[place.Chunk].Span;
            int at = place.At;
            for (int m = 0; m < measures; m++)
            {
                sketches![(e * measures) + m] = Held(span, ref at);
            }

            for (int a = 0; a < attached; a++)
            {
                distinct![(e * attached) + a] = Held(span, ref at);
            }
        }

        long read = 0;
        foreach (ReadOnlyMemory<byte> chunk in bytes)
        {
            read += chunk.Length;
        }

        return new ColumnSketches(entries.Count, measures, sketches, attached, distinct)
        {
            Bytes = read + ((long)entries.Count * (measures + attached) * (IntPtr.Size + 24)),
            ChunkSums = directory.Chunks.Count > 0 && directory.Chunks[0].Checksum is not null,
        };
    }

    /// <summary>The sketches' header and directory, from <paramref name="at"/>, past which the chunks start.</summary>
    private static SketchesDirectory Head(ReadOnlySpan<byte> bytes, ref int at)
    {
        if (bytes.Length < 1 || bytes[0] is not (Version or SummedVersion))
        {
            throw new VortexFormatException(bytes.Length < 1 ? "A column's sketches end before their header." : $"A column's sketches are of version {bytes[0]}, which this library does not read.");
        }

        bool sums = bytes[0] == SummedVersion;
        at = 1;
        int entries = Count(bytes, ref at);
        int measures = Count(bytes, ref at);
        int attached = Count(bytes, ref at);
        int count = Count(bytes, ref at);
        if (count > bytes.Length)
        {
            throw new VortexFormatException($"A column's sketches claim {count} chunks in {bytes.Length} bytes.");
        }

        (int Offset, int Length, int FirstEntry, ulong? Checksum)[] chunks = new (int, int, int, ulong?)[count];
        for (int chunk = 0; chunk < count; chunk++)
        {
            int offset = Count(bytes, ref at);
            int length = Count(bytes, ref at);
            int first = Count(bytes, ref at);
            ulong? checksum = null;
            if (sums)
            {
                if (bytes.Length - at < sizeof(long))
                {
                    throw new VortexFormatException($"A column's sketches end inside a checksum at {at}.");
                }

                checksum = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes[at..]);
                at += sizeof(long);
            }

            chunks[chunk] = (offset, length, first, checksum);
        }

        if (sums)
        {
            if (bytes.Length - at < sizeof(long)
                || System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes[at..]) != System.IO.Hashing.XxHash3.HashToUInt64(bytes[..at]))
            {
                throw new VortexFormatException("A column's sketches' header fails its checksum.");
            }

            at += sizeof(long);
        }

        return new SketchesDirectory(at, entries, measures, attached, chunks);
    }

    /// <summary>A sketch or registers, their length first; null for none.</summary>
    private static byte[]? Held(ReadOnlySpan<byte> bytes, ref int at)
    {
        int length = Count(bytes, ref at);
        if (length > bytes.Length - at)
        {
            throw new VortexFormatException($"A column's sketches hold one past their end at {at}.");
        }

        byte[]? held = length == 0 ? null : bytes.Slice(at, length).ToArray();
        at += length;
        return held;
    }

    private static int Count(ReadOnlySpan<byte> bytes, ref int at)
    {
        ulong count = Varint.Read64<Errors>(bytes, ref at);
        return count <= int.MaxValue ? (int)count : throw new VortexFormatException($"A column's sketches hold a count too large at {at}.");
    }

    private readonly struct Errors : IVarintErrors
    {
        public static ulong Truncated(int position) => throw new VortexFormatException($"A column's sketches end inside a varint at {position}.");

        public static ulong Malformed(int position, byte value) => throw new VortexFormatException($"A column's sketches hold a varint too wide at {position}.");
    }
}
