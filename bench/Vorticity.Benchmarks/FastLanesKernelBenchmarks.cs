// The FastLanes unpack kernel, scalar against vector, in ONE process.
//
// docs/90-registry.md calls `fastlanes.bitpacked` "the single most important kernel", and it was
// scalar. The shape of the win was predictable from the layout rather than hoped for: `lane` IS the
// SIMD lane, so for a fixed row the packed words are contiguous and - this is the part that decides
// it - so are the output positions, because `index(row, lane) = base(row) + lane` at every element
// width. No gather, no scatter, just loads, shifts, masks and stores.
//
// MEASURED AT THE KERNEL AND NOT END TO END, for the reason docs/05-benchmarks.md §1b now
// states outright: an end-to-end run puts tens of microseconds of open-and-walk in front of the
// kernel and leaves the answer inside the run-to-run spread. That mistake has already been made
// once in this repository, on the FSST kernel, and it produced a confident negative result about a
// change that was in fact 7.9x faster.
//
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays.Decoders.Compressed;

namespace Vorticity.Benchmarks;

/// <summary>The two shapes of the unpack loop, against one clock.</summary>
[Config(typeof(BenchmarkConfig))]
public class FastLanesKernelBenchmarks
{
    private const int Blocks = 64;

    private ulong[] _packed64 = [];
    private ulong[] _output64 = [];
    private uint[] _packed32 = [];
    private uint[] _output32 = [];

    /// <summary>
    /// Bits per packed value. 17 by default -- the width the reference chose for
    /// `types/i64_nonnull_r8192` -- because a kernel regression shows at one width; 10 and 33 come
    /// back under `--full` (10 is what our own writer chooses for the nullable column of
    /// `containers/zoned_many_zones_nulls`, 33 crosses the 32-bit boundary).
    /// </summary>
    [ParamsSource(nameof(BitWidths))]
    public int BitWidth { get; set; } = 17;

    /// <summary>The widths the current profile measures.</summary>
    public static IEnumerable<int> BitWidths => BenchmarkConfig.Full ? [10, 17, 33] : [17];

    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(20260912);

        _packed64 = new ulong[Blocks * 16 * BitWidth];
        random.NextBytes(MemoryMarshal.AsBytes(_packed64.AsSpan()));
        _output64 = new ulong[FastLanes.BlockSize];

        // 32-bit elements as well, because `lanes` is 32 rather than 16 there: the inner loop runs
        // twice as many vector steps per row, and a per-row cost that looked free at 64 bits would
        // show up.
        int width32 = Math.Min(BitWidth, 31);
        _packed32 = new uint[Blocks * 32 * width32];
        random.NextBytes(MemoryMarshal.AsBytes(_packed32.AsSpan()));
        _output32 = new uint[FastLanes.BlockSize];
    }

    [Benchmark(Baseline = true, Description = "i64 scalar")]
    public ulong Scalar64()
    {
        ReadOnlySpan<int> index = FastLanes.PackedIndexTable(64);
        int words = 16 * BitWidth;
        ulong sink = 0;
        for (int block = 0; block < Blocks; block++)
        {
            FastLanes.UnpackBlockScalar<ulong>(
                _packed64.AsSpan(block * words, words), BitWidth, _output64, index, 64, 16);
            sink += _output64[0];
        }

        return sink;
    }

    [Benchmark(Description = "i64 vector")]
    public ulong Vector64()
    {
        int words = 16 * BitWidth;
        ulong sink = 0;
        for (int block = 0; block < Blocks; block++)
        {
            FastLanes.UnpackBlock<ulong>(
                _packed64.AsSpan(block * words, words), BitWidth, _output64);
            sink += _output64[0];
        }

        return sink;
    }

    [Benchmark(Description = "i32 scalar")]
    public uint Scalar32()
    {
        int width = Math.Min(BitWidth, 31);
        ReadOnlySpan<int> index = FastLanes.PackedIndexTable(32);
        int words = 32 * width;
        uint sink = 0;
        for (int block = 0; block < Blocks; block++)
        {
            FastLanes.UnpackBlockScalar<uint>(
                _packed32.AsSpan(block * words, words), width, _output32, index, 32, 32);
            sink += _output32[0];
        }

        return sink;
    }

    [Benchmark(Description = "i32 vector")]
    public uint Vector32()
    {
        int width = Math.Min(BitWidth, 31);
        int words = 32 * width;
        uint sink = 0;
        for (int block = 0; block < Blocks; block++)
        {
            FastLanes.UnpackBlock<uint>(_packed32.AsSpan(block * words, words), width, _output32);
            sink += _output32[0];
        }

        return sink;
    }
}
