// What a filtered Count, Any and Max over one large file cost, at one lane and at several.
//
// A file of 4,194,304 rows (sorted `key`, a pseudo-random `measure` in [0, 1)) is written to a temporary
// path and opened. Every question carries a filter on `measure` that no zone map settles, so every split
// is read, decoded and judged: the count, the existence of a value absent from the file, and the largest
// `key` among the rows kept.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Expressions;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks.Audit;

/// <summary>Filtered Count, Any and Max over one file, against the scan's degree of parallelism.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditTerminalCountBenchmarks
{
    private const int Rows = 1 << 22;

    /// <summary>The scan's degree of parallelism.</summary>
    [Params(1, 2, 4, 8)]
    public int Degree { get; set; }

    private string _path = null!;
    private VortexFile _file = null!;
    private VortexExpr _low = null!;
    private VortexExpr _none = null!;

    /// <summary>Writes the file and opens it.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _path = Path.Combine(Path.GetTempPath(), $"vorticity-audit-terminal-{Guid.NewGuid():N}.vortex");
        WriteAsync().GetAwaiter().GetResult();
        _file = VortexFile.OpenAsync(_path).AsTask().GetAwaiter().GetResult();
        _low = Expr.Lt(Expr.Field("measure"), Expr.Literal(FilterLiteral.From(0.5)));
        _none = Expr.And(
            Expr.Gt(Expr.Field("measure"), Expr.Literal(FilterLiteral.From(0.25))),
            Expr.Lt(Expr.Field("measure"), Expr.Literal(FilterLiteral.From(0.25 + 1e-12))));
        Console.WriteLine($"// count {Count()}, any {Any()}, max {Max()}");
    }

    /// <summary>Closes and deletes the file.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _file.DisposeAsync().AsTask().GetAwaiter().GetResult();
        System.IO.File.Delete(_path);
    }

    /// <summary>The rows whose measure is below one half.</summary>
    [Benchmark]
    public long Count() => _file.ScanBuilder().Where(_low).WithDegreeOfParallelism(Degree).CountAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>Whether a row's measure lies in a band no row falls in, which every zone's bounds allow.</summary>
    [Benchmark]
    public bool Any() => _file.ScanBuilder().Where(_none).WithDegreeOfParallelism(Degree).AnyAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>The largest key among the rows whose measure is below one half.</summary>
    [Benchmark]
    public long Max() => _file.ScanBuilder().Where(_low).WithDegreeOfParallelism(Degree).MaxAsync("key").AsTask().GetAwaiter().GetResult().SignedValue;

    private async Task WriteAsync()
    {
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType f64 = types.Primitive(PType.F64, Nullability.NonNullable);
        DType schema = types.Struct(["key", "measure"], [i64, f64], Nullability.NonNullable);
        VortexBuffer keys = arena.Allocate(Rows * sizeof(long), sizeof(long), out Span<byte> keyBytes);
        VortexBuffer measures = arena.Allocate(Rows * sizeof(double), sizeof(double), out Span<byte> measureBytes);
        Span<long> keyValues = MemoryMarshal.Cast<byte, long>(keyBytes);
        Span<double> measureValues = MemoryMarshal.Cast<byte, double>(measureBytes);
        Random random = new Random(0x5C01);
        for (int row = 0; row < Rows; row++)
        {
            keyValues[row] = row;
            measureValues[row] = random.NextDouble();
        }

        int key = arena.AddPrimitive(i64, Rows, Validity.NonNullable, PType.I64, keys);
        int measure = arena.AddPrimitive(f64, Rows, Validity.NonNullable, PType.F64, measures);
        int root = arena.AddStruct(schema, Rows, Validity.NonNullable, [key, measure]);
        await using VortexFileWriter writer = VortexFileWriter.Create(_path, schema, new VortexWriteOptions());
        using (RecordBatch batch = new RecordBatch(arena, root, 0))
        {
            await writer.WriteAsync(batch).ConfigureAwait(false);
        }

        await writer.CompleteAsync().ConfigureAwait(false);
    }
}
