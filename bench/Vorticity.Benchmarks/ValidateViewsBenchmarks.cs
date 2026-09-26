// A varbinview column's views checked as it is decoded: every view's buffer and bounds, and for
// text, that every value is UTF-8. A column without nulls takes a sweep that decides inline views
// four at a time and every referenced span in one pass at the end.
//
// A text column of short values, all inline, the shape of labels and codes, and one where half the
// values are long enough to reference the data buffer.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;

namespace Vorticity.Benchmarks;

/// <summary><c>VarBinViewDecoder.ValidateViews</c> over 65 536 views.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
public class ValidateViewsBenchmarks
{
    private const int Rows = 1 << 16;

    [Params("inline", "mixed")]
    public string Shape { get; set; } = "inline";

    private readonly CanonicalArena _arena = new CanonicalArena();
    private byte[] _views = [];
    private VortexBuffer[] _buffers = [];

    /// <summary>What one invocation reads: a view per row.</summary>
    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters) => (Rows, Rows * 16L);

    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(20260927);
        byte[] heap = new byte[Rows * 24];
        for (int i = 0; i < heap.Length; i++)
        {
            heap[i] = (byte)('a' + random.Next(26));
        }

        _views = new byte[Rows * 16];
        int at = 0;
        for (int i = 0; i < Rows; i++)
        {
            Span<byte> view = _views.AsSpan(i * 16, 16);
            int size = Shape == "inline" || random.Next(2) == 0 ? random.Next(13) : random.Next(13, 25);
            BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)size);
            if (size <= 12)
            {
                heap.AsSpan(at, size).CopyTo(view[4..]);
                continue;
            }

            heap.AsSpan(at, 4).CopyTo(view[4..8]);
            BinaryPrimitives.WriteUInt32LittleEndian(view[12..], (uint)at);
            at += size;
        }

        VortexBuffer buffer = _arena.Allocate(heap.Length, 1, out Span<byte> bytes);
        heap.CopyTo(bytes);
        _buffers = [buffer];
    }

    [Benchmark(Description = "validate views")]
    public int Validate()
    {
        VarBinViewDecoder.ValidateViews(_views, _buffers, default, Rows, requireUtf8: true);
        return Rows;
    }
}
