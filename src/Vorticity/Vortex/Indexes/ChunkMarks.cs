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
/// of, each of <see cref="Codes"/>.
/// </summary>
/// <param name="Offset">Where the values' segment starts in the file.</param>
/// <param name="Length">The segment's bytes.</param>
/// <param name="AlignmentExponent">The segment's alignment.</param>
/// <param name="Values">How many values the dictionary holds.</param>
/// <param name="Codes">The integer type of the codes the chunks hold.</param>
/// <param name="Ordered">Whether the values lie in the order of their type, so that a smaller code is a smaller value.</param>
internal readonly record struct ColumnDictionary(ulong Offset, uint Length, byte AlignmentExponent, long Values, PType Codes, bool Ordered);

/// <summary>
/// The geometry of a column's chunks, beside where they lie (<see cref="SliceChunks"/>): an entry of the index directory,
/// <see cref="IndexKinds.Marks"/>, on the root, a payload region a column. What a reader needs to read the bytes of the
/// granules it keeps alone, before it reads any of them: each chunk's tree, and for each buffer whose rows take bytes of
/// their own length, the bytes FSST compressed or a binary column holds, where each granule starts in it.
/// </summary>
/// <remarks>
/// <para>
/// A region holds, behind a version byte, the rows of a granule, the column's dictionary when it has one, then each chunk's
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

    internal ColumnMarks(int granuleRows, ColumnDictionary? dictionary, ReadOnlyMemory<byte> dictionaryTree, ChunkGeometry[] chunks)
    {
        GranuleRows = granuleRows;
        Dictionary = dictionary;
        DictionaryTree = dictionaryTree;
        Chunks = chunks;
    }

    /// <summary>The rows of a granule: the writer's block.</summary>
    internal int GranuleRows { get; }

    /// <summary>The column's object dictionary, or null when its chunks hold its values.</summary>
    internal ColumnDictionary? Dictionary { get; }

    /// <summary>The tree of the dictionary's values segment; empty without a dictionary.</summary>
    internal ReadOnlyMemory<byte> DictionaryTree { get; }

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
            if (Dictionary is { } dictionary)
            {
                writer.WriteVarint(1);
                writer.WriteVarint(dictionary.Offset);
                writer.WriteVarint(dictionary.Length);
                writer.WriteVarint(dictionary.AlignmentExponent);
                writer.WriteVarint((ulong)dictionary.Values);
                writer.WriteVarint((ulong)dictionary.Codes);
                writer.WriteVarint(dictionary.Ordered ? 1UL : 0UL);
                writer.WriteVarint((ulong)DictionaryTree.Length);
                writer.WriteRawBytes(DictionaryTree.Span);
            }
            else
            {
                writer.WriteVarint(0);
            }

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

            ColumnDictionary? dictionary = null;
            ReadOnlyMemory<byte> dictionaryTree = default;
            if (reader.ReadVarint() == 1)
            {
                ulong offset = reader.ReadVarint();
                uint length = checked((uint)reader.ReadVarint());
                byte alignment = checked((byte)reader.ReadVarint());
                long values = checked((long)reader.ReadVarint());
                ulong codes = reader.ReadVarint();
                bool ordered = reader.ReadVarint() != 0;
                if (codes > (ulong)PType.F64 || !((PType)codes).IsInteger())
                {
                    return null;
                }

                dictionary = new ColumnDictionary(offset, length, alignment, values, (PType)codes, ordered);
                dictionaryTree = Slice(bytes, ref reader);
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

            return reader.End ? new ColumnMarks((int)granuleRows, dictionary, dictionaryTree, chunks) : null;
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
