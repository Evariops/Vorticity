// Audit AG-10: what selecting the rows of a run of dense mask words costs before a filtered aggregate folds them, the
// rows left out read as a filler: a run of 16 words of 64 rows, one row in two kept at random, by value width.
// The ISA under test is the process's: run once as is and once with DOTNET_EnableAVX512=0.
using System;

using BenchmarkDotNet.Attributes;

using Vorticity.Aggregating;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A run of mask words selected.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditWordSelectBenchmarks
{
    private const int Words = 16;
    private const int Rows = Words * 64;

    private readonly ulong[] _words = new ulong[Words];
    private readonly long[] _longs = new long[Rows];
    private readonly long[] _longsInto = new long[Rows];
    private readonly int[] _ints = new int[Rows];
    private readonly int[] _intsInto = new int[Rows];
    private readonly short[] _shorts = new short[Rows];
    private readonly short[] _shortsInto = new short[Rows];
    private readonly sbyte[] _bytes = new sbyte[Rows];
    private readonly sbyte[] _bytesInto = new sbyte[Rows];

    /// <summary>Draws the values and the words.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(1015);
        for (int w = 0; w < Words; w++)
        {
            _words[w] = (ulong)random.NextInt64() ^ ((ulong)random.Next() << 33);
        }

        for (int i = 0; i < Rows; i++)
        {
            _longs[i] = random.NextInt64();
            _ints[i] = random.Next();
            _shorts[i] = (short)random.Next();
            _bytes[i] = (sbyte)random.Next();
        }
    }

    /// <summary>64-bit values, by row.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public long Longs()
    {
        WordFold.Select<long>(_longs, _words, 0, _longsInto);
        return _longsInto[Rows - 1];
    }

    /// <summary>32-bit values, by row.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public int Ints()
    {
        WordFold.Select<int>(_ints, _words, 0, _intsInto);
        return _intsInto[Rows - 1];
    }

    /// <summary>16-bit values, by row.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public short Shorts()
    {
        WordFold.Select<short>(_shorts, _words, 0, _shortsInto);
        return _shortsInto[Rows - 1];
    }

    /// <summary>8-bit values, by row.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public sbyte Bytes()
    {
        WordFold.Select<sbyte>(_bytes, _words, 0, _bytesInto);
        return _bytesInto[Rows - 1];
    }
}
