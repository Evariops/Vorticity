// What turning a batch's verdicts into words of rows costs, which a filtered aggregate's condition read
// both ways paid twice: a pass over the verdicts for the rows where it holds, another for those where it does not;
// against the second as the first's complement, a word at a time.
//
// 65,536 verdicts drawn among true, false and unknown.
using System;

using BenchmarkDotNet.Attributes;

using Vorticity.Compute;

namespace Vorticity.Benchmarks.Audit;

/// <summary>Verdicts into words.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditVerdictWordsBenchmarks
{
    private const int Rows = 65_536;

    private readonly byte[] _states = new byte[Rows];
    private readonly ulong[] _true = new ulong[Rows / 64];
    private readonly ulong[] _notTrue = new ulong[Rows / 64];

    /// <summary>Draws the verdicts.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(1019);
        for (int i = 0; i < Rows; i++)
        {
            _states[i] = (byte)random.Next(3);
        }
    }

    /// <summary>A pass over the verdicts, by row.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public ulong Pass()
    {
        Trilean.ToWords(_states, Trilean.True, equal: false, _notTrue);
        return _notTrue[^1];
    }

    /// <summary>The complement of the words of a first pass, by row.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public ulong Complement()
    {
        ReadOnlySpan<ulong> held = _true;
        Span<ulong> into = _notTrue;
        for (int w = 0; w < into.Length; w++)
        {
            into[w] = ~held[w];
        }

        return into[^1];
    }
}
