// A column of more chunks than a page lays its chunk directory in pages of fixed-width entries, behind a table of their
// checksums: a read of a slice reads the pages of its rows alone.
using System;
using System.Collections.Generic;
using System.Linq;
using Vorticity.Indexes;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class SliceChunkPagesTests
{
    /// <summary>
    /// Ten chunks of a column laid in pages of four: the table says three pages and their checksums; each page reads back
    /// its chunks from the first row the pages' first rows give it; the whole region reads back every chunk, as a reader with
    /// no table of contents reads it; and a page with a byte flipped is refused, alone or in its region.
    /// </summary>
    [Fact]
    public void AColumnsPagesReadBackItsChunks()
    {
        ChunkSpan[] chunks = [.. Enumerable.Range(0, 10).Select(i => new ChunkSpan((ulong)(1_024 + (i * 640)), 600, 6, i * 100L, 100))];
        SliceChunks directory = new(["vortex.primitive"], [chunks, chunks]);
        (int[] columns, long[] rows) = directory.Paging(4)!.Value;
        Assert.Equal([0, 1], columns);
        Assert.Equal([0L, 400, 800], rows);

        byte[] bytes = directory.PagedBytes(0, 4, out int tableLength);
        Assert.Equal(SliceChunks.TableBytes(3), tableLength);
        Assert.Equal(tableLength + (10 * SliceChunks.EntryBytes), bytes.Length);
        SliceChunks.PageTable table = SliceChunks.TableFrom(bytes.AsSpan(0, tableLength))!;
        Assert.Equal((4, 10, 3, (byte)6), (table.PageEntries, table.Count, table.Checksums.Length, table.Alignment));
        List<ChunkSpan> read = [];
        for (int page = 0; page < 3; page++)
        {
            (long offset, int length) = table.Page(page);
            Assert.Equal(page == 2 ? 2 * SliceChunks.EntryBytes : 4 * SliceChunks.EntryBytes, length);
            read.AddRange(SliceChunks.PageFrom(table, page, bytes.AsSpan(tableLength + (int)offset, length), rows[page])!);
        }

        Assert.Equal(chunks, read);
        Assert.Equal(chunks, SliceChunks.ColumnFrom(bytes));

        // A page read at the wrong first row, or torn, is refused; so is its region read whole.
        Assert.Null(SliceChunks.PageFrom(table, 1, bytes.AsSpan(tableLength + (int)table.Page(1).Offset, table.Page(1).Length), rows[1] + 1));
        bytes[tableLength + 5] ^= 1;
        Assert.Null(SliceChunks.PageFrom(table, 0, bytes.AsSpan(tableLength, table.Page(0).Length), 0));
        Assert.Null(SliceChunks.ColumnFrom(bytes));
        Assert.Null(SliceChunks.TableFrom(bytes.AsSpan(0, tableLength - 1)));
    }

    /// <summary>
    /// The columns whose chunks start at the rows of the first paged one's, and hold more than a page, are paged: one whose
    /// chunks start elsewhere, one aligned otherwise, or one of a page or fewer, keeps its directory whole, and a file with
    /// none such pages none.
    /// </summary>
    [Fact]
    public void TheColumnsOfTheSameRowsAndMoreThanAPageArePaged()
    {
        ChunkSpan[] chunks = [.. Enumerable.Range(0, 10).Select(i => new ChunkSpan((ulong)(1_024 + (i * 640)), 600, 6, i * 100L, 100))];
        ChunkSpan[] elsewhere = [.. chunks.Select((c, i) => i == 3 ? c with { Rows = 99 } : i == 4 ? c with { FirstRow = 399, Rows = 101 } : c)];
        ChunkSpan[] aligned = [.. chunks.Select((c, i) => i == 7 ? c with { AlignmentExponent = 3 } : c)];
        ChunkSpan[] few = chunks[..4];
        Assert.Equal([0, 3], new SliceChunks(["x"], [chunks, elsewhere, aligned, chunks, few]).Paging(4)!.Value.Columns);
        Assert.Equal([1], new SliceChunks(["x"], [few, elsewhere, chunks]).Paging(4)!.Value.Columns);
        Assert.Null(new SliceChunks(["x"], [few, aligned[..4]]).Paging(4));
    }
}
