// What a delete by a filter pays an object it touches, its filter evaluated once to count the rows, again to
// mark them.
//
// 8 appends of 262,144 calls (20 services, a status of 500 in fifty), sorted by the service and the instant, compacted,
// in an in-memory store copied afresh before each delete. The delete: the calls of status 500, about 2 % of each object,
// scattered, which each object rewrites, its marks past their bound; or one service's, 5 %, a run of each object, which
// each takes as marks.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Dataset;
using Vorticity.IO;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A delete of 2 % of the rows, taken as marks.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditDeleteBenchmarks
{
    private const int Appends = 8;
    private const int RowsPerAppend = 262_144;

    private static readonly DateTime Start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    // A session of eight lanes, as a host that asks for its cores sets one; the default's is one.
    private static readonly VortexSession Lanes = VortexSession.Create(static options => options.MaxDegreeOfParallelism = 8);

    private readonly Dictionary<string, byte[]> _pristine = [];
    private MemoryObjectStore _store = null!;
    private VortexDataset _dataset = null!;

    /// <summary>A call: its instant, service and status.</summary>
    [VortexRecord]
    public partial record struct Call(DateTime At, string Service, short Status, long Bytes);

    /// <summary>Writes and compacts the dataset once, and keeps its objects.</summary>
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
        Console.WriteLine($"// {_dataset.ObjectCount} objects; {Delete()} rows deleted");
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

        _dataset = VortexDataset.OpenAsync(_store, new DatasetOptions { Session = Lanes }).AsTask().GetAwaiter().GetResult();
        _store.Latency = TimeSpan.FromMilliseconds(LatencyMs);
    }

    /// <summary>Disposes the dataset and its store.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _dataset.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _store.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>What the delete takes: the calls of status 500, scattered, which each object rewrites, or one service's, a run of each, which each object takes as marks.</summary>
    [Params("status", "service")]
    public string Taken { get; set; } = "status";

    /// <summary>What each request of the store waits, in milliseconds: none, or a remote store's.</summary>
    [Params(0, 5)]
    public int LatencyMs { get; set; }

    /// <summary>The calls taken deleted.</summary>
    [Benchmark]
    [InvocationCount(1)]
    public long Delete() => (Taken == "status"
        ? _dataset.DeleteAsync<Call>(c => c.Status == 500)
        : _dataset.DeleteAsync<Call>(c => c.Service == "svc-07")).AsTask().GetAwaiter().GetResult().Rows;

    private static async Task BuildAsync(MemoryObjectStore store)
    {
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Call.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                SortKey = [LayoutValue.Column(nameof(Call.Service)), LayoutValue.Column(nameof(Call.At))],
                MergeFanout = 8,
                CalmAfter = null,
            },
        }).ConfigureAwait(false);
        Random random = new Random(19);
        long at = 0;
        for (int append = 0; append < Appends; append++)
        {
            Call[] calls = [.. Enumerable.Range(0, RowsPerAppend).Select(_ => new Call(
                Start.AddMilliseconds(at++ * 3), $"svc-{random.Next(20):D2}", (short)(random.Next(50) == 0 ? 500 : 200), random.NextInt64(100, 100_000)))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Call>(calls).ConfigureAwait(false);
            await dataset.AppendAsync(draft).ConfigureAwait(false);
        }

        while (await dataset.CompactAsync().ConfigureAwait(false) is not null)
        {
        }
    }
}
