using System;
using System.Collections.Generic;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks;

/// <summary>
/// The transform a writer applies to each 1 024-row block of an integer column before packing it,
/// alone: the library's <c>ArrayBlobWriter.TransformBlock</c> against a frozen copy of it, in one
/// process and on one clock.
/// </summary>
/// <remarks>
/// <para>
/// <c>i64</c> is 64-bit values spanning twenty bits without nulls; <c>i64-nulls</c> the corpus's
/// nullable zstd column, <c>row % 17</c> with one row in seven null; <c>i32-nulls</c> 32-bit values
/// with one row in four null; <c>i64-random</c> the 64-bit values with half the rows null at random.
/// </para>
/// <para>The arms' blocks are checked against each other before anything is timed.</para>
/// </remarks>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
[MinColumn]
[MedianColumn]
[CyclesColumn]
public class TransformBenchmarks
{
    private CanonicalArena _arena = null!;
    private int _node;
    private BitPackPlan _plan;
    private readonly ulong[] _wide = new ulong[FastLanes.BlockSize];

    /// <summary>The column: <c>i64</c>, <c>i64-nulls</c>, <c>i32-nulls</c> or <c>i64-random</c>.</summary>
    [ParamsSource(nameof(Shapes))]
    public string Shape { get; set; } = "i64-nulls";

    /// <summary>Rows of the column.</summary>
    [ParamsSource(nameof(RowCounts))]
    public int Rows { get; set; } = 8192;

    /// <summary>Every shape in every profile.</summary>
    public static IEnumerable<string> Shapes => ["i64", "i64-nulls", "i32-nulls", "i64-random"];

    /// <summary>A small chunk, and a large one.</summary>
    public static IEnumerable<int> RowCounts => [8192, 131_072];

    /// <summary>What one invocation reads: a value a row.</summary>
    /// <param name="method">Unused; every arm reads the same.</param>
    /// <param name="parameters">The case's rows.</param>
    public static (long Rows, long Bytes) BenchmarkWork(string method, IReadOnlyDictionary<string, object?> parameters)
    {
        int rows = (int)parameters[nameof(Rows)]!;
        return (rows, (long)rows * ((string)parameters[nameof(Shape)]! == "i32-nulls" ? 4 : 8));
    }

    [GlobalSetup]
    public void Setup()
    {
        _arena = new CanonicalArena();
        DTypeArena types = new DTypeArena();
        Random random = new Random(20260925);
        bool narrow = Shape == "i32-nulls";
        int width = narrow ? 4 : 8;
        VortexBuffer values = _arena.Allocate(Rows * width, 64, out Span<byte> bytes);
        for (int row = 0; row < Rows; row++)
        {
            long value = Shape == "i64-nulls" ? row % 17 : 1_000_000_007L + random.Next(1 << 20);
            if (narrow)
            {
                BitConverter.TryWriteBytes(bytes[(row * 4)..], (int)value);
            }
            else
            {
                BitConverter.TryWriteBytes(bytes[(row * 8)..], value);
            }
        }

        Validity validity = Validity.NonNullable;
        bool nulls = Shape != "i64";
        if (nulls)
        {
            VortexBuffer bits = _arena.Allocate((Rows + 7) / 8, 8, out Span<byte> bitBytes);
            for (int row = 0; row < Rows; row++)
            {
                bool valid = Shape switch
                {
                    "i64-nulls" => row % 7 != 3,
                    "i32-nulls" => row % 4 != 1,
                    _ => random.Next(2) == 0,
                };
                if (valid)
                {
                    bitBytes[row >> 3] |= (byte)(1 << (row & 7));
                }
            }

            validity = Validity.Bitmap(_arena.AddBool(types.Bool(Nullability.NonNullable), Rows, Validity.NonNullable, bits, 0));
        }

        PType ptype = narrow ? PType.I32 : PType.I64;
        _node = _arena.AddPrimitive(
            types.Primitive(ptype, nulls ? Nullability.Nullable : Nullability.NonNullable), Rows, validity, ptype, values);
        _plan = BitPackPlan.TryBuild(_arena, _arena.GetNode(_node))
            ?? throw new InvalidOperationException($"{Shape}: the column does not bit-pack.");

        CanonicalNode node = _arena.GetNode(_node);
        ValidityMask mask = ValidityMask.From(_arena, node.Validity);
        ulong[] expected = new ulong[FastLanes.BlockSize];
        int elementBits = width * 8;
        for (int start = 0; start < Rows; start += FastLanes.BlockSize)
        {
            int count = Math.Min(FastLanes.BlockSize, Rows - start);
            TransformOriginal.TransformBlock(node.Values.Span, in mask, ptype, start, count, _plan, elementBits, expected);
            ArrayBlobWriter.TransformBlock(node.Values.Span, in mask, ptype, start, count, _plan, elementBits, _wide);
            if (!expected.AsSpan(0, count).SequenceEqual(_wide.AsSpan(0, count)))
            {
                throw new InvalidOperationException($"{Shape}: block {start / FastLanes.BlockSize} differs from the original's.");
            }
        }
    }

    [Benchmark(Baseline = true)]
    public ulong Original()
    {
        CanonicalNode node = _arena.GetNode(_node);
        ValidityMask mask = ValidityMask.From(_arena, node.Validity);
        PType ptype = node.PType;
        int elementBits = ptype.ByteWidth() * 8;
        ReadOnlySpan<byte> values = node.Values.Span;
        ulong sum = 0;
        for (int start = 0; start < Rows; start += FastLanes.BlockSize)
        {
            TransformOriginal.TransformBlock(values, in mask, ptype, start, FastLanes.BlockSize, _plan, elementBits, _wide);
            sum += _wide[FastLanes.BlockSize - 1];
        }

        return sum;
    }

    [Benchmark]
    public ulong Current()
    {
        CanonicalNode node = _arena.GetNode(_node);
        ValidityMask mask = ValidityMask.From(_arena, node.Validity);
        PType ptype = node.PType;
        int elementBits = ptype.ByteWidth() * 8;
        ReadOnlySpan<byte> values = node.Values.Span;
        ulong sum = 0;
        for (int start = 0; start < Rows; start += FastLanes.BlockSize)
        {
            ArrayBlobWriter.TransformBlock(values, in mask, ptype, start, FastLanes.BlockSize, _plan, elementBits, _wide);
            sum += _wide[FastLanes.BlockSize - 1];
        }

        return sum;
    }
}
