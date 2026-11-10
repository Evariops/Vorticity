// Audit DS-14: what a lookup of identifiers pays each consolidated partition whose index it reads, the index warm.
//
// Thirty days of calls (65,536 a day, four appends a day), partitioned by the day, sorted by the service, cut into objects
// of 128 KiB (four a day) or 8 KiB (twenty), each call's random identifier declared, compacted and consolidated: an index
// a day, of three pages of 64 KiB. The question: the calls of one identifier, or of four, over the thirty days: no day's
// summaries refute a random identifier, so each day's index is read for the values sought, a page a value, and the
// objects whose blocks hold none passed over.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Dataset;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A lookup of identifiers over a month of consolidated days.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditPartitionLookupBenchmarks
{
    private const int Days = 30;
    private const int AppendsADay = 4;
    private const int RowsAnAppend = 16_384;

    private static readonly DateTime Start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private MemoryObjectStore _store = null!;
    private VortexDataset _dataset = null!;
    private long[] _sought = [];

    /// <summary>The identifiers sought.</summary>
    [Params(1, 4)]
    public int Values { get; set; }

    /// <summary>The bytes past which an object is cut, in KiB: four objects a day at 128, twenty at 8.</summary>
    [Params(128, 8)]
    public int ObjectKiB { get; set; }

    /// <summary>A call: its service, instant, identifier and value.</summary>
    [VortexRecord]
    public partial record struct Row(string Service, DateTime At, long Id, double Value);

    /// <summary>Writes, compacts and consolidates the dataset.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _store = new MemoryObjectStore();
        List<long> daily = [];
        _dataset = BuildAsync(daily).GetAwaiter().GetResult();
        _sought = [.. new[] { 15, 3, 22, 29 }.Take(Values).Select(day => daily[day])];
        Console.WriteLine($"// {_dataset.ObjectCount} objects; {StatsAsync().GetAwaiter().GetResult()}; {Lookup()} read");
    }

    /// <summary>Disposes the dataset and its store.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _dataset.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _store.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>The calls of the identifiers sought, read.</summary>
    [Benchmark]
    public long Lookup() => LookupAsync().GetAwaiter().GetResult();

    private async Task<long> LookupAsync()
    {
        long read = 0;
        long[] sought = _sought;
        await foreach (Columns<Row> batch in (sought.Length == 1
            ? _dataset.Scan<Row>().Where(r => r.Id == sought[0])
            : _dataset.Scan<Row>().Where(r => r.Id.In(sought))))
        {
            read += Vorticity.Bench.Scenarios.Consume.Typed(batch);
        }

        return read;
    }

    /// <summary>The postings of 16 hashes found in the first page of the first day's index, the page in hand.</summary>
    [Benchmark]
    public int FindInAPage()
    {
        int found = 0;
        foreach (ulong hash in _hashes)
        {
            _found.Clear();
            _postings.Find(_page, _at, hash, _found);
            found += _found.Count;
        }

        return found;
    }

    /// <summary>The same postings, each lookup begun at the stride its hash lies in, as a page the cache holds is searched.</summary>
    [Benchmark]
    public int FindInAPageResumed()
    {
        int found = 0;
        foreach (ulong hash in _hashes)
        {
            _found.Clear();
            _postings.Find(_page, _at, hash, _found, _restarts);
            found += _found.Count;
        }

        return found;
    }

    /// <summary>The checksum of the first page of the first day's index, as each load of it checks it.</summary>
    [Benchmark]
    public ulong HashAPage() => System.IO.Hashing.XxHash3.HashToUInt64(_page);

    private readonly List<(int Rank, uint Block)> _found = [];
    private ulong[] _hashes = [];
    private byte[] _page = [];
    private ulong[] _restarts = [];
    private PartitionIndex.Page _at;
    private PartitionIndex.Entry _postings = null!;

    // The first day's index: its bytes, the objects it covers, its postings and its pages; its first page kept, and 16
    // hashes that fall in it.
    private async Task<string> StatsAsync()
    {
        DatasetSnapshot version = _dataset.Snapshot;
        await foreach (TreeEntry tree in version.Levels.PartitionIndexes.EnumerateAsync(version.Pages, default))
        {
            PartitionIndexEntry entry = PartitionIndexEntry.FromBytes(tree.Key.Span, tree.Value.Span);
            await using Vorticity.IO.ISegmentReader reader = await SealedObjectStore.OpenDataAsync(_dataset.Store, entry.ObjectKey, entry.Uid, entry.Bytes, default);
            PartitionIndex index = await PartitionPostings.ReadAsync(reader, default);
            _postings = index.Of(nameof(Row.Id), PartitionIndex.EntryKind.Postings)!;
            _at = _postings.Pages[0];
            using (Vorticity.Buffers.SegmentOwner page = await reader.ReadAsync(new Vorticity.Serialization.Schemas.SegmentSpec((ulong)_at.Offset, (uint)_at.Length, 0, 0, 0), default))
            {
                _page = page.Buffer.Span.ToArray();
                _restarts = _postings.Restarts(_page, _at);
            }

            Random random = new Random(5);
            List<ulong> hashes = [];
            while (hashes.Count < 16)
            {
                ulong hash = (ulong)random.NextInt64() << 1;
                if (_postings.PageOf(hash) == 0)
                {
                    hashes.Add(hash);
                }
            }

            _hashes = [.. hashes];
            return $"a day's index {entry.Bytes} bytes, {index.Objects.Count} objects, {_postings.Postings} postings, pages of {string.Join(", ", _postings.Pages.Select(p => p.Length))} bytes";
        }

        return "no index";
    }

    private async Task<VortexDataset> BuildAsync(List<long> daily)
    {
        Random random = new Random(71);
        VortexDataset dataset = await VortexDataset.CreateAsync(_store, Row.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(Row.At)).Truncate(CalendarUnit.Day),
                SortKey = [LayoutValue.Column(nameof(Row.Service))],
                ObjectBytes = ObjectKiB << 10,
                MergeFanout = 4,
                CalmAfter = null,
            },
            ContentsObjectBytes = 0,
            Identifiers = [nameof(Row.Id)],
        }).ConfigureAwait(false);
        Row[] rows = new Row[RowsAnAppend];
        for (int day = 0; day < Days; day++)
        {
            for (int a = 0; a < AppendsADay; a++)
            {
                for (int i = 0; i < rows.Length; i++)
                {
                    rows[i] = new Row($"svc-{random.Next(0, 20):D2}", Start.AddDays(day).AddTicks((long)(random.NextDouble() * TimeSpan.TicksPerDay) / 10 * 10), random.NextInt64(), random.NextDouble());
                }

                if (a == 1)
                {
                    daily.Add(rows[100].Id);
                }

                await using ObjectDraft draft = dataset.StartObject();
                await draft.Writer.WriteAsync<Row>(rows).ConfigureAwait(false);
                await dataset.AppendAsync(draft).ConfigureAwait(false);
            }

            while (await dataset.CompactAsync().ConfigureAwait(false) is not null)
            {
            }
        }

        await dataset.ConsolidateAsync().ConfigureAwait(false);
        return dataset;
    }
}
