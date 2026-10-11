using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Parquet.Metadata;
using Vorticity.Parquet.Reading;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// A page of codes into a dictionary reads its values through them, a byte a code where the codes are
/// eight bits wide or narrower and four bytes where they are wider; a code past the dictionary's
/// entries is refused either way, never read as a value past them.
/// </summary>
public sealed class DictionaryCodeTests : IDisposable
{
    private const int Rows = 20_000;

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vorticity-codes-{Guid.NewGuid():N}.parquet");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => System.IO.File.Delete(_path);

    [Theory]
    [InlineData(5, 3)]
    [InlineData(100, 7)]
    [InlineData(300, 9)]
    public async Task ReadsTheValuesItsCodesName(int entries, int width)
    {
        await WriteAsync(entries);
        WrittenFile written = new(await System.IO.File.ReadAllBytesAsync(_path, Ct));
        PageLocation page = written.Pages(0, 0)[0];
        PageHeader header = written.Header(page);
        Assert.Equal(ParquetEncoding.RleDictionary, header.Encoding);
        Assert.Equal(width, written.Values(page, header)[0]);

        await using ParquetFile file = await ParquetFile.OpenAsync(_path, Ct);
        long row = 0;
        await foreach (RecordBatch batch in file.Scan().ToBatchesAsync(Ct))
        {
            using (batch)
            {
                for (int r = 0; r < batch.RowCount; r++, row++)
                {
                    Assert.Equal(Value(row, entries), Render.Row(batch, 0, r));
                }
            }
        }

        Assert.Equal(Rows, row);
    }

    [Theory]
    [InlineData(5, 3)]
    [InlineData(300, 9)]
    public async Task RefusesACodePastItsDictionary(int entries, int width)
    {
        await WriteAsync(entries);

        // The first page's first group of eight codes, packed, made all ones: a code past the
        // dictionary's entries, which its width can name.
        byte[] bytes = await System.IO.File.ReadAllBytesAsync(_path, Ct);
        WrittenFile written = new(bytes);
        PageLocation page = written.Pages(0, 0)[0];
        PageHeader header = written.Header(page);
        int values = checked((int)page.Offset) + header.HeaderLength + header.RepetitionLevelsLength + header.DefinitionLevelsLength;
        Assert.Equal(width, bytes[values]);
        Assert.Equal(1, bytes[values + 1] & 1);
        int run = values + 2 + (bytes[values + 1] >= 0x80 ? 1 : 0);
        bytes.AsSpan(run, width).Fill(0xFF);
        await System.IO.File.WriteAllBytesAsync(_path, bytes, Ct);

        await using ParquetFile file = await ParquetFile.OpenAsync(_path, Ct);
        ParquetFormatException refused = await Assert.ThrowsAsync<ParquetFormatException>(async () =>
        {
            await foreach (RecordBatch batch in file.Scan().ToBatchesAsync(Ct))
            {
                batch.Dispose();
            }
        });
        Assert.Contains("passes its dictionary", refused.Message, StringComparison.Ordinal);
    }

    private static string Value(long row, int entries) => (row * 7 % entries * 3).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// A column of <paramref name="entries"/> values in no run, uncompressed, so that its pages' codes
    /// lie as they are packed.
    /// </summary>
    private async Task WriteAsync(int entries)
    {
        VortexSchema schema = [("code", VortexType.Int32)];
        await using ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter(
            _path, schema, new ParquetWriteOptions { Compression = ParquetCompression.Uncompressed, BlockRows = 4_096, RowGroupRows = 20_480 });
        ColumnsBuilder builder = writer.Builder();
        for (int row = 0; row < Rows; row++)
        {
            builder.Column<int>(0).Append((int)(row * 7L % entries * 3));
        }

        await writer.WriteAsync(builder, Ct);
        await writer.CompleteAsync(Ct);
    }
}
