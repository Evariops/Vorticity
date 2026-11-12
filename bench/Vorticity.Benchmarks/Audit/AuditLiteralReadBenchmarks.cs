// What a zone's bound costs read as a literal from a zone map's column, as the first open of a file's map reads
// two to four of them for every zone of every column.
//
// 1,024 zones of a primitive column of longs, each read once.
using System;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Audit;

/// <summary>Zone bounds read as literals.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditLiteralReadBenchmarks
{
    private const int Zones = 1_024;

    private CanonicalArena _arena = null!;
    private int _node;

    /// <summary>Builds the column.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _arena = new CanonicalArena(64);
        VortexBuffer values = _arena.Allocate(Zones * 8, 8, out Span<byte> into);
        new Random(191).NextBytes(into);
        _node = _arena.AddPrimitive(new DTypeArena().Primitive(PType.I64, Nullability.Nullable), Zones, Validity.AllValid, PType.I64, values);
    }

    /// <summary>Every zone's bound read, by zone.</summary>
    [Benchmark(OperationsPerInvoke = Zones)]
    public long Read()
    {
        long sum = 0;
        for (int zone = 0; zone < Zones; zone++)
        {
            if (LiteralReader.TryRead(_arena, _node, zone, out FilterLiteral literal))
            {
                sum += literal.SignedValue;
            }
        }

        return sum;
    }
}
