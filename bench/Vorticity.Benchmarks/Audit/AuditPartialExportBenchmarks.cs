// Audit AG-21: what exporting an aggregation's partial costs, its rows of states written to a file in memory: a group by of
// 1,048,576 groups' counts and sums (a partial of some 40 MB), and of 64 groups'.
//
// 4,194,304 rows from memory, at one lane.
using System;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.IO;

namespace Vorticity.Benchmarks.Audit;

/// <summary>An aggregation's partial exported.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditPartialExportBenchmarks
{
    private const int Rows = 1 << 22;

    private VortexFile _file = null!;

    /// <summary>The groups.</summary>
    [Params(64, 1 << 20)]
    public int Groups { get; set; }

    /// <summary>A row: its key and its value.</summary>
    [VortexRecord]
    public partial record struct Row(long Key, long Value);

    /// <summary>Writes the file and opens it from memory.</summary>
    [GlobalSetup]
    public void Setup()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vorticity-audit-partial-{Guid.NewGuid():N}.vortex");
        Random random = new Random(1020);
        Row[] rows = new Row[Rows];
        for (int i = 0; i < Rows; i++)
        {
            rows[i] = new Row(random.Next(Groups) * 7_919L, random.NextInt64(1L << 30));
        }

        WriteAsync(path, rows).GetAwaiter().GetResult();
        byte[] bytes = System.IO.File.ReadAllBytes(path);
        System.IO.File.Delete(path);
        _file = VortexFile.OpenAsync(new MemorySegmentSource(bytes), VortexOpenOptions.Default).AsTask().GetAwaiter().GetResult();
        Console.WriteLine($"// a partial of {Export() / 1024.0:F0} KiB");
    }

    /// <summary>Closes the file.</summary>
    [GlobalCleanup]
    public void Cleanup() => _file.DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>The partial exported; its length.</summary>
    [Benchmark]
    public long Export() => ExportAsync().GetAwaiter().GetResult();

    private async Task<long> ExportAsync()
    {
        AggregationPartial partial = await _file.Scan<Row>().With(new ScanOptions { DegreeOfParallelism = 1 })
            .GroupBy(r => r.Key).Select(g => (g.Key, g.Count(), g.Sum(r => r.Value))).PartialAsync().ConfigureAwait(false);
        return partial.Bytes.Length;
    }

    private static async Task WriteAsync(string path, Row[] rows)
    {
        await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Row>(path);
        await writer.WriteAsync<Row>(rows, CancellationToken.None).ConfigureAwait(false);
        await writer.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
