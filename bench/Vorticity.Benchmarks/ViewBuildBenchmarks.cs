using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Types;

namespace Vorticity.Benchmarks;

/// <summary>
/// The cut of a decoded heap into views alone, the step every encoding that tiles a heap ends
/// with: the library's <c>ViewKernels.BuildFromOffsets</c> and <c>BuildFromLengths</c> against a
/// frozen copy of the kernels as they were, in one process and on one clock.
/// </summary>
/// <remarks>
/// <para>
/// The shapes are the per-encoding corpus files': <c>varbin</c>, values of 7 to 12 bytes by
/// offsets, every view inline; <c>fsst</c>, 52-byte URLs by lengths, every view out of line;
/// <c>onpair</c>, values of 16 to 20 bytes by lengths; <c>mixed</c>, values of 0 to 19 bytes by
/// offsets, both kinds. All of it is ASCII text in a Utf8 column without nulls.
/// </para>
/// <para>The arms are checked against each other, view for view, before anything is timed.</para>
/// </remarks>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
[MinColumn]
[MedianColumn]
[CyclesColumn]
public class ViewBuildBenchmarks
{
    private const int ViewSize = 16;

    private byte[] _heap = [];
    private byte[] _rows = [];
    private byte[] _views = [];
    private bool _byOffsets;

    /// <summary>The values and how they are cut.</summary>
    [ParamsSource(nameof(Shapes))]
    public string Shape { get; set; } = "varbin";

    /// <summary>Rows cut in one call.</summary>
    [ParamsSource(nameof(RowCounts))]
    public int Rows { get; set; } = 1024;

    /// <summary>Every shape in every profile.</summary>
    public static IEnumerable<string> Shapes => ["varbin", "fsst", "onpair", "mixed"];

    /// <summary>Rows whose views stay in the first-level cache, and a scan window.</summary>
    public static IEnumerable<int> RowCounts => [1024, 131_072];

    /// <summary>What one invocation writes: a view a row.</summary>
    /// <param name="method">Unused; every arm writes the same.</param>
    /// <param name="parameters">The case's rows.</param>
    public static (long Rows, long Bytes) BenchmarkWork(string method, IReadOnlyDictionary<string, object?> parameters)
    {
        int rows = (int)parameters[nameof(Rows)]!;
        return (rows, (long)rows * ViewSize);
    }

    [GlobalSetup]
    public void Setup()
    {
        _byOffsets = Shape is "varbin" or "mixed";
        StringBuilder text = new StringBuilder();
        int[] lengths = new int[Rows];
        for (int row = 0; row < Rows; row++)
        {
            string value = Shape switch
            {
                "varbin" => string.Create(CultureInfo.InvariantCulture, $"value-{row}"),
                "fsst" => string.Create(CultureInfo.InvariantCulture, $"https://example.invalid/vortex/conformance/{row:D9}"),
                "onpair" => string.Create(CultureInfo.InvariantCulture, $"prefix-{row % 32}-suffix-{row % 7}"),
                _ => new string('x', row % 20),
            };
            text.Append(value);
            lengths[row] = value.Length;
        }

        _heap = Encoding.ASCII.GetBytes(text.ToString());
        if (_byOffsets)
        {
            _rows = new byte[(Rows + 1) * 4];
            int at = 0;
            for (int row = 0; row <= Rows; row++)
            {
                BitConverter.TryWriteBytes(_rows.AsSpan(row * 4), (uint)at);
                at += row < Rows ? lengths[row] : 0;
            }
        }
        else
        {
            _rows = new byte[Rows * 4];
            for (int row = 0; row < Rows; row++)
            {
                BitConverter.TryWriteBytes(_rows.AsSpan(row * 4), (uint)lengths[row]);
            }
        }

        _views = new byte[Rows * ViewSize];
        Original();
        byte[] expected = (byte[])_views.Clone();
        _views.AsSpan().Fill(0xA5);
        Current();
        if (!_views.AsSpan().SequenceEqual(expected))
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"{Shape}: the library's views differ from the original's at byte {_views.AsSpan().CommonPrefixLength(expected)}."));
        }
    }

    [Benchmark(Baseline = true)]
    public int Original()
    {
        if (_byOffsets)
        {
            ViewKernelsOriginal.BuildFromOffsets(_rows, PType.U32, _heap, _views, Rows, true, ValidityMask.NonNullable, "vortex.varbin");
            return Rows;
        }

        return ViewKernelsOriginal.BuildFromLengths(_rows, PType.U32, default, _heap, _views, Rows, true) ? Rows : -Rows;
    }

    [Benchmark]
    public int Current()
    {
        if (_byOffsets)
        {
            ViewKernels.BuildFromOffsets(_rows, PType.U32, _heap, _views, Rows, true, ValidityMask.NonNullable, "vortex.varbin");
            return Rows;
        }

        return ViewKernels.BuildFromLengths(_rows, PType.U32, default, _heap, _views, Rows, true) ? Rows : -Rows;
    }
}
