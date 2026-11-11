// What ordering a million groups costs, against their aggregation alone.
//
// A file of 4,194,304 rows whose `Key` is drawn at random among 1,048,576 values is grouped by its key and each
// group counted: as it comes, ordered by its count descending (ties by the key, the groups' order), and its first 100
// so ordered. The groups are read (Consume.Typed).
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A group by of a million groups, unordered, ordered and its top.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditGroupOrderBenchmarks
{
    private const int Rows = 1 << 22;
    private const int Groups = 1 << 20;

    private string _path = null!;
    private VortexFile _file = null!;

    /// <summary>A row: its key.</summary>
    [VortexRecord]
    public partial record struct Keyed(long Key);

    /// <summary>A group: its key and its rows.</summary>
    [VortexRecord]
    public partial record struct KeyCount(long Key, long Count);

    /// <summary>Writes the file and opens it.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _path = Path.Combine(Path.GetTempPath(), $"vorticity-audit-order-{Guid.NewGuid():N}.vortex");
        Random random = new Random(11);
        Keyed[] rows = new Keyed[Rows];
        for (int i = 0; i < Rows; i++)
        {
            rows[i] = new Keyed(random.Next(Groups));
        }

        WriteAsync(rows).GetAwaiter().GetResult();
        _file = VortexFile.OpenAsync(_path).AsTask().GetAwaiter().GetResult();
        Console.WriteLine($"// unordered {Unordered()}, ordered {Ordered()}, top {Top()}");
    }

    /// <summary>Closes and deletes the file.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _file.DisposeAsync().AsTask().GetAwaiter().GetResult();
        System.IO.File.Delete(_path);
    }

    /// <summary>The groups as they come.</summary>
    [Benchmark(Baseline = true)]
    public long Unordered() => ReadAsync(_file.Scan<Keyed>().GroupBy(r => r.Key).Select(g => (g.Key, g.Count())).As<KeyCount>()).GetAwaiter().GetResult();

    /// <summary>The groups by their count, descending.</summary>
    [Benchmark]
    public long Ordered() => ReadAsync(_file.Scan<Keyed>().GroupBy(r => r.Key).OrderByDescending(g => g.Count())
        .Select(g => (g.Key, g.Count())).As<KeyCount>()).GetAwaiter().GetResult();

    /// <summary>The 100 groups of the most rows.</summary>
    [Benchmark]
    public long Top() => ReadAsync(_file.Scan<Keyed>().GroupBy(r => r.Key).OrderByDescending(g => g.Count()).Take(100)
        .Select(g => (g.Key, g.Count())).As<KeyCount>()).GetAwaiter().GetResult();

    private static async Task<long> ReadAsync(Scan<KeyCount> groups)
    {
        long read = 0;
        await foreach (Columns<KeyCount> batch in groups)
        {
            read += Vorticity.Bench.Scenarios.Consume.Typed(batch);
        }

        return read;
    }

    private async Task WriteAsync(Keyed[] rows)
    {
        await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Keyed>(_path);
        await writer.WriteAsync<Keyed>(rows, CancellationToken.None).ConfigureAwait(false);
        await writer.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
