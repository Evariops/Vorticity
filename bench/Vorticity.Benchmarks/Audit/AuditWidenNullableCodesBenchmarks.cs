// Audit AR-12: what a nullable column's dictionary codes cost widened, the rows' validity tracked from the codes' and the
// values', as a decode of dictionary-encoded texts with nulls makes it.
//
// 65,536 codes of 16 or 32 bits into a dictionary of 5,000 values all valid, one code in ten null.
using System;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Audit;

/// <summary>Nullable dictionary codes widened.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditWidenNullableCodesBenchmarks
{
    private const int Rows = 65_536;
    private const int Values = 5_000;

    private readonly uint[] _destination = new uint[Rows];
    private readonly byte[] _output = new byte[Rows / 8];
    private byte[] _codes = [];
    private byte[] _codeBits = [];

    /// <summary>The codes' type.</summary>
    [Params("U16", "U32")]
    public string Codes { get; set; } = "U16";

    /// <summary>Draws the codes and their validity.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(109);
        int width = Codes == "U16" ? 2 : 4;
        _codes = new byte[Rows * width];
        _codeBits = new byte[Rows / 8];
        for (int i = 0; i < Rows; i++)
        {
            bool valid = random.Next(10) != 0;
            int code = valid ? random.Next(Values) : 60_000;
            if (valid)
            {
                _codeBits[i >> 3] |= (byte)(1 << (i & 7));
            }

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

    /// <summary>The codes widened and the rows' validity made, by code.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public int Widen()
    {
        _output.AsSpan().Clear();
        return RowKernels.WidenCodes(_codes, Codes == "U16" ? PType.U16 : PType.U32, Values, _destination, _codeBits, 0, default, 0, true, _output);
    }
}
