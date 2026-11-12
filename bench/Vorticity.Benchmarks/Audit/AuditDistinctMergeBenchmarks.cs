// What an exact distinct count of many groups costs to merge across lanes.
//
// A file of 4,194,304 rows, a `Key` drawn among 1,048,576 values and a `Value` among 64, grouped by key and its values
// counted distinct, at two lanes and at eight: each lane's pairs of a group and a value, made distinct across the lanes in parts
// side by side. The groups are read. Skewed, three rows in four are of one key, their values drawn among 2^24: that group
// holds about three pairs in four, as a dominant customer or an "unknown" value would.
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

namespace Vorticity.Benchmarks.Audit;

/// <summary>An exact distinct count by a million groups, at two lanes and at eight.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditDistinctMergeBenchmarks
{
    private const int Rows = 1 << 22;
    private const int Groups = 1 << 20;

    /// <summary>The lanes: two merge their partitions in series, eight in parts.</summary>
    [Params(2, 8)]
    public int Degree { get; set; } = 8;

    /// <summary>Whether one key holds three rows in four, of values drawn among 2^24.</summary>
    [Params(false, true)]
    public bool Skewed { get; set; }

    private string _path = null!;
    private VortexFile _file = null!;

    /// <summary>A row: its key and its value.</summary>
    [VortexRecord]
    public partial record struct Pair(long Key, long Value, string Label);

    /// <summary>Writes the file and opens it.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _path = Path.Combine(Path.GetTempPath(), $"vorticity-audit-distinct-{Guid.NewGuid():N}.vortex");
        Random random = new Random(17);
        Pair[] rows = new Pair[Rows];
        for (int i = 0; i < Rows; i++)
        {
            if (Skewed && random.Next(4) != 0)
            {
                int wide = random.Next(1 << 24);
                rows[i] = new Pair(0, wide, $"label-{wide:D8}");
                continue;
            }

            int value = random.Next(64);
            rows[i] = new Pair(random.Next(Groups), value, $"label-{value:D3}");
        }

        WriteAsync(rows).GetAwaiter().GetResult();
        _file = VortexFile.OpenAsync(_path).AsTask().GetAwaiter().GetResult();
        Console.WriteLine($"// {Count()} groups");
    }

    /// <summary>Closes and deletes the file.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _file.DisposeAsync().AsTask().GetAwaiter().GetResult();
        System.IO.File.Delete(_path);
    }

    /// <summary>Each key's distinct values, counted.</summary>
    [Benchmark]
    public long Count() => CountAsync("count_distinct(Value)").GetAwaiter().GetResult();

    /// <summary>Each key's distinct labels, text, counted.</summary>
    [Benchmark]
    public long CountText() => CountAsync("count_distinct(Label)").GetAwaiter().GetResult();

    private async Task<long> CountAsync(string aggregate)
    {
        long read = 0;
        Scan scan = _file.Scan().With(new ScanOptions { DegreeOfParallelism = Degree });
        await foreach (RecordBatch batch in scan.GroupBy(["Key"]).Select([aggregate]).ToBatchesAsync(CancellationToken.None).ConfigureAwait(false))
        {
            using (batch)
            {
                read += Vorticity.Bench.Scenarios.Consume.Batch(batch);
            }
        }

        return read;
    }

    private async Task WriteAsync(Pair[] rows)
    {
        await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Pair>(_path);
        await writer.WriteAsync<Pair>(rows, CancellationToken.None).ConfigureAwait(false);
        await writer.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
