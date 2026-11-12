// What a presence filter of a column sheet costs built, its keys distinct hashes, as a writer builds one a
// column of an object whose values repeat.
//
// 1,000 and 65,536 keys drawn at random.
using System;

using BenchmarkDotNet.Attributes;

using Vorticity.Indexes;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A binary fuse filter built.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditBinaryFuseBenchmarks
{
    private ulong[] _keys = [];

    /// <summary>The keys the filter holds.</summary>
    [Params(1_000, 65_536)]
    public int Keys { get; set; }

    /// <summary>Draws the keys.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(53);
        _keys = new ulong[Keys];
        for (int i = 0; i < Keys; i++)
        {
            _keys[i] = (ulong)random.NextInt64();
        }
    }

    /// <summary>The filter built.</summary>
    [Benchmark]
    public int Build() => BinaryFuse8.Build(_keys)!.Bytes;
}
