// Audit IO-04: what a read of an object under a store's prefix allocates besides its bytes: the prefixed key, made at
// each read, tail read or head.
//
// An object of 1 MiB in an in-memory store under "datasets/events/objects/", read 4 KiB at a time through a source over
// the prefixed store.
using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Buffers;
using Vorticity.Dataset;
using Vorticity.IO;
using Vorticity.Serialization.Schemas;

namespace Vorticity.Benchmarks.Audit;

/// <summary>Reads of an object under a prefix.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditPrefixedReadsBenchmarks
{
    private const int Reads = 256;

    private MemoryObjectStore _store = null!;
    private ObjectStoreSource _source = null!;

    /// <summary>Puts the object and opens the source.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _store = new MemoryObjectStore();
        byte[] bytes = new byte[1 << 20];
        new Random(1022).NextBytes(bytes);
        ((IObjectStore)_store).PutAsync("datasets/events/objects/0001-part-00042.vortex", new ReadOnlySequence<byte>(bytes), PutCondition.None, CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
        _source = new ObjectStoreSource(new PrefixedObjectStore(_store, "datasets/events/objects/"), "0001-part-00042.vortex", bytes.Length);
        Read();
    }

    /// <summary>Disposes the store.</summary>
    [GlobalCleanup]
    public void Cleanup() => _store.DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>4 KiB reads, by read.</summary>
    [Benchmark(OperationsPerInvoke = Reads)]
    public long Read() => ReadAsync().GetAwaiter().GetResult();

    private async Task<long> ReadAsync()
    {
        long read = 0;
        for (int i = 0; i < Reads; i++)
        {
            using SegmentOwner owner = await _source.ReadAsync(new SegmentSpec((ulong)(i * 4096), 4096, 0, 0, 0), CancellationToken.None).ConfigureAwait(false);
            read += owner.Buffer.Length;
        }

        return read;
    }
}
