// What a dictionary gather with nulls on both sides pays for its values' validity.
//
// One gather of a batch of 8,192 rows through a dictionary of V values, a tenth of the codes null
// and a tenth of the values: the shape of a nullable dictionary column read a batch at a time.
// `Original` is the gather as it was (`RowKernelsBefore`), which expands the values' validity to a
// byte per entry at every gather; `Library` is the reader's, which does so only for a dictionary of
// at most a quarter of the batch and otherwise reads the values' bitmap at each code.
using System;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>One gather of a batch through a nullable dictionary, against the dictionary's size.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class DictionaryGatherBenchmarks
{
    /// <summary>The dictionary's values.</summary>
    [Params(1_024, 2_048, 4_096, 8_192, 65_536, 1_048_576)]
    public int Values { get; set; }

    /// <summary>The rows of the batch.</summary>
    private const int Rows = 8_192;

    private uint[] _codes = [];
    private byte[] _codeBits = [];
    private long[] _values = [];
    private byte[] _valueBits = [];
    private long[] _target = [];
    private byte[] _output = [];

    /// <summary>Builds the codes, the dictionary and their validity.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(27);
        _codes = new uint[Rows];
        _codeBits = new byte[(Rows + 7) / 8 + 8];
        for (int row = 0; row < Rows; row++)
        {
            _codes[row] = (uint)random.Next(Values);
            if (random.Next(10) != 0)
            {
                _codeBits[row >> 3] |= (byte)(1 << (row & 7));
            }
        }

        _values = new long[Values];
        _valueBits = new byte[(Values + 7) / 8 + 8];
        for (int value = 0; value < Values; value++)
        {
            _values[value] = value * 3L;
            if (random.Next(10) != 0)
            {
                _valueBits[value >> 3] |= (byte)(1 << (value & 7));
            }
        }

        _target = new long[Rows];
        _output = new byte[(Rows + 7) / 8];
    }

    /// <summary>The values' validity expanded at every gather.</summary>
    [Benchmark(Baseline = true)]
    public int Original() => RowKernelsBefore.MaskedWords<uint, long>(
        _codes, _values, _target, _codeBits, 0, _valueBits, 0, valuesAllValid: false, (uint)Values, _output);

    /// <summary>The gather of the library.</summary>
    [Benchmark]
    public int Library() => RowKernels.GatherMasked(
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(_codes.AsSpan()), PType.U32,
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(_values.AsSpan()), sizeof(long), Values,
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(_target.AsSpan()), Rows,
        _codeBits, 0, _valueBits, 0, valuesAllValid: false, _output);
}
