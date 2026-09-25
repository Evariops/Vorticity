using System;
using System.Collections.Generic;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Benchmarks;

/// <summary>
/// The row lookup of a validity mask alone, as the writer's per-row loops ask it: the least of an
/// <c>i64</c> column's valid values, each row asking the mask first. The library's
/// <c>ValidityMask.IsValid</c> against a frozen copy of it, in one process and on one clock.
/// </summary>
/// <remarks>
/// <para>
/// <c>all-valid</c> is a column without nulls, still asked row by row; <c>nulls</c> one row in
/// seven null, as the corpus's nullable files have them; <c>random</c> half the rows null at random.
/// </para>
/// <para>The arms' answers are checked against each other before anything is timed.</para>
/// </remarks>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
[MinColumn]
[MedianColumn]
[CyclesColumn]
public class ValidityLookupBenchmarks
{
    private CanonicalArena _arena = null!;
    private Validity _validity;
    private long[] _values = [];

    /// <summary>The validity: <c>all-valid</c>, <c>nulls</c> or <c>random</c>.</summary>
    [ParamsSource(nameof(Shapes))]
    public string Shape { get; set; } = "nulls";

    /// <summary>Rows asked.</summary>
    [ParamsSource(nameof(RowCounts))]
    public int Rows { get; set; } = 8192;

    /// <summary>Every shape in every profile.</summary>
    public static IEnumerable<string> Shapes => ["all-valid", "nulls", "random"];

    /// <summary>One block, and sixteen.</summary>
    public static IEnumerable<int> RowCounts => [8192, 131_072];

    /// <summary>What one invocation reads: a value a row.</summary>
    /// <param name="method">Unused; every arm reads the same.</param>
    /// <param name="parameters">The case's rows.</param>
    public static (long Rows, long Bytes) BenchmarkWork(string method, IReadOnlyDictionary<string, object?> parameters)
    {
        int rows = (int)parameters[nameof(Rows)]!;
        return (rows, (long)rows * 8);
    }

    [GlobalSetup]
    public void Setup()
    {
        _arena = new CanonicalArena();
        DTypeArena types = new DTypeArena();
        Random random = new Random(20260925);
        _values = new long[Rows];
        for (int row = 0; row < Rows; row++)
        {
            _values[row] = random.NextInt64();
        }

        _validity = Validity.NonNullable;
        if (Shape != "all-valid")
        {
            VortexBuffer bits = _arena.Allocate((Rows + 7) / 8, 8, out Span<byte> bitBytes);
            for (int row = 0; row < Rows; row++)
            {
                if (Shape == "random" ? random.Next(2) == 0 : row % 7 != 3)
                {
                    bitBytes[row >> 3] |= (byte)(1 << (row & 7));
                }
            }

            _validity = Validity.Bitmap(_arena.AddBool(types.Bool(Nullability.NonNullable), Rows, Validity.NonNullable, bits, 0));
        }

        if (Original() != Current())
        {
            throw new InvalidOperationException($"{Shape}: the library's answer differs from the original's.");
        }
    }

    [Benchmark(Baseline = true)]
    public long Original()
    {
        ValidityMaskOriginal mask = ValidityMaskOriginal.From(_arena, _validity);
        long least = long.MaxValue;
        long[] values = _values;
        for (int row = 0; row < values.Length; row++)
        {
            if (!mask.IsValid(row))
            {
                continue;
            }

            least = Math.Min(least, values[row]);
        }

        return least;
    }

    [Benchmark]
    public long Current()
    {
        ValidityMask mask = ValidityMask.From(_arena, _validity);
        long least = long.MaxValue;
        long[] values = _values;
        for (int row = 0; row < values.Length; row++)
        {
            if (!mask.IsValid(row))
            {
                continue;
            }

            least = Math.Min(least, values[row]);
        }

        return least;
    }
}
