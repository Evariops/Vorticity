// Audit AG-04: what an approximate distinct count of numbers costs a row, each value hashed as its bytes.
//
// 4,194,304 rows of a 64-bit key drawn among 2^40, a 32-bit one among 2^31, and a group of 64 values; the approximate
// distinct count of the key per group, at one lane, from memory: each value hashed (XXH3-64 of its bytes, seed 0) into
// its group's sketch.
using System;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.IO;

namespace Vorticity.Benchmarks.Audit;

/// <summary>An approximate distinct count of numbers per group.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditApproxDistinctBenchmarks
{
    private const int Rows = 1 << 22;

    private VortexFile _file = null!;

    /// <summary>The key's width.</summary>
    [Params(64, 32)]
    public int Bits { get; set; }

    /// <summary>The groups (audit AG-21): 64, whose sketches are soon dense, or a million of four values each, kept exactly.</summary>
    [Params(64, 1_048_576)]
    public int Groups { get; set; } = 64;

    /// <summary>A row: its group, and its keys of 64 and 32 bits.</summary>
    [VortexRecord]
    public partial record struct Keyed(int Group, long Wide, int Narrow);

    /// <summary>A group's estimate.</summary>
    [VortexRecord]
    public partial record struct Estimate(int Group, long Distinct);

    /// <summary>Writes the file and opens it from memory.</summary>
    [GlobalSetup]
    public void Setup()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vorticity-audit-approx-{Guid.NewGuid():N}.vortex");
        Random random = new Random(59);
        Keyed[] rows = new Keyed[Rows];
        for (int i = 0; i < Rows; i++)
        {
            rows[i] = new Keyed(random.Next(Groups), random.NextInt64(1L << 40), random.Next());
        }

        WriteAsync(path, rows).GetAwaiter().GetResult();
        byte[] bytes = System.IO.File.ReadAllBytes(path);
        System.IO.File.Delete(path);
        _file = VortexFile.OpenAsync(new MemorySegmentSource(bytes), VortexOpenOptions.Default).AsTask().GetAwaiter().GetResult();
        Console.WriteLine($"// {Distinct()} estimated over the groups");
    }

    /// <summary>Closes the file.</summary>
    [GlobalCleanup]
    public void Cleanup() => _file.DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>The estimates of every group, summed.</summary>
    [Benchmark]
    public long Distinct() => DistinctAsync().GetAwaiter().GetResult();

    private async Task<long> DistinctAsync()
    {
        Scan<Keyed> scan = _file.Scan<Keyed>().With(new ScanOptions { DegreeOfParallelism = 1 });
        Aggregation estimates = Bits == 64
            ? scan.GroupBy(r => r.Group).Select(g => (g.Key, g.ApproximateCountDistinct(r => r.Wide)))
            : scan.GroupBy(r => r.Group).Select(g => (g.Key, g.ApproximateCountDistinct(r => r.Narrow)));
        long sum = 0;
        await foreach (Columns<Estimate> batch in estimates.As<Estimate>())
        {
            sum += Vorticity.Bench.Scenarios.Consume.Typed(batch);
        }

        return sum;
    }

    private static async Task WriteAsync(string path, Keyed[] rows)
    {
        await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Keyed>(path);
        await writer.WriteAsync<Keyed>(rows, CancellationToken.None).ConfigureAwait(false);
        await writer.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
