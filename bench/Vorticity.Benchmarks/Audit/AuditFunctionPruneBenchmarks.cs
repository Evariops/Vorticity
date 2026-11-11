// What a filter on a function of a column pays its zone maps, each decision asking the function's key.
//
// 4,194,304 rows of an instant a second apart (48 days) and a value, read from memory; the rows of one day counted, the
// filter on the instant truncated to a day, which the zone maps decide zone by zone through the function.
using System;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.IO;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A count filtered on a function of a column, pruned by its zones.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditFunctionPruneBenchmarks
{
    private const int Rows = 1 << 22;

    private static readonly DateTime Start = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    private VortexFile _file = null!;

    /// <summary>A row: its instant and its value.</summary>
    [VortexRecord]
    public partial record struct Timed([property: VortexColumn(TimeZone = "UTC")] DateTime At, long Value);

    /// <summary>Writes the file and opens it from memory.</summary>
    [GlobalSetup]
    public void Setup()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vorticity-audit-prune-{Guid.NewGuid():N}.vortex");
        Timed[] rows = new Timed[Rows];
        for (int i = 0; i < Rows; i++)
        {
            rows[i] = new Timed(Start.AddSeconds(i), i);
        }

        WriteAsync(path, rows).GetAwaiter().GetResult();
        byte[] bytes = System.IO.File.ReadAllBytes(path);
        System.IO.File.Delete(path);
        _file = VortexFile.OpenAsync(new MemorySegmentSource(bytes), VortexOpenOptions.Default).AsTask().GetAwaiter().GetResult();
        Console.WriteLine($"// {Count()} rows on the day");
    }

    /// <summary>Closes the file.</summary>
    [GlobalCleanup]
    public void Cleanup() => _file.DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>The rows of one day.</summary>
    [Benchmark]
    public long Count() => _file.Scan<Timed>().Where(r => r.At.Truncate(CalendarUnit.Day) == Start.AddDays(20))
        .CountAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>The rows of three days, an IN of the function, which the zone maps walk block by block.</summary>
    [Benchmark]
    public long CountIn()
    {
        DateTime[] days = [Start.AddDays(5), Start.AddDays(20), Start.AddDays(40)];
        return _file.Scan<Timed>().Where(r => r.At.Truncate(CalendarUnit.Day).In(days))
            .CountAsync().AsTask().GetAwaiter().GetResult();
    }

    private static async Task WriteAsync(string path, Timed[] rows)
    {
        await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Timed>(path);
        await writer.WriteAsync<Timed>(rows, CancellationToken.None).ConfigureAwait(false);
        await writer.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
