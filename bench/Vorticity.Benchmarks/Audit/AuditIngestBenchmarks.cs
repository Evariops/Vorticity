// Audit DS-50: what an append of records costs a dataset that sorts or partitions them.
//
// 1,048,576 records (an instant over 16 hours, a key, a service of 8, a value), their keys shuffled, appended as one
// object draft into an in-memory store: a dataset sorted by the key ("sorted"), and one partitioned by the hour of the
// instant and sorted by the key within ("partitioned"). The rows are copied into the draft's stores, routed to their
// partition, sorted, and written.
using System;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Dataset;

namespace Vorticity.Benchmarks.Audit;

/// <summary>An append of a million records to a sorted, or a partitioned and sorted, dataset.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditIngestBenchmarks
{
    private const int Rows = 1 << 20;

    private static readonly DateTime Start = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    private Event[] _rows = null!;

    /// <summary>The dataset's layout: sorted by the key, or partitioned by the hour and sorted.</summary>
    [Params("sorted", "partitioned")]
    public string Layout { get; set; } = "sorted";

    /// <summary>A record.</summary>
    [VortexRecord]
    public partial record struct Event(DateTime At, long Key, string Service, double Value);

    /// <summary>Makes the records.</summary>
    [GlobalSetup]
    public void Setup()
    {
        string[] services = ["auth", "billing", "catalog", "checkout", "search", "shipping", "users", "web"];
        Random random = new Random(5);
        _rows = new Event[Rows];
        for (int i = 0; i < Rows; i++)
        {
            _rows[i] = new Event(Start.AddMilliseconds(i * (16 * 3_600_000.0 / Rows)), i, services[random.Next(services.Length)], random.NextDouble());
        }

        // The keys shuffled, the instants left rising: the sort has work, the partitions come in runs.
        long[] keys = new long[Rows];
        for (int i = 0; i < Rows; i++)
        {
            keys[i] = i;
        }

        random.Shuffle(keys);
        for (int i = 0; i < Rows; i++)
        {
            _rows[i] = _rows[i] with { Key = keys[i] };
        }

        Console.WriteLine($"// {Layout}: {Append()} objects");
    }

    /// <summary>The records appended as one draft to a new dataset.</summary>
    [Benchmark]
    public int Append() => AppendAsync().GetAwaiter().GetResult();

    private async Task<int> AppendAsync()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        DatasetOptions options = Layout == "sorted"
            ? new DatasetOptions { Seed = 0xA0D1_7050_5EED, Layout = new DatasetLayout { SortKey = [LayoutValue.Column(nameof(Event.Key))] } }
            : new DatasetOptions
            {
                Seed = 0xA0D1_7050_5EED,
                Layout = new DatasetLayout
                {
                    Partition = LayoutValue.Column(nameof(Event.At)).Truncate(CalendarUnit.Hour),
                    SortKey = [LayoutValue.Column(nameof(Event.Key))],
                },
            };

        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Event.Schema, options).ConfigureAwait(false);
        await using ObjectDraft draft = dataset.StartObject();
        await draft.Writer.WriteAsync<Event>(_rows, CancellationToken.None).ConfigureAwait(false);
        await dataset.AppendAsync(draft).ConfigureAwait(false);
        int objects = 0;
        await foreach (PositionedObject _ in dataset.ScanBuilder().ObjectsAsync().ConfigureAwait(false))
        {
            objects++;
        }

        return objects;
    }
}
