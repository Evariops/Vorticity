// What a bucket of integers costs a value, the column's values floored to a multiple of its width.
//
// 1,048,576 values of 64 bits (and of 32), drawn among 10^12 (among 2^31) in no order, or sorted, floored to multiples
// of 1,000 by NumberBucket over a primitive block, as a group by on Bucket(size, 1000) reads its key.
using System;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A bucket of integers over a block of a million values.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditBucketBenchmarks
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

    private PType Storage => Bits == 64 ? PType.I64 : PType.I32;

    /// <summary>Whether the values come sorted, as a column of sizes in time order does not and of instants does.</summary>
    [Params(false, true)]
    public bool Sorted { get; set; }

    /// <summary>Draws the values into a block.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(41);
        _arena = new CanonicalArena();
        int width = Storage.ByteWidth();
        VortexBuffer buffer = _arena.Allocate(Rows * width, width, out Span<byte> bytes);
        if (Storage == PType.I64)
        {
            Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
            for (int i = 0; i < Rows; i++)
            {
                values[i] = random.NextInt64(1_000_000_000_000);
            }

            if (Sorted)
            {
                values.Sort();
            }
        }
        else
        {
            Span<int> values = MemoryMarshal.Cast<byte, int>(bytes);
            for (int i = 0; i < Rows; i++)
            {
                values[i] = random.Next();
            }

            if (Sorted)
            {
                values.Sort();
            }
        }

        _values = buffer;
        _dtype = new DTypeArena().Primitive(Storage, Nullability.NonNullable);
        _work = new CanonicalArena();
        _bucket = new NumberBucket(Storage, 1_000, 1_000.0, "1000");
    }

    /// <summary>The block's buckets, the middle one read.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public long Bucket()
    {
        _work.ResetKeepingBlocks();
        int column = _work.AddPrimitive(_dtype, Rows, Validity.NonNullable, Storage, _values);
        CanonicalNode node = _work.GetNode(_bucket.Apply(_work, column));
        return Storage == PType.I64 ? MemoryMarshal.Cast<byte, long>(node.Values.Span)[Rows / 2] : MemoryMarshal.Cast<byte, int>(node.Values.Span)[Rows / 2];
    }
}
