// What a merge of level 0 pays for a variant column: the first pass that counts its paths to choose what each
// object shreds, then the merge that reads the rows again.
//
// 8 appends of 65,536 events of a day (20 services, an instant, attributes of 10 keys of 100, texts and integers), sorted
// by the service and the instant, left in level 0 with their tables of contents, in an in-memory store copied afresh
// before each compaction, on one lane or eight; their instants follow one another from append to append, or interleave
// over the day, so that the merge takes a row at a time. The compaction: the one job that merges level 0 into level 1.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Dataset;
using Vorticity.IO;
using Vorticity.Types.Variants;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A merge of level 0's eight objects of events with attributes.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditVariantCompactionBenchmarks
{
    private const int Appends = 8;
    private const int RowsPerAppend = 65_536;

    private static readonly DateTime Start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly Dictionary<string, byte[]> _pristine = [];
    private VortexSession _session = null!;
    private MemoryObjectStore _store = null!;
    private VortexDataset _dataset = null!;

    /// <summary>The session's lanes: one, the default, or eight.</summary>
    [Params(1, 8)]
    public int Lanes { get; set; }

    /// <summary>Whether the appends' instants interleave over the day, their keys then merged a row at a time, or follow one another.</summary>
    [Params(false, true)]
    public bool Interleaved { get; set; }

    /// <summary>An event: its instant, service and attributes.</summary>
    [VortexRecord]
    public partial record struct Event(DateTime At, string Service, Variant Attributes);

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

    /// <summary>Disposes the dataset and its store.</summary>
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
            Partition = LayoutValue.Column(nameof(Event.At)).Truncate(CalendarUnit.Day),
            SortKey = [LayoutValue.Column(nameof(Event.Service)), LayoutValue.Column(nameof(Event.At))],
            MergeFanout = 8,
            CalmAfter = null,
            ObjectBytes = 64 << 20,
        },
        ContentsObjectBytes = 0,
    };

    private async Task BuildAsync(MemoryObjectStore store)
    {
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Event.Schema, Options(_session)).ConfigureAwait(false);
        Random random = new Random(41);
        long at = 0;
        for (int append = 0; append < Appends; append++)
        {
            Event[] events = new Event[RowsPerAppend];
            for (int i = 0; i < events.Length; i++)
            {
                Dictionary<string, Variant> fields = [];
                while (fields.Count < 10)
                {
                    int key = random.Next(100);
                    fields[$"k{key:D3}"] = key % 2 == 0 ? Variant.From($"value-{random.Next(500)}") : Variant.From(random.Next(100_000));
                }

                events[i] = new Event(Interleaved ? Start.AddMilliseconds(random.Next(86_400_000)) : Start.AddMilliseconds(at++ * 100), $"svc-{random.Next(20):D2}", Variant.Object([.. fields.Select(static field => (field.Key, field.Value))]));
            }

            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Event>(events).ConfigureAwait(false);
            await dataset.AppendAsync(draft).ConfigureAwait(false);
        }
    }
}
