// Audit KE-03: what a scan in key order filtered by a long IN on the key costs, each literal's slice found by two rank
// searches of the key column, one literal after the other.
//
// A file of 4,194,304 rows whose `Key` rises (0, 2, 4, ...), from memory; an IN of 10, 1,000 or 10,000 keys drawn at
// random among the file's (half of them absent), the rows read in key order and counted.
using System;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Expressions;
using Vorticity.IO;
using Vorticity.Scanning;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A key-ordered scan of an IN on the key.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditKeyInBenchmarks
{
    private const int Rows = 1 << 22;

    private VortexFile _file = null!;
    private VortexExpr _filter = null!;

    /// <summary>The literals of the IN.</summary>
    [Params(10, 1_000, 10_000)]
    public int Literals { get; set; }

    /// <summary>A row: its key and its value.</summary>
    [VortexRecord]
    public partial record struct Row(long Key, long Value);

    /// <summary>Writes the file, opens it from memory and draws the IN.</summary>
    [GlobalSetup]
    public void Setup()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vorticity-audit-keyin-{Guid.NewGuid():N}.vortex");
        Row[] rows = new Row[Rows];
        for (int i = 0; i < Rows; i++)
        {
            rows[i] = new Row(2L * i, i);
        }

        WriteAsync(path, rows).GetAwaiter().GetResult();
        byte[] bytes = System.IO.File.ReadAllBytes(path);
        System.IO.File.Delete(path);
        _file = VortexFile.OpenAsync(new MemorySegmentSource(bytes), VortexOpenOptions.Default).AsTask().GetAwaiter().GetResult();
        Random random = new Random(1021);
        FilterLiteral[] keys = new FilterLiteral[Literals];
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i] = FilterLiteral.From(random.NextInt64(2L * Rows));
        }

        _filter = Expr.In(Expr.Field("Key"), keys);
        Console.WriteLine($"// {Read()} rows read");
    }

    /// <summary>Closes the file.</summary>
    [GlobalCleanup]
    public void Cleanup() => _file.DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>The rows read in key order.</summary>
    [Benchmark]
    public long Read() => ReadAsync().GetAwaiter().GetResult();

    private async Task<long> ReadAsync()
    {
        long read = 0;
        await foreach (RecordBatch batch in _file.ScanBuilder().InKeyOrder("Key").Where(_filter).ExecuteAsync().ConfigureAwait(false))
        {
            read += batch.RowCount;
        }

        return read;
    }

    private static async Task WriteAsync(string path, Row[] rows)
    {
        await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Row>(path);
        await writer.WriteAsync<Row>(rows, CancellationToken.None).ConfigureAwait(false);
        await writer.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
