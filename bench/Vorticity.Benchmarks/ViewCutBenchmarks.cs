// The view cutter, where every string decoder ends: lengths or offsets in, sixteen-byte views out.
//
// The block loops it picks by the rows' sizes were written as vectors for NEON and as words
// elsewhere, and the vector ones ran on ARM alone: their one ARM-only instruction, `ext`, is
// `palignr` on x86. This class measures the loops a block of short rows takes, all inline or mixed
// with rows too long to inline, through the entry points the decoders call.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Types;

namespace Vorticity.Benchmarks;

/// <summary>Cutting a heap into views, by lengths and by offsets.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
public class ViewCutBenchmarks
{
    private const int Rows = 1 << 16;
    private const int ViewSize = 16;

    /// <summary>Rows of 0 to 12 bytes, every one inline; or 0 to 24, about half of them not.</summary>
    [Params("inline", "mixed")]
    public string Shape { get; set; } = "inline";

    /// <summary>Whether the rows are text, which checks that each starts on a character.</summary>
    [Params(false, true)]
    public bool Utf8 { get; set; }

    private byte[] _lengths = [];
    private byte[] _offsets = [];
    private byte[] _heap = [];
    private byte[] _views = [];

    /// <summary>What one invocation moves: every row, its length and its view.</summary>
    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters) =>
        (Rows, Rows * (long)(ViewSize + sizeof(uint)));

    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(20260926);
        int longest = Shape == "inline" ? 12 : 24;
        _lengths = new byte[Rows * sizeof(uint)];
        _offsets = new byte[(Rows + 1) * sizeof(uint)];
        Span<uint> lengths = MemoryMarshal.Cast<byte, uint>(_lengths);
        Span<uint> offsets = MemoryMarshal.Cast<byte, uint>(_offsets);
        uint total = 0;
        for (int i = 0; i < Rows; i++)
        {
            uint length = (uint)random.Next(0, longest + 1);
            lengths[i] = length;
            offsets[i] = total;
            total += length;
        }

        offsets[Rows] = total;
        _heap = new byte[total];
        for (int i = 0; i < _heap.Length; i++)
        {
            _heap[i] = (byte)('a' + random.Next(26));
        }

        _views = new byte[Rows * ViewSize];
    }

    [Benchmark(Description = "cut by lengths")]
    public bool ByLengths() =>
        ViewKernels.BuildFromLengths(_lengths, PType.U32, default, _heap, _views, Rows, Utf8);

    [Benchmark(Description = "cut by offsets")]
    public int ByOffsets()
    {
        ViewKernels.BuildFromOffsets(
            _offsets, PType.U32, _heap, _views, Rows, Utf8, default, VarBinDecoder.Id);
        return _views[ViewSize];
    }
}
