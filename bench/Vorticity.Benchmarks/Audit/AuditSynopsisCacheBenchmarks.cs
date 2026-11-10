// Audit SY-01: what a region read from the session's disk cache costs when the cache holds it.
//
// A cache in a temporary directory holds 4,096 regions of 1 KiB of 64 objects, written and flushed. Each operation
// reads one region a thread, a hit, its view released: from one thread, and from sixteen at once, as the lanes of a
// question ask their regions.
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Buffers;
using Vorticity.Indexes;

namespace Vorticity.Benchmarks.Audit;

/// <summary>Hits of the disk cache, from one thread and from sixteen.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditSynopsisCacheBenchmarks
{
    private const int Objects = 64;
    private const int RegionsPerObject = 64;
    private const int RegionBytes = 1_024;
    private const int Reads = 4_096;

    /// <summary>The threads reading at once.</summary>
    [Params(1, 16)]
    public int Threads { get; set; }

    private string _directory = null!;
    private SynopsisCache _cache = null!;
    private (UInt128 Uid, IndexSegment Region)[] _regions = null!;

    /// <summary>Writes the regions and flushes them.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"vorticity-audit-synopsis-{Guid.NewGuid():N}");
        _cache = new SynopsisCache(_directory, 256L << 20);
        _regions = new (UInt128, IndexSegment)[Objects * RegionsPerObject];
        byte[] bytes = new byte[RegionBytes];
        Random random = new Random(3);
        for (int o = 0; o < Objects; o++)
        {
            UInt128 uid = new UInt128((ulong)random.NextInt64(), (ulong)random.NextInt64());
            for (int r = 0; r < RegionsPerObject; r++)
            {
                random.NextBytes(bytes);
                IndexSegment region = new IndexSegment((ulong)(r * RegionBytes * 4), RegionBytes, 6, System.IO.Hashing.XxHash3.HashToUInt64(bytes));
                _cache.Admit(uid, region, bytes);
                _regions[(o * RegionsPerObject) + r] = (uid, region);
            }
        }

        _cache.Flush();
        Console.WriteLine($"// {Read()} hits");
    }

    /// <summary>Drops the cache and its directory.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _cache.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    /// <summary>A region a thread, read from the cache and let go.</summary>
    [Benchmark(OperationsPerInvoke = Reads)]
    public int Read()
    {
        if (Threads == 1)
        {
            return ReadSome(0, Reads);
        }

        int hits = 0;
        Parallel.For(0, Threads, new ParallelOptions { MaxDegreeOfParallelism = Threads }, t => Interlocked.Add(ref hits, ReadSome(t, Reads / Threads)));
        return hits;
    }

    private int ReadSome(int thread, int count)
    {
        int hits = 0;
        for (int i = 0; i < count; i++)
        {
            (UInt128 uid, IndexSegment region) = _regions[((thread * 977) + (i * 13)) % _regions.Length];
            if (_cache.TryRead(uid, region, out SegmentOwner owner))
            {
                owner.Release();
                hits++;
            }
        }

        return hits;
    }
}
