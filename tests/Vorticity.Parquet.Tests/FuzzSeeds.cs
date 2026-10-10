using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// The seeds the fuzzer's Parquet target mutates beside the standard's suite: this writer's files,
/// every page version, every codec, and per column each encoding it writes forced, ALP among them,
/// under row groups, pages, page index, Bloom filters, checksums and sorting columns.
/// </summary>
/// <remarks>Written only where <c>VX_FUZZ_SEEDS</c> names the directory to write them into.</remarks>
public sealed partial class FuzzSeeds
{
    [VortexRecord]
    public partial record struct Row(long Id, int? Small, double Measure, float? Ratio, string Label, string? Note, bool Flag, decimal Price, int[] Tags);

    [Fact]
    public async Task WritesTheSeeds()
    {
        string? root = Environment.GetEnvironmentVariable("VX_FUZZ_SEEDS");
        Assert.SkipWhen(root is null, "VX_FUZZ_SEEDS names no directory.");
        Directory.CreateDirectory(root!);
        CancellationToken ct = TestContext.Current.CancellationToken;
        Random random = new(97);
        Row[] rows = [.. Enumerable.Range(0, 6_000).Select(i => new Row(
            i * 3L,
            i % 5 == 0 ? null : random.Next(-1_000, 1_000),
            Math.Round(random.NextDouble() * 1_000, 2),
            i % 7 == 0 ? null : (float)random.NextDouble(),
            $"label-{random.Next(40)}",
            i % 3 == 0 ? null : $"https://example.org/{random.Next(10_000)}",
            i % 4 != 0,
            random.Next(-100_000, 100_000) / 100m,
            [.. Enumerable.Range(0, i % 4).Select(t => random.Next(100))]))];

        ParquetEncodingHint[] hints = [ParquetEncodingHint.Auto, ParquetEncodingHint.Plain, ParquetEncodingHint.Dictionary];
        ParquetCompression?[] codecs = [ParquetCompression.Uncompressed, ParquetCompression.Snappy, ParquetCompression.Zstd, ParquetCompression.Gzip, ParquetCompression.Lz4Raw, ParquetCompression.Brotli];
        int n = 0;
        foreach (DataPageVersion pages in (DataPageVersion[])[DataPageVersion.V1, DataPageVersion.V2])
        {
            foreach (ParquetCompression? codec in codecs)
            {
                foreach (ParquetEncodingHint hint in hints)
                {
                    // Under Auto, the encodings no choice of the writer's would take on these values.
                    Dictionary<string, ParquetEncodingHint> perColumn = new()
                    {
                        ["Id"] = hint == ParquetEncodingHint.Auto ? ParquetEncodingHint.DeltaBinaryPacked : hint,
                        ["Small"] = hint,
                        ["Measure"] = hint == ParquetEncodingHint.Auto ? ParquetEncodingHint.ByteStreamSplit : hint,
                        ["Label"] = hint == ParquetEncodingHint.Auto ? ParquetEncodingHint.DeltaByteArray : hint,
                        ["Note"] = hint == ParquetEncodingHint.Auto ? ParquetEncodingHint.DeltaLengthByteArray : hint,
                    };
                    string path = Path.Combine(root!, $"seed-{n++:D3}-{pages}-{codec}-{hint}.parquet");
                    await using ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter<Row>(path, new ParquetWriteOptions
                    {
                        DataPageVersion = pages,
                        Compression = codec,
                        Hints = perColumn,
                        BlockRows = 1_024,
                        RowGroupRows = 2_048,
                        PageBytes = 4 << 10,
                        Alp = hint == ParquetEncodingHint.Auto,
                        WriteChecksums = n % 2 == 0,
                        BloomFilters = new Dictionary<string, double> { ["Id"] = 0.05, ["Label"] = 0.05 },
                        SortingColumns = [new ParquetSortingColumn("Id")],
                    });
                    await writer.WriteAsync<Row>(rows, ct);
                    await writer.CompleteAsync(ct);
                }
            }
        }

        Assert.Equal(36, n);
    }
}
