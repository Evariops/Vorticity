using System;
using System.Collections.Generic;
using Vorticity.Aggregating;
using Vorticity.Serialization;

namespace Vorticity.Indexes;

/// <summary>
/// The sketches of a column's counts by hour (<see cref="IndexKinds.ColumnSketches"/>): for each entry of the counts
/// (<see cref="ColumnCounts"/>), in their order, a quantile sketch of each measure they carry, and the registers of the
/// distinct values of each wide column they attach. A region a column, right after its counts, which the object's table
/// of contents names: a question of counts, sums and extremes reads the counts alone, and one of quantiles reads both,
/// side by side, in one request.
/// </summary>
/// <remarks>
/// The region is a header, the entries and what each keeps, a directory of chunks of about
/// <see cref="ColumnCounts.ChunkBytes"/>, each naming its first entry, then the chunks: each entry's sketches, then its
/// registers, each its length first, 0 for none. A reader of one value's entries, which the counts' groups number, can
/// read the header and the chunks that hold them alone.
/// </remarks>
internal sealed class ColumnSketches
{
    /// <summary>The version this library writes and reads.</summary>
    internal const byte Version = 1;

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

    /// <summary>The quantile sketch of measure <paramref name="measure"/> over entry <paramref name="entry"/>'s rows, encoded; null for none.</summary>
    internal byte[]? SketchOf(int entry, int measure) => Sketches?[(entry * Measures) + measure];

    /// <summary>The registers of the wide column <paramref name="attached"/> over entry <paramref name="entry"/>'s rows, encoded; null for none.</summary>
    internal byte[]? DistinctOf(int entry, int attached) => Distinct?[(entry * Attached) + attached];

    /// <summary>The sketches' bytes, which <see cref="From"/> reads.</summary>
    internal byte[] ToBytes()
    {
        ColumnCounts.Output chunks = new ColumnCounts.Output(ColumnCounts.ChunkBytes);
        List<(int Offset, int Length, int FirstEntry)> directory = [];
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
                directory.Add((chunkStart, chunks.Length - chunkStart, chunkEntry));
                chunkStart = chunks.Length;
                chunkEntry = entry + 1;
            }
        }

        ColumnCounts.Output head = new ColumnCounts.Output(64);
        head.Byte(Version);
        head.Varint((ulong)Entries);
        head.Varint((ulong)Measures);
        head.Varint((ulong)Attached);
        head.Varint((ulong)directory.Count);
        foreach ((int offset, int length, int firstEntry) in directory)
        {
            head.Varint((ulong)offset);
            head.Varint((ulong)length);
            head.Varint((ulong)firstEntry);
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
        if (bytes.Length < 1 || bytes[0] != Version)
        {
            throw new VortexFormatException(bytes.Length < 1 ? "A column's sketches end before their header." : $"A column's sketches are of version {bytes[0]}, which this library does not read.");
        }

        int at = 1;
        int entries = Count(bytes, ref at);
        int measures = Count(bytes, ref at);
        int attached = Count(bytes, ref at);
        int chunks = Count(bytes, ref at);
        for (int chunk = 0; chunk < chunks; chunk++)
        {
            // The directory serves a reader of some entries; a reader of them all reads the chunks in their order.
            Count(bytes, ref at);
            Count(bytes, ref at);
            Count(bytes, ref at);
        }

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

        return new ColumnSketches(entries, measures, sketches, attached, distinct) { Bytes = bytes.Length + (held * (IntPtr.Size + 24)) };
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
