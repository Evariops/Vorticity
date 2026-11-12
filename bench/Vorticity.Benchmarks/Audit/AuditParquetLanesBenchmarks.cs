// A scan of every column of a Parquet file whose pages the scan's lanes decompress ahead of the read: 655,360 rows in four
// row groups, pages of 16,384 rows, an integer, a nullable double and three columns of text that seldom repeats, two of
// them PLAIN, under zstd (v2 and v1 pages) and snappy, on 4 and 16 lanes. A read comes to a page its lane is still
// decompressing at five to eleven of a scan's 105 to 152 pages ahead: what it does then is what this measures.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;
using Vorticity.Parquet;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A Parquet file's columns scanned whole, their pages decompressed ahead on the scan's lanes.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditParquetLanesBenchmarks
{
    private const int Rows = 655_360;
    private string _path = string.Empty;
    private VortexSession? _session;

    /// <summary>The codec, and the version of the data pages.</summary>
    [Params("zstd-v2", "zstd-v1", "snappy-v2")]
    public string Codec { get; set; } = "zstd-v2";

    /// <summary>The scan's degree: its lanes.</summary>
    [Params(4, 16)]
    public int Lanes { get; set; }

    /// <summary>Writes the file and opens a session of <see cref="Lanes"/> lanes.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _path = Path.Combine(Path.GetTempPath(), $"parquet-lanes-{Environment.ProcessId}.parquet");
        WriteAsync().AsTask().GetAwaiter().GetResult();
        _session = VortexSession.Create(options => options.MaxDegreeOfParallelism = Lanes);
    }

    /// <summary>Closes the session and removes the file.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _session!.DisposeAsync().AsTask().GetAwaiter().GetResult();
        System.IO.File.Delete(_path);
    }

    /// <summary>Every row of every column, each value read.</summary>
    [Benchmark]
    public async Task<long> Scan()
    {
        long rows = 0;
        await using ParquetFile file = await _session!.OpenParquetAsync(_path, null, CancellationToken.None).ConfigureAwait(false);
        await foreach (BatchView batch in file.Scan())
        {
            rows += Vorticity.Bench.Scenarios.Consume.View(batch);
        }

        return rows;
    }

    private async ValueTask WriteAsync()
    {
        ParquetWriteOptions options = new()
        {
            Compression = Codec.StartsWith("zstd", StringComparison.Ordinal) ? ParquetCompression.Zstd : ParquetCompression.Snappy,
            DataPageVersion = Codec.EndsWith("v1", StringComparison.Ordinal) ? DataPageVersion.V1 : DataPageVersion.V2,
            BlockRows = 16_384,
            RowGroupRows = Rows / 4,
            Hints = new Dictionary<string, ParquetEncodingHint> { ["note"] = ParquetEncodingHint.Plain, ["line"] = ParquetEncodingHint.Plain },
        };
        VortexSchema schema =
        [
            ("id", VortexType.Int64),
            ("price", VortexType.Float64.Nullable),
            ("payload", VortexType.Utf8),
            ("note", VortexType.Utf8.Nullable),
            ("line", VortexType.Utf8),
        ];
        await using ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter(_path, schema, options);
        ColumnsBuilder builder = writer.Builder();
        for (int row = 0; row < Rows; row++)
        {
            builder.Column<long>(0).Append(row);
            if (row % 7 == 0)
            {
                builder.Column<double?>(1).AppendNull();
            }
            else
            {
                builder.Column<double?>(1).Append((row * 7919 % 10_007) / 4.0);
            }

            builder.Column<string>(2).Append(string.Create(CultureInfo.InvariantCulture, $"payload {row * 2_654_435_761L % 1_000_003:D7} of the row, long enough to fill a page"));
            if (row % 13 == 0)
            {
                builder.Column<string?>(3).AppendNull();
            }
            else
            {
                builder.Column<string?>(3).Append(string.Create(CultureInfo.InvariantCulture, $"note {row * 40_503L % 1_000_033:D7}, {(row % 3 == 0 ? "short" : "a little longer than twelve bytes")}"));
            }

            builder.Column<string>(4).Append(row % 4 == 0 ? "tiny" : string.Create(CultureInfo.InvariantCulture, $"line {row * 69_069L % 999_983:D6} of the file, on its own"));
        }

        await writer.WriteAsync(builder, CancellationToken.None).ConfigureAwait(false);
        await writer.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
