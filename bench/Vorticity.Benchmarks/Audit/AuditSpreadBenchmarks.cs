// What dense values cost spread over the rows of a nullable column, as zstd and pco decodes do, a word of
// mixed validity expanded in registers on AVX-512 and row by row elsewhere.
//
// 65,536 rows, one in ten null or one in two, of 4- and 8-byte values, into zeroed rows. Run as it is (AVX-512 here) and
// under DOTNET_EnableAVX512=0 (AVX2).
using System;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays.Decoders.Compressed;

namespace Vorticity.Benchmarks.Audit;

/// <summary>Values spread over valid rows.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditSpreadBenchmarks
{
    private const int Rows = 65_536;

    private byte[] _bits = [];
    private byte[] _values = [];
    private byte[] _rows = [];

    /// <summary>One row in how many is null.</summary>
    [Params(2, 10)]
    public int NullEvery { get; set; }

    /// <summary>The values' bytes.</summary>
    [Params(4, 8)]
    public int Width { get; set; }

    /// <summary>Draws the validity and the values.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(73);
        _bits = new byte[Rows / 8];
        int valid = 0;
        for (int row = 0; row < Rows; row++)
        {
            if (random.Next(NullEvery) != 0)
            {
                _bits[row >> 3] |= (byte)(1 << (row & 7));
                valid++;
            }
        }

        _values = new byte[valid * Width];
        random.NextBytes(_values);
        _rows = new byte[Rows * Width];
    }

    /// <summary>The values spread, by row.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public byte Spread()
    {
        ValidRows.Spread(_values, _rows, Vorticity.Arrays.Decoders.Canonical.ValidityMask.Bitmap(_bits, 0), Rows, Width, "audit");
        return _rows[^1];
    }
}
