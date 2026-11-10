// Audit VA-03: what a shredded variant column costs a scan that reads it put back together, and the share of it the copy of
// the rebuilt values into the batch's arena takes.
//
// 65,536 rows of attributes, each 12 of 200 keys shredded as strings or integers and 2 of 50 left in the residual, read from
// a file in memory, the column alone, each row's value bytes summed.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.IO;
using Vorticity.Scanning;
using Vorticity.Types.Variants;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A shredded variant column read put back together.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditShreddedReadBenchmarks
{
    private const int Rows = 65_536;
    private const int Shredded = 200;

    private readonly ColumnFieldExpr _column = new ColumnFieldExpr(nameof(Line.Attributes));
    private VortexFile _file = null!;

    /// <summary>A log line.</summary>
    [VortexRecord]
    public partial record struct Line(long Id, Variant Attributes);

    /// <summary>Writes the file and opens it in memory.</summary>
    [GlobalSetup]
    public void Setup()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-audit-shredded-{Guid.NewGuid():N}.vortex");
        try
        {
            Random random = new Random(43);
            Line[] rows = new Line[Rows];
            for (int i = 0; i < Rows; i++)
            {
                Dictionary<string, Variant> fields = [];
                while (fields.Count < 12)
                {
                    int key = random.Next(Shredded);
                    fields[$"k{key:D3}"] = key % 2 == 0 ? Variant.From($"v{random.Next(100)}") : Variant.From(random.Next(1_000));
                }

                fields[$"r{random.Next(50):D2}"] = Variant.From(random.Next(10));
                fields[$"r{50 + random.Next(50):D2}"] = Variant.From("x");
                List<(string, Variant)> pairs = [];
                foreach (KeyValuePair<string, Variant> field in fields)
                {
                    pairs.Add((field.Key, field.Value));
                }

                rows[i] = new Line(i, Variant.Object([.. pairs]));
            }

            (string, VariantShredding)[] shredding = new (string, VariantShredding)[Shredded];
            for (int key = 0; key < Shredded; key++)
            {
                shredding[key] = ($"k{key:D3}", VariantShredding.Primitive(key % 2 == 0 ? VariantClass.String : VariantClass.Integer));
            }

            VortexWriteOptions options = new VortexWriteOptions
            {
                ShreddedVariants = new Dictionary<string, VariantShredding> { [nameof(Line.Attributes)] = VariantShredding.Object(shredding) },
            };
            WriteAsync(path, rows, options).GetAwaiter().GetResult();
            _file = VortexFile.OpenAsync(new MemorySegmentSource(System.IO.File.ReadAllBytes(path)), VortexOpenOptions.Default).AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            System.IO.File.Delete(path);
        }

        Console.WriteLine($"// {Read()} value bytes");
    }

    /// <summary>Closes the file.</summary>
    [GlobalCleanup]
    public void Cleanup() => _file.DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>The column read put back together, each row's value bytes summed, by row.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public long Read()
    {
        long bytes = 0;
        foreach (RecordBatch batch in _file.ScanBuilder().WithDegreeOfParallelism(1).Project(nameof(Line.Attributes)).ExecuteAsync().ToBlockingEnumerable())
        {
            using (batch)
            {
                int node = FilterEvaluator.Resolve(batch.Arena, batch.RootIndex, _column, batch.RowCount);
                PathCensus.Counting counting = PathCensus.Prepare(batch.Arena, node, batch.RowCount);
                ViewValues values = new ViewValues(batch.Arena.GetNode(counting.Value));
                for (int row = 0; row < batch.RowCount; row++)
                {
                    bytes += values.At(row).Length;
                }
            }
        }

        return bytes;
    }

    private static async Task WriteAsync(string path, Line[] rows, VortexWriteOptions options)
    {
        await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Line>(path, options);
        await writer.WriteAsync<Line>(rows, CancellationToken.None).ConfigureAwait(false);
        await writer.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
