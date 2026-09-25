using System;
using System.Collections.Generic;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks;

/// <summary>
/// A float column's ALP encoding alone, exponents chosen, integers laid out and exceptions
/// gathered: the library's <c>AlpPlan.TryBuild</c> against a frozen copy of it, in one process and
/// on one clock.
/// </summary>
/// <remarks>
/// <para>
/// Prices with two decimals, one in a thousand not: <c>f64</c> without nulls, <c>f64-nulls</c>
/// with one row in seven null, <c>f64-random</c> with half the rows null at random, and
/// <c>f32-nulls</c> single precision, prices below a hundred, with one row in four null.
/// </para>
/// <para>The arms' plans are checked against each other before anything is timed.</para>
/// </remarks>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
[MinColumn]
[MedianColumn]
[CyclesColumn]
public class AlpEncodeBenchmarks
{
    private CanonicalArena _arena = null!;
    private int _node;
    private long _plain;

    /// <summary>The column: <c>f64</c>, <c>f64-nulls</c>, <c>f64-random</c> or <c>f32-nulls</c>.</summary>
    [ParamsSource(nameof(Shapes))]
    public string Shape { get; set; } = "f64-nulls";

    /// <summary>Rows of the column.</summary>
    [ParamsSource(nameof(RowCounts))]
    public int Rows { get; set; } = 8192;

    /// <summary>Every shape in every profile.</summary>
    public static IEnumerable<string> Shapes => ["f64", "f64-nulls", "f64-random", "f32-nulls"];

    /// <summary>A small chunk, and a large one.</summary>
    public static IEnumerable<int> RowCounts => [8192, 131_072];

    /// <summary>What one invocation reads: a value a row.</summary>
    /// <param name="method">Unused; every arm reads the same.</param>
    /// <param name="parameters">The case's rows.</param>
    public static (long Rows, long Bytes) BenchmarkWork(string method, IReadOnlyDictionary<string, object?> parameters)
    {
        int rows = (int)parameters[nameof(Rows)]!;
        return (rows, (long)rows * ((string)parameters[nameof(Shape)]! == "f32-nulls" ? 4 : 8));
    }

    [GlobalSetup]
    public void Setup()
    {
        _arena = new CanonicalArena();
        DTypeArena types = new DTypeArena();
        Random random = new Random(20260925);
        bool single = Shape == "f32-nulls";
        int width = single ? 4 : 8;
        VortexBuffer values = _arena.Allocate(Rows * width, 64, out Span<byte> bytes);
        for (int row = 0; row < Rows; row++)
        {
            double price = random.Next(single ? 10_000 : 1_000_000) / 100.0;
            if (row % 1000 == 999)
            {
                price = Math.PI * row;
            }

            if (single)
            {
                BitConverter.TryWriteBytes(bytes[(row * 4)..], (float)price);
            }
            else
            {
                BitConverter.TryWriteBytes(bytes[(row * 8)..], price);
            }
        }

        Validity validity = Validity.NonNullable;
        bool nulls = Shape != "f64";
        if (nulls)
        {
            VortexBuffer bits = _arena.Allocate((Rows + 7) / 8, 8, out Span<byte> bitBytes);
            for (int row = 0; row < Rows; row++)
            {
                bool valid = Shape switch
                {
                    "f64-nulls" => row % 7 != 3,
                    "f32-nulls" => row % 4 != 1,
                    _ => random.Next(2) == 0,
                };
                if (valid)
                {
                    bitBytes[row >> 3] |= (byte)(1 << (row & 7));
                }
            }

            validity = Validity.Bitmap(_arena.AddBool(types.Bool(Nullability.NonNullable), Rows, Validity.NonNullable, bits, 0));
        }

        PType ptype = single ? PType.F32 : PType.F64;
        _node = _arena.AddPrimitive(
            types.Primitive(ptype, nulls ? Nullability.Nullable : Nullability.NonNullable), Rows, validity, ptype, values);
        _plain = (long)Rows * width;

        AlpPlanOriginal expected = AlpPlanOriginal.TryBuild(_arena, _node, _plain)
            ?? throw new InvalidOperationException($"{Shape}: the original does not encode the column.");
        AlpPlan got = AlpPlan.TryBuild(_arena, _node, _plain)
            ?? throw new InvalidOperationException($"{Shape}: the library does not encode the column.");
        bool same = expected.Encoded.SequenceEqual(got.Encoded) && expected.PatchIndices.SequenceEqual(got.PatchIndices)
            && expected.PatchValues.SequenceEqual(got.PatchValues) && expected.EncodedSize == got.EncodedSize;
        expected.Release();
        got.Release();
        if (!same)
        {
            throw new InvalidOperationException($"{Shape}: the library's plan differs from the original's.");
        }
    }

    [Benchmark(Baseline = true)]
    public long Original()
    {
        AlpPlanOriginal plan = AlpPlanOriginal.TryBuild(_arena, _node, _plain)!.Value;
        long size = plan.EncodedSize;
        plan.Release();
        return size;
    }

    [Benchmark]
    public long Current()
    {
        AlpPlan plan = AlpPlan.TryBuild(_arena, _node, _plain)!.Value;
        long size = plan.EncodedSize;
        plan.Release();
        return size;
    }
}
