using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays.Decoders.Canonical;

namespace Vorticity.Benchmarks;

/// <summary>
/// The move of a chunk's views into a concatenated column alone, each referencing view's buffer
/// index shifted past the chunks before it: the library's <c>CanonicalConcat.RebaseInto</c>
/// against a frozen copy of the copy and the view-at-a-time rebase it replaced.
/// </summary>
/// <remarks>
/// The views are the per-encoding corpus file's chunked ones, values of 0 to 19 bytes over three
/// data buffers, and the chunk lands after five buffers of earlier chunks. The arms are checked
/// against each other before anything is timed.
/// </remarks>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
[MinColumn]
[MedianColumn]
[CyclesColumn]
public class ViewRebaseBenchmarks
{
    private const int ViewSize = 16;
    private const int Buffers = 3;
    private const int Base = 5;

    private byte[] _source = [];
    private byte[] _block = [];

    /// <summary>Views moved in one call.</summary>
    [ParamsSource(nameof(RowCounts))]
    public int Rows { get; set; } = 1024;

    /// <summary>Views in the first-level cache, and a scan window's.</summary>
    public static IEnumerable<int> RowCounts => [1024, 131_072];

    /// <summary>What one invocation moves: a view a row.</summary>
    /// <param name="method">Unused; every arm moves the same.</param>
    /// <param name="parameters">The case's rows.</param>
    public static (long Rows, long Bytes) BenchmarkWork(string method, IReadOnlyDictionary<string, object?> parameters)
    {
        int rows = (int)parameters[nameof(Rows)]!;
        return (rows, (long)rows * ViewSize);
    }

    [GlobalSetup]
    public void Setup()
    {
        _source = new byte[Rows * ViewSize];
        for (int row = 0; row < Rows; row++)
        {
            Span<byte> view = _source.AsSpan(row * ViewSize, ViewSize);
            int size = row % 20;
            BitConverter.TryWriteBytes(view, size);
            view.Slice(4, Math.Min(size, 12)).Fill((byte)'x');
            if (size > 12)
            {
                BitConverter.TryWriteBytes(view[8..], row % Buffers);
                BitConverter.TryWriteBytes(view[12..], row * 3);
            }
        }

        _block = new byte[_source.Length];
        Original();
        byte[] expected = (byte[])_block.Clone();
        _block.AsSpan().Fill(0xA5);
        Current();
        if (!_block.AsSpan().SequenceEqual(expected))
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture, $"the library's views differ from the original's at byte {_block.AsSpan().CommonPrefixLength(expected)}."));
        }
    }

    [Benchmark(Baseline = true)]
    public int Original()
    {
        _source.AsSpan().CopyTo(_block);
        Span<uint> words = MemoryMarshal.Cast<byte, uint>(_block.AsSpan());
        for (int j = 0; j < Rows; j++)
        {
            int w = j * 4;
            if (words[w] <= 12)
            {
                continue;
            }

            uint index = words[w + 2];
            if (index >= Buffers)
            {
                throw new InvalidOperationException($"row {j} references buffer {index}");
            }

            words[w + 2] = (uint)(Base + (int)index);
        }

        return Rows;
    }

    [Benchmark]
    public int Current()
    {
        CanonicalConcat.RebaseInto(_source, _block, Rows, Base, Buffers, 0);
        return Rows;
    }
}
