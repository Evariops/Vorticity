// What decoding an object's counts of a column costs, a value at a time.
//
// The counts of a text column of 8 or 1,000 values of 16 to 24 bytes, each value's entry holding its rows and the
// states of two integer measures, written by ColumnCounts.ToBytes and read back by ColumnCounts.From, as a question
// that misses the synopsis memo reads them.
//
// Run one case at a time (`--case Values=1000`): run together, the dynamic PGO of the first case's process compiles the
// decode for its own sizes, and a change that takes another branch for small counts read 13 % slower on large ones.
using System;
using System.Text;

using BenchmarkDotNet.Attributes;

using Vorticity.Aggregating;
using Vorticity.Indexes;

namespace Vorticity.Benchmarks.Audit;

/// <summary>An object's counts of a column, decoded.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditSynopsisDecodeBenchmarks
{
    /// <summary>The column's distinct values.</summary>
    [Params(8, 1_000)]
    public int Values { get; set; }

    private byte[] _bytes = null!;

    /// <summary>Writes the counts.</summary>
    [GlobalSetup]
    public void Setup()
    {
        MeasureKind[] kinds = [MeasureKind.Signed, MeasureKind.Signed];
        int width = StateWords.Count(kinds);
        CountGroup[] groups = new CountGroup[Values];
        long[] words = new long[Values * width];
        string[] values = new string[Values];
        for (int v = 0; v < Values; v++)
        {
            values[v] = $"/api/v1/route/{v:D6}{new string('x', v % 9)}";
        }

        Array.Sort(values, StringComparer.Ordinal);
        for (int v = 0; v < Values; v++)
        {
            groups[v] = new CountGroup(CountedValue.Value, Encoding.UTF8.GetBytes(values[v]), v, 1);
            Span<long> state = words.AsSpan(v * width, width);
            StateWords.Clear(state, kinds);
            long rows = 100 + v;
            state[0] = rows;
            for (int m = 0, at = 1; m < kinds.Length; m++, at += MeasureLayout.IntegerWords)
            {
                state[at + MeasureLayout.Values] = rows;
                StateWords.Write((Int128)(rows * 7), state, at + MeasureLayout.Sum);
                state[at + MeasureLayout.IntegerLeast] = 1;
                state[at + MeasureLayout.IntegerGreatest] = 13;
            }
        }

        _bytes = new ColumnCounts([1, 2], kinds, groups, words).ToBytes();
        Console.WriteLine($"// {Values} values: {_bytes.Length} bytes, {Decode()} groups read");
    }

    /// <summary>The counts read back whole.</summary>
    [Benchmark]
    public int Decode() => ColumnCounts.From(_bytes).Groups.Length;
}
