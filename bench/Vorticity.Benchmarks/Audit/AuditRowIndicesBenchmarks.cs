// Audit S1: what turning a filter's verdicts into the indices of the rows it keeps costs per row, at half the rows kept
// and at a twentieth. Run it as it is and under DOTNET_EnableAVX512=0 (AVX2) and DOTNET_EnableAVX2=0 (SSE).
using System;

using BenchmarkDotNet.Attributes;

using Vorticity.Compute;

namespace Vorticity.Benchmarks.Audit;

/// <summary>The indices of the rows 65,536 verdicts keep.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditRowIndicesBenchmarks
{
    private const int Rows = 65_536;

    /// <summary>The share of rows kept, in percent.</summary>
    [Params(50, 5)]
    public int Kept { get; set; }

    private byte[] _states = null!;
    private int[] _indices = null!;

    /// <summary>Draws the verdicts.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(0x1D);
        _states = new byte[Rows];
        _indices = new int[Rows];
        for (int i = 0; i < Rows; i++)
        {
            _states[i] = random.Next(100) < Kept ? Trilean.True : (byte)random.Next(0, 3) == Trilean.True ? Trilean.False : (byte)random.Next(0, 3);
            if (_states[i] == Trilean.True && random.Next(100) >= Kept)
            {
                _states[i] = Trilean.Unknown;
            }
        }
    }

    /// <summary>The rows whose verdict is true.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public int FromStates() => RowIndices.FromStates(_states, _indices);
}
