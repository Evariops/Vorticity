using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Parquet.Metadata;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// Data pages v1, written for readers that read no other: every data page of the file is one, its
/// levels inside its compressed bytes, and the file reads as the same rows its v2 twin does.
/// </summary>
public sealed class DataPageVersionTests : IDisposable
{
    private const int Rows = 20_000;

    private static readonly VortexSchema Schema =
    [
        ("id", VortexType.Int64),
        ("small", VortexType.Int32.Nullable),
        ("measure", VortexType.Float64.Nullable),
        ("label", VortexType.Utf8.Nullable),
        ("flag", VortexType.Bool.Nullable),
        ("blob", VortexType.Binary),
    ];

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
    [InlineData(ParquetCompression.Uncompressed)]
    [InlineData(ParquetCompression.Zstd)]
    [InlineData(ParquetCompression.Snappy)]
    [InlineData(ParquetCompression.Gzip)]
    public async Task WritesVersionOnePagesThatReadAsTheirVersionTwoTwins(ParquetCompression compression)
    {
        ParquetWriteOptions options = new() { Compression = compression, BlockRows = 4_096, RowGroupRows = 8_192, WriteChecksums = true };
        string v1 = await WriteAsync(options with { DataPageVersion = DataPageVersion.V1 });
        string v2 = await WriteAsync(options);

        WrittenFile written = new(await System.IO.File.ReadAllBytesAsync(v1, Ct));
        int pages = 0;
        for (int group = 0; group < written.Footer.RowGroups.Length; group++)
        {
            for (int column = 0; column < Schema.Count; column++)
            {
                foreach (PageLocation page in written.Pages(group, column))
                {
                    Assert.Equal(PageType.DataPage, written.Header(page).Type);
                    pages++;
                }
            }
        }

        Assert.True(pages >= 3 * Schema.Count);

        // Every page held to its checksum, and the footer to the rows.
        await using (ParquetFile file = await ParquetFile.OpenAsync(v1, new ParquetOpenOptions { VerifyChecksums = true }, VortexSession.Default, Ct))
        {
            Assert.Equal(await RowsAsync(v2), await RowsAsync(file));
            Assert.Empty(await file.VerifyAsync(Ct));
            Assert.True(file.Metadata.RowGroups[0].Chunks.Single(chunk => chunk.Column == "label").IsFullyDictionaryEncoded);
        }
    }

    [Fact]
    public async Task RefusesAVersionTheStandardHasNot()
    {
        string path = NewPath();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            await using ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter(path, Schema, new ParquetWriteOptions { DataPageVersion = (DataPageVersion)3 });
        });
        Assert.False(System.IO.File.Exists(path));
    }

    private async Task<string> WriteAsync(ParquetWriteOptions options)
    {
        string path = NewPath();
        await using ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter(path, Schema, options);
        ColumnsBuilder builder = writer.Builder();
        Random random = new(29);
        for (int i = 0; i < Rows; i++)
        {
            bool none = i % 13 == 0;
            builder.Column<long>(0).Append(1_000_000L + i);
            if (none)
            {
                builder.Column<int?>(1).AppendNull();
                builder.Column<double?>(2).AppendNull();
                builder.Column<string?>(3).AppendNull();
                builder.Column<bool?>(4).AppendNull();
            }
            else
            {
                builder.Column<int?>(1).Append(i % 300);
                builder.Column<double?>(2).Append(20.0 + (5.0 * Math.Sin(i / 400.0)));
                builder.Column<string?>(3).Append($"label-{i % 10}");
                builder.Column<bool?>(4).Append(i % 1_000 < 900);
            }

            byte[] blob = new byte[random.Next(0, 24)];
            random.NextBytes(blob);
            builder.Column<ReadOnlyMemory<byte>>(5).Append(blob);
        }

        await writer.WriteAsync(builder, Ct);
        await writer.CompleteAsync(Ct);
        return path;
    }

    private static async Task<string[][]> RowsAsync(string path)
    {
        await using ParquetFile file = await ParquetFile.OpenAsync(path, Ct);
        return await RowsAsync(file);
    }

    private static async Task<string[][]> RowsAsync(ParquetFile file)
    {
        List<string[]> rows = [];
        await foreach (RecordBatch batch in file.Scan().ToBatchesAsync(Ct))
        {
            using (batch)
            {
                for (int r = 0; r < batch.RowCount; r++)
                {
                    rows.Add([.. Enumerable.Range(0, batch.Schema.Count).Select(c => Render.Row(batch, c, r))]);
                }
            }
        }

        return [.. rows];
    }

    /// <summary>A path of its own for each file, since a file the session has mapped stays mapped while it is cached.</summary>
    private string NewPath()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vx-pages-{Guid.NewGuid():N}.parquet");
        _paths.Add(path);
        return path;
    }
}
