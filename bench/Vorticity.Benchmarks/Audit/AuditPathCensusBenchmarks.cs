// Audit VA-02: what a census of a variant column's paths costs a row, its values whole, by a selection of its rows.
//
// 65,536 rows of attributes, 6 of 40 keys each, one row in ten null, read from a file in memory; half the rows selected.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.IO;
using Vorticity.Scanning;
using Vorticity.Types.Variants;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A census of a variant column's paths, its values whole, a batch at a time.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditPathCensusBenchmarks
{
    private const int Rows = 65_536;

    private readonly List<RecordBatch> _batches = [];
    private readonly List<ulong[]> _selections = [];
    private readonly ColumnFieldExpr _column = new ColumnFieldExpr(nameof(Line.Attributes));
    private readonly PathCensus _census = new PathCensus();
    private VortexFile _file = null!;

    /// <summary>A log line.</summary>
    [VortexRecord]
    public partial record struct Line(long Id, Variant? Attributes);

    /// <summary>Writes the file, reads its batches and makes their selections.</summary>
    [GlobalSetup]
    public void Setup()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-audit-census-{Guid.NewGuid():N}.vortex");
        try
        {
            Random random = new Random(47);
            Line[] rows = new Line[Rows];
            for (int i = 0; i < Rows; i++)
            {
                Dictionary<string, Variant> fields = [];
                while (fields.Count < 6)
                {
                    int key = random.Next(40);
                    fields[$"k{key:D2}"] = key % 2 == 0 ? Variant.From($"v{random.Next(100)}") : Variant.From(random.Next(1_000));
                }

                List<(string, Variant)> pairs = [];
                foreach (KeyValuePair<string, Variant> field in fields)
                {
                    pairs.Add((field.Key, field.Value));
                }

                rows[i] = new Line(i, random.Next(10) == 0 ? null : Variant.Object([.. pairs]));
            }

            WriteAsync(path, rows).GetAwaiter().GetResult();
            _file = VortexFile.OpenAsync(new MemorySegmentSource(System.IO.File.ReadAllBytes(path)), VortexOpenOptions.Default).AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            System.IO.File.Delete(path);
        }

        Random draw = new Random(9);
        foreach (RecordBatch batch in _file.ScanBuilder().WithDegreeOfParallelism(1).ExecuteAsync().ToBlockingEnumerable())
        {
            using (batch)
            {
                _batches.Add(RecordBatch.Own(batch.Arena, batch.RootIndex, batch.StartRow, null, null, default, 0));
            }

            ulong[] keep = new ulong[(_batches[^1].RowCount + 63) >> 6];
            for (int row = 0; row < _batches[^1].RowCount; row++)
            {
                if (draw.Next(2) == 0)
                {
                    keep[row >> 6] |= 1UL << (row & 63);
                }
            }

            _selections.Add(keep);
        }

        Console.WriteLine($"// {_batches.Count} batches; {Count()} rows counted");
    }

    /// <summary>Disposes the batches and the file.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (RecordBatch batch in _batches)
        {
            batch.Dispose();
        }

        _census.Release();
        _file.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Each batch's selected rows counted, by row of the batches.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public long Count()
    {
        for (int b = 0; b < _batches.Count; b++)
        {
            RecordBatch batch = _batches[b];
            int node = FilterEvaluator.Resolve(batch.Arena, batch.RootIndex, _column, batch.RowCount);
            _census.Add(PathCensus.Prepare(batch.Arena, node, batch.RowCount), _selections[b]);
        }

        return _census.Rows;
    }

    private static async Task WriteAsync(string path, Line[] rows)
    {
        await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Line>(path);
        await writer.WriteAsync<Line>(rows, CancellationToken.None).ConfigureAwait(false);
        await writer.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
