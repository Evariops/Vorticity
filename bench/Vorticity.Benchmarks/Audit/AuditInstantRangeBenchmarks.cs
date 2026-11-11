// What a range on instants costs when their DateTimeParts encoding answers it.
//
// A file of 1,048,576 instants in microseconds, scattered over 30 days so that no zone map prunes a
// chunk, written as days, seconds and microseconds. The filter keeps a range that starts and ends
// within a day, so that every part is asked: the parts' own bounds first (seconds within a day,
// microseconds within a second, days that do not wrap), then the days and the times of day.
// The scan counts the rows the range keeps.
using System;
using System.Collections.Immutable;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Expressions;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A range on DateTimeParts instants, answered by the encoding.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditInstantRangeBenchmarks
{
    private const int Rows = 1 << 20;
    private const long Day = 86_400L * 1_000_000;
    private const long Origin = 20_000 * Day;

    private string _path = null!;
    private VortexFile _file = null!;
    private VortexExpr _filter = null!;

    /// <summary>Writes the file and opens it.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _path = Path.Combine(Path.GetTempPath(), $"vorticity-instants-{Guid.NewGuid():N}.vortex");
        WriteAsync().GetAwaiter().GetResult();
        _file = VortexFile.OpenAsync(_path).AsTask().GetAwaiter().GetResult();

        // Day 10, 08:00 to day 20, 17:30: a first and a last day cut by a time of day.
        long low = Origin + (10 * Day) + (8L * 3_600_000_000);
        long high = Origin + (20 * Day) + (17L * 3_600_000_000) + (30L * 60_000_000);
        _filter = Expr.And(
            Expr.Ge(Expr.Field("at"), Expr.Literal(FilterLiteral.From(low))),
            Expr.Lt(Expr.Field("at"), Expr.Literal(FilterLiteral.From(high))));
        Console.Out.WriteLine(
            $"INSTANT RANGE: {Count()} rows of {Rows}, chunks of {Vorticity.Scanning.SplitPlan.NaturalBatchRows(_file.LayoutTree)} rows");
    }

    /// <summary>Closes and deletes the file.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _file.DisposeAsync().AsTask().GetAwaiter().GetResult();
        System.IO.File.Delete(_path);
    }

    /// <summary>The rows the range keeps, counted.</summary>
    [Benchmark]
    public long Count() =>
        _file.Scan("at").Where(_filter).With(new ScanOptions { UseIndexes = false })
            .CountAsync().AsTask().GetAwaiter().GetResult();

    private async Task WriteAsync()
    {
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        DType storage = types.Primitive(PType.I64, Nullability.NonNullable);
        DType instant = types.Extension("vortex.timestamp"u8, storage, [(byte)VortexTimeUnit.Microseconds, 0, 0]);
        DType schema = types.Struct(["at"], [instant], Nullability.NonNullable);
        VortexBuffer buffer = arena.Allocate(Rows * sizeof(long), sizeof(long), out Span<byte> bytes);
        Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
        Random random = new Random(1111);
        for (int row = 0; row < Rows; row++)
        {
            values[row] = Origin + random.NextInt64(30 * Day);
        }

        int stored = arena.AddPrimitive(storage, Rows, Validity.NonNullable, PType.I64, buffer);
        int column = arena.AddExtension(instant, Rows, stored);
        int root = arena.AddStruct(schema, Rows, Validity.NonNullable, [column]);
        VortexWriteOptions options = new VortexWriteOptions
        {
            Hints = ImmutableDictionary<string, EncodingHint>.Empty.Add("at", EncodingHint.DateTimeParts),
        };

        await using VortexFileWriter writer = VortexFileWriter.Create(_path, schema, options);
        using (RecordBatch batch = new RecordBatch(arena, root, 0))
        {
            await writer.WriteAsync(batch).ConfigureAwait(false);
        }

        await writer.CompleteAsync().ConfigureAwait(false);
    }
}
