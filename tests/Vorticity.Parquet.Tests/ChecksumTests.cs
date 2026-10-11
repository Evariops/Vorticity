using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// Page checksums: the files of the standard's suite whose pages carry one, read with checks on and
/// off, the ones whose bytes were altered caught; and this writer's checksums held to by its reader.
/// </summary>
public sealed partial class ChecksumTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vorticity-checksum-{Guid.NewGuid():N}.parquet");

    [VortexRecord]
    public partial record struct Reading(long Id, string Label, double? Value);

    private static string? Root => Environment.GetEnvironmentVariable("VORTICITY_PARQUET_DATA");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ParquetOpenOptions Verified => new() { VerifyChecksums = true };

    private string Altered => _path + ".altered";

    public void Dispose()
    {
        System.IO.File.Delete(_path);
        System.IO.File.Delete(Altered);
    }

    [Theory]
    [InlineData("data/datapage_v1-uncompressed-checksum.parquet")]
    [InlineData("data/datapage_v1-snappy-compressed-checksum.parquet")]
    [InlineData("data/rle-dict-snappy-checksum.parquet")]
    [InlineData("data/plain-dict-uncompressed-checksum.parquet")]
    public async Task ReadsThePagesThatMatchTheirChecksums(string relative)
    {
        string path = Resolve(relative);
        List<string> unchecked_ = await RowsAsync(path, ParquetOpenOptions.Default);
        List<string> verified = await RowsAsync(path, Verified);
        Assert.NotEmpty(verified);
        Assert.Equal(unchecked_, verified);
    }

    [Theory]
    [InlineData("data/datapage_v1-corrupt-checksum.parquet")]
    [InlineData("data/rle-dict-uncompressed-corrupt-checksum.parquet")]
    public async Task CatchesThePagesThatDoNot(string relative)
    {
        string path = Resolve(relative);
        await Assert.ThrowsAsync<ParquetFormatException>(() => RowsAsync(path, Verified));

        // Unchecked, an altered page reads as whatever its bytes now say.
        Assert.NotEmpty(await RowsAsync(path, ParquetOpenOptions.Default));
    }

    [Theory]
    [InlineData(CompressionProfile.Auto)]
    [InlineData(CompressionProfile.None)]
    public async Task HoldsThisWritersPagesToTheirChecksums(CompressionProfile profile)
    {
        await WriteAsync(new ParquetWriteOptions { WriteChecksums = true, Profile = profile, BlockRows = 1_024, RowGroupRows = 4_096 });
        List<string> expected = await RowsAsync(_path, ParquetOpenOptions.Default);
        Assert.Equal(expected, await RowsAsync(_path, Verified));

        // A byte of the last page of the first chunk altered, in a copy, since the session keeps the
        // file it scanned mapped: the check catches it.
        long end;
        await using (ParquetFile file = await ParquetFile.OpenAsync(_path, Ct))
        {
            (long start, int length) = file.ChunkRange(file.Footer.Chunk(0, 0));
            end = start + length;
        }

        byte[] bytes = await System.IO.File.ReadAllBytesAsync(_path, Ct);
        bytes[end - 3] ^= 0x10;
        await System.IO.File.WriteAllBytesAsync(Altered, bytes, Ct);
        await Assert.ThrowsAsync<ParquetFormatException>(() => RowsAsync(Altered, Verified));
    }

    [Fact]
    public async Task WritesNoChecksumUnasked()
    {
        await WriteAsync(new ParquetWriteOptions { Profile = CompressionProfile.None });
        byte[] bytes = await System.IO.File.ReadAllBytesAsync(_path, Ct);
        long end;
        await using (ParquetFile file = await ParquetFile.OpenAsync(_path, Ct))
        {
            (long start, int length) = file.ChunkRange(file.Footer.Chunk(0, 0));
            end = start + length;
        }

        // Without a checksum, an altered PLAIN value is a value: the check has nothing to hold it to.
        bytes[end - 3] ^= 0x10;
        await System.IO.File.WriteAllBytesAsync(Altered, bytes, Ct);
        Assert.NotEmpty(await RowsAsync(Altered, Verified));
    }

    private static string Resolve(string relative)
    {
        Assert.SkipWhen(Root is null || !Directory.Exists(Root), "VORTICITY_PARQUET_DATA names no directory of Parquet files.");
        return Path.Combine(Root!, relative);
    }

    private async Task WriteAsync(ParquetWriteOptions options)
    {
        Reading[] rows = new Reading[10_000];
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i] = new Reading(i * 7L, $"label-{i % 37}", i % 9 == 0 ? null : i * 0.25);
        }

        await using ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter<Reading>(_path, options);
        await writer.WriteAsync<Reading>(rows, Ct);
        await writer.CompleteAsync(Ct);
    }

    /// <summary>Every row of the file, its columns' values joined.</summary>
    private static async Task<List<string>> RowsAsync(string path, ParquetOpenOptions options)
    {
        await using ParquetFile file = await VortexSession.Default.OpenParquetAsync(path, options, Ct);
        List<string> rows = [];
        await foreach (RecordBatch batch in file.Scan().ToBatchesAsync(Ct))
        {
            using (batch)
            {
                for (int r = 0; r < batch.RowCount; r++)
                {
                    string[] values = new string[batch.Schema.Count];
                    for (int c = 0; c < values.Length; c++)
                    {
                        values[c] = Render.Row(batch, c, r);
                    }

                    rows.Add(string.Join(" | ", values));
                }
            }
        }

        return rows;
    }
}
