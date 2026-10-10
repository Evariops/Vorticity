// The first batch of a Parquet file read by positional reads, which do not read in place, and every
// batch: a row group of four million rows read in windows of batches, each while the window before it
// is decoded, against groups sixteen times smaller, each read in one request.
//
// Four million rows: an id, a random double and one of a thousand labels, 39 MB under ZSTD.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Parquet;

namespace Vorticity.Benchmarks;

/// <summary>The time to a Parquet file's first batch, and to its last, over row groups sixteen times apart.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Path)]
public class ParquetWindowBenchmarks
{
    private const int Rows = 4 << 20;

    private string _path = string.Empty;
    private VortexSession _session = null!;

    /// <summary>The rows of a row group: a quarter million, read whole, or every row, read in windows.</summary>
    [Params(1 << 18, Rows)]
    public int GroupRows { get; set; } = Rows;

    [GlobalSetup]
    public void Setup()
    {
        _path = Path.Combine(Path.GetTempPath(), $"vorticity-window-{GroupRows}-{Environment.ProcessId}.parquet");
        VortexSchema schema = [("id", VortexType.Int64), ("value", VortexType.Float64), ("label", VortexType.Utf8)];
        ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter(_path, schema, new ParquetWriteOptions { RowGroupRows = GroupRows });
        ColumnsBuilder builder = writer.Builder();
        Random random = new(5);
        for (int i = 0; i < Rows; i++)
        {
            builder.Column<long>(0).Append(i);
            builder.Column<double>(1).Append(random.NextDouble());
            builder.Column<string>(2).Append($"label-{random.Next(1_000)}");
        }

        writer.WriteAsync(builder, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        writer.CompleteAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        writer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _session = VortexSession.Create(options => options.MapFiles = false);
        Console.WriteLine($"// groups of {GroupRows} rows: {new FileInfo(_path).Length:N0} bytes");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        System.IO.File.Delete(_path);
    }

    [Benchmark(Description = "first batch")]
    public async Task<long> FirstBatch()
    {
        await using ParquetFile file = await _session.OpenParquetAsync(_path, null, CancellationToken.None).ConfigureAwait(false);
        await using IAsyncEnumerator<RecordBatch> batches = file.Scan().ToBatchesAsync(CancellationToken.None).GetAsyncEnumerator();
        await batches.MoveNextAsync().ConfigureAwait(false);
        long rows = batches.Current.RowCount;
        batches.Current.Dispose();
        return rows;
    }

    [Benchmark(Description = "every batch")]
    public async Task<long> EveryBatch()
    {
        await using ParquetFile file = await _session.OpenParquetAsync(_path, null, CancellationToken.None).ConfigureAwait(false);
        long rows = 0;
        await foreach (RecordBatch batch in file.Scan().ToBatchesAsync(CancellationToken.None).ConfigureAwait(false))
        {
            rows += batch.RowCount;
            batch.Dispose();
        }

        return rows;
    }
}
