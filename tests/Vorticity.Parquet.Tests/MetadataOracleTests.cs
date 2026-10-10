using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Parquet.Verification;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// The metadata oracle: what a file says of itself held to its rows. This writer's files say only
/// what is true; the standard's suite's files that lie are caught, and every real file is checked.
/// </summary>
public sealed partial class MetadataOracleTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vorticity-oracle-{Guid.NewGuid():N}.parquet");

    [VortexRecord]
    public partial record struct Mixed(long Id, string Name, double? Score, decimal Price, string Long, string[] Tags, int Small);

    private static string? Root => Environment.GetEnvironmentVariable("VORTICITY_PARQUET_DATA");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => System.IO.File.Delete(_path);

    [Theory]
    [InlineData(ParquetCompression.Zstd, 8_192, 1 << 20)]
    [InlineData(ParquetCompression.Snappy, 1_024, 16 << 10)]
    [InlineData(ParquetCompression.Uncompressed, 2_048, 1 << 20)]
    public async Task FindsNothingToSayOfThisWritersFiles(ParquetCompression compression, int blockRows, int pageBytes)
    {
        Random random = new(37);
        Mixed[] rows = new Mixed[50_000];
        for (int i = 0; i < rows.Length; i++)
        {
            double? score = (i % 11) switch
            {
                0 => null,
                1 => double.NaN,
                2 => -0.0,
                3 => 0.0,
                _ => Math.Round(random.NextDouble() * 1_000, 2),
            };
            rows[i] = new Mixed(
                random.NextInt64(-1_000_000, 1_000_000),
                $"name-{random.Next(500)}",
                score,
                random.Next(-1_000_000, 1_000_000) / 100m,
                new string((char)('a' + random.Next(26)), 70) + i,
                [.. Enumerable.Range(0, random.Next(4)).Select(t => $"t{random.Next(20)}")],
                random.Next(100));
        }

        ParquetWriteOptions options = new()
        {
            Compression = compression,
            BlockRows = blockRows,
            RowGroupRows = 8 * blockRows,
            PageBytes = pageBytes,
            WriteChecksums = true,
            BloomFilters = new Dictionary<string, double> { ["Id"] = 0.01, ["Name"] = 0.01, ["Score"] = 0.05, ["Price"] = 0.01 },
        };
        await using (ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter<Mixed>(_path, options))
        {
            await writer.WriteAsync<Mixed>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        List<ParquetFinding> findings = await ParquetVerifier.VerifyAsync(_path, VortexSession.Default, Ct);
        Assert.Empty(findings);

        // The public surface says the same, and describes the file as its footer does.
        await using ParquetFile file = await ParquetFile.OpenAsync(_path, Ct);
        Assert.Empty(await file.VerifyAsync(Ct));
        ParquetMetadata metadata = file.Metadata;
        Assert.StartsWith("Vorticity.Parquet version", metadata.CreatedBy, StringComparison.Ordinal);
        Assert.Equal(rows.Length, metadata.RowCount);
        Assert.Equal(rows.Length, metadata.RowGroups.Sum(group => group.RowCount));
        Assert.Equal("INT64", metadata.Columns.Single(c => c.Path == "Id").PhysicalType);
        Assert.Equal("STRING", metadata.Columns.Single(c => c.Path == "Name").LogicalType);
        Assert.StartsWith("DECIMAL(", metadata.Columns.Single(c => c.Path == "Price").LogicalType, StringComparison.Ordinal);
        Assert.Equal(1, metadata.Columns.Single(c => c.Path.StartsWith("Tags", StringComparison.Ordinal)).MaxRepetitionLevel);
        ParquetChunkInfo id = metadata.RowGroups[0].Chunks[0];
        Assert.Equal("Id", id.Column);
        Assert.True(id.HasBloomFilter && id.HasColumnIndex && id.HasOffsetIndex);
        Assert.Equal(compression == ParquetCompression.Uncompressed ? "UNCOMPRESSED" : compression.ToString().ToUpperInvariant(), id.Codec);
        Assert.Equal(0, id.Statistics!.NullCount);
        long first = metadata.RowGroups[0].RowCount;
        Assert.Equal(rows.Take((int)first).Min(r => r.Id).ToString(System.Globalization.CultureInfo.InvariantCulture), id.Statistics.Min);
        Assert.True(id.Statistics.MinExact);
        Assert.Contains(metadata.KeyValues, pair => pair.Key == "vorticity.schema");
    }

    [Theory]
    [InlineData("data/datapage_v1-uncompressed-checksum.parquet", "column index")]
    [InlineData("data/datapage_v1-corrupt-checksum.parquet", "checksum")]
    [InlineData("data/rle-dict-uncompressed-corrupt-checksum.parquet", "checksum")]
    public async Task FindsWhatTheSuitesFilesSayFalsely(string relative, string structure)
    {
        Assert.SkipWhen(Root is null || !Directory.Exists(Root), "VORTICITY_PARQUET_DATA names no directory of Parquet files.");
        string? path = Directory.EnumerateFiles(Root!, Path.GetFileName(relative), SearchOption.AllDirectories).FirstOrDefault();
        Assert.SkipWhen(path is null, $"{relative} is not under VORTICITY_PARQUET_DATA.");
        await using ParquetFile file = await ParquetFile.OpenAsync(path!, Ct);
        IReadOnlyList<ParquetFinding> findings = await file.VerifyAsync(Ct);
        Assert.Contains(findings, finding => finding.ToString().Contains(structure, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChecksEveryRealFile()
    {
        Assert.SkipWhen(Root is null || !Directory.Exists(Root), "VORTICITY_PARQUET_DATA names no directory of Parquet files.");
        string? reportPath = Environment.GetEnvironmentVariable("VORTICITY_PARQUET_REPORT");
        using StreamWriter? report = reportPath is null ? null : new StreamWriter(reportPath, append: false);
        int checkedFiles = 0;
        int clean = 0;
        foreach (string path in Directory.EnumerateFiles(Root!, "*.parquet", SearchOption.AllDirectories))
        {
            List<ParquetFinding> findings;
            try
            {
                findings = await ParquetVerifier.VerifyAsync(path, VortexSession.Default, Ct);
            }
            catch (Exception e) when (e is ParquetUnsupportedException or ParquetFormatException)
            {
                continue;
            }

            checkedFiles++;
            clean += findings.Count == 0 ? 1 : 0;
            foreach (ParquetFinding finding in findings.Take(5))
            {
                report?.WriteLine($"{Path.GetRelativePath(Root!, path)}: {finding}");
            }
        }

        report?.WriteLine($"{checkedFiles} files checked, {clean} say only what is true");
        Assert.True(checkedFiles > 0);
    }
}
