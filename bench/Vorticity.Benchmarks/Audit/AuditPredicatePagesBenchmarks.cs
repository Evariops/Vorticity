// Audit DS-13: what a selective filter asked again costs, with its pages kept in memory, in the session's disk cache
// alone, or nowhere.
//
// 100,000 events (an instant, a code of twenty thousand values, a body of 300 bytes) in 4 objects, blocks of 512 rows; a
// rare code held by 30 rows scattered over them. The question counts the rows of the rare code and their distinct bodies,
// the code asked as a range, which no sheet estimates and no zone map rules out: the first time every body is read, after
// that the granules its pages say held a row. The handle keeps the pages within its budget (0: none), and the session's
// synopsis cache, when it has one, keeps them on disk.
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Dataset;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A selective filter asked again, against where its pages are kept.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditPredicatePagesBenchmarks
{
    private const int Rows = 100_000;
    private const int Rare = 12_345;

    private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The bytes the handle keeps of the pages: none, or the default.</summary>
    [Params(0L, 16L << 20)]
    public long Budget { get; set; }

    /// <summary>Whether the session has a synopsis cache on disk.</summary>
    [Params(false, true)]
    public bool Disk { get; set; }

    private MemoryObjectStore _store = null!;
    private string _directory = null!;
    private VortexSession _session = null!;
    private VortexDataset _dataset = null!;

    /// <summary>An event.</summary>
    [VortexRecord]
    public partial record struct Event(DateTimeOffset At, int Code, string Body);

    /// <summary>Writes the dataset, opens a handle, and asks the question once.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(0xF4);
        System.Collections.Generic.HashSet<int> rare = [];
        while (rare.Count < 30)
        {
            rare.Add(random.Next(Rows));
        }

        Event[] rows = [.. Enumerable.Range(0, Rows).Select(i =>
        {
            byte[] body = new byte[225];
            random.NextBytes(body);
            int code = random.Next(20_000);
            return new Event(Start.AddSeconds(i), rare.Contains(i) ? Rare : code == Rare ? Rare + 1 : code, Convert.ToBase64String(body));
        })];

        _store = new MemoryObjectStore();
        DatasetOptions options = new DatasetOptions
        {
            Layout = new DatasetLayout { SortKey = [LayoutValue.Column(nameof(Event.At))], ObjectBytes = 32 << 20 },
            ContentsObjectBytes = 1,
            ChunkBytes = 64 << 10,
            Write = new VortexWriteOptions { BlockRows = 512, ChunkTargetBytes = 64 << 10 },
        };
        WriteAsync(options, rows).GetAwaiter().GetResult();

        _directory = Directory.CreateTempSubdirectory("vorticity-audit-pages-").FullName;
        _session = VortexSession.Create(o =>
        {
            if (Disk)
            {
                o.SynopsisCache = new SynopsisCache(_directory, 64L << 20);
            }
        });
        _dataset = VortexDataset.OpenAsync(_store, options with { Session = _session, PredicatePageBytes = Budget }).AsTask().GetAwaiter().GetResult();
        Console.WriteLine($"// budget {Budget}, disk {Disk}: {Ask()} rows, then {Ask()}");
    }

    /// <summary>Closes the handle, the session and the store, and deletes the cache's directory.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _dataset.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _store.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Directory.Delete(_directory, recursive: true);
    }

    /// <summary>The rows of the rare code and their distinct bodies, asked again.</summary>
    [Benchmark]
    public long Ask() => AskAsync().GetAwaiter().GetResult();

    private async Task<long> AskAsync()
    {
        long read = 0;
        Scan scan = _dataset.Scan().With(new ScanOptions { UseSynopses = true, UseStatistics = true })
            .Where(VortexExpr.Parse($"Code >= {Rare} and Code <= {Rare}"));
        await foreach (RecordBatch batch in scan.Aggregate(["count()", "count_distinct(Body)"]).ToBatchesAsync(CancellationToken.None).ConfigureAwait(false))
        {
            using (batch)
            {
                read += Vorticity.Bench.Scenarios.Consume.Batch(batch);
            }
        }

        return read;
    }

    private async Task WriteAsync(DatasetOptions options, Event[] rows)
    {
        await using VortexDataset writing = await VortexDataset.CreateAsync(_store, Event.Schema, options).ConfigureAwait(false);
        for (int from = 0; from < rows.Length; from += 25_000)
        {
            await using ObjectDraft draft = writing.StartObject();
            await draft.Writer.WriteAsync<Event>(rows.AsSpan(from, 25_000).ToArray(), CancellationToken.None).ConfigureAwait(false);
            await writing.AppendAsync(draft).ConfigureAwait(false);
        }

        while (await writing.CompactAsync(new CompactionOptions { LevelZeroCeiling = 1 }).ConfigureAwait(false) is not null)
        {
        }
    }
}
