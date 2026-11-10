// Audit AR-10: what the views of selected rows of texts cost cut from their offsets, a row's bytes checked as UTF-8 one row
// at a time, against the same rows taken as bytes, unchecked.
//
// 16,384 rows of 8 to 24 ASCII bytes, one in two selected, or one in sixteen.
using System;
using System.Collections.Generic;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Audit;

/// <summary>Views of selected texts.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditSelectedTextsBenchmarks
{
    private const int Rows = 16_384;

    private byte[] _offsets = [];
    private byte[] _heap = [];
    private int[] _wanted = [];
    private byte[] _views = [];

    /// <summary>One row in how many is selected.</summary>
    [Params(2, 16)]
    public int Every { get; set; }

    /// <summary>Draws the texts and the selection.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(101);
        List<byte> heap = [];
        int[] offsets = new int[Rows + 1];
        for (int row = 0; row < Rows; row++)
        {
            int length = random.Next(8, 25);
            for (int i = 0; i < length; i++)
            {
                heap.Add((byte)random.Next('a', 'z' + 1));
            }

            offsets[row + 1] = heap.Count;
        }

        _heap = [.. heap];
        _offsets = new byte[offsets.Length * 4];
        Buffer.BlockCopy(offsets, 0, _offsets, 0, _offsets.Length);
        List<int> wanted = [];
        for (int row = 0; row < Rows; row += Every)
        {
            wanted.Add(row);
        }

        _wanted = [.. wanted];
        _views = new byte[_wanted.Length * 16];
    }

    /// <summary>Texts, each checked as UTF-8, by row selected.</summary>
    [Benchmark]
    public byte Texts()
    {
        ViewKernels.BuildFromOffsetsSelected(_offsets, PType.U32, _heap, _views, _wanted, requireUtf8: true, ValidityMask.NonNullable);
        return _views[^1];
    }

    /// <summary>The same rows as bytes.</summary>
    [Benchmark(Baseline = true)]
    public byte Bytes()
    {
        ViewKernels.BuildFromOffsetsSelected(_offsets, PType.U32, _heap, _views, _wanted, requireUtf8: false, ValidityMask.NonNullable);
        return _views[^1];
    }
}
