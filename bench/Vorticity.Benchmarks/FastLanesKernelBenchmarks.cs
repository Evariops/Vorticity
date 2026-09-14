// The FastLanes unpack kernel, scalar against vector, in ONE process.
//
// docs/90-registry.md calls `fastlanes.bitpacked` "the single most important kernel", and it was
// scalar. The shape of the win was predictable from the layout rather than hoped for: `lane` IS the
// SIMD lane, so for a fixed row the packed words are contiguous and - this is the part that decides
// it - so are the output positions, because `index(row, lane) = base(row) + lane` at every element
// width. No gather, no scatter, just loads, shifts, masks and stores.
//
// THE VECTOR ARMS CALL `UnpackBlocks`, WHICH IS WHAT SHIPS, and that is the whole difference
// between a number and a number about nothing. They called `UnpackBlock` -- the single-block
// entry point -- until 2026-09-14, and `71b18f0` moved the library off it: a node's blocks all
// share one bit width, so the per-row shape table is built once for the run instead of once per
// block. The singular form still exists for the partial first and last block of a window, and it
// pays that table per call: measured here on the same 64 blocks into the same output, 26.53 us
// per block against 19.92 us for the run, -25%. So the arm read +20% against BASELINE's 21.6 us
// while the shipped path had got FASTER, and the bisect that cost (BENCH-AUDIT.md §4.4) found a
// benchmark, not a regression. An arm that does not call what ships measures nothing anyone runs.
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
[BenchmarkCategory(BenchmarkConfig.Kernel)]
public class FastLanesKernelBenchmarks
{
    private const int Blocks = 64;

    private ulong[] _packed64 = [];
    private ulong[] _output64 = [];
    private uint[] _packed32 = [];
    private uint[] _output32 = [];
    private ulong[] _values64 = [];
    private uint[] _values32 = [];

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

    /// <summary>What one invocation moves: <c>Blocks</c> blocks of 1 024 values, at its width.</summary>
    /// <param name="method">The arm, which says whether a value is four bytes or eight.</param>
    /// <param name="parameters">Unused; the row count does not depend on the bit width.</param>
    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters)
    {
        long values = (long)Blocks * FastLanes.BlockSize;
        int width = method.EndsWith("32", StringComparison.Ordinal) ? sizeof(uint) : sizeof(ulong);
        return (values, values * width);
    }

    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(20260912);

        _packed64 = new ulong[Blocks * 16 * BitWidth];
        random.NextBytes(MemoryMarshal.AsBytes(_packed64.AsSpan()));
        _output64 = new ulong[Blocks * FastLanes.BlockSize];

        // 32-bit elements as well, because `lanes` is 32 rather than 16 there: the inner loop runs
        // twice as many vector steps per row, and a per-row cost that looked free at 64 bits would
        // show up.
        int width32 = Math.Min(BitWidth, 31);
        _packed32 = new uint[Blocks * 32 * width32];
        random.NextBytes(MemoryMarshal.AsBytes(_packed32.AsSpan()));
        _output32 = new uint[Blocks * FastLanes.BlockSize];

        // THE PACK SIDE HAS ITS OWN INPUT, and it is masked to the width: `PackBlock` requires
        // every value below 2^bitWidth, and reusing an unpack arm's output would make one arm
        // depend on another having run. v2 W-14 asks for these arms -- `PackBlock` is the exact
        // mirror of `UnpackBlock`, which went 3.6x (i64) and 7x (i32) when it was vectorized, and
        // it is still the scalar loop through the index table.
        _values64 = new ulong[Blocks * FastLanes.BlockSize];
        ulong mask64 = BitWidth == 64 ? ulong.MaxValue : (1UL << BitWidth) - 1;
        for (int i = 0; i < _values64.Length; i++)
        {
            _values64[i] = (ulong)random.NextInt64() & mask64;
        }

        _values32 = new uint[Blocks * FastLanes.BlockSize];
        uint mask32 = width32 == 32 ? uint.MaxValue : (1u << width32) - 1;
        for (int i = 0; i < _values32.Length; i++)
        {
            _values32[i] = (uint)random.Next() & mask32;
        }
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
                _packed64.AsSpan(block * words, words),
                BitWidth,
                _output64.AsSpan(block * FastLanes.BlockSize, FastLanes.BlockSize),
                index,
                64,
                16);
            sink += _output64[block * FastLanes.BlockSize];
        }

        return sink;
    }

    [Benchmark(Description = "i64 vector")]
    public ulong Vector64()
    {
        FastLanes.UnpackBlocks<ulong>(_packed64, BitWidth, _output64, Blocks);
        return _output64[0];
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
                _packed32.AsSpan(block * words, words),
                width,
                _output32.AsSpan(block * FastLanes.BlockSize, FastLanes.BlockSize),
                index,
                32,
                32);
            sink += _output32[block * FastLanes.BlockSize];
        }

        return sink;
    }

    [Benchmark(Description = "i64 pack")]
    public int Pack64()
    {
        int words = 16 * BitWidth;
        for (int block = 0; block < Blocks; block++)
        {
            FastLanes.PackBlock<ulong>(
                _values64.AsSpan(block * FastLanes.BlockSize, FastLanes.BlockSize),
                BitWidth,
                _packed64.AsSpan(block * words, words));
        }

        return _packed64.Length;
    }

    [Benchmark(Description = "i32 pack")]
    public int Pack32()
    {
        int width = Math.Min(BitWidth, 31);
        int words = 32 * width;
        for (int block = 0; block < Blocks; block++)
        {
            FastLanes.PackBlock<uint>(
                _values32.AsSpan(block * FastLanes.BlockSize, FastLanes.BlockSize),
                width,
                _packed32.AsSpan(block * words, words));
        }

        return _packed32.Length;
    }

    [Benchmark(Description = "i32 vector")]
    public uint Vector32()
    {
        int width = Math.Min(BitWidth, 31);
        FastLanes.UnpackBlocks<uint>(_packed32, width, _output32, Blocks);
        return _output32[0];
    }
}
