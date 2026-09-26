// A dictionary's codes widened to 32 bits and range-checked, how a dictionary column kept encoded
// for its filters holds its codes whatever width they were written at.
using System;
using System.Collections.Generic;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Types;

namespace Vorticity.Benchmarks;

/// <summary><c>RowKernels.WidenCodes</c> of 65 536 codes, with no validity on either side.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
public class WidenCodesBenchmarks
{
    private const int Rows = 1 << 16;

    private const int Entries = 200;

    /// <summary>The codes' width.</summary>
    [Params("u8", "u16")]
    public string Codes { get; set; } = "u8";

    private byte[] _codes = [];
    private uint[] _destination = [];

    /// <summary>What one invocation reads and writes: a code and a 32-bit code per row.</summary>
    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters) =>
        (Rows, Rows * (parameters.TryGetValue(nameof(Codes), out object? c) && c is "u16" ? 6L : 5L));

    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(20260926);
        int width = Codes == "u16" ? 2 : 1;
        _codes = new byte[Rows * width];
        for (int i = 0; i < Rows; i++)
        {
            int code = random.Next(Entries);
            if (width == 1)
            {
                _codes[i] = (byte)code;
            }
            else
            {
                BitConverter.TryWriteBytes(_codes.AsSpan(i * 2, 2), (ushort)code);
            }
        }

        _destination = new uint[Rows];
    }

    [Benchmark(Description = "widen codes")]
    public int Widen() =>
        RowKernels.WidenCodes(
            _codes, Codes == "u16" ? PType.U16 : PType.U8, Entries, _destination,
            default, 0, default, 0, true, default);
}
