// A Bloom index's writer hashes each fixed-width row into a set of the block's distinct hashes,
// then sets each distinct hash's bits in the filter; its pruner tests a literal's bits per block.
//
// The ported arms are the loops as they were: XXH3 a value at a time, and a block's eight words
// set or tested a word at a time. The shipped arms are `BloomBuilder.HashRows`, which hashes 64
// values eight an instruction before it adds them to the set, and `SplitBlockBloom.Insert` and
// `Contains`, a block as one 256-bit vector. Both run in one process, so tiered PGO is best turned
// off (`DOTNET_TieredPGO=0`).
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Indexes;
using Vorticity.Writing;

namespace Vorticity.Benchmarks;

/// <summary>65 536 rows hashed into a block's set, or 65 536 hashes set in or tested against a filter.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
public class BloomKernelBenchmarks
{
    private const int Rows = 1 << 16;

    /// <summary>
    /// "rows" hashes a column of 4 or 8 bytes a value, over 1 024 distinct values or all distinct;
    /// "insert" and "contains" set or test 65 536 hashes in a filter of 4 096 blocks.
    /// </summary>
    [Params("rows u32 1k", "rows u64 1k", "rows u64 all", "insert", "contains")]
    public string Case { get; set; } = "rows u64 1k";

    private static readonly uint[] Salts = [0x47b6137b, 0x44974d91, 0x8824ad5b, 0xa2b7289d, 0x705495c7, 0x2df1424b, 0x9efc4947, 0x5c6bfb31];

    private byte[] _values = [];
    private int _width;
    private ulong[] _hashes = [];
    private uint[] _filter = [];
    private HashSet64 _set = new HashSet64();

    /// <summary>What one invocation reads: a value or a hash per row.</summary>
    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters) =>
        (Rows, Rows * (parameters.TryGetValue(nameof(Case), out object? c) && c is "rows u32 1k" ? 4L : 8L));

    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(20260927);
        _width = Case == "rows u32 1k" ? 4 : 8;
        _values = new byte[Rows * _width];
        random.NextBytes(_values);
        if (Case.EndsWith("1k", StringComparison.Ordinal))
        {
            // 1 024 distinct values, drawn at random from the first 1 024 rows.
            for (int row = 1_024; row < Rows; row++)
            {
                _values.AsSpan(random.Next(1_024) * _width, _width).CopyTo(_values.AsSpan(row * _width));
            }
        }

        _hashes = new ulong[Rows];
        random.NextBytes(MemoryMarshal.AsBytes(_hashes.AsSpan()));
        _filter = new uint[4_096 * SplitBlockBloom.WordsPerBlock];
        foreach (ulong hash in _hashes.AsSpan(0, Rows / 2))
        {
            SplitBlockBloom.Insert(_filter, hash);
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _set.Dispose();

    [Benchmark(Baseline = true, Description = "bloom, ported")]
    public int Ported()
    {
        switch (Case)
        {
            case "insert":
                foreach (ulong hash in _hashes)
                {
                    int block = (int)(((hash >> 32) * (ulong)(uint)(_filter.Length / 8)) >> 32);
                    Span<uint> lanes = _filter.AsSpan(block * 8, 8);
                    uint key = (uint)hash;
                    for (int i = 0; i < 8; i++)
                    {
                        lanes[i] |= 1u << (int)((key * Salts[i]) >> 27);
                    }
                }

                return 0;
            case "contains":
                int found = 0;
                foreach (ulong hash in _hashes)
                {
                    int block = (int)(((hash >> 32) * (ulong)(uint)(_filter.Length / 8)) >> 32);
                    ReadOnlySpan<uint> lanes = _filter.AsSpan(block * 8, 8);
                    uint key = (uint)hash;
                    uint missing = 0;
                    for (int i = 0; i < 8; i++)
                    {
                        missing |= ~lanes[i] & (1u << (int)((key * Salts[i]) >> 27));
                    }

                    found += missing == 0 ? 1 : 0;
                }

                return found;
            default:
                _set.Clear();
                if (_width == 4)
                {
                    foreach (uint word in MemoryMarshal.Cast<byte, uint>(_values))
                    {
                        _set.Add(XxHash3Fixed.Hash4(word));
                    }
                }
                else
                {
                    foreach (ulong word in MemoryMarshal.Cast<byte, ulong>(_values))
                    {
                        _set.Add(XxHash3Fixed.Hash8(word));
                    }
                }

                return _set.Count;
        }
    }

    [Benchmark(Description = "bloom, shipped")]
    public int Shipped()
    {
        switch (Case)
        {
            case "insert":
                foreach (ulong hash in _hashes)
                {
                    SplitBlockBloom.Insert(_filter, hash);
                }

                return 0;
            case "contains":
                int found = 0;
                foreach (ulong hash in _hashes)
                {
                    found += SplitBlockBloom.Contains(_filter, hash) ? 1 : 0;
                }

                return found;
            default:
                _set.Clear();
                BloomBuilder.HashRows(_values, _width, 0, Rows, _set);
                return _set.Count;
        }
    }
}
