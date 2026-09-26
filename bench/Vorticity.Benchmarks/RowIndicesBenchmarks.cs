// The rows a filter kept, as the list of their indices: from a verdict per row, as the evaluator
// leaves them, and from the bitmap an encoding answered with.
//
// The ported arms are the loops as they were: branchless, one index written per row and kept by its
// verdict. The shipped arms call `RowIndices`, which with AVX-512 compresses sixteen indices at a
// time and reads a bitmap a word at a time. The densities bracket a selective filter, an even one
// and one that keeps almost everything.
using System;
using System.Collections.Generic;

using BenchmarkDotNet.Attributes;

using Vorticity.Compute;

namespace Vorticity.Benchmarks;

/// <summary>Verdicts and bitmaps to indices: ported loops against the shipped kernel.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
public class RowIndicesBenchmarks
{
    private const int Rows = 1 << 16;

    /// <summary>Percent of rows selected.</summary>
    [Params(1, 50, 99)]
    public int Density { get; set; } = 50;

    private byte[] _states = [];
    private byte[] _bits = [];
    private byte[] _valid = [];
    private int[] _indices = [];

    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters) => (Rows, Rows * 4L);

    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(20260926);
        _states = new byte[Rows];
        _bits = new byte[Rows / 8];
        _valid = new byte[Rows / 8];
        for (int i = 0; i < Rows; i++)
        {
            bool kept = random.Next(100) < Density;
            _states[i] = kept ? Trilean.True : Trilean.False;
            if (kept)
            {
                _bits[i >> 3] |= (byte)(1 << (i & 7));
            }

            if (random.Next(16) != 0)
            {
                _valid[i >> 3] |= (byte)(1 << (i & 7));
            }
        }

        _indices = new int[Rows];
    }

    [Benchmark(Baseline = true, Description = "states, ported")]
    [BenchmarkCategory("states")]
    public int StatesPorted()
    {
        ReadOnlySpan<byte> states = _states;
        Span<int> slots = _indices;
        int count = 0;
        for (int i = 0; i < states.Length; i++)
        {
            slots[count] = i;
            count += states[i] == Trilean.True ? 1 : 0;
        }

        return count;
    }

    [Benchmark(Description = "states, shipped")]
    [BenchmarkCategory("states")]
    public int StatesShipped() => RowIndices.FromStates(_states, _indices);

    [Benchmark(Description = "bits and validity, ported")]
    [BenchmarkCategory("bits")]
    public int BitsPorted()
    {
        ReadOnlySpan<byte> bits = _bits;
        ReadOnlySpan<byte> validBits = _valid;
        Span<int> slots = _indices;
        int count = 0;
        for (int row = 0; row < Rows; row++)
        {
            slots[count] = row;
            count += (bits[row >> 3] >> (row & 7)) & (validBits[row >> 3] >> (row & 7)) & 1;
        }

        return count;
    }

    [Benchmark(Description = "bits and validity, shipped")]
    [BenchmarkCategory("bits")]
    public int BitsShipped() => RowIndices.FromBits(_bits, 0, _valid, 0, allValid: false, Rows, _indices);
}
