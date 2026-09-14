// The materialized form of a constant column against the form Z1b would introduce, on the three
// arms the §3.7 entry conditions require: build, consume, take.
//
// WHAT Z1b PROPOSES. A canonical kind that is "one value and a length" instead of N copies of that
// value. `ConstantCanonicalizer` tiles today (`:214-321`): it writes the element once and doubles
// it over the whole column, which for a million rows of eight bytes is eight megabytes to say one
// number. The constant form stores the element and the length, and every consumer resolves a row
// by returning the same window.
//
// WHY THE BENCH EXISTS AT ALL, and it is not to find out whether building is cheaper -- of course
// it is, and R22 already measured that side: on the `constant` axis `Tile` is ENTIRELY bytes
// (+44,6 % when the work is doubled, and nothing when only the calls are), so the materialized
// build is exactly the bytes a constant form would not write. The question this class answers is
// the OTHER two arms. §2.4 claims "coût consommateur : aucun", and §3.7 condition 3 refuses to take
// that on trust: a form that saves on build and loses on every read is a bad trade, and the only
// way to know is to run both.
//
// THE TWO WIDTHS ARE A PRIMITIVE AND A VIEW, not a sweep. Eight bytes is an `i64` constant, the
// common `vortex.constant`; sixteen is an Arrow view, which is what a variant's `metadata` column
// is made of and the case §2.4 says Z1b conditions Z2 for. The arms are expected to differ in
// magnitude across them, not in sign.
//
// THE CONSTANT ARMS MODEL THE FORM, they do not call it -- it does not exist yet, and building it
// is Z1b-c. That is the same thing `VarBinFormBenchmarks` does for the offsets form, and it is
// sound for the same reason: what is being priced is the memory traffic and the per-row work each
// shape implies, and both are fully determined by the shape.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

using Vorticity.Arrays.Decoders.Compressed;

namespace Vorticity.Benchmarks;

