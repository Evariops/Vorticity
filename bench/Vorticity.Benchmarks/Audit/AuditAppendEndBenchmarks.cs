// Audit DS-51: what finding where an append's rows begin costs a dataset partitioned with no key, against the append.
//
// A dataset partitioned by a day with no sort key holds 2,048 objects, a day each, of 64 rows, in an in-memory store.
// An append to it first finds the end of every row's position: the greatest of every object's place and rows, by a walk
// of every entry (VortexDataset.EndAsync). Timed alone, and an append of 64 rows to a new day.
using System;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Dataset;

namespace Vorticity.Benchmarks.Audit;

/// <summary>The end of a keyless partitioned dataset's positions, against an append to it.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditAppendEndBenchmarks
{
    private const int Days = 2_048;
    private const int RowsPerDay = 64;

    private MemoryObjectStore _store = null!;
    private VortexDataset _dataset = null!;
    private Logged[] _rows = null!;
    private int _day = Days;

    /// <summary>A record of a day.</summary>
    [VortexRecord]
    public partial record struct Logged(int Day, long Sequence, double Value);

    /// <summary>Writes the dataset, a draft a day.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _store = new MemoryObjectStore();
        _dataset = VortexDataset.CreateAsync(_store, Logged.Schema, new DatasetOptions
        {
            Seed = 0xA0D1_7051_5EED,
            Layout = new DatasetLayout { Partition = LayoutValue.Column(nameof(Logged.Day)) },
        }).AsTask().GetAwaiter().GetResult();
        _rows = new Logged[RowsPerDay];
        for (int day = 0; day < Days; day++)
        {
            AppendDayAsync(day).GetAwaiter().GetResult();
        }

        Console.WriteLine($"// end {End()}");
    }

    /// <summary>Disposes the dataset and its store.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _dataset.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _store.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>The end of the dataset's positions.</summary>
    [Benchmark]
    public long End() => _dataset.EndAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();

    /// <summary>An append of 64 rows to a new day.</summary>
    [Benchmark]
    public ulong Append() => AppendDayAsync(_day++).GetAwaiter().GetResult();

    private async Task<ulong> AppendDayAsync(int day)
    {
        for (int i = 0; i < _rows.Length; i++)
        {
            _rows[i] = new Logged(day, ((long)day * RowsPerDay) + i, i * 0.5);
        }

        await using ObjectDraft draft = _dataset.StartObject();
        await draft.Writer.WriteAsync<Logged>(_rows, CancellationToken.None).ConfigureAwait(false);
        return await _dataset.AppendAsync(draft).ConfigureAwait(false);
    }
}
