// Audit S2: what a filter's verdicts cost as a byte a row against two bitmaps, past the comparisons.
//
// 65,536 rows, two conjuncts' verdicts, each true for 70 % of rows drawn at random, no unknown. A byte a row: the
// conjunction in place (Trilean.And), the rows it rules out counted (Count), the kept rows' indices (FromStates). Two
// bitmaps: the words ANDed, their bits counted, the kept rows' indices (FromBits). Each operation is what a conjunct
// past the first, then the selection, costs a split on top of its comparison.
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Compute;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A conjunct's verdict handling and the selection, a byte a row against a bit a row.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditVerdictFormsBenchmarks
{
    private const int Rows = 1 << 16;

    private byte[] _left = null!;
    private byte[] _right = null!;
    private byte[] _work = null!;
    private ulong[] _leftBits = null!;
    private ulong[] _rightBits = null!;
    private ulong[] _workBits = null!;
    private int[] _indices = null!;

    /// <summary>Draws the two verdicts in both forms.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(37);
        _left = new byte[Rows];
        _right = new byte[Rows];
        _work = new byte[Rows];
        _leftBits = new ulong[Rows / 64];
        _rightBits = new ulong[Rows / 64];
        _workBits = new ulong[Rows / 64];
        _indices = new int[Rows];
        for (int i = 0; i < Rows; i++)
        {
            bool left = random.Next(100) < 70;
            bool right = random.Next(100) < 70;
            _left[i] = left ? Trilean.True : Trilean.False;
            _right[i] = right ? Trilean.True : Trilean.False;
            _leftBits[i >> 6] |= left ? 1UL << i : 0;
            _rightBits[i >> 6] |= right ? 1UL << i : 0;
        }
    }

    /// <summary>A conjunct past the first, as bytes: the conjunction in place, then the rows ruled out counted.</summary>
    [Benchmark(Baseline = true)]
    public int BytesConjunct()
    {
        _left.CopyTo(_work, 0);
        Trilean.And(_work, _right);
        return _work.AsSpan().Count(Trilean.False);
    }

    /// <summary>A conjunct past the first, as bytes, the conjunction counting the rows it rules out as it goes.</summary>
    [Benchmark]
    public int BytesConjunctFused()
    {
        _left.CopyTo(_work, 0);
        return Trilean.AndCountingFalse(_work, _right);
    }

    /// <summary>The copy alone, which the conjunct's measure includes.</summary>
    [Benchmark]
    public int BytesCopy()
    {
        _left.CopyTo(_work, 0);
        return _work[0];
    }

    /// <summary>The copy and the conjunction, without the count.</summary>
    [Benchmark]
    public int BytesAnd()
    {
        _left.CopyTo(_work, 0);
        Trilean.And(_work, _right);
        return _work[0];
    }

    /// <summary>The count alone.</summary>
    [Benchmark]
    public int BytesCount() => _left.AsSpan().Count(Trilean.False);

    /// <summary>The same, as bitmaps: the words ANDed, the bits counted.</summary>
    [Benchmark]
    public int BitsConjunct()
    {
        ref ulong l = ref MemoryMarshal.GetArrayDataReference(_leftBits);
        ref ulong r = ref MemoryMarshal.GetArrayDataReference(_rightBits);
        ref ulong w = ref MemoryMarshal.GetArrayDataReference(_workBits);
        int kept = 0;
        for (int i = 0; i < _workBits.Length; i++)
        {
            ulong word = Unsafe.Add(ref l, i) & Unsafe.Add(ref r, i);
            Unsafe.Add(ref w, i) = word;
            kept += BitOperations.PopCount(word);
        }

        return Rows - kept;
    }

    /// <summary>The kept rows' indices from a byte a row.</summary>
    [Benchmark]
    public int BytesSelect() => RowIndices.FromStates(_left, _indices);

    /// <summary>The kept rows' indices from a bit a row.</summary>
    [Benchmark]
    public int BitsSelect() => RowIndices.FromBits(MemoryMarshal.AsBytes(_leftBits.AsSpan()), 0, default, 0, allValid: true, Rows, _indices);
}