/// <summary>Materialized against constant, on build, consume and take. A probe: <c>--explore</c>.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class ConstantFormBenchmarks
{
    /// <summary>Rows per operation, as <see cref="VarBinFormBenchmarks"/> uses.</summary>
    private const int Rows = 1 << 16;

    /// <summary>Rows a scattered take asks for, matching `--throughput --take`.</summary>
    private const int TakeRows = 64;

    /// <summary>
    /// Bytes per value. 8 is an `i64` constant, 16 is an Arrow view -- a variant's `metadata`.
    /// </summary>
    [Params(8, 16)]
    public int Width { get; set; } = 8;

    private byte[] _element = [];
    private byte[] _materialized = [];
    private byte[] _taken = [];
    private int[] _wanted = [];

    /// <summary>What one invocation moves, per arm.</summary>
    /// <param name="method">The benchmark method's name.</param>
    /// <param name="parameters">The case's <c>[Params]</c>, which carry <see cref="Width"/>.</param>
    /// <returns>Rows and bytes, for the ns/row and GB/s columns.</returns>
    /// <remarks>
    /// The constant arms move ONE element whatever the row count, which is the entire claim, so
    /// their byte figure is the element and not the column. Reporting the column for both would
    /// make the constant form look like it moved eight megabytes it never touches.
    /// </remarks>
    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(parameters);
        long width = parameters.TryGetValue(nameof(Width), out object? value) && value is int w ? w : 0;

        return method switch
        {
            nameof(BuildMaterialized) => (Rows, Rows * width),
            nameof(BuildConstant) => (Rows, width),
            nameof(ConsumeMaterialized) => (Rows, Rows * width),
            nameof(ConsumeConstant) => (Rows, width),
            nameof(TakeMaterialized) => (TakeRows, TakeRows * 2L * width),
            _ => (TakeRows, TakeRows * width),
        };
    }

    [GlobalSetup]
    public void Setup()
    {
        _element = new byte[Width];
        for (int i = 0; i < _element.Length; i++)
        {
            _element[i] = (byte)(i + 1);
        }

        _materialized = new byte[Rows * Width];
        RowKernels.Tile(_materialized, _element);

        _taken = new byte[TakeRows * Width];
        _wanted = new int[TakeRows];
        for (int i = 0; i < TakeRows; i++)
        {
            _wanted[i] = i * (Rows / TakeRows);
        }
    }

    // ------------------------------------------------------------------------------------ build

    /// <summary>What `ConstantCanonicalizer` pays today: the element, tiled over every row.</summary>
    [Benchmark(Description = "build, materialized")]
    [BenchmarkCategory("build")]
    public int BuildMaterialized()
    {
        RowKernels.Tile(_materialized, _element);
        return _materialized.Length;
    }

    /// <summary>What the constant form would pay: the element, once.</summary>
    [Benchmark(Baseline = true, Description = "build, constant")]
    [BenchmarkCategory("build")]
    public int BuildConstant()
    {
        _element.AsSpan().CopyTo(_materialized.AsSpan(0, Width));
        return Width;
    }

    // ---------------------------------------------------------------------------------- consume

    /// <summary>
    /// A consumer walking every row of the materialized column, one window per row.
    /// </summary>
    /// <remarks>
    /// The fold is what keeps the reads from being elided; it is the same in both consume arms, so
    /// the distance between them is the addressing and the memory traffic and nothing else.
    /// </remarks>
    [Benchmark(Description = "consume every row, materialized")]
    [BenchmarkCategory("consume")]
    public int ConsumeMaterialized()
    {
        ReadOnlySpan<byte> column = _materialized;
        int width = Width;
        int fold = 0;
        for (int row = 0; row < Rows; row++)
        {
            ReadOnlySpan<byte> value = column.Slice(row * width, width);
            fold += value[0] + value[width - 1];
        }

        return fold;
    }

    /// <summary>The same walk over a constant column: every row resolves to the same window.</summary>
    [Benchmark(Description = "consume every row, constant")]
    [BenchmarkCategory("consume")]
    public int ConsumeConstant()
    {
        ReadOnlySpan<byte> element = _element;
        int width = Width;
        int fold = 0;
        for (int row = 0; row < Rows; row++)
        {
            ReadOnlySpan<byte> value = element;
            fold += value[0] + value[width - 1];
        }

        return fold;
    }

    // ------------------------------------------------------------------------------------- take

    /// <summary>Sixty-four scattered rows gathered out of the materialized column.</summary>
    [Benchmark(Description = "take 64 scattered, materialized")]
    [BenchmarkCategory("take")]
    public int TakeMaterialized()
    {
        ReadOnlySpan<byte> column = _materialized;
        Span<byte> destination = _taken;
        int width = Width;
        for (int i = 0; i < _wanted.Length; i++)
        {
            column.Slice(_wanted[i] * width, width).CopyTo(destination.Slice(i * width, width));
        }

        return destination.Length;
    }

    /// <summary>
    /// The same take from a constant column, which stays constant: the output is the element and a
    /// length, so the rows are never gathered at all.
    /// </summary>
    /// <remarks>
    /// THIS IS THE ARM THAT COULD HAVE GONE WRONG, and the reason §3.7 asks for it. §2.4's rule for
    /// Z1a is that "a filter's or a take's output stays VarBinView whatever the input", because the
    /// take and filter axes would regress otherwise. A constant has no such constraint -- taking N
    /// rows of one value gives one value and a length -- so the take arm writes the element once
    /// rather than gathering. If that turned out to be wrong the form would have to materialize on
    /// take, and the arm would say so.
    /// </remarks>
    [Benchmark(Description = "take 64 scattered, constant")]
    [BenchmarkCategory("take")]
    public int TakeConstant()
    {
        _element.AsSpan().CopyTo(_taken.AsSpan(0, Width));
        return Width;
    }
}
