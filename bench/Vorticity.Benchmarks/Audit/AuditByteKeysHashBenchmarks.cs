// What a key of bytes costs hashed for the dictionaries keyed by bytes (paths, names, values), by the
// runtime's HashCode, which ByteKeys used, and by XXH3 under a seed drawn for the process.
//
// 1,024 keys of 8, 24 or 64 bytes, each hashed once.
using System;
using System.IO.Hashing;

using BenchmarkDotNet.Attributes;

using Vorticity.Buffers;

namespace Vorticity.Benchmarks.Audit;

/// <summary>Byte keys hashed.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditByteKeysHashBenchmarks
{
    private const int Keys = 1_024;
    private static readonly long Seed = Random.Shared.NextInt64();

    private byte[][] _keys = [];

    /// <summary>The keys' length.</summary>
    [Params(8, 24, 64)]
    public int Length { get; set; }

    /// <summary>Draws the keys.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(127);
        _keys = new byte[Keys][];
        for (int i = 0; i < Keys; i++)
        {
            _keys[i] = new byte[Length];
            random.NextBytes(_keys[i]);
        }
    }

    /// <summary>The keys hashed as ByteKeys hashes them, by key.</summary>
    [Benchmark(Baseline = true, OperationsPerInvoke = Keys)]
    public int ByteKeysHash()
    {
        int sum = 0;
        foreach (byte[] key in _keys)
        {
            sum += ByteKeys.Instance.GetHashCode((ReadOnlySpan<byte>)key);
        }

        return sum;
    }

    /// <summary>The keys hashed by XXH3 under the process's seed, by key.</summary>
    [Benchmark(OperationsPerInvoke = Keys)]
    public int Xxh3()
    {
        int sum = 0;
        foreach (byte[] key in _keys)
        {
            sum += (int)XxHash3.HashToUInt64(key, Seed);
        }

        return sum;
    }
}
