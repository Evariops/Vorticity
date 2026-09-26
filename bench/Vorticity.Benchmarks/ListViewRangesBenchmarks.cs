// A list-view node's rows checked as it is decoded: every row's offset and size non-negative and
// ending within the elements, the whole of a list-view decode's own work per row.
//
// 65 536 rows over as many elements, at the widths writers leave: 64-bit signed as Arrow's, 64-bit
// unsigned, and 32-bit.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Types;

namespace Vorticity.Benchmarks;

/// <summary><c>ListViewDecoder.ValidateRanges</c> over 65 536 rows.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
public class ListViewRangesBenchmarks
{
    private const int Rows = 1 << 16;

    [Params("i64", "u64", "i32")]
    public string Width { get; set; } = "i64";

    private PType Physical => Width switch { "u64" => PType.U64, "i32" => PType.I32, _ => PType.I64 };

    private byte[] _offsets = [];
    private byte[] _sizes = [];

    /// <summary>What one invocation reads: an offset and a size per row.</summary>
    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters) =>
        (Rows, Rows * (parameters.TryGetValue(nameof(Width), out object? w) && w is "i32" ? 8L : 16L));

    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(20260927);
        long[] offsets = new long[Rows];
        long[] sizes = new long[Rows];
        for (int i = 0; i < Rows; i++)
        {
            offsets[i] = random.Next(Rows);
            sizes[i] = random.Next(Rows - (int)offsets[i] + 1);
        }

        if (Physical == PType.I32)
        {
            _offsets = MemoryMarshal.AsBytes(Array.ConvertAll(offsets, v => (int)v).AsSpan()).ToArray();
            _sizes = MemoryMarshal.AsBytes(Array.ConvertAll(sizes, v => (int)v).AsSpan()).ToArray();
        }
        else
        {
            _offsets = MemoryMarshal.AsBytes(offsets.AsSpan()).ToArray();
            _sizes = MemoryMarshal.AsBytes(sizes.AsSpan()).ToArray();
        }
    }

    [Benchmark(Description = "list view ranges")]
    public int Validate()
    {
        ListViewDecoder.ValidateRanges(_offsets, Physical, _sizes, Physical, Rows, Rows);
        return Rows;
    }
}
