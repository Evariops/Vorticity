using System;
using System.Collections.Generic;
using System.Text;
using Vorticity.Serialization.Protobuf;

namespace Vorticity.Indexes;

/// <summary>One chunk of a column: where its segment lies, its alignment, and the rows it holds.</summary>
internal readonly record struct ChunkSpan(ulong Offset, uint Length, byte AlignmentExponent, long FirstRow, long Rows)
{
    internal long EndRow => FirstRow + Rows;
}

/// <summary>
/// Where the chunks of every top-level column lie, and the array encodings their trees name: what a
/// reader that opens no footer needs to read the rows of a slice. An entry of the index directory,
/// <see cref="IndexKinds.SliceChunks"/>, on the root, whose one payload region holds it.
/// </summary>
/// <remarks>
/// <para>
/// A chunk's segment ends with its encoding tree, whose encodings are indices into the footer's table
/// of array encodings: the directory carries that table, so a chunk decodes from its own bytes, the
/// table and its column's dtype. A slice's rows are a range of rows, and the chunks of a column that
/// overlap it are found by halves on their ends.
/// </para>
/// <para>
/// Encoded behind a version byte: the encodings, each its length and its bytes, then per column its
/// chunk count, and per chunk its offset, length, alignment exponent and rows, each a varint. That is
/// about ten bytes a chunk, so a day of 10⁹ rows in 20 columns cut in chunks of 40 960 rows takes about
/// 5 MB. It is a region of its own rather than options of the directory, which every open reads.
/// </para>
/// </remarks>
internal sealed class SliceChunks
{
    private const byte Version = 1;

    internal SliceChunks(IReadOnlyList<string> encodings, IReadOnlyList<ChunkSpan[]> columns)
    {
        Encodings = encodings;
        Columns = columns;
    }

    /// <summary>The array encodings of the file, in the order their trees name them.</summary>
    internal IReadOnlyList<string> Encodings { get; }

    /// <summary>Per top-level column, its chunks in row order.</summary>
    internal IReadOnlyList<ChunkSpan[]> Columns { get; }

    /// <summary>The chunks of column <paramref name="column"/> that hold a row of <c>[first, first + rows)</c>.</summary>
    internal ReadOnlySpan<ChunkSpan> Overlapping(int column, long first, long rows)
    {
        ChunkSpan[] chunks = Columns[column];
        long end = first + rows;
        int low = 0;
        int high = chunks.Length;
        while (low < high)
        {
            int middle = (int)((uint)(low + high) >> 1);
            if (chunks[middle].EndRow <= first)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        int stop = low;
        while (stop < chunks.Length && chunks[stop].FirstRow < end)
        {
            stop++;
        }

        return chunks.AsSpan(low, stop - low);
    }

    internal byte[] ToBytes()
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            writer.WriteVarint(Version);
            writer.WriteVarint((ulong)Encodings.Count);
            foreach (string encoding in Encodings)
            {
                byte[] utf8 = Encoding.UTF8.GetBytes(encoding);
                writer.WriteVarint((ulong)utf8.Length);
                writer.WriteRawBytes(utf8);
            }

            writer.WriteVarint((ulong)Columns.Count);
            foreach (ChunkSpan[] chunks in Columns)
            {
                writer.WriteVarint((ulong)chunks.Length);
                foreach (ChunkSpan chunk in chunks)
                {
                    writer.WriteVarint(chunk.Offset);
                    writer.WriteVarint(chunk.Length);
                    writer.WriteVarint(chunk.AlignmentExponent);
                    writer.WriteVarint((ulong)chunk.Rows);
                }
            }

            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>The directory <paramref name="bytes"/> encode, or null when they do not decode as one.</summary>
    internal static SliceChunks? FromBytes(ReadOnlySpan<byte> bytes)
    {
        try
        {
            ProtoReader reader = new ProtoReader(bytes);
            if (reader.ReadVarint() != Version)
            {
                return null;
            }

            ulong encodings = reader.ReadVarint();
            if (encodings > (ulong)bytes.Length)
            {
                return null;
            }

            string[] names = new string[(int)encodings];
            for (int i = 0; i < names.Length; i++)
            {
                names[i] = Encoding.UTF8.GetString(reader.ReadLengthDelimited());
            }

            ulong count = reader.ReadVarint();
            if (count > (ulong)bytes.Length)
            {
                return null;
            }

            ChunkSpan[][] columns = new ChunkSpan[(int)count][];
            for (int c = 0; c < columns.Length; c++)
            {
                ulong chunks = reader.ReadVarint();
                if (chunks > (ulong)bytes.Length)
                {
                    return null;
                }

                ChunkSpan[] spans = new ChunkSpan[(int)chunks];
                long row = 0;
                for (int i = 0; i < spans.Length; i++)
                {
                    ulong offset = reader.ReadVarint();
                    uint length = checked((uint)reader.ReadVarint());
                    byte alignment = checked((byte)reader.ReadVarint());
                    long rows = checked((long)reader.ReadVarint());
                    spans[i] = new ChunkSpan(offset, length, alignment, row, rows);
                    row = checked(row + rows);
                }

                columns[c] = spans;
            }

            return reader.End ? new SliceChunks(names, columns) : null;
        }
        catch (Exception malformed) when (malformed is VortexFormatException or OverflowException)
        {
            return null;
        }
    }
}
