// What one Bloom probe and one key's hash cost: the two prices a Bloom pruner weighs, in its unit
// of a probe, against the blocks it rules out.
//
// `Probe` asks a split-block filter of `FilterBlocks` blocks about 4,096 hashes of keys it does not
// hold, the filter loaded as a writer loads it at a 1 % rate (sixteen keys a block): one of 4 blocks
// stays in the first-level cache, one of 32,768 (a megabyte) does not. `Hash` turns 4,096 Int64
// literals into the column's key bytes and hashes them, as the pruner does before it probes; it does
// not depend on the filter. Both report the time of one key.
using System;

using BenchmarkDotNet.Attributes;

using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>One Bloom probe and one key's hash, against the filter's size.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class BloomProbeBenchmarks
{
    /// <summary>The keys asked, each benchmark's operations.</summary>
    private const int Keys = 4_096;

    /// <summary>The keys a block of the filter holds, as a filter sized for a 1 % rate holds them.</summary>
    private const int KeysPerBlock = 16;

    /// <summary>The filter's 256-bit blocks.</summary>
    [Params(4, 512, 32_768)]
    public int FilterBlocks { get; set; }

    private uint[] _filter = [];
    private ulong[] _hashes = [];
    private FilterLiteral[] _literals = [];
    private KeyLayout _layout;

    /// <summary>Fills the filter with keys of its own and draws the keys asked, none of them among those.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(0x0B10_0F);
        _filter = new uint[FilterBlocks * SplitBlockBloom.WordsPerBlock];
        for (int i = 0; i < FilterBlocks * KeysPerBlock; i++)
        {
            SplitBlockBloom.Insert(_filter, Mix((ulong)(2 * i)));
        }

        _hashes = new ulong[Keys];
        _literals = new FilterLiteral[Keys];
        for (int i = 0; i < Keys; i++)
        {
            _hashes[i] = Mix((ulong)((2 * i) + 1));
            _literals[i] = FilterLiteral.From(random.NextInt64());
        }

        DTypeArena types = new DTypeArena();
        if (!KeyLayout.TryOf(types.Primitive(PType.I64, Nullability.NonNullable), out _layout))
        {
            throw new InvalidOperationException("An Int64 column has no key layout.");
        }
    }

    /// <summary>The keys the filter may hold, of keys it does not.</summary>
    [Benchmark(OperationsPerInvoke = Keys)]
    public int Probe()
    {
        ReadOnlySpan<uint> words = _filter;
        int maybe = 0;
        foreach (ulong hash in _hashes)
        {
            if (SplitBlockBloom.Contains(words, hash))
            {
                maybe++;
            }
        }

        return maybe;
    }

    /// <summary>The literals' key bytes, hashed.</summary>
    [Benchmark(OperationsPerInvoke = Keys)]
    public ulong Hash()
    {
        Span<byte> scratch = stackalloc byte[sizeof(ulong)];
        Span<byte> zero = stackalloc byte[sizeof(ulong)];
        ulong sum = 0;
        foreach (FilterLiteral literal in _literals)
        {
            if (_layout.TryEncode(literal, scratch, out ReadOnlySpan<byte> key, zero, out _))
            {
                sum += SplitBlockBloom.Hash(key, BloomHash.XxHash3);
            }
        }

        return sum;
    }

    /// <summary>A key's stand-in hash: splitmix64, so the keys spread as a real hash spreads them.</summary>
    private static ulong Mix(ulong value)
    {
        ulong z = value + 0x9E37_79B9_7F4A_7C15;
        z = (z ^ (z >> 30)) * 0xBF58_476D_1CE4_E5B9;
        z = (z ^ (z >> 27)) * 0x94D0_49BB_1331_11EB;
        return z ^ (z >> 31);
    }
}
