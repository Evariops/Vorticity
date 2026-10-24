using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Hashing;
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

    /// <summary>
    /// The version of a column's region whose chunks lie in pages of fixed-width entries, behind a table that says how many
    /// and holds each page's checksum (<see cref="PageTable"/>): a read of a slice reads the pages of its rows alone, where a
    /// day's column held 44 000 chunks in half a megabyte that every read of a slice read and parsed whole.
    /// </summary>
    internal const byte PagedVersion = 2;

    /// <summary>The bytes of a chunk's entry in a page: its first row, its offset, its length and its rows, little-endian.</summary>
    internal const int EntryBytes = 24;

    /// <summary>The entries of a page the writer lays by default: 128, 3 KB.</summary>
    internal const int DefaultPageEntries = 128;

    // A page table's header: the version, the chunks' alignment, two bytes kept, the entries a page and the chunks.
    private const int TableHeaderBytes = 12;

    /// <summary>The bytes of a page table of <paramref name="pages"/> pages: its header, and a checksum a page.</summary>
    internal static int TableBytes(int pages) => TableHeaderBytes + (8 * pages);

    internal SliceChunks(IReadOnlyList<string> encodings, IReadOnlyList<ChunkSpan[]> columns)
    {
        Encodings = encodings;
        Columns = columns;
    }

    /// <summary>
    /// The columns whose directories lie in pages of <paramref name="pageEntries"/> chunks, and the first row of each page: those
    /// whose chunks start at the rows the first such column's do, each of them aligned alike, when they hold more chunks than a
    /// page. Every paged column shares the pages' first rows, which the table of contents holds once. None when no column has
    /// more than a page of chunks.
    /// </summary>
    internal (int[] Columns, long[] PageRows)? Paging(int pageEntries)
    {
        ChunkSpan[]? reference = null;
        List<int> paged = [];
        for (int column = 0; column < Columns.Count; column++)
        {
            ChunkSpan[] chunks = Columns[column];
            if (chunks.Length <= pageEntries || !Pageable(chunks) || (reference is not null && !SameRows(reference, chunks)))
            {
                continue;
            }

            reference ??= chunks;
            paged.Add(column);
        }

        if (reference is null)
        {
            return null;
        }

        long[] rows = new long[(reference.Length + pageEntries - 1) / pageEntries];
        for (int page = 0; page < rows.Length; page++)
        {
            rows[page] = reference[page * pageEntries].FirstRow;
        }

        return ([.. paged], rows);
    }

    /// <summary>Whether a page's entries hold <paramref name="chunks"/>: one alignment for all, and rows a 32-bit count holds.</summary>
    private static bool Pageable(ChunkSpan[] chunks)
    {
        foreach (ChunkSpan chunk in chunks)
        {
            if (chunk.AlignmentExponent != chunks[0].AlignmentExponent || chunk.Rows > uint.MaxValue)
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameRows(ChunkSpan[] reference, ChunkSpan[] chunks)
    {
        if (reference.Length != chunks.Length)
        {
            return false;
        }

        for (int i = 0; i < chunks.Length; i++)
        {
            if (reference[i].FirstRow != chunks[i].FirstRow || reference[i].Rows != chunks[i].Rows)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The region of paged column <paramref name="column"/>: its page table, of <paramref name="tableLength"/> bytes, then its
    /// pages of <paramref name="pageEntries"/> entries each, the last shorter, end to end.
    /// </summary>
    internal byte[] PagedBytes(int column, int pageEntries, out int tableLength)
    {
        ChunkSpan[] chunks = Columns[column];
        int pages = (chunks.Length + pageEntries - 1) / pageEntries;
        tableLength = TableBytes(pages);
        byte[] bytes = new byte[tableLength + (chunks.Length * EntryBytes)];
        Span<byte> table = bytes.AsSpan(0, tableLength);
        table[0] = PagedVersion;
        table[1] = chunks[0].AlignmentExponent;
        BinaryPrimitives.WriteInt32LittleEndian(table[4..], pageEntries);
        BinaryPrimitives.WriteInt32LittleEndian(table[8..], chunks.Length);
        for (int i = 0; i < chunks.Length; i++)
        {
            Span<byte> entry = bytes.AsSpan(tableLength + (i * EntryBytes), EntryBytes);
            BinaryPrimitives.WriteInt64LittleEndian(entry, chunks[i].FirstRow);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[8..], chunks[i].Offset);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[16..], chunks[i].Length);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[20..], checked((uint)chunks[i].Rows));
        }

        for (int page = 0; page < pages; page++)
        {
            int start = tableLength + (page * pageEntries * EntryBytes);
            int length = Math.Min(pageEntries, chunks.Length - (page * pageEntries)) * EntryBytes;
            BinaryPrimitives.WriteUInt64LittleEndian(table[(TableHeaderBytes + (8 * page))..], XxHash3.HashToUInt64(bytes.AsSpan(start, length)));
        }

        return bytes;
    }

    /// <summary>
    /// The page table of a paged column's region: the chunks' alignment, the entries of a page, the chunks, and each page's
    /// checksum. Page <c>k</c> lies <c>k × PageEntries × EntryBytes</c> past the table's end, its entries those of its chunks.
    /// </summary>
    internal sealed record PageTable(byte Alignment, int PageEntries, int Count, ulong[] Checksums)
    {
        /// <summary>The table's own bytes.</summary>
        internal int Length => TableBytes(Checksums.Length);

        /// <summary>Where page <paramref name="page"/> lies past the table's end, and its bytes.</summary>
        internal (long Offset, int Length) Page(int page) =>
            ((long)page * PageEntries * EntryBytes, Math.Min(PageEntries, Count - (page * PageEntries)) * EntryBytes);
    }

    /// <summary>The page table <paramref name="bytes"/> hold, or null when they hold none, or one whose counts do not agree.</summary>
    internal static PageTable? TableFrom(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < TableHeaderBytes || bytes[0] != PagedVersion)
        {
            return null;
        }

        int pageEntries = BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]);
        int count = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
        if (pageEntries <= 0 || count <= 0)
        {
            return null;
        }

        long pages = ((long)count + pageEntries - 1) / pageEntries;
        if (bytes.Length != TableHeaderBytes + (8 * pages))
        {
            return null;
        }

        ulong[] checksums = new ulong[pages];
        for (int page = 0; page < checksums.Length; page++)
        {
            checksums[page] = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(TableHeaderBytes + (8 * page))..]);
        }

        return new PageTable(bytes[1], pageEntries, count, checksums);
    }

    /// <summary>
    /// The chunks page <paramref name="page"/> of <paramref name="table"/> holds, its <paramref name="bytes"/>: null when they
    /// do not hold the page's checksum, or chunks that do not follow each other, or do not start at <paramref name="firstRow"/>.
    /// </summary>
    internal static ChunkSpan[]? PageFrom(PageTable table, int page, ReadOnlySpan<byte> bytes, long firstRow)
    {
        if (bytes.Length != table.Page(page).Length || XxHash3.HashToUInt64(bytes) != table.Checksums[page])
        {
            return null;
        }

        ChunkSpan[] chunks = new ChunkSpan[bytes.Length / EntryBytes];
        long row = firstRow;
        for (int i = 0; i < chunks.Length; i++)
        {
            ReadOnlySpan<byte> entry = bytes.Slice(i * EntryBytes, EntryBytes);
            long first = BinaryPrimitives.ReadInt64LittleEndian(entry);
            uint rows = BinaryPrimitives.ReadUInt32LittleEndian(entry[20..]);
            if (first != row)
            {
                return null;
            }

            chunks[i] = new ChunkSpan(BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]), BinaryPrimitives.ReadUInt32LittleEndian(entry[16..]), table.Alignment, first, rows);
            row += rows;
        }

        return chunks;
    }

    /// <summary>The chunks of a paged column's whole region <paramref name="bytes"/>, every page checked; null when one does not hold.</summary>
    private static ChunkSpan[]? PagedFrom(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < TableHeaderBytes)
        {
            return null;
        }

        int pageEntries = BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]);
        int count = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
        long pages = pageEntries <= 0 || count <= 0 ? -1 : ((long)count + pageEntries - 1) / pageEntries;
        if (pages < 0 || bytes.Length != TableHeaderBytes + (8 * pages) + ((long)count * EntryBytes)
            || TableFrom(bytes[..(TableHeaderBytes + (int)(8 * pages))]) is not { } table)
        {
            return null;
        }

        ChunkSpan[] chunks = new ChunkSpan[count];
        long row = 0;
        for (int page = 0; page < table.Checksums.Length; page++)
        {
            (long offset, int length) = table.Page(page);
            if (PageFrom(table, page, bytes.Slice(table.Length + (int)offset, length), row) is not { } held)
            {
                return null;
            }

            held.CopyTo(chunks, page * pageEntries);
            row = held[^1].EndRow;
        }

        return chunks;
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

        // A paged column's whole region, as a reader with no table of contents reads it.
        if (!bytes.IsEmpty && bytes[0] == PagedVersion)
        {
            if (PagedFrom(bytes) is not { } paged)
            {
                return -1;
            }

            chunks = System.Buffers.ArrayPool<ChunkSpan>.Shared.Rent(Math.Max(paged.Length, 1));
            paged.CopyTo(chunks, 0);
            return paged.Length;
        }

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
        if (!bytes.IsEmpty && bytes[0] == PagedVersion)
        {
            return PagedFrom(bytes);
        }

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
