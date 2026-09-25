using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Benchmarks;

/// <summary>
/// The bytes a string column's rows name, which a writer reads once per batch to size its chunks:
/// the library's <c>CanonicalArena.NamedBytes</c> against a frozen copy of the view walk as it was,
/// in one process and on one clock.
/// </summary>
/// <remarks>
/// <para>
/// <c>heap</c> is views of 40-byte values, every one out of line; <c>inline</c> views of 8-byte
/// values; <c>mixed</c> lengths from 0 to 40 in no order. Only the views are read, so the data
/// buffer is a shared placeholder.
/// </para>
/// <para>The arms' totals are checked against each other before anything is timed.</para>
/// </remarks>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
[MinColumn]
[MedianColumn]
[CyclesColumn]
public class NamedBytesBenchmarks
{
    private const int ViewSize = 16;

    private CanonicalArena _arena = null!;
    private int _node;

    /// <summary>The views: <c>heap</c>, <c>inline</c> or <c>mixed</c>.</summary>
    [ParamsSource(nameof(Shapes))]
    public string Shape { get; set; } = "mixed";

    /// <summary>Views summed.</summary>
    [ParamsSource(nameof(RowCounts))]
    public int Rows { get; set; } = 131_072;

    /// <summary>Every shape in every profile.</summary>
    public static IEnumerable<string> Shapes => ["heap", "inline", "mixed"];

    /// <summary>A scan window, and a batch of a million.</summary>
    public static IEnumerable<int> RowCounts => [131_072, 1_048_576];

    /// <summary>What one invocation reads: a view a row.</summary>
    /// <param name="method">Unused; every arm reads the same.</param>
    /// <param name="parameters">The case's rows.</param>
    public static (long Rows, long Bytes) BenchmarkWork(string method, IReadOnlyDictionary<string, object?> parameters)
    {
        int rows = (int)parameters[nameof(Rows)]!;
        return (rows, (long)rows * ViewSize);
    }

    [GlobalSetup]
    public void Setup()
    {
        _arena = new CanonicalArena();
        DTypeArena types = new DTypeArena();
        Random random = new Random(20260925);
        VortexBuffer views = _arena.Allocate(Rows * ViewSize, 16, out Span<byte> bytes);
        for (int row = 0; row < Rows; row++)
        {
            int length = Shape switch
            {
                "heap" => 40,
                "inline" => 8,
                _ => random.Next(41),
            };
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[(row * ViewSize)..], (uint)length);
        }

        VortexBuffer heap = _arena.Allocate(64, 16, out _);
        _node = _arena.AddVarBinView(types.Utf8(Nullability.NonNullable), Rows, Validity.NonNullable, views, [heap]);
        if (Original() != Current())
        {
            throw new InvalidOperationException($"{Shape}: the library's total differs from the original's.");
        }
    }

    [Benchmark(Baseline = true)]
    public long Original()
    {
        ReadOnlySpan<byte> views = _arena.GetNode(_node).Views.Span;
        ReadOnlySpan<uint> words = MemoryMarshal.Cast<byte, uint>(views[..(Rows * ViewSize)]);
        long bytes = 0;
        for (int row = 0; row < words.Length; row += ViewSize / sizeof(uint))
        {
            uint length = words[row];
            bytes += length > 12 ? length : 0u;
        }

        return views.Length + bytes;
    }

    [Benchmark]
    public long Current() => _arena.NamedBytes(_node);
}
