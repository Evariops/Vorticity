// Audit CO-03: what a column's values cost a consumer when they lie as the file laid them out, 16 bytes from a 64-byte
// boundary, against their copy into an aligned block, which the first typed access makes.
//
// 65,536 longs summed by 512-bit vectors (a consumer's pass): in an aligned block, in place 16 bytes off, and copied
// into an aligned block first, as ColumnData.Values does once a batch.
using System;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using BenchmarkDotNet.Attributes;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A pass over 65,536 longs, aligned, misaligned, or copied to alignment first.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public unsafe class AuditValuesAlignmentBenchmarks : IDisposable
{
    private const int Values = 65_536;

    private byte* _block;
    private byte* _copy;

    /// <summary>Allocates the blocks, 64-byte aligned, and fills the values.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _block = (byte*)NativeMemory.AlignedAlloc((nuint)((Values * 8) + 64), 64);
        _copy = (byte*)NativeMemory.AlignedAlloc((nuint)(Values * 8), 64);
        Random random = new Random(3);
        for (int i = 0; i < (Values * 8) + 64; i += 8)
        {
            *(long*)(_block + i) = random.Next();
        }
    }

    /// <summary>Frees the blocks.</summary>
    [GlobalCleanup]
    public void Dispose()
    {
        NativeMemory.AlignedFree(_block);
        NativeMemory.AlignedFree(_copy);
        GC.SuppressFinalize(this);
    }

    /// <summary>A pass over aligned values.</summary>
    [Benchmark(Baseline = true)]
    public long Aligned() => Sum(new ReadOnlySpan<long>(_block, Values));

    /// <summary>A pass over values 16 bytes off a boundary, read in place.</summary>
    [Benchmark]
    public long InPlace() => Sum(new ReadOnlySpan<long>(_block + 16, Values));

    /// <summary>The values copied into an aligned block, then a pass over them.</summary>
    [Benchmark]
    public long Copied()
    {
        new ReadOnlySpan<byte>(_block + 16, Values * 8).CopyTo(new Span<byte>(_copy, Values * 8));
        return Sum(new ReadOnlySpan<long>(_copy, Values));
    }

    private static long Sum(ReadOnlySpan<long> values)
    {
        Vector512<long> sum = Vector512<long>.Zero;
        ref long at = ref MemoryMarshal.GetReference(values);
        int i = 0;
        for (; i <= values.Length - 8; i += 8)
        {
            sum += Vector512.LoadUnsafe(ref at, (nuint)i);
        }

        return Vector512.Sum(sum);
    }
}
