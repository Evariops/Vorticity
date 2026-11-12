// What a census of a shredded variant column's paths costs a batch of an edge granule, read as its shredding
// holds it, by the selections words it is walked under.
//
// 65,536 rows of attributes, each 12 of 200 keys shredded as strings or integers and 2 of 50 left in the residual, read
// from a file in memory as their shredding holds them; half the rows kept, half left, each batch's rows walked once.
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

/// <summary>A census of a shredded column's paths, a batch at a time.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditApartCensusBenchmarks
{
    private const int Rows = 65_536;
    private const int Shredded = 200;

    private readonly List<RecordBatch> _batches = [];
    private readonly List<(ulong[] Keep, ulong[] Other)> _selections = [];
    private readonly ColumnFieldExpr _column = new ColumnFieldExpr(nameof(Line.Attributes));
    private readonly EdgeCounts _counts = new EdgeCounts();
    private VortexFile _file = null!;

    /// <summary>A log line.</summary>
    [VortexRecord]
    public partial record struct Line(long Id, Variant Attributes);

    /// <summary>Writes the file, reads its batches apart and makes their selections.</summary>
    [GlobalSetup]
    public void Setup()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-audit-apart-{Guid.NewGuid():N}.vortex");
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

        Random draw = new Random(7);
        foreach (RecordBatch batch in _file.ScanBuilder().WithDegreeOfParallelism(1).ApartAsync(null).ToBlockingEnumerable())
        {
            using (batch)
            {
                _batches.Add(RecordBatch.Own(batch.Arena, batch.RootIndex, batch.StartRow, null, null, default, 0));
            }

            int words = (_batches[^1].RowCount + 63) >> 6;
            ulong[] keep = new ulong[words];
            ulong[] other = new ulong[words];
            for (int row = 0; row < _batches[^1].RowCount; row++)
            {
                ref ulong side = ref draw.Next(2) == 0 ? ref keep[row >> 6] : ref other[row >> 6];
                side |= 1UL << (row & 63);
            }

            _selections.Add((keep, other));
        }

        Console.WriteLine($"// {_batches.Count} batches; {Count()} paths");
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

    /// <summary>Each batch's paths counted, as an edge's census of a question counts them.</summary>
    [Benchmark]
    public int Count()
    {
        for (int b = 0; b < _batches.Count; b++)
        {
            RecordBatch batch = _batches[b];
            int node = FilterEvaluator.Resolve(batch.Arena, batch.RootIndex, _column, batch.RowCount);
            ApartCensus.Add(batch.Arena, node, batch.RowCount, _selections[b].Keep, _selections[b].Other, _counts);
        }

        return _counts.Count;
    }

    private static async Task WriteAsync(string path, Line[] rows, VortexWriteOptions options)
    {
        await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Line>(path, options);
        await writer.WriteAsync<Line>(rows, CancellationToken.None).ConfigureAwait(false);
        await writer.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
