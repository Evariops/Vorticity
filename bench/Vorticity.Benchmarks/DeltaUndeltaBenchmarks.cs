// A delta column of 32-bit integers decoded: each block's 32 lanes summed row by row in FastLanes'
// transposed order, then laid out in row order.
//
// The ported arm is the decode as it was: the sums stored as a block, then the block walked
// through the untranspose table, a table load, a load and a store a value. The shipped arm is
// `DeltaDecoder.Undelta`, which transposes the sums four by four in registers and stores each
// lane's run where it belongs. Both run in one process, so tiered PGO is best turned off
// (`DOTNET_TieredPGO=0`).
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays.Decoders.Compressed;

namespace Vorticity.Benchmarks;

/// <summary>65 536 u32 values in 64 blocks: ported loop against the shipped kernel.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
public class DeltaUndeltaBenchmarks
{
    private const int Rows = 1 << 16;

    private const int BlockSize = 1024;

    private const int Lanes = 32;

    private byte[] _bases = [];
    private byte[] _deltas = [];
    private byte[] _output = [];
    private uint[] _block = new uint[BlockSize];

    /// <summary>What one invocation writes: four bytes per value.</summary>
    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters) => (Rows, Rows * 4L);

    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(20260926);
        _bases = new byte[Rows / BlockSize * Lanes * 4];
        _deltas = new byte[Rows * 4];
        random.NextBytes(_bases);
        random.NextBytes(_deltas);
        _output = new byte[Rows * 4];
    }

    [Benchmark(Baseline = true, Description = "undelta u32, ported")]
    public uint Ported()
    {
        ReadOnlySpan<uint> bases = MemoryMarshal.Cast<byte, uint>(_bases);
        ReadOnlySpan<uint> deltas = MemoryMarshal.Cast<byte, uint>(_deltas);
        Span<uint> output = MemoryMarshal.Cast<byte, uint>(_output);
        ref int table = ref MemoryMarshal.GetReference(FastLanes.UntransposeTable);
        for (int b = 0; b < Rows / BlockSize; b++)
        {
            AccumulateInRegisters(bases.Slice(b * Lanes, Lanes), deltas.Slice(b * BlockSize, BlockSize), _block);
            ref uint blockRef = ref MemoryMarshal.GetArrayDataReference(_block);
            ref uint outputRef = ref MemoryMarshal.GetReference(output.Slice(b * BlockSize, BlockSize));
            for (int p = 0; p < BlockSize; p++)
            {
                Unsafe.Add(ref outputRef, p) = Unsafe.Add(ref blockRef, Unsafe.Add(ref table, p));
            }
        }

        return output[^1];
    }

    [Benchmark(Description = "undelta u32, shipped")]
    public uint Shipped()
    {
        DeltaDecoder.Undelta<uint>(_bases, _deltas, _output, Lanes, 0, Rows);
        return MemoryMarshal.Cast<byte, uint>(_output)[^1];
    }

    private static void AccumulateInRegisters(ReadOnlySpan<uint> bases, ReadOnlySpan<uint> deltaBlock, Span<uint> block)
    {
        nuint step = 4;
        ref uint start = ref MemoryMarshal.GetReference(bases);
        Vector128<uint> r0 = Vector128.LoadUnsafe(ref start);
        Vector128<uint> r1 = Vector128.LoadUnsafe(ref start, step);
        Vector128<uint> r2 = Vector128.LoadUnsafe(ref start, 2 * step);
        Vector128<uint> r3 = Vector128.LoadUnsafe(ref start, 3 * step);
        Vector128<uint> r4 = Vector128.LoadUnsafe(ref start, 4 * step);
        Vector128<uint> r5 = Vector128.LoadUnsafe(ref start, 5 * step);
        Vector128<uint> r6 = Vector128.LoadUnsafe(ref start, 6 * step);
        Vector128<uint> r7 = Vector128.LoadUnsafe(ref start, 7 * step);

        ref uint delta = ref MemoryMarshal.GetReference(deltaBlock);
        ref uint into = ref MemoryMarshal.GetReference(block);
        ref byte order = ref MemoryMarshal.GetReference(FastLanes.Order);
        for (int row = 0; row < BlockSize / Lanes; row++)
        {
            nuint at = (nuint)((Unsafe.Add(ref order, row >> 3) * 16) + ((row & 7) * 128));
            r0 += Vector128.LoadUnsafe(ref delta, at);
            r0.StoreUnsafe(ref into, at);
            r1 += Vector128.LoadUnsafe(ref delta, at + step);
            r1.StoreUnsafe(ref into, at + step);
            r2 += Vector128.LoadUnsafe(ref delta, at + (2 * step));
            r2.StoreUnsafe(ref into, at + (2 * step));
            r3 += Vector128.LoadUnsafe(ref delta, at + (3 * step));
            r3.StoreUnsafe(ref into, at + (3 * step));
            r4 += Vector128.LoadUnsafe(ref delta, at + (4 * step));
            r4.StoreUnsafe(ref into, at + (4 * step));
            r5 += Vector128.LoadUnsafe(ref delta, at + (5 * step));
            r5.StoreUnsafe(ref into, at + (5 * step));
            r6 += Vector128.LoadUnsafe(ref delta, at + (6 * step));
            r6.StoreUnsafe(ref into, at + (6 * step));
            r7 += Vector128.LoadUnsafe(ref delta, at + (7 * step));
            r7.StoreUnsafe(ref into, at + (7 * step));
        }
    }
}
