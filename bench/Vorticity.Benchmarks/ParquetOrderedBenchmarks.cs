// A scan in the order of a key, and a group by on it, over a Parquet file whose rows lie in that
// order: declared, through its row groups' sorting columns, the file streams as it lies; undeclared,
// the same rows are sorted, or grouped whole by hash.
//
// Four million rows in sixteen row groups of 262 144: a key on one to five rows each, a label and a
// price.
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Parquet;

namespace Vorticity.Benchmarks;

/// <summary>An ordered scan and a group by on a key a Parquet file declares its rows sorted on, or not.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Path)]
public partial class ParquetOrderedBenchmarks
{
    private const int Rows = 4 << 20;
    private const int GroupRows = 1 << 18;

    private string _path = string.Empty;

    private Sale[] _rows = [];

    /// <summary>Whether the row groups declare the key as their sorting column.</summary>
    [Params(true, false)]
    public bool Declared { get; set; } = true;

    [VortexRecord]
    public partial record struct Sale(long Key, string Label, double Price);

    [VortexRecord]
    public partial record struct KeySum(long Key, double Price);

    [GlobalSetup]
    public void Setup()
    {
        _path = Path.Combine(Path.GetTempPath(), $"vorticity-ordered-{Declared}-{Environment.ProcessId}.parquet");
        Random random = new(29);
        Sale[] rows = new Sale[Rows];
        long key = 0;
        for (int i = 0; i < Rows; i++)
        {
            key += random.Next(3) == 0 ? 1 : 0;
            rows[i] = new Sale(key, $"label-{random.Next(1_000):D4}", Math.Round(random.NextDouble() * 100, 2));
        }

        _rows = rows;
        WriteAsync(_path).AsTask().GetAwaiter().GetResult();
        Console.WriteLine($"// declared {Declared}: {new FileInfo(_path).Length:N0} bytes");
    }

    [GlobalCleanup]
    public void Cleanup() => System.IO.File.Delete(_path);

    [Benchmark(Description = "order by key, sum the prices")]
    public async Task<double> OrderBy()
    {
        await using ParquetFile file = await ParquetFile.OpenAsync(_path, CancellationToken.None).ConfigureAwait(false);
        double sum = 0;
        await foreach (Columns<Sale> batch in file.Scan<Sale>().OrderBy(s => s.Key))
        {
            foreach (double price in batch.Column<double>(2).Values)
            {
                sum += price;
            }
        }

        return sum;
    }

    [Benchmark(Description = "group by key, sum price")]
    public async Task<long> GroupBy()
    {
        await using ParquetFile file = await ParquetFile.OpenAsync(_path, CancellationToken.None).ConfigureAwait(false);
        Aggregation sums = file.Scan<Sale>().GroupBy(s => s.Key).Select(g => (g.Key, g.Sum(s => s.Price)));
        long groups = 0;
        await foreach (Columns<KeySum> batch in sums.As<KeySum>())
        {
            groups += batch.RowCount;
        }

        return groups;
    }

    [Benchmark(Description = "write, the order held to the key")]
    public async Task<long> Write()
    {
        string path = _path + ".write";
        await WriteAsync(path).ConfigureAwait(false);
        long length = new FileInfo(path).Length;
        System.IO.File.Delete(path);
        return length;
    }

    private async ValueTask WriteAsync(string path)
    {
        Sale[] rows = _rows;
        await using ParquetFileWriter parquet = VortexSession.Default.CreateParquetWriter<Sale>(path, new ParquetWriteOptions
        {
            RowGroupRows = GroupRows,
            SortingColumns = Declared ? [new ParquetSortingColumn("Key")] : null,
        });
        await parquet.WriteAsync<Sale>(rows, CancellationToken.None).ConfigureAwait(false);
        await parquet.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
