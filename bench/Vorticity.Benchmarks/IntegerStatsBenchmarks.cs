using System;
using System.Collections.Generic;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks;

/// <summary>
/// The block statistics of an integer column alone, its bounds, runs, order and width histograms:
/// the library's <c>BlockStatsPass.Accumulate</c> against a frozen copy of the pass as it was, a
/// block of 8 192 rows at a time, in one process and on one clock.
/// </summary>
/// <remarks>
/// <para>
/// <c>i64</c> is random values without nulls; <c>i64-seq</c> a progression climbing by three, as
/// the corpus's wide table's columns are; <c>i64-nulls</c> the corpus's nullable zstd column,
/// <c>row % 17</c> with one row in seven null; <c>u32-nulls</c> random 32-bit values with one row in
/// four null; <c>i64-random</c> random values with half the rows null at random.
/// </para>
/// <para>The arms' statistics and histograms are checked against each other before anything is timed.</para>
/// </remarks>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
[MinColumn]
[MedianColumn]
[CyclesColumn]
public class IntegerStatsBenchmarks
{
    private const int BlockRows = 8192;

    private CanonicalArena _arena = null!;
    private int _node;
    private readonly int[] _widths = new int[BitPackWidths.Length];

    /// <summary>The column: <c>i64</c>, <c>i64-seq</c>, <c>i64-nulls</c>, <c>u32-nulls</c> or <c>i64-random</c>.</summary>
    [ParamsSource(nameof(Shapes))]
    public string Shape { get; set; } = "i64-nulls";

    /// <summary>Rows summarized in one call.</summary>
    [ParamsSource(nameof(RowCounts))]
    public int Rows { get; set; } = BlockRows;

    /// <summary>Every shape in every profile.</summary>
    public static IEnumerable<string> Shapes => ["i64", "i64-seq", "i64-nulls", "u32-nulls", "i64-random"];

    /// <summary>One block, and sixteen.</summary>
    public static IEnumerable<int> RowCounts => [BlockRows, 131_072];

    /// <summary>What one invocation reads: a value a row.</summary>
    /// <param name="method">Unused; every arm reads the same.</param>
    /// <param name="parameters">The case's rows.</param>
    public static (long Rows, long Bytes) BenchmarkWork(string method, IReadOnlyDictionary<string, object?> parameters)
    {
        int rows = (int)parameters[nameof(Rows)]!;
        return (rows, (long)rows * ((string)parameters[nameof(Shape)]! == "u32-nulls" ? 4 : 8));
    }

    [GlobalSetup]
    public void Setup()
    {
        _arena = new CanonicalArena();
        DTypeArena types = new DTypeArena();
        Random random = new Random(20260925);
        bool narrow = Shape == "u32-nulls";
        int width = narrow ? 4 : 8;
        VortexBuffer values = _arena.Allocate(Rows * width, 64, out Span<byte> bytes);
        for (int row = 0; row < Rows; row++)
        {
            if (narrow)
            {
                BitConverter.TryWriteBytes(bytes[(row * 4)..], (uint)random.Next());
            }
            else
            {
                long value = Shape switch
                {
                    "i64-nulls" => row % 17,
                    "i64-seq" => 1_000_000_007L + (3L * row),
                    _ => random.NextInt64(),
                };
                BitConverter.TryWriteBytes(bytes[(row * 8)..], value);
            }
        }

        Validity validity = Validity.NonNullable;
        bool nulls = Shape is not ("i64" or "i64-seq");
        if (nulls)
        {
            VortexBuffer bits = _arena.Allocate((Rows + 7) / 8, 8, out Span<byte> bitBytes);
            for (int row = 0; row < Rows; row++)
            {
                bool valid = Shape switch
                {
                    "i64-nulls" => row % 7 != 3,
                    "u32-nulls" => row % 4 != 1,
                    _ => random.Next(2) == 0,
                };
                if (valid)
                {
                    bitBytes[row >> 3] |= (byte)(1 << (row & 7));
                }
            }

            validity = Validity.Bitmap(_arena.AddBool(types.Bool(Nullability.NonNullable), Rows, Validity.NonNullable, bits, 0));
        }

        PType ptype = narrow ? PType.U32 : PType.I64;
        _node = _arena.AddPrimitive(
            types.Primitive(ptype, nulls ? Nullability.Nullable : Nullability.NonNullable), Rows, validity, ptype, values);

        int[] expectedWidths = new int[BitPackWidths.Length];
        int[] gotWidths = new int[BitPackWidths.Length];
        for (int start = 0; start < Rows; start += BlockRows)
        {
            BlockStats expected = default;
            BlockStats got = default;
            BlockStatsPassOriginal.Accumulate(_arena, _node, start, BlockRows, ref expected, widths: expectedWidths);
            BlockStatsPass.Accumulate(_arena, _node, start, BlockRows, ref got, widths: gotWidths);
            if (!expected.Equals(got) || !expectedWidths.AsSpan().SequenceEqual(gotWidths))
            {
                throw new InvalidOperationException($"{Shape}: block {start / BlockRows}'s statistics differ from the original's.");
            }
        }
    }

    [Benchmark(Baseline = true)]
    public long Original()
    {
        long boundaries = 0;
        for (int start = 0; start < Rows; start += BlockRows)
        {
            BlockStats stats = default;
            BlockStatsPassOriginal.Accumulate(_arena, _node, start, BlockRows, ref stats, widths: _widths);
            boundaries += stats.RunBoundaries;
        }

        return boundaries;
    }

    [Benchmark]
    public long Current()
    {
        long boundaries = 0;
        for (int start = 0; start < Rows; start += BlockRows)
        {
            BlockStats stats = default;
            BlockStatsPass.Accumulate(_arena, _node, start, BlockRows, ref stats, widths: _widths);
            boundaries += stats.RunBoundaries;
        }

        return boundaries;
    }
}
