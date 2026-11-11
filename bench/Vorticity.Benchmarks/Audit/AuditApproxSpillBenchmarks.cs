// What an approximate distinct count by many small groups pays when its lanes spill: 3,145,728 rows, a key among
// 1,048,576 drawn at random and a value among 8, so that each group holds its first hashes in line; grouped by key under a
// budget of 16 MiB, at one lane, its tables written to the scratch and read back.
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

namespace Vorticity.Benchmarks.Audit;

/// <summary>An approximate distinct count by a million groups of a few values, spilled.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditApproxSpillBenchmarks
{
    private const int Rows = 3 << 20;
    private const int Groups = 1 << 20;

    private string _path = null!;
    private string _scratch = null!;
    private VortexSession _session = null!;
    private VortexFile _file = null!;

    /// <summary>A row: its key and its value.</summary>
    [VortexRecord]
    public partial record struct Pair(long Key, long Value);

    /// <summary>A group's estimate.</summary>
    [VortexRecord]
    public partial record struct Estimate(long Key, long Distinct);

    /// <summary>Writes the file and opens it under a session of 16 MiB.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _path = Path.Combine(Path.GetTempPath(), $"vorticity-audit-approx-spill-{Guid.NewGuid():N}.vortex");
        _scratch = Path.Combine(Path.GetTempPath(), $"vorticity-audit-approx-spill-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratch);
        Random random = new Random(23);
        Pair[] rows = new Pair[Rows];
        for (int i = 0; i < Rows; i++)
        {
            rows[i] = new Pair(random.Next(Groups), random.Next(8));
        }

        WriteAsync(rows).GetAwaiter().GetResult();
        _session = VortexSession.Create(options =>
        {
            options.MaxDegreeOfParallelism = 1;
            options.MemoryBudget = new QueryMemoryBudget(16L << 20);
            options.ScratchDirectory = _scratch;
        });
        _file = _session.OpenAsync(_path).AsTask().GetAwaiter().GetResult();
        Console.WriteLine($"// {Count()} groups");
    }

    /// <summary>Closes the file and the session and deletes them.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _file.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        System.IO.File.Delete(_path);
        Directory.Delete(_scratch, recursive: true);
    }

    /// <summary>Each key's distinct values, counted approximately.</summary>
    [Benchmark]
    public long Count() => CountAsync().GetAwaiter().GetResult();

    private async Task<long> CountAsync()
    {
        long sum = 0;
        await foreach (Columns<Estimate> batch in _file.Scan<Pair>().GroupBy(r => r.Key).Select(g => (g.Key, g.ApproximateCountDistinct(r => r.Value)))
            .As<Estimate>())
        {
            sum += Vorticity.Bench.Scenarios.Consume.Typed(batch);
        }

        return sum;
    }

    private async Task WriteAsync(Pair[] rows)
    {
        await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Pair>(_path);
        await writer.WriteAsync<Pair>(rows, CancellationToken.None).ConfigureAwait(false);
        await writer.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
