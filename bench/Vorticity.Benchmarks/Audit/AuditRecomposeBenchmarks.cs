// Audit DE-20: what putting an instant back together from its days, seconds and fractions costs a row.
//
// 1,048,576 instants in microseconds cut as the DateTimeParts encoding cuts them: days since the epoch as i32, the
// second of the day as u32, the microsecond as u32, recomposed into i64. Run under DOTNET_EnableAVX512=0 for the AVX2
// path, and DOTNET_EnableAVX2=0 for SSE.
using System;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Audit;

/// <summary>An instant's parts recomposed, a million rows.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditRecomposeBenchmarks
{
    private const int Rows = 1 << 20;
    private const long Divisor = 1_000_000;

    private byte[] _days = null!;
    private byte[] _seconds = null!;
    private byte[] _fractions = null!;
    private long[] _values = null!;

    /// <summary>Cuts a million instants around now, some before the epoch.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(47);
        int[] days = new int[Rows];
        uint[] seconds = new uint[Rows];
        uint[] fractions = new uint[Rows];
        for (int i = 0; i < Rows; i++)
        {
            days[i] = random.Next(-20_000, 40_000);
            seconds[i] = (uint)random.Next(86_400);
            fractions[i] = (uint)random.Next(1_000_000);
        }

        _days = MemoryMarshal.AsBytes(days.AsSpan()).ToArray();
        _seconds = MemoryMarshal.AsBytes(seconds.AsSpan()).ToArray();
        _fractions = MemoryMarshal.AsBytes(fractions.AsSpan()).ToArray();
        _values = new long[Rows];
    }

    /// <summary>The instants recomposed, the middle one read.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public long Recompose()
    {
        IntegerKernels.Recompose(_days, PType.I32, _seconds, PType.U32, _fractions, PType.U32, _values, 86_400 * Divisor, Divisor);
        return _values[Rows / 2];
    }
}
