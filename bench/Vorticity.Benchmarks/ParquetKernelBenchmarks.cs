// The Parquet decoders' kernels, each against a copy of its output: what decoding a page costs
// beyond moving its values. A million values of each shape a column takes: a key that climbs by one
// (every miniblock 0 bits wide), small and medium deltas, and noise (the full width).
using System;

using BenchmarkDotNet.Attributes;

using Vorticity.Parquet.Encodings;

namespace Vorticity.Benchmarks;

/// <summary>DELTA_BINARY_PACKED and BYTE_STREAM_SPLIT decoded, against a copy of their output.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
public class ParquetKernelBenchmarks
{
    private const int Count = 1 << 20;

    private readonly long[] _longs = new long[Count];
    private readonly int[] _ints = new int[Count];
    private readonly long[] _decodedLongs = new long[Count];
    private readonly int[] _decodedInts = new int[Count];
    private byte[] _deltas64 = [];
    private byte[] _deltas32 = [];
    private readonly byte[] _split = new byte[Count * sizeof(double)];
    private readonly byte[] _gathered = new byte[Count * sizeof(double)];

    /// <summary>The values' shape: climbing by one, small or medium deltas, or noise.</summary>
    [Params("climbing", "small", "medium", "noise")]
    public string Shape { get; set; } = "climbing";

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(11);
        long last = 1_000_000;
        for (int i = 0; i < Count; i++)
        {
            last += Shape switch
            {
                "climbing" => 1,
                "small" => random.Next(0, 16),
                "medium" => random.Next(-2_000, 2_000),
                _ => random.NextInt64(),
            };
            _longs[i] = last;
            _ints[i] = (int)last;
        }

        _deltas64 = new byte[DeltaBinaryPacked.Size64(_longs)];
        DeltaBinaryPacked.Encode64(_longs, _deltas64);
        _deltas32 = new byte[DeltaBinaryPacked.Size32(_ints)];
        DeltaBinaryPacked.Encode32(_ints, _deltas32);
        ByteStreamSplit.Encode(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_longs.AsSpan()), sizeof(double), _split);
    }

    [Benchmark(Baseline = true, Description = "copy 8 MB")]
    public long Copy()
    {
        _longs.AsSpan().CopyTo(_decodedLongs);
        return _decodedLongs[^1];
    }

    [Benchmark(Description = "DELTA_BINARY_PACKED, INT64")]
    public long Delta64()
    {
        DeltaBinaryPacked.Decode64(_deltas64, _decodedLongs);
        return _decodedLongs[^1];
    }

    [Benchmark(Description = "DELTA_BINARY_PACKED, INT32")]
    public int Delta32()
    {
        DeltaBinaryPacked.Decode32(_deltas32, _decodedInts);
        return _decodedInts[^1];
    }

    [Benchmark(Description = "BYTE_STREAM_SPLIT, 8 bytes")]
    public byte Split()
    {
        ByteStreamSplit.Decode(_split, sizeof(double), _gathered);
        return _gathered[^1];
    }
}
