// The view cutter's row-at-a-time paths: a column with nulls, where each valid row's view is built
// in turn, and a take of scattered rows, where each wanted row's offsets are read fresh. Both build
// one view a row from a value of random size, inline or not.
//
// Rows of 0 to 12 bytes, every one inline, or 0 to 24, about half of them not.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Benchmarks;

/// <summary>Views cut row by row: a quarter of the rows null, or a third of them taken.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
public class ViewCutSparseBenchmarks
{
    private const int Rows = 1 << 16;
    private const int ViewSize = 16;

    [Params("inline", "mixed")]
    public string Shape { get; set; } = "inline";

    private readonly CanonicalArena _arena = new CanonicalArena();
    private Validity _validity;
    private byte[] _offsets = [];
    private byte[] _heap = [];
    private byte[] _views = [];
    private int[] _wanted = [];

    /// <summary>What one invocation moves: every row's offsets and its view.</summary>
    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters) =>
        (Rows, Rows * (long)(ViewSize + sizeof(uint)));

    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(20260927);
        int longest = Shape == "inline" ? 12 : 24;
        _offsets = new byte[(Rows + 1) * sizeof(uint)];
        Span<uint> offsets = MemoryMarshal.Cast<byte, uint>(_offsets);
        uint total = 0;
        for (int i = 0; i < Rows; i++)
        {
            offsets[i] = total;
            total += (uint)random.Next(0, longest + 1);
        }

        offsets[Rows] = total;
        _heap = new byte[total];
        random.NextBytes(_heap);
        _views = new byte[Rows * ViewSize];

        VortexBuffer bits = _arena.Allocate(Rows / 8, 1, out Span<byte> valid);
        for (int i = 0; i < valid.Length; i++)
        {
            valid[i] = (byte)(random.Next(256) | random.Next(256));
        }

        _validity = Validity.Bitmap(_arena.AddBool(new DTypeArena().Bool(Nullability.NonNullable), Rows, Validity.NonNullable, bits, 0));
        List<int> wanted = [];
        for (int i = 0; i < Rows; i += 1 + random.Next(5))
        {
            wanted.Add(i);
        }

        _wanted = wanted.ToArray();
    }

    [Benchmark(Description = "cut with nulls")]
    public int WithNulls()
    {
        ValidityMask mask = ValidityMask.From(_arena, _validity);
        ViewKernels.BuildFromOffsets(_offsets, PType.U32, _heap, _views, Rows, false, in mask, VarBinDecoder.Id);
        return _views[ViewSize];
    }

    [Benchmark(Description = "cut taken rows")]
    public int Taken()
    {
        ViewKernels.BuildFromOffsetsSelected(_offsets, PType.U32, _heap, _views, _wanted, false, default);
        return _views[ViewSize];
    }
}
