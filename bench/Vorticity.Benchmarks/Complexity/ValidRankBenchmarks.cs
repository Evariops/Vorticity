// What a chunk read a range at a time pays to find where each range's first value sits.
//
// A nullable chunk of N rows whose values are stored for the valid rows alone, as zstd frames
// store them, read in ranges of W rows as a scan reads a chunk larger than a window: each range's
// first value is the count of valid rows before it. `Original` counts the bitmap from the chunk's
// first row at every range; `Library` asks the decode context, which counts from where the range
// before of the same node stopped.
using System;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>The valid rows before every range of a chunk, against the chunk's length.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class ValidRankBenchmarks
{
    /// <summary>The chunk's rows.</summary>
    [Params(1 << 20, 1 << 24)]
    public int Rows { get; set; }

    /// <summary>The rows of a range.</summary>
    [Params(8_192, 131_072)]
    public int RangeRows { get; set; }

    private byte[] _bits = [];
    private ScanContext _context = null!;

    /// <summary>Builds the bitmap and opens a scope as a reader does.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _bits = new byte[Rows / 8];
        new Random(21).NextBytes(_bits);
        for (int i = 0; i < _bits.Length; i++)
        {
            _bits[i] |= 0b0111_0111;
        }

        _context = new ScanContext([]);
        _context.BeginNodeCheckScope(7);
    }

    /// <summary>Gives the context back.</summary>
    [GlobalCleanup]
    public void Cleanup() => _context.Dispose();

    /// <summary>Every range counted from the chunk's first row.</summary>
    [Benchmark(Baseline = true)]
    public long Original()
    {
        long total = 0;
        for (int start = RangeRows; start < Rows; start += RangeRows)
        {
            total += BitmapKernels.CountSet(_bits, 0, start);
        }

        return total;
    }

    /// <summary>Every range counted by the decode context.</summary>
    [Benchmark]
    public long Library()
    {
        ArrayDecodeContext decode = _context.Decode;
        ArrayNode node = default;
        long total = 0;
        for (int start = RangeRows; start < Rows; start += RangeRows)
        {
            total += decode.ValidBefore(in node, _bits, 0, start);
        }

        return total;
    }
}
