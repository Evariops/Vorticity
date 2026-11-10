// Audit DS-64: what giving a dataset's objects an index policy pays, its walks over the objects until one builds nothing.
//
// 64 appends of 16,384 calls (40 services) to a dataset sorted by the instant, in an in-memory store that answers each
// request in 5 ms, copied afresh before each run. The run: a Bloom filter on the service for every object, a fragment each.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Dataset;
using Vorticity.IO;

namespace Vorticity.Benchmarks.Audit;

/// <summary>An index policy given to 64 objects.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditIndexingBenchmarks
{
    private const int Appends = 64;
    private const int RowsPerAppend = 16_384;

    private static readonly DateTime Start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly Dictionary<string, byte[]> _pristine = [];
    private MemoryObjectStore _store = null!;
    private VortexDataset _dataset = null!;

    /// <summary>A call: its instant, service and bytes.</summary>
    [VortexRecord]
    public partial record struct Call(DateTime At, string Service, long Bytes);

    /// <summary>Writes the dataset once and keeps its objects.</summary>
    [GlobalSetup]
    public void Setup()
    {
        MemoryObjectStore store = new MemoryObjectStore();
        BuildAsync(store).GetAwaiter().GetResult();
        foreach (ObjectListing listed in store.ListAsync(string.Empty, null, CancellationToken.None).ToBlockingEnumerable())
        {
            byte[] bytes = new byte[listed.Head.Length];
            store.ReadAsync(listed.Key, 0, bytes, null, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            _pristine[listed.Key] = bytes;
        }

        store.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Fresh();
        Console.WriteLine($"// {_dataset.ObjectCount} objects; {Index()} walks");
    }

    /// <summary>Copies the dataset afresh, its objects as written, its store slowed.</summary>
    [IterationSetup]
    public void Fresh()
    {
        _dataset?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _store?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _store = new MemoryObjectStore();
        foreach ((string key, byte[] bytes) in _pristine)
        {
            System.IO.Pipelines.PipeReader content = System.IO.Pipelines.PipeReader.Create(new System.Buffers.ReadOnlySequence<byte>(bytes));
            _store.PutAsync(key, content, bytes.Length, PutCondition.None, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }

        _dataset = VortexDataset.OpenAsync(_store).AsTask().GetAwaiter().GetResult();
        _store.Latency = TimeSpan.FromMilliseconds(5);
    }

    /// <summary>Disposes the dataset and its store.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _dataset.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _store.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>A Bloom filter on the service given to every object.</summary>
    [Benchmark]
    [InvocationCount(1)]
    public int Index() => _dataset.IndexAsync(IndexPolicy.None.Bloom(nameof(Call.Service), falsePositiveRate: 0.01))
        .AsTask().GetAwaiter().GetResult().Walks;

    private static async Task BuildAsync(MemoryObjectStore store)
    {
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Call.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout { SortKey = [LayoutValue.Column(nameof(Call.At))], CalmAfter = null },
        }).ConfigureAwait(false);
        Random random = new Random(59);
        long at = 0;
        for (int append = 0; append < Appends; append++)
        {
            Call[] calls = [.. Enumerable.Range(0, RowsPerAppend).Select(_ => new Call(
                Start.AddMilliseconds(at++ * 5), $"svc-{random.Next(40):D2}", random.NextInt64(100, 100_000)))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Call>(calls).ConfigureAwait(false);
            await dataset.AppendAsync(draft).ConfigureAwait(false);
        }
    }
}
