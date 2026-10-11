// A group-by over a Parquet file of many row groups, on one lane and on several: the file's rows cut
// at its row groups, each lane reading whole groups of its own, against the same table as Vortex.
//
// Four million rows in sixteen row groups of 262 144: a key of a thousand labels, a price and a
// quantity, summed per label.
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Parquet;

namespace Vorticity.Benchmarks;

/// <summary>A group-by of four million rows read as Parquet and as Vortex, at a degree each.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Path)]
public partial class ParquetAggregateBenchmarks
{
    private const int Rows = 4 << 20;
    private const int GroupRows = 1 << 18;

    private string _path = string.Empty;

    /// <summary>The format: <c>vortex</c>, <c>parquet</c> under its default codec, ZSTD, or <c>parquet-none</c>, uncompressed, or <c>parquet-one-group</c>, its rows in one row group.</summary>
    [Params("vortex", "parquet", "parquet-none", "parquet-one-group")]
    public string Format { get; set; } = "parquet";

    /// <summary>The lanes.</summary>
    [Params(1, 4, 8)]
    public int Degree { get; set; } = 1;

    [VortexRecord]
    public partial record struct Sale(string Label, double Price, long Quantity);

    [VortexRecord]
    public partial record struct LabelSum(string Label, double Price, long Quantity);

    [GlobalSetup]
    public void Setup()
    {
        _path = Path.Combine(Path.GetTempPath(), $"vorticity-aggregate-{Format}-{Environment.ProcessId}.{(Format == "vortex" ? "vortex" : "parquet")}");
        Random random = new(23);
        string[] labels = new string[1_000];
        for (int i = 0; i < labels.Length; i++)
        {
            labels[i] = $"label-{i:D4}";
        }

        Sale[] rows = new Sale[Rows];
        for (int i = 0; i < Rows; i++)
        {
            rows[i] = new Sale(labels[random.Next(labels.Length)], Math.Round(random.NextDouble() * 100, 2), random.Next(1, 100));
        }

        WriteAsync(rows).AsTask().GetAwaiter().GetResult();
        Console.WriteLine($"// {Format}: {new FileInfo(_path).Length:N0} bytes");
    }

    [GlobalCleanup]
    public void Cleanup() => System.IO.File.Delete(_path);

    [Benchmark(Description = "group by label, sum price and quantity")]
    public async Task<long> GroupBy()
    {
        long groups = 0;
        if (Format == "vortex")
        {
            await using VortexFile file = await VortexFile.OpenAsync(_path, CancellationToken.None).ConfigureAwait(false);
            groups = await CountAsync(file.Scan<Sale>()).ConfigureAwait(false);
        }
        else
        {
            await using ParquetFile file = await ParquetFile.OpenAsync(_path, CancellationToken.None).ConfigureAwait(false);
            groups = await CountAsync(file.Scan<Sale>()).ConfigureAwait(false);
        }

        return groups;
    }

    private async ValueTask<long> CountAsync(Scan<Sale> scan)
    {
        Aggregation sums = scan.With(new ScanOptions { DegreeOfParallelism = Degree })
            .GroupBy(s => s.Label).Select(g => (g.Key, g.Sum(s => s.Price), g.Sum(s => s.Quantity)));
        long groups = 0;
        await foreach (Columns<LabelSum> batch in sums.As<LabelSum>())
        {
            groups += batch.RowCount;
        }

        return groups;
    }

    private async ValueTask WriteAsync(Sale[] rows)
    {
        if (Format == "vortex")
        {
            await using VortexFileWriter vortex = VortexSession.Default.CreateWriter<Sale>(_path);
            await vortex.WriteAsync<Sale>(rows, CancellationToken.None).ConfigureAwait(false);
            await vortex.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }

        await using ParquetFileWriter parquet = VortexSession.Default.CreateParquetWriter<Sale>(_path, new ParquetWriteOptions
        {
            RowGroupRows = Format == "parquet-one-group" ? Rows : GroupRows,
            Compression = Format == "parquet-none" ? ParquetCompression.Uncompressed : null,
        });
        await parquet.WriteAsync<Sale>(rows, CancellationToken.None).ConfigureAwait(false);
        await parquet.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
