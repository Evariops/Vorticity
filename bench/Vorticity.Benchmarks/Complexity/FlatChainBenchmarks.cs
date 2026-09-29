// What a flat chain of conjuncts costs, against its terms.
//
// `Terms` comparisons on one Int64 column joined by AND: built one `Expr.And` at a time, as
// chained `Where` calls build it; parsed from text; and counted over a file of 65,536 rows in
// memory, which evaluates every term. A chain nests as deep as it is long where it leans, and the
// original refuses one past 64 terms. Run in a checkout of the original and in the tree.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>A flat conjunction built, parsed and evaluated, against its terms.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class FlatChainBenchmarks
{
    private const int Rows = 65_536;

    /// <summary>Comparisons joined by AND.</summary>
    [Params(2, 4, 16, 64, 1_024)]
    public int Terms { get; set; }

    private string _text = null!;
    private VortexFile _file = null!;

    /// <summary>Writes the file and the filter's text.</summary>
    [GlobalSetup]
    public void Setup()
    {
        StringBuilder text = new StringBuilder();
        for (int i = 0; i < Terms; i++)
        {
            text.Append(i == 0 ? string.Empty : " and ").Append("c0 >= ").Append(i);
        }

        _text = text.ToString();
        _file = VortexFile.OpenAsync(new MemorySegmentSource(WriteAsync().GetAwaiter().GetResult()), new VortexOpenOptions(), default)
            .AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Closes the file.</summary>
    [GlobalCleanup]
    public void Cleanup() => _file.DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>The chain built one conjunct at a time.</summary>
    [Benchmark]
    public int Build() => Chain().Height;

    /// <summary>The chain parsed from its text.</summary>
    [Benchmark]
    public int Parse() => VortexExpr.Parse(_text).Height;

    /// <summary>The rows the chain keeps, counted.</summary>
    [Benchmark]
    public long Count() => _file.ScanBuilder().Where(Chain()).CountAsync().AsTask().GetAwaiter().GetResult();

    private VortexExpr Chain()
    {
        FieldExpr column = Expr.Field("c0");
        VortexExpr chain = Expr.Ge(column, Expr.Literal(FilterLiteral.From(0L)));
        for (int i = 1; i < Terms; i++)
        {
            chain = Expr.And(chain, Expr.Ge(column, Expr.Literal(FilterLiteral.From((long)i))));
        }

        return chain;
    }

    private static async Task<byte[]> WriteAsync()
    {
        DTypeArena types = new DTypeArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType schema = types.Struct(["c0"], [i64], Nullability.NonNullable);
        CanonicalArena arena = new CanonicalArena();
        VortexBuffer buffer = arena.Allocate(Rows * sizeof(long), sizeof(long), out Span<byte> bytes);
        Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
        for (int i = 0; i < Rows; i++)
        {
            values[i] = i;
        }

        MemoryStream stream = new MemoryStream();
        await using (VortexFileWriter writer = VortexFileWriter.Create(new StreamSegmentSink(stream), schema, new VortexWriteOptions()))
        {
            int column = arena.AddPrimitive(i64, Rows, Validity.NonNullable, PType.I64, buffer);
            using (RecordBatch batch = new RecordBatch(arena, arena.AddStruct(schema, Rows, Validity.NonNullable, [column]), 0))
            {
                await writer.WriteAsync(batch).ConfigureAwait(false);
            }

            await writer.CompleteAsync().ConfigureAwait(false);
        }

        return stream.ToArray();
    }
}
