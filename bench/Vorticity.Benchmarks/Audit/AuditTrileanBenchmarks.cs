// What the connectives of a filter's verdicts cost per row: AND of two columns of verdicts, NOT of one, and the
// selection as words of bits. Run it as it is and under DOTNET_EnableAVX512=0 (AVX2) and DOTNET_EnableAVX2=0 (SSE).
using System;

using BenchmarkDotNet.Attributes;

using Vorticity.Compute;

namespace Vorticity.Benchmarks.Audit;

/// <summary>Connectives over 65,536 verdicts.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditTrileanBenchmarks
{
    private const int Rows = 65_536;

    private byte[] _left = null!;
    private byte[] _right = null!;
    private byte[] _work = null!;
    private ulong[] _words = null!;

    /// <summary>Draws the verdicts.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(0x7A1);
        _left = new byte[Rows];
        _right = new byte[Rows];
        _work = new byte[Rows];
        _words = new ulong[Rows / 64];
        for (int i = 0; i < Rows; i++)
        {
            _left[i] = (byte)random.Next(3);
            _right[i] = (byte)random.Next(3);
        }
    }

    /// <summary>AND of two columns of verdicts, in place.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public byte And()
    {
        _left.CopyTo(_work, 0);
        Trilean.And(_work, _right);
        return _work[Rows - 1];
    }

    /// <summary>NOT of a column of verdicts, in place.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public byte Not()
    {
        _left.CopyTo(_work, 0);
        Trilean.Not(_work);
        return _work[Rows - 1];
    }

    /// <summary>The rows a column of verdicts selects, as words of bits.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public int Words() => Trilean.ToWords(_left, Trilean.True, equal: true, _words);
}
