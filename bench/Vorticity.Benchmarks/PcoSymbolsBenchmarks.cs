// The tANS symbol pass of pco pages of many bins: 256 batches of 256 symbols read from one stream,
// four interleaved states, each value's bin bound and offset width recorded, as
// `PcoLatentState.ReadPreDelta` reads them.
//
// The loop is the decoder's, copied here so that it can run on random tables: what it measures is
// `PcoBitReader.ReadUInt`, a read per symbol, which two builds of the library compare. The table
// and the stream are random: the pass costs the same per symbol whatever it decodes.
using System;
using System.Collections.Generic;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays.Decoders.Compressed.Pco;

namespace Vorticity.Benchmarks;

/// <summary>65 536 tANS symbols read through <c>PcoBitReader</c>.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
public class PcoSymbolsBenchmarks
{
    private const int Batch = 256;

    private const int Batches = 256;

    private const int States = 1 << 10;

    private PcoAnsNode[] _nodes = [];
    private ulong[] _lowers = [];
    private byte[] _stream = [];
    private ulong[] _scratch = new ulong[Batch];
    private int[] _offsetBits = new int[Batch];
    private long[] _offsetStarts = new long[Batch];
    private int[] _states = new int[4];

    /// <summary>What one invocation decodes: a symbol per value.</summary>
    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters) => (Batch * Batches, Batch * Batches * 8L);

    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(20260927);
        _nodes = new PcoAnsNode[States];
        _lowers = new ulong[States];
        for (int s = 0; s < States; s++)
        {
            int bits = random.Next(1, 8);
            _nodes[s] = new PcoAnsNode(random.Next(States - (1 << bits) + 1), random.Next(12), bits);
            _lowers[s] = (ulong)random.NextInt64();
        }

        _stream = new byte[(Batch * Batches) + 64];
        random.NextBytes(_stream);
    }

    [Benchmark(Description = "tans symbols")]
    public long Symbols()
    {
        PcoBitReader reader = new PcoBitReader(_stream);
        _states.AsSpan().Clear();
        long total = 0;
        for (int b = 0; b < Batches; b++)
        {
            long offsetBitTotal = 0;
            for (int i = 0; i < Batch; i++)
            {
                int slot = _states[i % 4];
                PcoAnsNode node = _nodes[slot];
                ulong ansValue = reader.ReadUInt(node.BitsToRead);
                _scratch[i] = _lowers[slot];
                _offsetBits[i] = node.OffsetBits;
                _offsetStarts[i] = offsetBitTotal;
                offsetBitTotal += node.OffsetBits;
                _states[i % 4] = node.NextStateIndexBase + (int)ansValue;
            }

            total += offsetBitTotal;
        }

        return total;
    }
}
