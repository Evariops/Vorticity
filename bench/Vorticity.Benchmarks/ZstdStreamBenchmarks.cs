using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks;

/// <summary>
/// The stream a zstd trial compresses for a string column, alone: its length, then each valid value
/// behind its <c>u32</c> length, cut where a block of 8 192 rows ends. The library's
/// <c>ZstdPlan.StreamBytes</c> and <c>ZstdPlan.LayViews</c> against a frozen copy of the passes as
/// they were, in one process and on one clock; the compression itself is left out.
/// </summary>
/// <remarks>
/// <para>
/// <c>urls</c> is 52-byte URLs, every one in the data buffer; <c>codes</c> 8-byte codes, every one
/// inline; <c>uuids</c> 36-byte text; <c>mixed</c> lengths from 0 to 40 in no order, a third of
/// them inline; <c>urls-nulls</c> the URLs with one row in ten null, its view garbage.
/// </para>
/// <para>The arms' streams and frame tables are checked against each other before anything is timed.</para>
/// </remarks>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
[MinColumn]
[MedianColumn]
[CyclesColumn]
public class ZstdStreamBenchmarks
{
    private const int FrameRows = 8192;
    private const int FrameFields = 3;

    private CanonicalArena _arena = null!;
    private int _node;
    private int _blocks;
    private byte[] _stream = [];
    private int[] _frames = [];

    /// <summary>The text: <c>urls</c>, <c>codes</c>, <c>uuids</c>, <c>mixed</c> or <c>urls-nulls</c>.</summary>
    [ParamsSource(nameof(Shapes))]
    public string Shape { get; set; } = "urls";

    /// <summary>Rows of the column.</summary>
    [ParamsSource(nameof(RowCounts))]
    public int Rows { get; set; } = FrameRows;

    /// <summary>Every shape in every profile.</summary>
    public static IEnumerable<string> Shapes => ["urls", "codes", "uuids", "mixed", "urls-nulls"];

    /// <summary>One block, and sixteen.</summary>
    public static IEnumerable<int> RowCounts => [FrameRows, 131_072];

    /// <summary>What one invocation reads: a view a row.</summary>
    /// <param name="method">Unused; every arm reads the same.</param>
    /// <param name="parameters">The case's rows.</param>
    public static (long Rows, long Bytes) BenchmarkWork(string method, IReadOnlyDictionary<string, object?> parameters)
    {
        int rows = (int)parameters[nameof(Rows)]!;
        return (rows, (long)rows * 16);
    }

    [GlobalSetup]
    public void Setup()
    {
        _arena = new CanonicalArena();
        DTypeArena types = new DTypeArena();
        bool nulls = Shape == "urls-nulls";
        byte[] views = new byte[Rows * 16];
        List<byte> heap = [];
        Span<byte> scratch = stackalloc byte[64];
        Span<byte> raw = stackalloc byte[16];
        Random random = new Random(20260925);
        for (int row = 0; row < Rows; row++)
        {
            Span<byte> view = views.AsSpan(row * 16, 16);
            if (nulls && row % 10 == 3)
            {
                random.NextBytes(view);
                continue;
            }

            BinaryPrimitives.WriteUInt64LittleEndian(raw, (ulong)row * 0x9E3779B97F4A7C15UL);
            BinaryPrimitives.WriteUInt64LittleEndian(raw[8..], ~(ulong)row * 0xC2B2AE3D27D4EB4FUL);
            string text = Shape switch
            {
                "codes" => string.Create(CultureInfo.InvariantCulture, $"v{row:D7}"),
                "uuids" => new Guid(raw).ToString("D"),
                "mixed" => new string('m', random.Next(41)),
                _ => string.Create(CultureInfo.InvariantCulture, $"https://example.invalid/vortex/conformance/{row:D9}"),
            };
            int length = Encoding.ASCII.GetBytes(text, scratch);
            BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)length);
            if (length <= 12)
            {
                scratch[..length].CopyTo(view[4..]);
                continue;
            }

            scratch[..4].CopyTo(view[4..]);
            BinaryPrimitives.WriteUInt32LittleEndian(view[12..], (uint)heap.Count);
            heap.AddRange(scratch[..length].ToArray());
        }

        VortexBuffer viewBuffer = _arena.Allocate(views.Length, 16, out Span<byte> viewBytes);
        views.CopyTo(viewBytes);
        VortexBuffer heapBuffer = _arena.Allocate(Math.Max(heap.Count, 1), 16, out Span<byte> heapBytes);
        heap.ToArray().CopyTo(heapBytes);
        Validity validity = Validity.NonNullable;
        if (nulls)
        {
            VortexBuffer bits = _arena.Allocate((Rows + 7) / 8, 8, out Span<byte> bitBytes);
            for (int row = 0; row < Rows; row++)
            {
                if (row % 10 != 3)
                {
                    bitBytes[row >> 3] |= (byte)(1 << (row & 7));
                }
            }

            validity = Validity.Bitmap(_arena.AddBool(types.Bool(Nullability.NonNullable), Rows, Validity.NonNullable, bits, 0));
        }

        _node = _arena.AddVarBinView(types.Utf8(nulls ? Nullability.Nullable : Nullability.NonNullable), Rows, validity, viewBuffer, [heapBuffer]);
        CanonicalNode node = _arena.GetNode(_node);
        _blocks = (Rows + FrameRows - 1) / FrameRows;
        long expected = ZstdStreamOriginal.StreamBytes(_arena, node, out int expectedValues);
        long got = ZstdPlan.StreamBytes(_arena, node, out int gotValues);
        if (expected != got || expectedValues != gotValues)
        {
            throw new InvalidOperationException($"{Shape}: the library's stream length differs from the original's.");
        }

        byte[] expectedStream = new byte[expected + ZstdPlan.StreamSlack];
        int[] expectedFrames = new int[_blocks * FrameFields];
        ZstdStreamOriginal.Lay(_arena, node, expectedStream, expectedFrames, _blocks, FrameRows);
        _stream = new byte[expected + ZstdPlan.StreamSlack];
        _frames = new int[_blocks * FrameFields];
        ZstdPlan.LayViews(_arena, node, _stream, _frames, _blocks, FrameRows);
        if (!expectedStream.AsSpan(0, (int)expected).SequenceEqual(_stream.AsSpan(0, (int)expected)) ||
            !expectedFrames.AsSpan().SequenceEqual(_frames))
        {
            throw new InvalidOperationException($"{Shape}: the library's stream differs from the original's.");
        }
    }

    [Benchmark(Baseline = true)]
    public long Original()
    {
        CanonicalNode node = _arena.GetNode(_node);
        long bytes = ZstdStreamOriginal.StreamBytes(_arena, node, out _);
        ZstdStreamOriginal.Lay(_arena, node, _stream, _frames, _blocks, FrameRows);
        return bytes;
    }

    [Benchmark]
    public long Current()
    {
        CanonicalNode node = _arena.GetNode(_node);
        long bytes = ZstdPlan.StreamBytes(_arena, node, out _);
        ZstdPlan.LayViews(_arena, node, _stream, _frames, _blocks, FrameRows);
        return bytes;
    }
}
