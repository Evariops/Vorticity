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
/// <see cref="IndexKinds.SliceChunks"/>, on the root: the encodings in its options, and one payload
/// region per column, in column order, holding that column's chunks.
/// </summary>
/// <remarks>
/// <para>
/// A chunk's segment ends with its encoding tree, whose encodings are indices into the footer's table
/// of array encodings: the entry carries that table, so a chunk decodes from its own bytes, the table
/// and its column's dtype. A slice's rows are a range of rows, and the chunks of a column that overlap
/// it are found by halves on their ends.
/// </para>
/// <para>
/// A column's region holds, behind a version byte, its chunk count, then per chunk its offset, length,
/// alignment exponent and rows, each a varint: about ten bytes a chunk, so a day of 10⁹ rows in chunks
/// of 40 960 rows takes about 250 KB a column. A region per column lets a read of one column, the
/// instant at the edges of a window, fetch that column's alone, and keeps all of it out of the
/// directory every open reads.
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

    /// <summary>The chunks of <paramref name="chunks"/> that hold a row of <c>[first, first + rows)</c>.</summary>
    internal static ReadOnlySpan<ChunkSpan> Overlapping(ChunkSpan[] chunks, long first, long rows) =>
        Overlapping(chunks.AsSpan(), first, rows);

    /// <summary>The chunks of <paramref name="chunks"/>, in row order, that hold a row of <c>[first, first + rows)</c>.</summary>
    internal static ReadOnlySpan<ChunkSpan> Overlapping(ReadOnlySpan<ChunkSpan> chunks, long first, long rows)
    {
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

        return chunks.Slice(low, stop - low);
    }

    /// <summary>The entry's options: the encodings, each its length and its bytes, behind a version byte.</summary>
    internal byte[] OptionBytes()
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

            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>The region of column <paramref name="column"/>.</summary>
    internal byte[] ColumnBytes(int column)
    {
        ChunkSpan[] chunks = Columns[column];
        ProtoWriter writer = new ProtoWriter();
        try
        {
            writer.WriteVarint(Version);
            writer.WriteVarint((ulong)chunks.Length);
            foreach (ChunkSpan chunk in chunks)
            {
                writer.WriteVarint(chunk.Offset);
                writer.WriteVarint(chunk.Length);
                writer.WriteVarint(chunk.AlignmentExponent);
                writer.WriteVarint((ulong)chunk.Rows);
            }

            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>The encodings the options <paramref name="bytes"/> hold, or null when they do not decode.</summary>
    internal static string[]? EncodingsFrom(ReadOnlySpan<byte> bytes)
    {
        try
        {
            ProtoReader reader = new ProtoReader(bytes);
            if (reader.ReadVarint() != Version)
            {
                return null;
            }

            ulong count = reader.ReadVarint();
            if (count > (ulong)bytes.Length)
            {
                return null;
            }

            string[] names = new string[(int)count];
            for (int i = 0; i < names.Length; i++)
            {
                names[i] = Encoding.UTF8.GetString(reader.ReadLengthDelimited());
            }

            return reader.End ? names : null;
        }
        catch (VortexFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The chunks a column's region <paramref name="bytes"/> holds, in an array rented from the shared pool, which its
    /// caller gives back; their count, or -1 when they do not decode, nothing rented then.
    /// </summary>
    internal static int ColumnInto(ReadOnlySpan<byte> bytes, out ChunkSpan[] chunks)
    {
        chunks = [];
        try
        {
            ProtoReader reader = new ProtoReader(bytes);
            if (reader.ReadVarint() != Version)
            {
                return -1;
            }

            ulong count = reader.ReadVarint();
            if (count > (ulong)bytes.Length)
            {
                return -1;
            }

            ChunkSpan[] rented = System.Buffers.ArrayPool<ChunkSpan>.Shared.Rent(Math.Max((int)count, 1));
            try
            {
                long row = 0;
                for (int i = 0; i < (int)count; i++)
                {
                    ulong offset = reader.ReadVarint();
                    uint length = checked((uint)reader.ReadVarint());
                    byte alignment = checked((byte)reader.ReadVarint());
                    long rows = checked((long)reader.ReadVarint());
                    rented[i] = new ChunkSpan(offset, length, alignment, row, rows);
                    row = checked(row + rows);
                }

                if (!reader.End)
                {
                    System.Buffers.ArrayPool<ChunkSpan>.Shared.Return(rented);
                    return -1;
                }
            }
            catch
            {
                System.Buffers.ArrayPool<ChunkSpan>.Shared.Return(rented);
                throw;
            }

            chunks = rented;
            return (int)count;
        }
        catch (Exception malformed) when (malformed is VortexFormatException or OverflowException)
        {
            return -1;
        }
    }

    /// <summary>The chunks a column's region <paramref name="bytes"/> holds, or null when they do not decode.</summary>
    internal static ChunkSpan[]? ColumnFrom(ReadOnlySpan<byte> bytes)
    {
        try
        {
            ProtoReader reader = new ProtoReader(bytes);
            if (reader.ReadVarint() != Version)
            {
                return null;
            }

            ulong count = reader.ReadVarint();
            if (count > (ulong)bytes.Length)
            {
                return null;
            }

            ChunkSpan[] chunks = new ChunkSpan[(int)count];
            long row = 0;
            for (int i = 0; i < chunks.Length; i++)
            {
                ulong offset = reader.ReadVarint();
                uint length = checked((uint)reader.ReadVarint());
                byte alignment = checked((byte)reader.ReadVarint());
                long rows = checked((long)reader.ReadVarint());
                chunks[i] = new ChunkSpan(offset, length, alignment, row, rows);
                row = checked(row + rows);
            }

            return reader.End ? chunks : null;
        }
        catch (Exception malformed) when (malformed is VortexFormatException or OverflowException)
        {
            return null;
        }
    }
}
