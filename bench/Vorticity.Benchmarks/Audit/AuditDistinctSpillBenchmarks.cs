// What an exact distinct count by key pays when its lanes spill: 2,097,152 rows, a key among 65,536 and a value among
// 2^24, grouped by key under a budget of 16 MiB at one lane, its tables written to the scratch and read back part by
// part. Dominant gives one key half the rows, each of a value of its own: its million pairs go whole to one part, which
// no part of the budget holds, merged in passes that each count a share of the values.
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

namespace Vorticity.Benchmarks.Audit;

/// <summary>An exact distinct count by key, spilled, its groups even or one of them holding half the values.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditDistinctSpillBenchmarks
{
    private const int Rows = 2 << 20;
    private const int Groups = 1 << 16;

    private string _path = null!;
    private string _scratch = null!;
    private VortexSession _session = null!;
    private VortexFile _file = null!;

    /// <summary>Whether one key holds half the rows, a value of its own each.</summary>
    [Params(false, true)]
    public bool Dominant { get; set; }

    /// <summary>A row: its key and its value.</summary>
    [VortexRecord]
    public partial record struct Pair(long Key, long Value);

    /// <summary>A group's count.</summary>
    [VortexRecord]
    public partial record struct Counted(long Key, long Distinct);

    /// <summary>Writes the file and opens it under a session of 16 MiB.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _path = Path.Combine(Path.GetTempPath(), $"vorticity-audit-distinct-spill-{Guid.NewGuid():N}.vortex");
        _scratch = Path.Combine(Path.GetTempPath(), $"vorticity-audit-distinct-spill-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratch);
        Random random = new Random(29);
        Pair[] rows = new Pair[Rows];
        for (int i = 0; i < Rows; i++)
        {
            rows[i] = Dominant && (i & 1) == 0 ? new Pair(7, i) : new Pair(random.Next(Groups), random.Next(1 << 24));
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

    /// <summary>Each key's distinct values, counted exactly: the groups read.</summary>
    [Benchmark]
    public long Count() => CountAsync().GetAwaiter().GetResult();

    private async Task<long> CountAsync()
    {
        long sum = 0;
        await foreach (Columns<Counted> batch in _file.Scan<Pair>().GroupBy(r => r.Key).Select(g => (g.Key, g.CountDistinct(r => r.Value)))
            .As<Counted>())
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
