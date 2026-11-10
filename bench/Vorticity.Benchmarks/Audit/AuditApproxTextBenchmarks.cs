// Audit AG-03: what an approximate distinct count of a text column a dictionary holds costs a row, per group.
//
// 4,194,304 rows of a text among 1,000 of 20 to 30 bytes, written as a dictionary, and a group of 64 values; the
// approximate distinct count of the text per group, at one lane, from memory: each row's text hashed into its group's
// sketch, or each entry of a batch's dictionary once.
using System;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.IO;

namespace Vorticity.Benchmarks.Audit;

/// <summary>An approximate distinct count of a dictionary's text per group.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditApproxTextBenchmarks
{
    private const int Rows = 1 << 22;

    private VortexFile _file = null!;

    /// <summary>A row: its group and its text.</summary>
    [VortexRecord]
    public partial record struct Named(int Group, string Name);

    /// <summary>A group's estimate.</summary>
    [VortexRecord]
    public partial record struct Estimate(int Group, long Distinct);

    /// <summary>Writes the file and opens it from memory.</summary>
    [GlobalSetup]
    public void Setup()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vorticity-audit-approx-text-{Guid.NewGuid():N}.vortex");
        Random random = new Random(71);
        string[] names = new string[1_000];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = $"service-{i:D4}-{new string((char)('a' + (i % 26)), 10 + (i % 10))}";
        }

        Named[] rows = new Named[Rows];
        for (int i = 0; i < Rows; i++)
        {
            rows[i] = new Named(random.Next(64), names[random.Next(names.Length)]);
        }

        WriteAsync(path, rows).GetAwaiter().GetResult();
        byte[] bytes = System.IO.File.ReadAllBytes(path);
        System.IO.File.Delete(path);
        _file = VortexFile.OpenAsync(new MemorySegmentSource(bytes), VortexOpenOptions.Default).AsTask().GetAwaiter().GetResult();
        Console.WriteLine($"// {bytes.Length} bytes; {Distinct()} read");
    }

    /// <summary>Closes the file.</summary>
    [GlobalCleanup]
    public void Cleanup() => _file.DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>The estimates of every group, read.</summary>
    [Benchmark]
    public long Distinct() => DistinctAsync().GetAwaiter().GetResult();

    private async Task<long> DistinctAsync()
    {
        long sum = 0;
        await foreach (Columns<Estimate> batch in _file.Scan<Named>().With(new ScanOptions { DegreeOfParallelism = 1 })
            .GroupBy(r => r.Group).Select(g => (g.Key, g.ApproximateCountDistinct(r => r.Name))).As<Estimate>())
        {
            sum += Vorticity.Bench.Scenarios.Consume.Typed(batch);
        }

        return sum;
    }

    private static async Task WriteAsync(string path, Named[] rows)
    {
        await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Named>(path);
        await writer.WriteAsync<Named>(rows, CancellationToken.None).ConfigureAwait(false);
        await writer.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
