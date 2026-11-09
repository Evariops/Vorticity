// Audit IO-01, IO-02: what a hit of the process's segment cache costs, alone and with sixteen threads at once.
//
// 4,096 segments of 4 KiB of one object of a store are kept in a cache, under the store's reader of the object
// (an ObjectStoreSource, whose key a lookup hashes). `Lookup` asks the cache for each of them in turn, segment by
// segment; `Sets` asks a session's reader for them eight at a time, as a scan's request set asks, every slot a
// hit. Each thread walks every segment; the time is per segment asked.
using System;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Buffers;
using Vorticity.Dataset;
using Vorticity.IO;
using Vorticity.Serialization.Schemas;

namespace Vorticity.Benchmarks.Audit;

/// <summary>Hits of the segment cache, segment by segment and a request set at a time, against the threads asking.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditSegmentCacheBenchmarks
{
    private const int Segments = 4_096;
    private const int SegmentBytes = 4_096;
    private const int SetSlots = 8;

    /// <summary>Threads asking at once.</summary>
    [Params(1, 16)]
    public int Threads { get; set; }

    private MemoryObjectStore _store = null!;
    private ObjectStoreSource _source = null!;
    private SegmentCache _cache = null!;
    private VortexSession _session = null!;
    private ISegmentReader _reader = null!;
    private SegmentOwner[] _owners = null!;

    /// <summary>Keeps the segments.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _store = new MemoryObjectStore();
        _source = new ObjectStoreSource(_store, "data/tenant-0042/2026/11/09/hour-17/part-000123-4f6c2a9e.vortex", (long)Segments * SegmentBytes);
        _cache = new SegmentCache(256L << 20);
        _session = VortexSession.Create(options => options.SegmentCache = _cache);
        _reader = SessionReader.Wrap(_source, _session);
        _owners = new SegmentOwner[Segments];
        for (int i = 0; i < Segments; i++)
        {
            _owners[i] = AlignedBufferPool.Shared.Rent(SegmentBytes, 64);
            _cache.Add(_source, (long)i * SegmentBytes, _owners[i], _owners[i].Buffer);
        }
    }

    /// <summary>Drops the segments.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _cache.Clear();
        foreach (SegmentOwner owner in _owners)
        {
            owner.Dispose();
        }

        _session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _source.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _store.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Every segment asked of the cache, one lookup each.</summary>
    [Benchmark(OperationsPerInvoke = Segments)]
    public long Lookup() => Run(LookupOnce);

    /// <summary>Every segment asked of the session's reader, eight to a request set.</summary>
    [Benchmark(OperationsPerInvoke = Segments)]
    public long Sets() => Run(SetsOnce);

    private long Run(Func<int, long> walk)
    {
        if (Threads == 1)
        {
            return walk(0);
        }

        long total = 0;
        Parallel.For(0, Threads, new ParallelOptions { MaxDegreeOfParallelism = Threads }, thread => Interlocked.Add(ref total, walk(thread)));
        return total;
    }

    private long LookupOnce(int thread)
    {
        long bytes = 0;
        for (int n = 0; n < Segments; n++)
        {
            int i = (n * 7 + thread * 131) % Segments;
            if (_cache.TryGet(_source, (long)i * SegmentBytes, SegmentBytes, out SegmentOwner owner, out VortexBuffer buffer))
            {
                bytes += buffer.Length;
                owner.Release();
            }
        }

        return bytes;
    }

    private long SetsOnce(int thread)
    {
        long bytes = 0;
        using SegmentRequestSet set = new SegmentRequestSet(SetSlots);
        for (int n = 0; n < Segments; n += SetSlots)
        {
            for (int s = 0; s < SetSlots; s++)
            {
                int i = ((n + s) * 7 + thread * 131) % Segments;
                set.Add(new SegmentSpec((ulong)i * SegmentBytes, SegmentBytes, 6, 0, 0));
            }

            _reader.ReadManyAsync(set, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            for (int s = 0; s < SetSlots; s++)
            {
                bytes += set.GetBuffer(s).Length;
            }

            set.Release();
        }

        return bytes;
    }
}
