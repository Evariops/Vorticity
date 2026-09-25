using System;
using System.Collections.Generic;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks;

/// <summary>
/// The bit-packing decision alone, the width histograms a writer takes over each integer column
/// of a chunk: the library's <c>BitPackPlan.TryBuild</c> against a frozen copy of it as it was, in
/// one process and on one clock.
/// </summary>
/// <remarks>
/// <para>
/// <c>i64</c> is signed values with the minimum known and no ingest pass, so both histograms are
/// taken; <c>u64</c> unsigned ones, the framed histogram alone; <c>i64-nulls</c> the signed values
/// with one row in ten null. The values span about twenty bits over a random base.
/// </para>
/// <para>The arms' plans are checked against each other before anything is timed.</para>
/// </remarks>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
[MinColumn]
[MedianColumn]
[CyclesColumn]
public class BitPackPlanBenchmarks
{
    private CanonicalArena _arena = null!;
    private int _node;
    private ulong _minimum;

    /// <summary>The column: <c>i64</c>, <c>u64</c> or <c>i64-nulls</c>.</summary>
    [ParamsSource(nameof(Shapes))]
    public string Shape { get; set; } = "i64";

    /// <summary>Rows of the chunk.</summary>
    [ParamsSource(nameof(RowCounts))]
    public int Rows { get; set; } = 8192;

    /// <summary>Every shape in every profile.</summary>
    public static IEnumerable<string> Shapes => ["i64", "u64", "i64-nulls"];

    /// <summary>A small chunk, and a large one.</summary>
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
        bool signed = Shape != "u64";
        bool nulls = Shape == "i64-nulls";
        Random random = new Random(20260925);
        VortexBuffer values = _arena.Allocate(Rows * 8, 64, out Span<byte> bytes);
        long least = long.MaxValue;
        for (int row = 0; row < Rows; row++)
        {
            long value = 1_000_000_007L + random.Next(1 << 20);
            BitConverter.TryWriteBytes(bytes[(row * 8)..], value);
            least = Math.Min(least, value);
        }

        _minimum = (ulong)least;
        Validity validity = Validity.NonNullable;
        if (nulls)
        {
            VortexBuffer bits = _arena.Allocate((Rows + 7) / 8, 8, out Span<byte> bitBytes);
            for (int row = 0; row < Rows; row++)
            {
                if (row % 10 != 3)
                {
                    bitBytes[row >> 3] |= (byte)(1 << (row & 7));
                }
            }

            validity = Validity.Bitmap(_arena.AddBool(types.Bool(Nullability.NonNullable), Rows, Validity.NonNullable, bits, 0));
        }

        PType ptype = signed ? PType.I64 : PType.U64;
        _node = _arena.AddPrimitive(types.Primitive(ptype, nulls ? Nullability.Nullable : Nullability.NonNullable), Rows, validity, ptype, values);

        BitPackPlanOriginal? expected = BitPackPlanOriginal.TryBuild(_arena, _arena.GetNode(_node), reference: _minimum);
        BitPackPlan? got = BitPackPlan.TryBuild(_arena, _arena.GetNode(_node), reference: _minimum);
        if (expected?.BitWidth != got?.BitWidth || expected?.Transform != got?.Transform || expected?.Exceptions != got?.Exceptions)
        {
            throw new InvalidOperationException($"{Shape}: the library's plan differs from the original's.");
        }
    }

    [Benchmark(Baseline = true)]
    public int Original() => BitPackPlanOriginal.TryBuild(_arena, _arena.GetNode(_node), reference: _minimum)?.BitWidth ?? -1;

    [Benchmark]
    public int Current() => BitPackPlan.TryBuild(_arena, _arena.GetNode(_node), reference: _minimum)?.BitWidth ?? -1;
}
