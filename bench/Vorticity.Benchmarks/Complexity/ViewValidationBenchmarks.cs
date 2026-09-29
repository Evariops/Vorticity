// What validating a Utf8 varbinview pays for the span its views reach in their data buffer.
//
// One batch of 8,192 views, each naming an ASCII value of 24 or 400 bytes in one data buffer, the
// values laid in row order with a gap between them: none, as a writer lays a chunk and a window of
// it reads a run of it, up to thousands of bytes, as when a window's views are scattered over a
// buffer that holds far more than they name. `Original` sweeps the span from the first value named
// to the end of the last (`ViewValidationBefore.cs`); `Library` sweeps it unless the views leave
// much of it unnamed, and then checks the values named one by one.
using System;
using System.Buffers.Binary;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>One batch of views validated, against the gap between the values they name.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class ViewValidationBenchmarks
{
    /// <summary>The bytes between two values the views name.</summary>
    [Params(0, 64, 240, 512, 4_096)]
    public int Gap { get; set; }

    /// <summary>The bytes of a value.</summary>
    [Params(24, 400)]
    public int ValueBytes { get; set; }

    private const int Views = 8_192;

    private byte[] _views = [];
    private byte[] _heap = [];
    private VortexBuffer _buffer;

    /// <summary>Lays the values and their views.</summary>
    [GlobalSetup]
    public void Setup()
    {
        int stride = ValueBytes + Gap;
        _heap = GC.AllocateArray<byte>(Views * stride, pinned: true);
        _buffer = VortexBuffer.FromPinned(_heap, 0);
        _views = new byte[Views * CanonicalSupport.ViewSize];
        for (int i = 0; i < Views; i++)
        {
            Span<byte> value = _heap.AsSpan(i * stride, ValueBytes);
            "value-number-"u8.CopyTo(value);
            i.TryFormat(value[13..], out int digits);
            value[(13 + digits)..].Fill((byte)'x');
            Span<byte> view = _views.AsSpan(i * CanonicalSupport.ViewSize, CanonicalSupport.ViewSize);
            BinaryPrimitives.WriteInt32LittleEndian(view, ValueBytes);
            value[..4].CopyTo(view[4..]);
            BinaryPrimitives.WriteInt32LittleEndian(view[8..], 0);
            BinaryPrimitives.WriteInt32LittleEndian(view[12..], i * stride);
        }
    }

    /// <summary>The span from the first value named to the last swept.</summary>
    [Benchmark(Baseline = true)]
    public int Original()
    {
        ReadOnlySpan<VortexBuffer> buffers = [_buffer];
        ViewValidationBefore.ValidateViews(_views, buffers, ValidityMask.NonNullable, Views, requireUtf8: true);
        return buffers.Length;
    }

    /// <summary>The library's validation.</summary>
    [Benchmark]
    public int Library()
    {
        ReadOnlySpan<VortexBuffer> buffers = [_buffer];
        VarBinViewDecoder.ValidateViews(_views, buffers, ValidityMask.NonNullable, Views, requireUtf8: true);
        return buffers.Length;
    }
}
