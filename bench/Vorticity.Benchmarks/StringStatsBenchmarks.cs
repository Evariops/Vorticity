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
/// The block statistics of a string column alone, its bytes, runs and order: the library's
/// <c>BlockStatsPass.Accumulate</c> against a frozen copy of the pass as it was, a block of 8 192
/// rows at a time, in one process and on one clock.
/// </summary>
/// <remarks>
/// <para>
/// <c>urls</c> is the per-encoding corpus FSST file's text, 52-byte URLs rising row by row, so the
/// order stays tracked to the end and every pair shares 43 bytes; <c>codes</c> short values rising
/// the same way, every one inline; <c>labels</c> sixteen short labels in no order; <c>uuids</c>
/// random 36-byte text, in no order either; <c>urls-split</c> the URLs spread over four data
/// buffers, as a batch concatenated from several chunks holds them.
/// </para>
/// <para>The arms' statistics are checked against each other before anything is timed.</para>
/// </remarks>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
[MinColumn]
[MedianColumn]
[CyclesColumn]
public class StringStatsBenchmarks
{
    private const int BlockRows = 8192;

    private CanonicalArena _arena = null!;
    private int _node;

    /// <summary>The text: <c>urls</c>, <c>codes</c>, <c>labels</c>, <c>uuids</c> or <c>urls-split</c>.</summary>
    [ParamsSource(nameof(Shapes))]
    public string Shape { get; set; } = "urls";

    /// <summary>Rows summarized in one call.</summary>
    [ParamsSource(nameof(RowCounts))]
    public int Rows { get; set; } = BlockRows;

    /// <summary>Every shape in every profile.</summary>
    public static IEnumerable<string> Shapes => ["urls", "codes", "labels", "uuids", "urls-split"];

    /// <summary>One block, and a scan window of sixteen.</summary>
    public static IEnumerable<int> RowCounts => [BlockRows, 131_072];

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
        byte[] views = new byte[Rows * 16];
        int heapCount = Shape == "urls-split" ? 4 : 1;
        List<byte>[] heaps = new List<byte>[heapCount];
        for (int h = 0; h < heapCount; h++)
        {
            heaps[h] = [];
        }

        Span<byte> scratch = stackalloc byte[64];
        Span<byte> raw = stackalloc byte[16];
        for (int row = 0; row < Rows; row++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(raw, (ulong)row * 0x9E3779B97F4A7C15UL);
            BinaryPrimitives.WriteUInt64LittleEndian(raw[8..], ~(ulong)row * 0xC2B2AE3D27D4EB4FUL);
            string text = Shape switch
            {
                "urls" or "urls-split" => string.Create(CultureInfo.InvariantCulture, $"https://example.invalid/vortex/conformance/{row:D9}"),
                "codes" => string.Create(CultureInfo.InvariantCulture, $"v{row:D7}"),
                "labels" => string.Create(CultureInfo.InvariantCulture, $"label-{(row * 7) % 16:D2}"),
                _ => new Guid(raw).ToString("D"),
            };
            int length = Encoding.ASCII.GetBytes(text, scratch);
            Span<byte> view = views.AsSpan(row * 16, 16);
            BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)length);
            if (length <= 12)
            {
                scratch[..length].CopyTo(view[4..]);
                continue;
            }

            int buffer = row % heapCount;
            scratch[..4].CopyTo(view[4..]);
            BinaryPrimitives.WriteUInt32LittleEndian(view[8..], (uint)buffer);
            BinaryPrimitives.WriteUInt32LittleEndian(view[12..], (uint)heaps[buffer].Count);
            heaps[buffer].AddRange(scratch[..length].ToArray());
        }

        VortexBuffer viewBuffer = _arena.Allocate(views.Length, 16, out Span<byte> viewBytes);
        views.CopyTo(viewBytes);
        VortexBuffer[] heapBuffers = new VortexBuffer[heapCount];
        for (int h = 0; h < heapCount; h++)
        {
            heapBuffers[h] = _arena.Allocate(Math.Max(heaps[h].Count, 1), 16, out Span<byte> heapBytes);
            heaps[h].ToArray().CopyTo(heapBytes);
        }

        _node = _arena.AddVarBinView(types.Utf8(Nullability.NonNullable), Rows, Validity.NonNullable, viewBuffer, heapBuffers);

        for (int start = 0; start < Rows; start += BlockRows)
        {
            BlockStats expected = default;
            BlockStats got = default;
            BlockStatsPassOriginal.Accumulate(_arena, _node, start, BlockRows, ref expected);
            BlockStatsPass.Accumulate(_arena, _node, start, BlockRows, ref got);
            if (expected.RunBoundaries != got.RunBoundaries || expected.TotalBytes != got.TotalBytes ||
                expected.Unsorted != got.Unsorted || expected.Repeats != got.Repeats || expected.NullCount != got.NullCount)
            {
                throw new InvalidOperationException($"{Shape}: block {start / BlockRows}'s statistics differ from the original's.");
            }
        }
    }

    [Benchmark(Baseline = true)]
    public long Original()
    {
        long boundaries = 0;
        for (int start = 0; start < Rows; start += BlockRows)
        {
            BlockStats stats = default;
            BlockStatsPassOriginal.Accumulate(_arena, _node, start, BlockRows, ref stats);
            boundaries += stats.RunBoundaries;
        }

        return boundaries;
    }

    [Benchmark]
    public long Current()
    {
        long boundaries = 0;
        for (int start = 0; start < Rows; start += BlockRows)
        {
            BlockStats stats = default;
            BlockStatsPass.Accumulate(_arena, _node, start, BlockRows, ref stats);
            boundaries += stats.RunBoundaries;
        }

        return boundaries;
    }
}
