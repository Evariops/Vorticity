// Audit S1: what spreading a dictionary's answers over its rows by their codes costs per row, a dictionary of 8 values
// coded in bytes, as a dictionary decoder answers a pushed predicate. Run it under DOTNET_EnableAVX512=0 (AVX2) and
// DOTNET_EnableAVX2=0 (SSE): the kernel was vectorised under AVX-512 VBMI alone.
using System;

using BenchmarkDotNet.Attributes;

using Vorticity.Compute;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Audit;

/// <summary>The verdicts of 65,536 rows from their byte codes into a dictionary of 8 answers.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditCodeAnswersBenchmarks
{
    private const int Rows = 65_536;

    /// <summary>Whether a tenth of the rows is null.</summary>
    [Params(false, true)]
    public bool Nulls { get; set; }

    private byte[] _answers = null!;
    private byte[] _codes = null!;
    private byte[] _validity = null!;
    private byte[] _states = null!;

    /// <summary>Draws the codes and the validity.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(0xC0);
        _answers = [Trilean.False, Trilean.True, Trilean.False, Trilean.False, Trilean.True, Trilean.False, Trilean.False, Trilean.True];
        _codes = new byte[Rows];
        _validity = new byte[Rows / 8];
        _states = new byte[Rows];
        _validity.AsSpan().Fill(0xFF);
        for (int i = 0; i < Rows; i++)
        {
            _codes[i] = (byte)random.Next(8);
            if (Nulls && random.Next(10) == 0)
            {
                _validity[i >> 3] &= (byte)~(1 << (i & 7));
                _codes[i] = (byte)random.Next(256);
            }
        }
    }

    /// <summary>Every row's verdict.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public int Expand() => CodeAnswers.Expand(_answers, _codes, PType.U8, _validity, 0, !Nulls, _states);
}
