// Audit DS-62: what a merge of level 0 into the level above pays, on one lane or on a session's eight.
//
// 16 appends of 131,072 calls (20 services, an instant, bytes), sorted by the service and the instant, left in level 0, in
// an in-memory store copied afresh before each compaction. The compaction: the one job that merges level 0 into level 1.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Dataset;
using Vorticity.IO;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A merge of level 0's sixteen objects.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditCompactionBenchmarks
{
    private const int Appends = 16;
    private const int RowsPerAppend = 131_072;

    private static readonly DateTime Start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly Dictionary<string, byte[]> _pristine = [];
    private VortexSession _session = null!;
    private MemoryObjectStore _store = null!;
    private VortexDataset _dataset = null!;

    /// <summary>The session's lanes: one, the default, or eight, as a host that asks for its cores sets.</summary>
    [Params(1, 8)]
    public int Lanes { get; set; }

    /// <summary>A call: its instant, service and bytes.</summary>
    [VortexRecord]
    public partial record struct Call(DateTime At, string Service, long Bytes);

    /// <summary>Writes the dataset once, its objects in level 0, and keeps them.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _session = VortexSession.Create(options => options.MaxDegreeOfParallelism = Lanes);
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
        Console.WriteLine($"// {_dataset.ObjectCount} objects; {Compact()} rows merged");
    }

    /// <summary>Copies the dataset afresh, its objects as written.</summary>
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

        _dataset = VortexDataset.OpenAsync(_store, Options(_session)).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Disposes the dataset, its store and the session.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _dataset.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _store.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _session.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Level 0 merged into level 1.</summary>
    [Benchmark]
    [InvocationCount(1)]
    public long Compact() => _dataset.CompactAsync().AsTask().GetAwaiter().GetResult()?.Rows ?? 0;

    private static DatasetOptions Options(VortexSession session) => new DatasetOptions
    {
        Session = session,
        Layout = new DatasetLayout
        {
            SortKey = [LayoutValue.Column(nameof(Call.Service)), LayoutValue.Column(nameof(Call.At))],
            MergeFanout = 32,
            CalmAfter = null,
            ObjectBytes = 4 << 20,
        },
    };

    private async Task BuildAsync(MemoryObjectStore store)
    {
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Call.Schema, Options(_session)).ConfigureAwait(false);
        Random random = new Random(37);
        long at = 0;
        for (int append = 0; append < Appends; append++)
        {
            Call[] calls = [.. Enumerable.Range(0, RowsPerAppend).Select(_ => new Call(
                Start.AddMilliseconds(at++ * 3), $"svc-{random.Next(20):D2}", random.NextInt64(100, 100_000)))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Call>(calls).ConfigureAwait(false);
            await dataset.AppendAsync(draft).ConfigureAwait(false);
        }
    }
}
