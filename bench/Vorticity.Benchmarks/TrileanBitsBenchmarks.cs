// A filter's verdicts, one byte per row, turned into the bitmaps the scan hands on: the selection a
// filtered batch carries, and the value and validity of a predicate a layout answered.
//
// The loops that did it tested each row and branched on it, which a filter's verdicts, following
// the data, mispredict as often as the data is irregular. The ported arms are those loops, as they
// were; the shipped arms call `Trilean.ToWords`, which compares 64 verdicts into one word.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Compute;

namespace Vorticity.Benchmarks;

/// <summary>Verdicts to bitmaps: ported loops against the shipped kernel.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
public class TrileanBitsBenchmarks
{
    private const int Rows = 1 << 16;

    /// <summary>Whether a row may be unknown, which the answered-predicate arm makes a validity of.</summary>
    [Params(false, true)]
    public bool Nulls { get; set; }

    private byte[] _states = [];
    private ulong[] _words = [];
    private byte[] _truth = [];
    private byte[] _known = [];

    /// <summary>What one invocation reads: a byte per row.</summary>
    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters) => (Rows, Rows);

    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(20260926);
        _states = new byte[Rows];
        for (int i = 0; i < Rows; i++)
        {
            int draw = random.Next(Nulls ? 3 : 2);
            _states[i] = draw == 0 ? Trilean.False : draw == 1 ? Trilean.True : Trilean.Unknown;
        }

        _words = new ulong[(Rows + 63) >> 6];
        _truth = new byte[_words.Length * 8];
        _known = new byte[_words.Length * 8];
    }

    // ------------------------------------------------------------------------- selection words

    /// <summary>The count, then a test and a branch per row, as `ExecuteSelected` did.</summary>
    [Benchmark(Baseline = true, Description = "selection, ported")]
    [BenchmarkCategory("selection")]
    public int SelectionPorted()
    {
        ReadOnlySpan<byte> window = _states;
        int selected = Trilean.CountTrue(window);
        Span<ulong> bits = _words;
        bits.Clear();
        for (int row = 0; row < window.Length; row++)
        {
            if (window[row] == Trilean.True)
            {
                bits[row >> 6] |= 1UL << (row & 63);
            }
        }

        return selected;
    }

    [Benchmark(Description = "selection, shipped")]
    [BenchmarkCategory("selection")]
    public int SelectionShipped() => Trilean.ToWords(_states, Trilean.True, equal: true, _words);

    // ---------------------------------------------------------------------- answered predicate

    /// <summary>The value bits and the unknown flag in one branching loop, the validity in a second.</summary>
    [Benchmark(Description = "answer, ported")]
    [BenchmarkCategory("answer")]
    public bool AnswerPorted()
    {
        ReadOnlySpan<byte> states = _states;
        Span<byte> bits = _truth;
        bits.Clear();
        bool unknown = false;
        for (int row = 0; row < states.Length; row++)
        {
            byte state = states[row];
            if (state == Trilean.True)
            {
                bits[row >> 3] |= (byte)(1 << (row & 7));
            }
            else if (state == Trilean.Unknown)
            {
                unknown = true;
            }
        }

        if (unknown)
        {
            Span<byte> valid = _known;
            valid.Clear();
            for (int row = 0; row < states.Length; row++)
            {
                if (states[row] != Trilean.Unknown)
                {
                    valid[row >> 3] |= (byte)(1 << (row & 7));
                }
            }
        }

        return unknown;
    }

    [Benchmark(Description = "answer, shipped")]
    [BenchmarkCategory("answer")]
    public bool AnswerShipped()
    {
        ReadOnlySpan<byte> states = _states;
        Trilean.ToWords(states, Trilean.True, equal: true, MemoryMarshal.Cast<byte, ulong>(_truth.AsSpan()));
        bool unknown = states.Contains(Trilean.Unknown);
        if (unknown)
        {
            Trilean.ToWords(states, Trilean.Unknown, equal: false, MemoryMarshal.Cast<byte, ulong>(_known.AsSpan()));
        }

        return unknown;
    }
}
