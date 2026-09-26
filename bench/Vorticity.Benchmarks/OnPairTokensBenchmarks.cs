// An onpair dictionary's token table: each token's start and size packed into one word from the
// dictionary's offsets, which every decode of a node of the encoding builds before it concatenates
// a code, the ranges of a scan included.
//
// A full dictionary, 65 536 tokens of one to sixteen bytes, its offsets 32 bits wide.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Types;

namespace Vorticity.Benchmarks;

/// <summary><c>OnPairDecoder.BuildTokenTable</c> over 65 536 tokens.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
public class OnPairTokensBenchmarks
{
    private const int Tokens = 1 << 16;

    private byte[] _offsets = [];
    private long[] _table = new long[Tokens];
    private int _dictionaryLength;

    /// <summary>What one invocation reads and writes: an offset and a packed token per token.</summary>
    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters) => (Tokens, Tokens * 12L);

    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(20260927);
        uint[] offsets = new uint[Tokens + 1];
        for (int t = 0; t < Tokens; t++)
        {
            offsets[t + 1] = offsets[t] + (uint)random.Next(1, 17);
        }

        _dictionaryLength = (int)offsets[Tokens];
        _offsets = MemoryMarshal.AsBytes(offsets.AsSpan()).ToArray();
    }

    [Benchmark(Description = "token table")]
    public long Build()
    {
        OnPairDecoder.BuildTokenTable(_offsets, PType.U32, Tokens, _table, _dictionaryLength);
        return _table[^1];
    }
}
