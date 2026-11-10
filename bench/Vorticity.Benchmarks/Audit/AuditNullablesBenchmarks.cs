// Audit CO-04: what a nullable column's values cost a reader that takes them as T?, on the vector widths a machine has.
//
// 65,536 rows, one in ten null, as longs, ints and bools. Run as it is (AVX-512 here), and under DOTNET_EnableAVX512=0
// (AVX2: a row at a time).
using System;

using BenchmarkDotNet.Attributes;

using Vorticity.Columns;

namespace Vorticity.Benchmarks.Audit;

/// <summary>Nullable values written whole, per row.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditNullablesBenchmarks
{
    private const int Rows = 65_536;

    private readonly long[] _longs = new long[Rows];
    private readonly int[] _ints = new int[Rows];
    private readonly ulong[] _valid = new ulong[Rows / 64];
    private readonly ulong[] _bits = new ulong[Rows / 64];
    private readonly long?[] _nullableLongs = new long?[Rows];
    private readonly int?[] _nullableInts = new int?[Rows];
    private readonly bool?[] _nullableBools = new bool?[Rows];

    /// <summary>Draws the values and their nulls.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(5);
        for (int i = 0; i < Rows; i++)
        {
            _longs[i] = random.NextInt64();
            _ints[i] = random.Next();
            if (random.Next(10) != 0)
            {
                _valid[i >> 6] |= 1UL << (i & 63);
            }

            if (random.Next(2) == 0)
            {
                _bits[i >> 6] |= 1UL << (i & 63);
            }
        }
    }

    /// <summary>Nullable longs.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public long? Longs()
    {
        ColumnKernels.Nullables<long>(_longs, _valid, _nullableLongs);
        return _nullableLongs[Rows - 1];
    }

    /// <summary>Nullable ints.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public int? Ints()
    {
        ColumnKernels.Nullables<int>(_ints, _valid, _nullableInts);
        return _nullableInts[Rows - 1];
    }

    /// <summary>Nullable bools.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public bool? Bools()
    {
        ColumnKernels.NullableBools(_valid, _bits, _nullableBools);
        return _nullableBools[Rows - 1];
    }
}
