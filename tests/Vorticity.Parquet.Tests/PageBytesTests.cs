using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Parquet.Metadata;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// Blocks whose values pass <see cref="ParquetWriteOptions.PageBytes"/>: written as pages of
/// power-of-two fractions of the block while those hold 1 024 rows, then cut by bytes, and read back
/// as written; a block the dictionary codes, whole.
/// </summary>
public sealed partial class PageBytesTests : IDisposable
{
    private const int BlockRows = 8_192;
    private const int Rows = (2 * BlockRows) + 3_000;

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vorticity-pagebytes-{Guid.NewGuid():N}.parquet");

    [VortexRecord]
    public partial record struct Row(long Id, string Text, string? Sparse, string Repeated, string[] Words);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => System.IO.File.Delete(_path);

    [Fact]
    public async Task CutsABlockIntoFractionsThenByBytes()
    {
        // 28 bytes a row: a block's 224 KiB in quarters of 56 KiB under 64 KiB.
        Row[] rows = Rows_(i => new string((char)('a' + (i % 26)), 3) + i.ToString("D21"));
        await WriteAsync(rows, 64 << 10);

        long[] text = FirstRows(column: 1);
        long[] expected = [.. Enumerable.Range(0, Rows / 2_048 + 1).Select(i => i * 2_048L).Where(r => r < Rows)];
        Assert.Equal(expected, text);

        // Ids are 8 bytes a row: a block's 64 KiB, one page.
        Assert.Equal([0L, BlockRows, 2 * BlockRows], FirstRows(column: 0));

        // A sparse column cuts by its values' bytes, its nulls taking none.
        Assert.True(FirstRows(column: 2).Length < text.Length);

        // Ten values: the dictionary codes every block whole.
        Assert.Equal([0L, BlockRows, 2 * BlockRows], FirstRows(column: 3));

        Assert.Equal(rows, await ReadAsync());
    }

    [Fact]
    public async Task CutsRowsTooWideForTheGridByBytes()
    {
        // 300 bytes a row: 1 024 rows pass 64 KiB, so pages are cut by bytes, off the grid.
        Row[] rows = Rows_(i => i.ToString("D8") + new string('x', 292));
        await WriteAsync(rows, 64 << 10);
        long[] text = FirstRows(column: 1);
        Assert.Contains(text, first => first % 1_024 != 0);
        Assert.All(text.Zip(text.Skip(1)), pair => Assert.InRange(pair.Second - pair.First, 1, (64 << 10) / 300));
        Assert.Equal(rows, await ReadAsync());

        // A filter's page index still bounds each page.
        await using ParquetFile file = await ParquetFile.OpenAsync(_path, Ct);
        string probe = rows[10_000].Text;
        Assert.Equal(1, await file.Scan<Row>().Where(r => r.Text == probe).CountAsync(Ct));
    }

    [Fact]
    public async Task CutsANestedColumnByItsRows()
    {
        Row[] rows = Rows_(i => i.ToString("D8"), words: i => [.. Enumerable.Range(0, 1 + (i % 5)).Select(w => $"w{i:D7}-{w}")]);
        await WriteAsync(rows, 16 << 10);
        long[] words = FirstRows(column: 4);
        Assert.True(words.Length > 3);
        Assert.Equal(0, words[0]);
        Assert.Equal(rows.Select(Render_), (await ReadAsync()).Select(Render_));
    }

    /// <summary>A row as text: its record compares its list by reference.</summary>
    private static string Render_(Row row) => $"{row.Id}|{row.Text}|{row.Sparse ?? "null"}|{row.Repeated}|{string.Join(',', row.Words)}";

    private static Row[] Rows_(Func<int, string> text, Func<int, string[]>? words = null)
    {
        Row[] rows = new Row[Rows];
        for (int i = 0; i < Rows; i++)
        {
            rows[i] = new Row(i, text(i), i % 3 == 0 ? $"sparse-{i:D20}" : null, $"repeated-{i % 10}-{new string('r', 40)}", words?.Invoke(i) ?? []);
        }

        return rows;
    }

    private async Task WriteAsync(Row[] rows, int pageBytes)
    {
        await using ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter<Row>(
            _path, new ParquetWriteOptions { BlockRows = BlockRows, RowGroupRows = 4 * BlockRows, PageBytes = pageBytes, Compression = ParquetCompression.Snappy });
        await writer.WriteAsync<Row>(rows, Ct);
        await writer.CompleteAsync(Ct);
    }

    private async Task<Row[]> ReadAsync()
    {
        await using ParquetFile file = await ParquetFile.OpenAsync(_path, Ct);
        List<Row> rows = [];
        await foreach (Row row in file.Scan<Row>().ToRecordsAsync(Ct))
        {
            rows.Add(row);
        }

        return [.. rows];
    }

    /// <summary>The first row of each page of the column's chunk in the first row group, from its offset index.</summary>
    private long[] FirstRows(int column)
    {
        byte[] bytes = System.IO.File.ReadAllBytes(_path);
        int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(bytes.Length - 8));
        ParquetFooter footer = ParquetFooter.Read(bytes.AsMemory(bytes.Length - 8 - length, length));
        ColumnChunkMetadata chunk = footer.Chunk(0, column);
        return [.. OffsetIndex.Read(bytes.AsSpan(checked((int)chunk.OffsetIndexOffset), chunk.OffsetIndexLength), footer.RowGroups[0].RowCount).Select(page => page.FirstRow)];
    }
}
