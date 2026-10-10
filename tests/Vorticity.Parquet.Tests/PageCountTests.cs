using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Expressions;
using Vorticity.Parquet.Metadata;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// The pages a scan decodes and decompresses, counted against the file's own pages walked by their
/// headers: each page decoded and decompressed once per scan, a page the page index rules out never,
/// and a batch of this writer's files a page's slots in place, never copied out of two pages.
/// </summary>
public sealed class PageCountTests : IDisposable
{
    private const int Rows = 200_000;
    private const int GroupRows = 65_536;
    private const int BatchRows = 8_192;

    private readonly List<string> _paths = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (string path in _paths)
        {
            System.IO.File.Delete(path);
        }
    }

    [Theory]
    [InlineData(ParquetCompression.Zstd, DataPageVersion.V2, true)]
    [InlineData(ParquetCompression.Zstd, DataPageVersion.V2, false)]
    [InlineData(ParquetCompression.Snappy, DataPageVersion.V1, true)]
    [InlineData(ParquetCompression.Uncompressed, DataPageVersion.V2, true)]
    public async Task AScanDecodesAndDecompressesEachPageOnce(ParquetCompression compression, DataPageVersion pages, bool mapped)
    {
        string path = await WriteAsync(new ParquetWriteOptions { Compression = compression, DataPageVersion = pages, RowGroupRows = GroupRows });
        await using VortexSession session = VortexSession.Create(options => options.MapFiles = mapped);
        await using ParquetFile file = await ParquetFile.OpenAsync(path, ParquetOpenOptions.Default, session, Ct);
        Expected expected = Walk(file, path, (group, column, first, end) => true);
        Assert.True(expected.Pages >= 4 * file.Compiled.Columns.Length, $"{expected.Pages} pages");

        file.Counters.Reset();
        Assert.Equal(Rows, await RowsAsync(file.Scan()));
        Assert.Equal(expected.Pages, file.Counters.Pages);
        Assert.Equal(expected.Dictionaries, file.Counters.Dictionaries);
        Assert.Equal(expected.Compressed, file.Counters.Decompressions);
        Assert.Equal(0, file.Counters.Gathers);
        if (compression == ParquetCompression.Uncompressed)
        {
            Assert.Equal(0, file.Counters.Decompressions);
        }
    }

    /// <summary>
    /// A filter the page index answers: of each flat column, the pages of the batches it leaves are
    /// decoded, those of the batches it rules out stepped over unread, on a mapped file as over reads.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task APageThePageIndexRulesOutIsNeverDecoded(bool mapped)
    {
        string path = await WriteAsync(new ParquetWriteOptions { Compression = ParquetCompression.Zstd, RowGroupRows = GroupRows });
        await using VortexSession session = VortexSession.Create(options => options.MapFiles = mapped);
        await using ParquetFile file = await ParquetFile.OpenAsync(path, ParquetOpenOptions.Default, session, Ct);
        const long From = 130_000;
        const long To = 140_000;
        VortexExpr filter = Expr.And(
            Expr.Ge(Expr.Field("id"), Expr.Literal(FilterLiteral.From(From))),
            Expr.Lt(Expr.Field("id"), Expr.Literal(FilterLiteral.From(To))));

        // The batches the id's pages may hold a row of, which the scan keeps; the pages of every
        // column that reach into one of them, which it decodes.
        HashSet<(int Group, long Batch)> live = [];
        ParquetFooter footer = file.Footer;
        for (int group = 0; group < footer.RowGroups.Length; group++)
        {
            long first = footer.RowGroups[group].FirstRow;
            foreach ((long start, long end) in Pages(file, path, group, 0))
            {
                if (first + start < To && first + end > From)
                {
                    for (long batch = start / BatchRows; batch * BatchRows < end; batch++)
                    {
                        live.Add((group, batch));
                    }
                }
            }
        }

        Expected expected = Walk(file, path, (group, column, start, end) =>
            Enumerable.Range((int)(start / BatchRows), (int)((end - 1) / BatchRows) - (int)(start / BatchRows) + 1).Any(b => live.Contains((group, b))));
        Expected whole = Walk(file, path, (group, column, start, end) => true);
        Assert.True(expected.Pages * 4 < whole.Pages, $"{expected.Pages} of {whole.Pages} pages live");

        file.Counters.Reset();
        Assert.Equal(To - From, await RowsAsync(file.Scan().Where(filter)));
        Assert.Equal(expected.Pages, file.Counters.Pages);
        Assert.Equal(expected.Dictionaries, file.Counters.Dictionaries);
        Assert.Equal(expected.Compressed, file.Counters.Decompressions);
        Assert.Equal(0, file.Counters.Gathers);
    }

    /// <summary>
    /// A filter on a column of dictionary codes whose every group holds the value: each group's
    /// dictionary, decoded to prune it and found to hold the value, is the one the group is read with,
    /// not decoded nor decompressed again.
    /// </summary>
    [Theory]
    [InlineData(ParquetCompression.Zstd, true)]
    [InlineData(ParquetCompression.Uncompressed, true)]
    [InlineData(ParquetCompression.Zstd, false)]
    public async Task ADictionaryDecodedToPruneIsNotDecodedAgain(ParquetCompression compression, bool mapped)
    {
        string path = await WriteAsync(new ParquetWriteOptions { Compression = compression, RowGroupRows = GroupRows });
        await using VortexSession session = VortexSession.Create(options => options.MapFiles = mapped);
        await using ParquetFile file = await ParquetFile.OpenAsync(path, ParquetOpenOptions.Default, session, Ct);
        Expected whole = Walk(file, path, (group, column, start, end) => true);
        Assert.True(whole.Dictionaries >= file.Footer.RowGroups.Length, $"{whole.Dictionaries} dictionaries");

        file.Counters.Reset();
        VortexExpr filter = Expr.Eq(Expr.Field("label"), Expr.Literal(FilterLiteral.From("label-7")));
        Assert.True(await RowsAsync(file.Scan().Where(filter)) > 0);
        Assert.Equal(whole.Pages, file.Counters.Pages);
        Assert.Equal(whole.Dictionaries, file.Counters.Dictionaries);
        Assert.Equal(whole.Compressed, file.Counters.Decompressions);

        // A value no group holds: each dictionary decoded once, and no group read.
        file.Counters.Reset();
        VortexExpr absent = Expr.Eq(Expr.Field("label"), Expr.Literal(FilterLiteral.From("label-77")));
        Assert.Equal(0, await RowsAsync(file.Scan().Where(absent)));
        Assert.Equal(0, file.Counters.Pages);
        Assert.Equal(file.Footer.RowGroups.Length, file.Counters.Dictionaries);
    }

    /// <summary>
    /// Pages of another writer, of three rows each, read in batches of four: a batch that spans two
    /// pages is copied out of them, which the count of gathers says, where every batch of this
    /// writer's files is one page's.
    /// </summary>
    [Fact]
    public async Task ABatchAcrossTwoPagesIsCopiedOutOfThem()
    {
        HandBuiltFile built = new HandBuiltFile(1) { Rows = 9 }.Leaf("Id", FieldRepetition.Required, PhysicalType.Int64);
        built.Chunk(PhysicalType.Int64, "Id")
            .V2(null, 0, [0, 0, 0], 0, 3, HandBuiltFile.Longs(1, 2, 3))
            .V2(null, 0, [0, 0, 0], 0, 3, HandBuiltFile.Longs(4, 5, 6))
            .V2(null, 0, [0, 0, 0], 0, 3, HandBuiltFile.Longs(7, 8, 9));
        string path = Track();
        await System.IO.File.WriteAllBytesAsync(path, built.ToBytes(), Ct);
        await using ParquetFile file = await ParquetFile.OpenAsync(path, Ct);

        file.Counters.Reset();
        Assert.Equal(9, await RowsAsync(file.Scan().With(new ScanOptions { BatchRows = 4 })));
        Assert.Equal(3, file.Counters.Pages);
        Assert.Equal(0, file.Counters.Decompressions);
        Assert.Equal(2, file.Counters.Gathers);
    }

    /// <summary>What a scan should decode of the pages a predicate keeps, by their rows.</summary>
    private readonly record struct Expected(long Pages, long Dictionaries, long Compressed);

    /// <summary>
    /// Walks every chunk of the file by its pages' headers: the data pages whose rows, from the chunk's
    /// offset index, <paramref name="keeps"/> says a batch reads, a chunk's dictionary page where it
    /// keeps one of them, and of those the pages whose bytes go through the codec.
    /// </summary>
    private static Expected Walk(ParquetFile file, string path, Func<int, int, long, long, bool> keeps)
    {
        byte[] bytes = System.IO.File.ReadAllBytes(path);
        ParquetFooter footer = file.Footer;
        long pages = 0;
        long dictionaries = 0;
        long compressed = 0;
        for (int group = 0; group < footer.RowGroups.Length; group++)
        {
            for (int column = 0; column < file.Compiled.Columns.Length; column++)
            {
                ColumnChunkMetadata chunk = footer.Chunk(group, column);
                bool codec = chunk.Codec != CompressionCodec.Uncompressed;
                (long start, int length) = file.ChunkRange(chunk);
                List<(long Start, long End)> rows = Pages(file, path, group, column);
                int page = 0;
                bool any = false;
                long dictionaryPages = 0;
                long dictionaryCompressed = 0;
                for (int at = 0; at < length;)
                {
                    PageHeader header = PageHeader.Read(bytes.AsSpan((int)start + at, length - at));
                    if (header.Type == PageType.DictionaryPage)
                    {
                        dictionaryPages++;
                        dictionaryCompressed += codec ? 1 : 0;
                    }
                    else if (header.IsDataPage)
                    {
                        (long first, long end) = rows[page++];
                        if (keeps(group, column, first, end))
                        {
                            any = true;
                            pages++;
                            int levels = header.Type == PageType.DataPageV2 ? header.DefinitionLevelsLength + header.RepetitionLevelsLength : 0;
                            bool squeezed = header.Type == PageType.DataPage || header.IsCompressed;
                            compressed += codec && squeezed && header.UncompressedPageSize > levels ? 1 : 0;
                        }
                    }

                    at += header.HeaderLength + header.CompressedPageSize;
                }

                Assert.Equal(rows.Count, page);
                if (any)
                {
                    dictionaries += dictionaryPages;
                    compressed += dictionaryCompressed;
                }
            }
        }

        return new Expected(pages, dictionaries, compressed);
    }

    /// <summary>The rows of the row group each data page of column <paramref name="column"/>'s chunk holds, by its offset index.</summary>
    private static List<(long Start, long End)> Pages(ParquetFile file, string path, int group, int column)
    {
        ColumnChunkMetadata chunk = file.Footer.Chunk(group, column);
        long rows = file.Footer.RowGroups[group].RowCount;
        byte[] index = new byte[chunk.OffsetIndexLength];
        using (System.IO.FileStream stream = System.IO.File.OpenRead(path))
        {
            stream.Position = chunk.OffsetIndexOffset;
            stream.ReadExactly(index);
        }

        PageLocation[] locations = OffsetIndex.Read(index, rows);
        return [.. locations.Select((location, i) => (location.FirstRow, i + 1 < locations.Length ? locations[i + 1].FirstRow : rows))];
    }

    private static async Task<long> RowsAsync(Scan scan)
    {
        long rows = 0;
        await foreach (RecordBatch batch in scan.ToBatchesAsync(Ct))
        {
            using (batch)
            {
                rows += batch.SelectionWords.IsEmpty ? batch.RowCount : Selected(batch);
            }
        }

        return rows;
    }

    private static int Selected(RecordBatch batch)
    {
        int selected = 0;
        for (int r = 0; r < batch.RowCount; r++)
        {
            selected += (int)((batch.SelectionWords[r >> 6] >> (r & 63)) & 1);
        }

        return selected;
    }

    private string Track()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vx-pages-{Guid.NewGuid():N}.parquet");
        _paths.Add(path);
        return path;
    }

    private async Task<string> WriteAsync(ParquetWriteOptions options)
    {
        VortexSchema schema =
        [
            ("id", VortexType.Int64),
            ("value", VortexType.Float64.Nullable),
            ("label", VortexType.Utf8),
            ("tags", VortexType.List(VortexType.Int32)),
        ];
        string path = Track();
        await using ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter(path, schema, options);
        ColumnsBuilder builder = writer.Builder();
        Random random = new(41);
        for (int i = 0; i < Rows; i++)
        {
            builder.Column<long>(0).Append(i);
            if (i % 9 == 0)
            {
                builder.Column<double?>(1).AppendNull();
            }
            else
            {
                builder.Column<double?>(1).Append(random.NextDouble());
            }

            builder.Column<string>(2).Append($"label-{random.Next(50)}");
            ColumnBuilder<ReadOnlyMemory<int>> tags = builder.Column<ReadOnlyMemory<int>>(3);
            tags.BeginList();
            for (int t = 0; t < i % 3; t++)
            {
                tags.Elements.Append(i + t);
            }

            tags.EndList();        }

        await writer.WriteAsync(builder, Ct);
        await writer.CompleteAsync(Ct);
        return path;
    }
}
