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
/// The string extremes a writer keeps for each zone alone: the library's <c>StringBounds</c>
/// against a frozen copy of it as it was, each zone of 8 192 rows accumulated then closed, in one
/// process and on one clock.
/// </summary>
/// <remarks>
/// <para>
/// <c>urls</c> is the per-encoding corpus FSST file's text, 52-byte URLs rising row by row, so
/// every row is a new maximum and every key the same; <c>labels</c> sixteen short labels, every
/// row between the bounds; <c>uuids</c> random 36-byte text, the keys differing. The bounds are
/// cut to 16 bytes, the writer's default.
/// </para>
/// <para>The arms' zones are checked against each other before anything is timed.</para>
/// </remarks>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
[MinColumn]
[MedianColumn]
[CyclesColumn]
public class StringBoundsBenchmarks
{
    private const int Limit = 16;
    private const int ZoneRows = 8192;

    private CanonicalArena _arena = null!;
    private int _node;

    /// <summary>The text: <c>urls</c>, <c>labels</c> or <c>uuids</c>.</summary>
    [ParamsSource(nameof(Shapes))]
    public string Shape { get; set; } = "urls";

    /// <summary>Rows folded in one call.</summary>
    [ParamsSource(nameof(RowCounts))]
    public int Rows { get; set; } = ZoneRows;

    /// <summary>Every shape in every profile.</summary>
    public static IEnumerable<string> Shapes => ["urls", "labels", "uuids"];

    /// <summary>One zone, and a scan window of sixteen.</summary>
    public static IEnumerable<int> RowCounts => [ZoneRows, 131_072];

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
        for (int row = 0; row < Rows; row++)
        {
            string text = Shape switch
            {
                "urls" => string.Create(CultureInfo.InvariantCulture, $"https://example.invalid/vortex/conformance/{row:D9}"),
                "labels" => string.Create(CultureInfo.InvariantCulture, $"label-{row % 16:D2}"),
                _ => new Guid(Pad(row)).ToString("D"),
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
        int index = _arena.AddVarBinView(types.Utf8(Nullability.NonNullable), Rows, Validity.NonNullable, viewBuffer, [heapBuffer]);
        _node = index;

        IReadOnlyList<ZoneString> expected = OriginalZones();
        IReadOnlyList<ZoneString> got = CurrentZones();
        for (int zone = 0; zone < expected.Count; zone++)
        {
            ReadOnlyMemory<byte> expectedMax = expected[zone].Max ?? default;
            ReadOnlyMemory<byte> gotMax = got[zone].Max ?? default;
            if (!expected[zone].Min.Span.SequenceEqual(got[zone].Min.Span) ||
                expected[zone].Max.HasValue != got[zone].Max.HasValue ||
                !expectedMax.Span.SequenceEqual(gotMax.Span))
            {
                throw new InvalidOperationException($"{Shape}: zone {zone}'s bounds differ from the original's.");
            }
        }
    }

    private static byte[] Pad(int row)
    {
        byte[] raw = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(raw, (ulong)row * 0x9E3779B97F4A7C15UL);
        BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(8), ~(ulong)row * 0xC2B2AE3D27D4EB4FUL);
        return raw;
    }

    [Benchmark(Baseline = true)]
    public int Original() => OriginalZones().Count;

    [Benchmark]
    public int Current() => CurrentZones().Count;

    private IReadOnlyList<ZoneString> OriginalZones()
    {
        StringZones zones = new StringZones(Limit);
        StringBoundsOriginal bounds = new StringBoundsOriginal(Limit, utf8: true);
        for (int start = 0; start < Rows; start += ZoneRows)
        {
            bounds.Accumulate(_arena, _arena.GetNode(_node), start, Math.Min(ZoneRows, Rows - start));
            zones.Seed(bounds.Close(zones));
        }

        return zones.All(zones.Count)!;
    }

    private IReadOnlyList<ZoneString> CurrentZones()
    {
        StringZones zones = new StringZones(Limit);
        for (int start = 0; start < Rows; start += ZoneRows)
        {
            zones.Accumulate(_arena, _arena.GetNode(_node), start, Math.Min(ZoneRows, Rows - start));
            zones.Close();
        }

        return zones.All(zones.Count)!;
    }
}
