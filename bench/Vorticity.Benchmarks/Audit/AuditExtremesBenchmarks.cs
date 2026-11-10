// Audit CP-04: what the row of a column's least value costs found, as a scan asked for the row of its minimum does, a
// batch at a time.
//
// 65,536 random longs or ints, every row valid.
using System;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Audit;

/// <summary>The row of the least value.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditExtremesBenchmarks
{
    private const int Rows = 65_536;

    private CanonicalArena _arena = null!;
    private int _node;

    /// <summary>The values' width.</summary>
    [Params(4, 8)]
    public int Width { get; set; }

    /// <summary>Builds the column.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _arena = new CanonicalArena(64);
        VortexBuffer values = _arena.Allocate(Rows * Width, 64, out Span<byte> into);
        new Random(149).NextBytes(into);
        PType ptype = Width == 8 ? PType.I64 : PType.I32;
        _node = _arena.AddPrimitive(new DTypeArena().Primitive(ptype, Nullability.NonNullable), Rows, Validity.NonNullable, ptype, values);
    }

    /// <summary>The least value's row, by row.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public int Least() => Extremes.TryFind(_arena, _node, default, listed: false, wantMin: true, out int row) ? row : -1;
}
