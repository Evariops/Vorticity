// What a bucket of floats costs a value, the column's values floored to a multiple of its width, as a group
// by on Bucket(latency, 2.5) reads its key.
//
// 1,048,576 doubles (or floats) drawn in [0, 1000), floored to multiples of 2.5 by NumberBucket over a primitive block.
using System;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A bucket of floats over a block of a million values.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditFloatBucketBenchmarks
{
    private const int Rows = 1 << 20;

    private CanonicalArena _arena = null!;
    private CanonicalArena _work = null!;
    private NumberBucket _bucket = null!;
    private VortexBuffer _values;
    private DType _dtype;

    /// <summary>The values' storage.</summary>
    [Params(64, 32)]
    public int Bits { get; set; }

    private PType Storage => Bits == 64 ? PType.F64 : PType.F32;

    /// <summary>Draws the values into a block.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(157);
        _arena = new CanonicalArena();
        int width = Storage.ByteWidth();
        _values = _arena.Allocate(Rows * width, width, out Span<byte> bytes);
        for (int i = 0; i < Rows; i++)
        {
            double value = random.NextDouble() * 1_000;
            if (Bits == 64)
            {
                MemoryMarshal.Cast<byte, double>(bytes)[i] = value;
            }
            else
            {
                MemoryMarshal.Cast<byte, float>(bytes)[i] = (float)value;
            }
        }

        _dtype = new DTypeArena().Primitive(Storage, Nullability.NonNullable);
        _work = new CanonicalArena();
        _bucket = new NumberBucket(Storage, 0, 2.5, "2.5");
    }

    /// <summary>The block's buckets, the middle one read.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public double Bucket()
    {
        _work.ResetKeepingBlocks();
        int column = _work.AddPrimitive(_dtype, Rows, Validity.NonNullable, Storage, _values);
        CanonicalNode node = _work.GetNode(_bucket.Apply(_work, column));
        return Storage == PType.F64 ? MemoryMarshal.Cast<byte, double>(node.Values.Span)[Rows / 2] : MemoryMarshal.Cast<byte, float>(node.Values.Span)[Rows / 2];
    }
}
