// What reading a column of lists costs a row, its boundaries turned into each row's start and size.
//
// A file of 1,048,576 rows of a list of zero to six longs, from memory, at one lane; every row's list read and its
// values counted.
using System;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.IO;
using Vorticity.Scanning;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A column of lists read.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditListReadBenchmarks
{
    private const int Rows = 1 << 20;

    private VortexFile _file = null!;

    /// <summary>A row: its id and its list.</summary>
    [VortexRecord]
    public partial record struct Row(long Id, long[] Values);

    /// <summary>Writes the file and opens it from memory.</summary>
    [GlobalSetup]
    public void Setup()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vorticity-audit-lists-{Guid.NewGuid():N}.vortex");
        Random random = new Random(1023);
        Row[] rows = new Row[Rows];
        for (int i = 0; i < Rows; i++)
        {
            long[] values = new long[random.Next(7)];
            for (int v = 0; v < values.Length; v++)
            {
                values[v] = random.NextInt64(1_000_000);
            }

            rows[i] = new Row(i, values);
        }

        WriteAsync(path, rows).GetAwaiter().GetResult();
        byte[] bytes = System.IO.File.ReadAllBytes(path);
        System.IO.File.Delete(path);
        _file = VortexFile.OpenAsync(new MemorySegmentSource(bytes), VortexOpenOptions.Default).AsTask().GetAwaiter().GetResult();
        Console.WriteLine($"// {Read()} values");
    }

    /// <summary>Closes the file.</summary>
    [GlobalCleanup]
    public void Cleanup() => _file.DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>The lists read, their values counted.</summary>
    [Benchmark]
    public long Read() => ReadAsync().GetAwaiter().GetResult();

    private async Task<long> ReadAsync()
    {
        long values = 0;
        await foreach (RecordBatch batch in _file.ScanBuilder().WithDegreeOfParallelism(1).ExecuteAsync().ConfigureAwait(false))
        {
            values += batch.RowCount;
        }

        return values;
    }

    private static async Task WriteAsync(string path, Row[] rows)
    {
        await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Row>(path);
        await writer.WriteAsync<Row>(rows, CancellationToken.None).ConfigureAwait(false);
        await writer.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
