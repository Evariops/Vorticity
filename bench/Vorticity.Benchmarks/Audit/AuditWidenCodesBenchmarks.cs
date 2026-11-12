// What a dictionary's codes cost widened to the 32-bit indices a decode takes, every code valid, by the codes'
// type: 16-bit codes went by vectors, 32-bit ones a row at a time.
//
// 65,536 codes into a dictionary of 100,000 values (60,000 for 16-bit codes).
using System;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Audit;

/// <summary>Dictionary codes widened.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditWidenCodesBenchmarks
{
    private const int Rows = 65_536;

    private readonly uint[] _destination = new uint[Rows];
    private byte[] _codes = [];
    private int _values;

    /// <summary>The codes' type.</summary>
    [Params("U16", "U32", "I32")]
    public string Codes { get; set; } = "U32";

    /// <summary>Draws the codes.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(71);
        _values = Codes == "U16" ? 60_000 : 100_000;
        int width = Codes == "U16" ? 2 : 4;
        _codes = new byte[Rows * width];
        for (int i = 0; i < Rows; i++)
        {
            int code = random.Next(_values);
            if (width == 2)
            {
                MemoryMarshal.Write(_codes.AsSpan(i * 2), (ushort)code);
            }
            else
            {
                MemoryMarshal.Write(_codes.AsSpan(i * 4), code);
            }
        }
    }

    /// <summary>The codes widened, by code.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public int Widen() =>
        RowKernels.WidenCodes(_codes, Codes switch { "U16" => PType.U16, "U32" => PType.U32, _ => PType.I32 }, _values, _destination, default, 0, default, 0, true, default);
}
