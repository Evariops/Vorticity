// What closing a file costs a session's segment cache, against the segments the cache holds.
//
// A cache holds `Entries` segments of 64 bytes from 64 files. `Absent` evicts a file the cache holds
// nothing of, as the close of a file whose segments the budget already pushed out; `Present`
// evicts a file of 16 segments and keeps them again, as the close of a file and the open of the
// next. Run in a checkout of the original and in the tree.
using System;

using BenchmarkDotNet.Attributes;

using Vorticity.Buffers;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>A file's segments evicted from a session cache, against the segments it holds.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class SegmentCacheEvictBenchmarks
{
    private const int Sources = 64;

    private const int Closing = 16;

    /// <summary>Segments the cache holds.</summary>
    [Params(1_024, 65_536)]
    public int Entries { get; set; }

    private readonly object _absent = new object();
    private readonly object _closing = new object();
    private SegmentCache _cache = null!;

    /// <summary>Fills the cache, and checks a file's eviction takes its segments and no other.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _cache = new SegmentCache(long.MaxValue);
        object[] sources = new object[Sources];
        for (int s = 0; s < Sources; s++)
        {
            sources[s] = new object();
        }

        for (int i = 0; i < Entries - Closing; i++)
        {
            Keep(sources[i % Sources], i);
        }

        Refill();
        long size = _cache.Size;
        Present();
        if (_cache.Size != size || size != 64L * Entries)
        {
            throw new InvalidOperationException("The cache does not hold what it was given back.");
        }
    }

    /// <summary>Drops the cache's segments.</summary>
    [GlobalCleanup]
    public void Cleanup() => _cache.Clear();

    /// <summary>A file the cache holds nothing of, evicted.</summary>
    [Benchmark]
    public long Absent()
    {
        _cache.Evict(_absent);
        return _cache.Size;
    }

    /// <summary>A file of 16 segments evicted, and its segments kept again.</summary>
    [Benchmark]
    public long Present()
    {
        _cache.Evict(_closing);
        Refill();
        return _cache.Size;
    }

    private void Refill()
    {
        for (int i = 0; i < Closing; i++)
        {
            Keep(_closing, i);
        }
    }

    private void Keep(object source, long offset)
    {
        NativeSegmentOwner owner = AlignedBufferPool.Shared.Rent(64, 64);
        _cache.Add(source, offset * 64, owner, owner.Buffer);
        owner.Release();
    }
}
