// What a Parquet chunk's metadata costs decoded from the footer, and a question that asks for it, by group and
// column, several times.
//
// 524,288 rows of eight columns in 64 row groups of 8,192. `Chunks` decodes every chunk's metadata once; `Count` counts
// the rows of a filter on two columns that their statistics leave to read in every group.
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Parquet;

namespace Vorticity.Benchmarks.Audit;

/// <summary>Chunk metadata decoded, and a question that reads it.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditParquetChunkBenchmarks
{
    private const int Rows = 1 << 19;
    private const int GroupRows = 8_192;

    private string _path = string.Empty;
    private ParquetFile _file = null!;

    /// <summary>A row of eight columns.</summary>
    [VortexRecord]
    public partial record struct Wide(long A, long B, int C, int D, double E, double F, string G, string H);

    /// <summary>Writes the file and opens it.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _path = Path.Combine(Path.GetTempPath(), $"vorticity-audit-chunks-{Guid.NewGuid():N}.parquet");
        Random random = new Random(59);
        Wide[] rows = new Wide[Rows];
        for (int i = 0; i < Rows; i++)
        {
            rows[i] = new Wide(random.NextInt64(1_000_000), random.Next(), random.Next(100), random.Next(), random.NextDouble(), random.NextDouble(), $"g{random.Next(50)}", $"h{random.Next(5_000)}");
        }

        WriteAsync(rows).GetAwaiter().GetResult();
        _file = ParquetFile.OpenAsync(_path, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        Console.WriteLine($"// {_file.Footer.RowGroups.Length} row groups; {Chunks()} chunks; {Count().GetAwaiter().GetResult()} rows kept");
    }

    /// <summary>Closes and deletes the file.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _file.DisposeAsync().AsTask().GetAwaiter().GetResult();
        System.IO.File.Delete(_path);
    }

    /// <summary>Every chunk's metadata decoded once.</summary>
    [Benchmark]
    public long Chunks()
    {
        long sum = 0;
        for (int g = 0; g < _file.Footer.RowGroups.Length; g++)
        {
            for (int c = 0; c < 8; c++)
            {
                sum += _file.Footer.Chunk(g, c).TotalUncompressedSize;
            }
        }

        return sum;
    }

    /// <summary>The rows of a filter on two columns, counted.</summary>
    [Benchmark]
    public async Task<long> Count() =>
        await _file.Scan<Wide>().Where(r => r.A < 500_000 & r.C == 7).CountAsync(CancellationToken.None).ConfigureAwait(false);

    private async Task WriteAsync(Wide[] rows)
    {
        await using ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter<Wide>(_path, new ParquetWriteOptions { RowGroupRows = GroupRows });
        await writer.WriteAsync<Wide>(rows, CancellationToken.None).ConfigureAwait(false);
        await writer.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
