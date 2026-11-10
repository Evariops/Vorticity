// Audit DS-53: what the counts of the rows a delete takes cost a row, on an object that keeps counts.
//
// 65,536 calls (a service among 40, a status, bytes, a latency) read from a file in memory, in the batches its scan gives,
// owned. The service is counted, with the bytes summed as integers and the latency as floats, as a dataset counts it; the
// rows a delete takes are every row, or one in sixteen, through each batch's selection.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Aggregating;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.Scanning;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Audit;

/// <summary>The counts of the rows a delete takes.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditDeletedCountsBenchmarks
{
    private const int Rows = 65_536;

    private readonly List<RecordBatch> _batches = [];
    private readonly List<ulong[]> _selections = [];
    private VortexFile _file = null!;
    private DType _schema;
    private (int Field, ColumnCounts Counts)[] _counted = [];

    /// <summary>One row in how many the delete takes.</summary>
    [Params(1, 16)]
    public int Every { get; set; }

    /// <summary>A call.</summary>
    [VortexRecord]
    public partial record struct Call(string Service, short Status, long Bytes, double LatencyMs);

    /// <summary>Writes the file, reads its batches and makes their selections.</summary>
    [GlobalSetup]
    public void Setup()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-audit-deleted-{Guid.NewGuid():N}.vortex");
        try
        {
            Random random = new Random(41);
            Call[] rows = new Call[Rows];
            for (int i = 0; i < Rows; i++)
            {
                rows[i] = new Call($"svc-{random.Next(40):D2}", (short)(random.Next(10) == 0 ? 500 : 200), random.NextInt64(100, 100_000), random.NextDouble() * 100);
            }

            WriteAsync(path, rows).GetAwaiter().GetResult();
            _file = VortexFile.OpenAsync(new MemorySegmentSource(System.IO.File.ReadAllBytes(path)), VortexOpenOptions.Default).AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            System.IO.File.Delete(path);
        }

        _schema = _file.DType;
        foreach (RecordBatch batch in _file.ScanBuilder().WithDegreeOfParallelism(1).ExecuteAsync().ToBlockingEnumerable())
        {
            using (batch)
            {
                _batches.Add(RecordBatch.Own(batch.Arena, batch.RootIndex, batch.StartRow, null, null, default, 0));
            }
        }

        foreach (RecordBatch batch in _batches)
        {
            ulong[] words = new ulong[(batch.RowCount + 63) >> 6];
            for (int row = 0; row < batch.RowCount; row++)
            {
                if (row % Every == 0)
                {
                    words[row >> 6] |= 1UL << (row & 63);
                }
            }

            _selections.Add(words);
        }

        _counted = [(_schema.IndexOfField(nameof(Call.Service)), new ColumnCounts(
            [_schema.IndexOfField(nameof(Call.Bytes)), _schema.IndexOfField(nameof(Call.LatencyMs))], [MeasureKind.Signed, MeasureKind.Float], [], []))];
        Console.WriteLine($"// {_batches.Count} batches; {Count()} entries counted");
    }

    /// <summary>Disposes the batches and the file.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (RecordBatch batch in _batches)
        {
            batch.Dispose();
        }

        _file.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>The rows taken counted, by row read.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public int Count()
    {
        DeletedCounts.Builder builder = new DeletedCounts.Builder(_schema, _counted);
        for (int b = 0; b < _batches.Count; b++)
        {
            builder.Add(_batches[b], _selections[b]);
        }

        return builder.Finish().CountsOf(_counted[0].Field)!.Groups.Length;
    }

    private static async Task WriteAsync(string path, Call[] rows)
    {
        await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Call>(path);
        await writer.WriteAsync<Call>(rows, CancellationToken.None).ConfigureAwait(false);
        await writer.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
