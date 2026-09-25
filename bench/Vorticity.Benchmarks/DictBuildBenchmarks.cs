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
/// The dictionary a writer builds for a string column alone, a code per row and each distinct value
/// once: the library's <c>DistinctTable</c> against a frozen copy of it as it was, in one process
/// and on one clock.
/// </summary>
/// <remarks>
/// <para>
/// <c>labels200</c> is the per-encoding corpus file's two hundred short values, cycling;
/// <c>labels5</c> its five; <c>names</c> 64 distinct 20-byte values, out of line, at random;
/// <c>uuids</c> random 36-byte text, every row distinct.
/// </para>
/// <para>The arms' codes are checked against each other before anything is timed.</para>
/// </remarks>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
[MinColumn]
[MedianColumn]
[CyclesColumn]
public class DictBuildBenchmarks
{
    private CanonicalArena _arena = null!;
    private int _node;

    /// <summary>The values: <c>labels200</c>, <c>labels5</c>, <c>names</c> or <c>uuids</c>.</summary>
    [ParamsSource(nameof(Shapes))]
    public string Shape { get; set; } = "labels200";

    /// <summary>Rows probed in one call.</summary>
    [ParamsSource(nameof(RowCounts))]
    public int Rows { get; set; } = 8192;

    /// <summary>Every shape in every profile.</summary>
    public static IEnumerable<string> Shapes => ["labels200", "labels5", "names", "uuids"];

    /// <summary>A block's rows, and a chunk's.</summary>
    public static IEnumerable<int> RowCounts => [8192, 131_072];

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
        List<byte> heap = [];
        Span<byte> scratch = stackalloc byte[64];
        Span<byte> raw = stackalloc byte[16];
        Random random = new Random(20260925);
        for (int row = 0; row < Rows; row++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(raw, (ulong)row * 0x9E3779B97F4A7C15UL);
            BinaryPrimitives.WriteUInt64LittleEndian(raw[8..], ~(ulong)row * 0xC2B2AE3D27D4EB4FUL);
            string text = Shape switch
            {
                "labels200" => string.Create(CultureInfo.InvariantCulture, $"v{row % 200:D3}"),
                "labels5" => new string('d', row % 5),
                "names" => string.Create(CultureInfo.InvariantCulture, $"customer-name-{random.Next(64):D6}"),
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

            scratch[..4].CopyTo(view[4..]);
            BinaryPrimitives.WriteUInt32LittleEndian(view[12..], (uint)heap.Count);
            heap.AddRange(scratch[..length].ToArray());
        }

        VortexBuffer viewBuffer = _arena.Allocate(views.Length, 16, out Span<byte> viewBytes);
        views.CopyTo(viewBytes);
        VortexBuffer heapBuffer = _arena.Allocate(Math.Max(heap.Count, 1), 16, out Span<byte> heapBytes);
        heap.ToArray().CopyTo(heapBytes);
        _node = _arena.AddVarBinView(types.Utf8(Nullability.NonNullable), Rows, Validity.NonNullable, viewBuffer, [heapBuffer]);

        DistinctTableOriginal expected = DistinctTableOriginal.For(_arena.GetNode(_node))!;
        DistinctTable got = DistinctTable.For(_arena.GetNode(_node))!;
        expected.Probe(_arena, _arena.GetNode(_node), 0, Rows);
        got.Probe(_arena, _arena.GetNode(_node), 0, Rows);
        if (expected.Distinct != got.Distinct || !expected.Codes.SequenceEqual(got.Codes))
        {
            throw new InvalidOperationException($"{Shape}: the library's codes differ from the original's.");
        }
    }

    [Benchmark(Baseline = true)]
    public int Original()
    {
        DistinctTableOriginal table = DistinctTableOriginal.For(_arena.GetNode(_node))!;
        table.Probe(_arena, _arena.GetNode(_node), 0, Rows);
        int distinct = table.Distinct;
        table.Reset();
        return distinct;
    }

    [Benchmark]
    public int Current()
    {
        DistinctTable table = DistinctTable.For(_arena.GetNode(_node))!;
        table.Probe(_arena, _arena.GetNode(_node), 0, Rows);
        int distinct = table.Distinct;
        table.Reset();
        return distinct;
    }
}
