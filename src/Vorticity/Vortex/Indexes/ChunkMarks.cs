using System;
using System.Collections.Generic;
using Vorticity.Serialization.Protobuf;
using Vorticity.Types;

namespace Vorticity.Indexes;

/// <summary>Where each granule of a chunk starts in one of its buffers of variable length, in bytes from the buffer's first.</summary>
/// <param name="Buffer">The buffer, by its place in the chunk's list of buffers.</param>
/// <param name="Starts">A start a granule, the chunk's first granule first.</param>
internal readonly record struct ChunkMark(int Buffer, long[] Starts);

/// <summary>
/// The geometry of one chunk: its encoding tree, the <c>Array</c> FlatBuffer its segment ends with, which says where each of
/// its buffers lies and what each node holds, and its marks.
/// </summary>
/// <param name="Tree">The tree's bytes.</param>
/// <param name="Marks">The marks of its buffers of variable length; empty for a chunk of none.</param>
internal readonly record struct ChunkGeometry(ReadOnlyMemory<byte> Tree, ChunkMark[] Marks);

/// <summary>
/// A column's object dictionary: its values, written once in a segment of their own, which the column's chunks hold the codes
/// of, each of <see cref="Codes"/>. Not a hint: a reader of the column's chunks without the footer gathers their codes
/// through it, so the table of contents names it, and the geometry's entry for a reader of the index directory.
/// </summary>
/// <param name="Offset">Where the values' segment starts in the file.</param>
/// <param name="Length">The segment's bytes.</param>
/// <param name="AlignmentExponent">The segment's alignment.</param>
/// <param name="Values">How many values the dictionary holds.</param>
/// <param name="Codes">The integer type of the codes the chunks hold.</param>
/// <param name="Ordered">Whether the values lie in the order of their type, so that a smaller code is a smaller value.</param>
internal readonly record struct ColumnDictionary(ulong Offset, uint Length, byte AlignmentExponent, long Values, PType Codes, bool Ordered)
{
    private const int TagField = 1;
    private const int TagOffset = 2;
    private const int TagLength = 3;
    private const int TagAlignment = 4;
    private const int TagValues = 5;
    private const int TagCodes = 6;
    private const int TagOrdered = 7;

    /// <summary>Column <paramref name="field"/>'s dictionary as a message's fields.</summary>
    internal void Write(ref ProtoWriter writer, int field)
    {
        writer.WriteUInt32Always(TagField, (uint)field);
        writer.WriteUInt64Always(TagOffset, Offset);
        writer.WriteUInt64Always(TagLength, Length);
        writer.WriteUInt64Always(TagAlignment, AlignmentExponent);
        writer.WriteUInt64Always(TagValues, (ulong)Values);
        writer.WriteUInt64Always(TagCodes, (ulong)Codes);
        writer.WriteUInt64Always(TagOrdered, Ordered ? 1UL : 0UL);
    }

    /// <summary>The dictionary a message names and its column; null when a field is missing or its codes are no integers.</summary>
    internal static (int Field, ColumnDictionary Dictionary)? Read(ProtoReader reader)
    {
        int field = -1;
        ulong offset = 0;
        uint length = 0;
        byte alignment = 0;
        long values = -1;
        PType codes = PType.F64;
        bool ordered = false;
        while (reader.TryReadTag(out int tag, out ProtoWireType wire))
        {
            switch (tag, wire)
            {
                case (TagField, ProtoWireType.Varint):
                    field = checked((int)reader.ReadVarint32());
                    break;
                case (TagOffset, ProtoWireType.Varint):
                    offset = reader.ReadVarint();
                    break;
                case (TagLength, ProtoWireType.Varint):
                    length = checked((uint)reader.ReadVarint());
                    break;
                case (TagAlignment, ProtoWireType.Varint):
                    alignment = checked((byte)reader.ReadVarint());
                    break;
                case (TagValues, ProtoWireType.Varint):
                    values = checked((long)reader.ReadVarint());
                    break;
                case (TagCodes, ProtoWireType.Varint):
                    ulong ptype = reader.ReadVarint();
                    codes = ptype <= (ulong)PType.F64 ? (PType)ptype : PType.F64;
                    break;
                case (TagOrdered, ProtoWireType.Varint):
                    ordered = reader.ReadVarint() != 0;
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return field >= 0 && values >= 0 && length > 0 && codes.IsInteger()
            ? (field, new ColumnDictionary(offset, length, alignment, values, codes, ordered))
            : null;
    }

    /// <summary>The columns' dictionaries, those of the columns that have one, as messages end to end, each length-delimited.</summary>
    internal static byte[] ToBytes(IReadOnlyList<ColumnDictionary?> dictionaries)
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            for (int field = 0; field < dictionaries.Count; field++)
            {
                if (dictionaries[field] is { } dictionary)
                {
                    using ProtoWriter.MessageScope message = writer.BeginMessage(1);
                    dictionary.Write(ref writer, field);
                }
            }

            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>
    /// The dictionaries of <paramref name="columns"/> columns <paramref name="bytes"/> hold, by the column's place; null when they
    /// do not decode, or name a column past the last.
    /// </summary>
    internal static ColumnDictionary?[]? From(ReadOnlySpan<byte> bytes, int columns)
    {
        try
        {
            ColumnDictionary?[] dictionaries = new ColumnDictionary?[columns];
            ProtoReader reader = new ProtoReader(bytes);
            while (reader.TryReadTag(out int tag, out ProtoWireType wire))
            {
                if (tag != 1 || wire != ProtoWireType.LengthDelimited)
                {
                    reader.SkipField(wire);
                    continue;
                }

                if (Read(reader.ReadMessage()) is not { } named || named.Field >= columns)
                {
                    return null;
                }

                dictionaries[named.Field] = named.Dictionary;
            }

            return dictionaries;
        }
        catch (Exception malformed) when (malformed is VortexFormatException or OverflowException)
        {
            return null;
        }
    }
}

/// <summary>
/// The geometry of a column's chunks, beside where they lie (<see cref="SliceChunks"/>): an entry of the index directory,
/// <see cref="IndexKinds.Marks"/>, on the root, a payload region a column. What a reader needs to read the bytes of the
/// granules it keeps alone, before it reads any of them: each chunk's tree, and for each buffer whose rows take bytes of
/// their own length, the bytes FSST compressed or a binary column holds, where each granule starts in it.
/// </summary>
/// <remarks>
/// <para>
/// A region holds, behind a version byte, the rows of a granule, then each chunk's
/// tree as its length and its bytes and its marks: their count, and for each the buffer and its starts, the first absolute
/// and each later one the gap from the one before, varints all. A tree takes 100 to 300 bytes and a mark about two bytes a
/// granule.
/// </para>
/// <para>
/// A buffer of fixed width a row needs no mark: a granule's bytes are its rows' times the width, or the blocks of 1 024 rows
/// that hold them for a packing. Like every index it is a hint: a region that does not decode leaves the reader to read whole
/// chunks.
/// </para>
/// </remarks>
internal sealed class ColumnMarks
{
    private const byte Version = 1;

    internal ColumnMarks(int granuleRows, ChunkGeometry[] chunks)
    {
        GranuleRows = granuleRows;
        Chunks = chunks;
    }

    /// <summary>The rows of a granule: the writer's block.</summary>
    internal int GranuleRows { get; }

    /// <summary>Each chunk's geometry, in the order of the column's chunks.</summary>
    internal ChunkGeometry[] Chunks { get; }

    /// <summary>The region's bytes.</summary>
    internal byte[] ToBytes()
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            writer.WriteVarint(Version);
            writer.WriteVarint((ulong)GranuleRows);

            writer.WriteVarint((ulong)Chunks.Length);
            foreach (ChunkGeometry chunk in Chunks)
            {
                writer.WriteVarint((ulong)chunk.Tree.Length);
                writer.WriteRawBytes(chunk.Tree.Span);
                writer.WriteVarint((ulong)chunk.Marks.Length);
                foreach (ChunkMark mark in chunk.Marks)
                {
                    writer.WriteVarint((ulong)mark.Buffer);
                    writer.WriteVarint((ulong)mark.Starts.Length);
                    long before = 0;
                    foreach (long start in mark.Starts)
                    {
                        writer.WriteVarint((ulong)(start - before));
                        before = start;
                    }
                }
            }

            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>
    /// The geometry a region's <paramref name="bytes"/> hold, its trees slices of <paramref name="bytes"/> itself, which the
    /// caller keeps; null when they do not decode, or hold a start that goes back.
    /// </summary>
    internal static ColumnMarks? From(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            ProtoReader reader = new ProtoReader(bytes.Span);
            if (reader.ReadVarint() != Version)
            {
                return null;
            }

            ulong granuleRows = reader.ReadVarint();
            if (granuleRows == 0 || granuleRows > int.MaxValue)
            {
                return null;
            }

            ulong count = reader.ReadVarint();
            if (count > (ulong)bytes.Length)
            {
                return null;
            }

            ChunkGeometry[] chunks = new ChunkGeometry[(int)count];
            List<ChunkMark> marks = [];
            for (int i = 0; i < chunks.Length; i++)
            {
                ReadOnlyMemory<byte> tree = Slice(bytes, ref reader);
                ulong markCount = reader.ReadVarint();
                if (markCount > (ulong)bytes.Length)
                {
                    return null;
                }

                marks.Clear();
                for (ulong m = 0; m < markCount; m++)
                {
                    int buffer = checked((int)reader.ReadVarint());
                    ulong startCount = reader.ReadVarint();
                    if (startCount > (ulong)bytes.Length)
                    {
                        return null;
                    }

                    long[] starts = new long[(int)startCount];
                    long start = 0;
                    for (int s = 0; s < starts.Length; s++)
                    {
                        start = checked(start + (long)reader.ReadVarint());
                        starts[s] = start;
                    }

                    marks.Add(new ChunkMark(buffer, starts));
                }

                chunks[i] = new ChunkGeometry(tree, markCount == 0 ? [] : [.. marks]);
            }

            return reader.End ? new ColumnMarks((int)granuleRows, chunks) : null;
        }
        catch (Exception malformed) when (malformed is VortexFormatException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>A length-delimited run of the region as a slice of it, the reader moved past it.</summary>
    private static ReadOnlyMemory<byte> Slice(ReadOnlyMemory<byte> bytes, ref ProtoReader reader)
    {
        int length = reader.ReadLengthDelimited().Length;
        return bytes.Slice(reader.Position - length, length);
    }
}
