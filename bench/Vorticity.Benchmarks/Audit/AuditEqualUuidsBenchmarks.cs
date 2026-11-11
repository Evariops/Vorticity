// What an equality costs on a column of UUIDs, sixteen bytes a row, as a lookup by an identifier filters it.
//
// 65,536 random UUIDs, the literal one of them.
using System;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Audit;

/// <summary>An equality over UUIDs.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditEqualUuidsBenchmarks
{
    private const int Rows = 65_536;

    private readonly byte[] _states = new byte[Rows];
    private CanonicalArena _arena = null!;
    private int _node;
    private FilterLiteral _literal;

    /// <summary>Builds the column.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(137);
        _arena = new CanonicalArena(64);
        VortexBuffer bytes = _arena.Allocate(Rows * 16, 16, out Span<byte> into);
        random.NextBytes(into);
        DTypeArena types = new DTypeArena();
        DType u8 = types.Primitive(PType.U8, Nullability.NonNullable);
        int elements = _arena.AddPrimitive(u8, Rows * 16, Validity.NonNullable, PType.U8, bytes);
        _node = _arena.AddFixedSizeList(types.FixedSizeList(u8, 16, Nullability.NonNullable), Rows, Validity.NonNullable, elements, 16);
        _literal = FilterLiteral.From(into.Slice(777 * 16, 16));
    }

    /// <summary>The rows equal to the literal, by row.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public byte Equal()
    {
        ComparisonKernels.Compare(_arena, _node, ComparisonOp.Equal, _literal, _states);
        return _states[777];
    }
}
