// The three row-movement kernels against the per-element switch they replaced, in ONE process.
//
// BENCH-AUDIT.md §3.2: `RowKernels` was written against the 1M throughput gate alone, and its own
// header makes a claim that gate cannot check -- "in a gather the dispatch IS the loop body", where
// bench/BRANCHING.md had measured the same switch at +0.9% inside `OnPairDecoder.Concatenate` and
// called it refuted. Both can be true, and the only way to know is to measure the two loops
// separately. This class does that: the scalar arm is the shape the header describes as the
// previous state -- read a code through `switch (ptype)`, bounds-check it, then
// `Slice(row * width, width).CopyTo(...)` -- and it reads the code through `RowKernels.CodeAt`,
// which IS that switch, still in the library for the single-code callers.
//
// ALIGNMENT MEASURED, NOT ASSUMED: these buffers are plain GC arrays, and §4.4 asked whether that
// changes the number against the library's aligned arena allocations. Answered on M4 Pro: eight
// bytes past a 64-byte boundary costs 0.3% to 1.8% over two runs, the same sign every time but
// under the fast profile's own +-3% fidelity. Not re-measured on x64. The curve that answered it
// is gone, the answer being wanted once.
//
// 65 536 rows of 8 bytes gathered out of a 4 096-row dictionary: half a mebibyte of destination and
// 32 KiB of values, so the values stay in L1 and the destination is a streaming write. That is the
// shape `vortex.dict` actually has -- a small dictionary read at random, a big output written in
// order -- and it keeps the measurement about dispatch rather than about DRAM.
using System;
using System.Collections.Generic;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Types;

namespace Vorticity.Benchmarks;

/// <summary>Gather, masked gather and tile, against the per-row switch each replaced.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class RowKernelBenchmarks
{
    /// <summary>Rows gathered per operation.</summary>
    private const int Rows = 1 << 16;

    /// <summary>Rows in the dictionary the codes index.</summary>
    private const int Values = 4096;

    /// <summary>Bytes per row: an i64 or an f64, the width every dictionary column here has.</summary>
    private const int Width = 8;

    private const PType CodesType = PType.U32;

    private byte[] _codes = [];
    private byte[] _values = [];
    private byte[] _destination = [];
    private byte[] _codeBits = [];
    private byte[] _outputBits = [];
    private byte[] _element = [];

    /// <summary>What one invocation moves, per arm.</summary>
    /// <param name="method">The arm.</param>
    /// <param name="parameters">Unused; this class has no <c>[Params]</c>.</param>
    /// <remarks>
    /// A gather reads a code and writes a row; a tile writes rows and reads one element. The codes
    /// are four bytes each, which is a fifth of the traffic at this width and not noise.
    /// </remarks>
    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters) => method switch
    {
        nameof(TileScalar) or nameof(TileLibrary) => (Rows, (long)Rows * Width),
        _ => (Rows, (long)Rows * (Width + sizeof(uint))),
    };

    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(20260914);

        _codes = new byte[Rows * sizeof(uint)];
        Span<uint> codes = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(_codes);
        for (int i = 0; i < codes.Length; i++)
        {
            codes[i] = (uint)random.Next(0, Values);
        }

        _values = new byte[Values * Width];
        random.NextBytes(_values);

        _destination = new byte[Rows * Width];

        // One row in eight is null, which is what a nullable dictionary column looks like and what
        // makes the masked arm take both of its branches.
        _codeBits = new byte[(Rows + 7) / 8];
        for (int i = 0; i < Rows; i++)
        {
            if (i % 8 != 3)
            {
                _codeBits[i >> 3] |= (byte)(1 << (i & 7));
            }
        }

        _outputBits = new byte[(Rows + 7) / 8];
        _element = new byte[Width];
        random.NextBytes(_element);
    }

    [BenchmarkCategory("gather")]
    [Benchmark(Baseline = true, Description = "gather, switch per row")]
    public int GatherScalar() =>
        GatherPerRow(_codes, CodesType, _values, Width, Values, _destination, Rows);

    [BenchmarkCategory("gather")]
    [Benchmark(Description = "gather, library")]
    public int GatherLibrary() =>
        RowKernels.Gather(_codes, CodesType, _values, Width, Values, _destination, Rows);

    [BenchmarkCategory("masked")]
    [Benchmark(Baseline = true, Description = "masked gather, switch per row")]
    public int MaskedScalar()
    {
        _outputBits.AsSpan().Clear();
        return MaskedPerRow(
            _codes, CodesType, _values, Width, Values, _destination, Rows, _codeBits, _outputBits);
    }

    [BenchmarkCategory("masked")]
    [Benchmark(Description = "masked gather, library")]
    public int MaskedLibrary()
    {
        _outputBits.AsSpan().Clear();
        return RowKernels.GatherMasked(
            _codes, CodesType, _values, Width, Values, _destination, Rows,
            _codeBits, 0, [], 0, valuesAllValid: true, _outputBits);
    }

    [BenchmarkCategory("tile")]
    [Benchmark(Baseline = true, Description = "tile, copy per row")]
    public int TileScalar()
    {
        TilePerRow(_destination, _element);
        return _destination[0];
    }

    [BenchmarkCategory("tile")]
    [Benchmark(Description = "tile, library")]
    public int TileLibrary()
    {
        RowKernels.Tile(_destination, _element);
        return _destination[0];
    }

    /// <summary>The gather as it was: the physical-type switch inside the loop, then a `CopyTo`.</summary>
    private static int GatherPerRow(
        ReadOnlySpan<byte> codes, PType codesPType, ReadOnlySpan<byte> values, int width,
        int valuesLength, Span<byte> destination, int count)
    {
        for (int row = 0; row < count; row++)
        {
            uint code = RowKernels.CodeAt(codes, codesPType, row);
            if (code >= (uint)valuesLength)
            {
                return row;
            }

            values.Slice((int)code * width, width).CopyTo(destination.Slice(row * width, width));
        }

        return -1;
    }

    /// <summary>The masked gather as it was: a bit read and a switch per row.</summary>
    private static int MaskedPerRow(
        ReadOnlySpan<byte> codes, PType codesPType, ReadOnlySpan<byte> values, int width,
        int valuesLength, Span<byte> destination, int count, ReadOnlySpan<byte> codeBits,
        Span<byte> outputBits)
    {
        for (int row = 0; row < count; row++)
        {
            if (((codeBits[row >> 3] >> (row & 7)) & 1) == 0)
            {
                destination.Slice(row * width, width).Clear();
                continue;
            }

            uint code = RowKernels.CodeAt(codes, codesPType, row);
            if (code >= (uint)valuesLength)
            {
                return row;
            }

            values.Slice((int)code * width, width).CopyTo(destination.Slice(row * width, width));
            outputBits[row >> 3] |= (byte)(1 << (row & 7));
        }

        return -1;
    }

    /// <summary>The tile as it was: one `CopyTo` of the element per row.</summary>
    private static void TilePerRow(Span<byte> destination, ReadOnlySpan<byte> element)
    {
        int width = element.Length;
        for (int offset = 0; offset + width <= destination.Length; offset += width)
        {
            element.CopyTo(destination.Slice(offset, width));
        }
    }
}
