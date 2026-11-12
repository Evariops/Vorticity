// What a distinct of many values costs on the core's path, whose appliers hand batches to one
// reader.
//
// A file of 4,194,304 rows of 64-bit keys drawn among 2^40 (no statistics bound them, so the core's path takes them),
// their distinct values read at eight lanes: the appliers copy each value into a batch as it enters its part's set,
// the batches handed to the reader through a channel; the core's sub-tables, the result, given back once delivered.
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

namespace Vorticity.Benchmarks.Audit;

/// <summary>The distinct values of a column of many, on the core's path.</summary>
/// <remarks>
/// No collection forced between iterations: two in a row, no question between them, sweep the process's shelf empty
/// (ArrayShelf's sweep), and each iteration of one or two questions would read a shelf a service under questions never
/// sees.
/// </remarks>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
[GcForce(false)]
public partial class AuditDistinctEmitBenchmarks
{
    private const int Rows = 1 << 22;

    private string _path = null!;
    private VortexFile _file = null!;

    /// <summary>A row: its key.</summary>
    [VortexRecord]
    public partial record struct Keyed(long Key);

    /// <summary>Writes the file and opens it.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _path = Path.Combine(Path.GetTempPath(), $"vorticity-audit-emit-{Guid.NewGuid():N}.vortex");
        Random random = new Random(29);
        Keyed[] rows = new Keyed[Rows];
        for (int i = 0; i < Rows; i++)
        {
            rows[i] = new Keyed(random.NextInt64(1L << 40));
        }

        WriteAsync(rows).GetAwaiter().GetResult();
        _file = VortexFile.OpenAsync(_path).AsTask().GetAwaiter().GetResult();
        Console.WriteLine($"// {Distinct()} values");
    }

    /// <summary>Closes and deletes the file.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _file.DisposeAsync().AsTask().GetAwaiter().GetResult();
        System.IO.File.Delete(_path);
    }

    /// <summary>The distinct keys, read.</summary>
    [Benchmark]
    public long Distinct() => DistinctAsync().GetAwaiter().GetResult();

    private async Task<long> DistinctAsync()
    {
        long sum = 0;
        await foreach (long key in _file.Scan<Keyed>().With(new ScanOptions { DegreeOfParallelism = 8 }).Select(r => r.Key).Distinct())
        {
            sum += key;
        }

        return sum;
    }

    private async Task WriteAsync(Keyed[] rows)
    {
        await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Keyed>(_path);
        await writer.WriteAsync<Keyed>(rows, CancellationToken.None).ConfigureAwait(false);
        await writer.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
