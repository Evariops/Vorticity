// Audit S1: what the filter kernel comparing a column against a literal costs per row, by column type, with and without
// nulls. Run it as it is and again under DOTNET_EnableAVX512=0 (AVX2; on .NET 11 DOTNET_EnableAVX512F=0 no longer turns
// AVX-512 off), DOTNET_EnableAVX2=0 (SSE) and
// DOTNET_EnableHWIntrinsic=0 (scalar): the kernel was vectorised under AVX-512 alone.
using System;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A column of 65,536 rows compared with a literal, a verdict a row.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditCompareKernelBenchmarks
{
    private const int Rows = 65_536;

    /// <summary>The column compared.</summary>
    [Params("i32", "i64", "f64", "i64 nulls")]
    public string Column { get; set; } = "i32";

    private CanonicalArena _arena = null!;
    private ColumnStore _store = null!;
    private int _node;
    private FilterLiteral _literal;
    private byte[] _states = null!;

    /// <summary>Builds the column.</summary>
    [GlobalSetup]
    public void Setup()
    {
        DTypeArena types = new DTypeArena();
        bool nulls = Column.EndsWith("nulls", StringComparison.Ordinal);
        Nullability nullable = nulls ? Nullability.Nullable : Nullability.NonNullable;
        PType ptype = Column.StartsWith("i32", StringComparison.Ordinal) ? PType.I32 : Column.StartsWith("f64", StringComparison.Ordinal) ? PType.F64 : PType.I64;
        DType type = types.Primitive(ptype, nullable);
        FixedStore store = (FixedStore)ColumnStores.Create(type, AlignedBufferPool.Shared, null);
        Random random = new Random(0x51);
        for (int row = 0; row < Rows; row++)
        {
            if (nulls && row % 10 == 0)
            {
                store.AppendNull();
                continue;
            }

            switch (ptype)
            {
                case PType.I32:
                    store.Append(random.Next(1_000_000));
                    break;
                case PType.I64:
                    store.Append((long)random.Next(1_000_000));
                    break;
                default:
                    store.Append(random.NextDouble() * 1_000_000);
                    break;
            }
        }

        _store = store;
        _arena = new CanonicalArena();
        _node = store.Build(_arena, Rows);
        _literal = ptype == PType.F64 ? FilterLiteral.From(500_000.0) : FilterLiteral.From(500_000L);
        _states = new byte[Rows];
        Console.WriteLine(
            $"// ISA: Vector512 {System.Runtime.Intrinsics.Vector512.IsHardwareAccelerated}, AVX-512BW {System.Runtime.Intrinsics.X86.Avx512BW.IsSupported}, " +
            $"Vector256 {System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated}, AVX2 {System.Runtime.Intrinsics.X86.Avx2.IsSupported}, " +
            $"Vector128 {System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated}, SSSE3 {System.Runtime.Intrinsics.X86.Ssse3.IsSupported}");
    }

    /// <summary>Every row's verdict on <c>column &lt; 500000</c>.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public byte Less()
    {
        ComparisonKernels.Compare(_arena, _node, ComparisonOp.Less, _literal, _states);
        return _states[Rows - 1];
    }
}
