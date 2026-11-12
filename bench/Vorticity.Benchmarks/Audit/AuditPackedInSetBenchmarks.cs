// What an IN of more than 16 members costs over packed codes of at most 8 bits, a dictionary's of at most
// 256 values: each block of 1,024 codes unpacked, then each code looked up in a bit a value of the width. 65,536 rows of
// `BitWidth`-bit codes, `Members` of whose values are in the set. Run as it is (AVX-512 VBMI on Zen 4) and under
// DOTNET_EnableAVX512=0 (AVX2) and DOTNET_EnableHWIntrinsic=0 (scalar).
using System;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays.Decoders.Compressed;

namespace Vorticity.Benchmarks.Audit;

/// <summary>An IN of many members over 65,536 packed codes of a few bits.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditPackedInSetBenchmarks
{
    private const int Blocks = 64;

    /// <summary>Bits a code.</summary>
    [Params(5, 8)]
    public int BitWidth { get; set; }

    /// <summary>The values of the set, among the width's.</summary>
    [Params(20, 100)]
    public int Members { get; set; }

    private byte[] _packed = [];
    private ulong[] _set = [];
    private ulong[] _words = [];

    /// <summary>Packs the codes and draws the set.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(22);
        int values = 1 << BitWidth;
        int wordsPerBlock = FastLanes.BlockSize / 8 * BitWidth;
        _packed = new byte[Blocks * wordsPerBlock];
        byte[] block = new byte[FastLanes.BlockSize];
        for (int b = 0; b < Blocks; b++)
        {
            for (int i = 0; i < block.Length; i++)
            {
                block[i] = (byte)random.Next(values);
            }

            FastLanes.PackBlock<byte>(block, BitWidth, _packed.AsSpan(b * wordsPerBlock, wordsPerBlock));
        }

        _set = new ulong[Math.Max(1, values / 64)];
        int members = Math.Min(Members, values);
        for (int m = 0; m < members; m++)
        {
            int value = (int)((long)m * values / members);
            _set[value >> 6] |= 1UL << (value & 63);
        }

        _words = new ulong[Blocks * FastLanes.BlockSize / 64];
    }

    /// <summary>The codes in the set, a bit a row.</summary>
    [Benchmark(OperationsPerInvoke = Blocks * FastLanes.BlockSize)]
    public ulong InSet()
    {
        FastLanes.InSet<byte>(_packed, BitWidth, _set, _words, Blocks);
        return _words[Blocks / 2];
    }
}
