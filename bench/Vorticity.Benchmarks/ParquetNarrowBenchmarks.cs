// A scan of the Parquet columns whose values are not stored at their width: INT(8) and INT(16)
// annotations on INT32, narrowed under a range check, and decimals of more than eighteen digits as
// big-endian fixed-length byte arrays, sign-extended into little-endian storage.
//
// Four million rows: a byte, a short, and a decimal(28, 4) of up to fourteen digits.
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Parquet;

namespace Vorticity.Benchmarks;

/// <summary>Narrowed integers and wide decimals read from a Parquet file, uncompressed so that the decode is what is timed.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Path)]
public class ParquetNarrowBenchmarks
{
    private const int Rows = 4 << 20;

    private string _path = string.Empty;

    /// <summary>The column scanned.</summary>
    [Params("byte", "short", "decimal")]
    public string Column { get; set; } = "byte";

    [GlobalSetup]
    public void Setup()
    {
        _path = Path.Combine(Path.GetTempPath(), $"vorticity-narrow-{Environment.ProcessId}.parquet");
        VortexSchema schema = [("byte", VortexType.Int8), ("short", VortexType.Int16), ("decimal", VortexType.Decimal(28, 4))];
        ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter(_path, schema, new ParquetWriteOptions
        {
            Compression = ParquetCompression.Uncompressed,
            Hints = new System.Collections.Generic.Dictionary<string, ParquetEncodingHint> { ["byte"] = ParquetEncodingHint.Plain, ["short"] = ParquetEncodingHint.Plain, ["decimal"] = ParquetEncodingHint.Plain },
        });
        ColumnsBuilder builder = writer.Builder();
        Random random = new(11);
        for (int i = 0; i < Rows; i++)
        {
            builder.Column<sbyte>(0).Append((sbyte)random.Next(-128, 128));
            builder.Column<short>(1).Append((short)random.Next(-32_768, 32_768));
            builder.Column<decimal>(2).Append(Math.Round((decimal)((random.NextDouble() - 0.5) * 2e10), 4));
        }

        writer.WriteAsync(builder, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        writer.CompleteAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        writer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Console.WriteLine($"// {new FileInfo(_path).Length:N0} bytes");
    }

    [GlobalCleanup]
    public void Cleanup() => System.IO.File.Delete(_path);

    [Benchmark(Description = "scan the column")]
    public async Task<long> Scan()
    {
        await using ParquetFile file = await ParquetFile.OpenAsync(_path, CancellationToken.None).ConfigureAwait(false);
        long rows = 0;
        await foreach (BatchView batch in file.Scan(Column).WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }
}
