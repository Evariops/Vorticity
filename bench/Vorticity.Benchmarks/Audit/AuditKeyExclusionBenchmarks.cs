// Audit DS-15: what a question pays to search each object's sparse index when its filter bounds no leading component of
// the key, the boxes of granules judged by an exclusion search.
//
// 8 appends of 524,288 calls (8 services, 200 pods, a status), sorted by (Service, Pod, At), the service and the pod
// indexed, not compacted: 8 objects of 64 granules, 8 a service. The question: the calls of one pod, counted, which bounds the
// second component alone, so that each object's index is searched by boxes, a service's granules apart from the next's.
using System;
using System.Linq;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Dataset;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A count of one pod's calls, the key's second component.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditKeyExclusionBenchmarks
{
    private const int Appends = 8;
    private const int RowsPerAppend = 524_288;

    private static readonly DateTime Start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private MemoryObjectStore _store = null!;
    private VortexDataset _dataset = null!;

    /// <summary>A call: its instant, service, pod and status.</summary>
    [VortexRecord]
    public partial record struct Call(DateTime At, string Service, string Pod, short Status);

    /// <summary>Writes the dataset.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _store = new MemoryObjectStore();
        _dataset = BuildAsync().GetAwaiter().GetResult();
        _first = _dataset.ScanBuilder().ObjectsAsync().ToBlockingEnumerable().First();
        _keys = _dataset.KeyIndexAsync(_first.Entry, _first.Entry.Contents!, default).AsTask().GetAwaiter().GetResult()!;
        Console.WriteLine($"// {_keys.Granules} granules an object, {SearchAnIndex()} ranges kept");
        Console.WriteLine($"// {_dataset.ObjectCount} objects; {Pod()} calls of the pod");
    }

    /// <summary>Disposes the dataset and its store.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _keys.Dispose();
        _dataset.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _store.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>The calls of one pod, counted.</summary>
    [Benchmark]
    public long Pod() => _dataset.Scan<Call>().Where(c => c.Pod == "pod-107").CountAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>The first object's index searched for the pod's granules, by a pruning made for it as a question makes one.</summary>
    [Benchmark]
    public int SearchAnIndex() => KeyPruning.Of(_dataset.Snapshot.Schema, _filter)!.Granules(_keys, []).Count;

    /// <summary>The first object's index searched again by one pruning, as a question's search of its next objects is.</summary>
    [Benchmark]
    public int SearchAgain() => (_pruning ??= KeyPruning.Of(_dataset.Snapshot.Schema, _filter)!).Granules(_keys, []).Count;

    private KeyPruning? _pruning;

    /// <summary>The first object's index read, from the session's caches, and given back, as a question's search does.</summary>
    [Benchmark]
    public int ReadAnIndex()
    {
        using Vorticity.Indexes.GranuleKeys keys = _dataset.KeyIndexAsync(_first.Entry, _first.Entry.Contents!, default).AsTask().GetAwaiter().GetResult()!;
        return keys.Granules;
    }

    private readonly VortexExpr _filter =
        Vorticity.Expressions.Expr.Eq(Vorticity.Expressions.Expr.Field(nameof(Call.Pod)), Vorticity.Expressions.Expr.Literal(Vorticity.Expressions.FilterLiteral.From("pod-107")));

    private PositionedObject _first;
    private Vorticity.Indexes.GranuleKeys _keys = null!;

    private async Task<VortexDataset> BuildAsync()
    {
        VortexDataset dataset = await VortexDataset.CreateAsync(_store, Call.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                SortKey = [LayoutValue.Column(nameof(Call.Service)), LayoutValue.Column(nameof(Call.Pod)), LayoutValue.Column(nameof(Call.At))],
                IndexedPrefix = 2,
                ObjectBytes = 1L << 30,
                CalmAfter = null,
            },
            ContentsObjectBytes = 0,
            SheetObjectBytes = 0,
        }).ConfigureAwait(false);
        Random random = new Random(61);
        long at = 0;
        for (int append = 0; append < Appends; append++)
        {
            Call[] calls = [.. Enumerable.Range(0, RowsPerAppend).Select(_ => new Call(
                Start.AddMilliseconds(at++ * 7), $"svc-{random.Next(8)}", $"pod-{random.Next(200):D3}", (short)(random.Next(20) == 0 ? 500 : 200)))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Call>(calls).ConfigureAwait(false);
            await dataset.AppendAsync(draft).ConfigureAwait(false);
        }

        return dataset;
    }
}
