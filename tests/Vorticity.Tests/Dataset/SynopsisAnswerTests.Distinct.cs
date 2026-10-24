// An approximate distinct count of a key deeper than the question's merges the distinct sketches a table of sketches
// holds of it, a row a group, where states keyed by it come a row for each of its values in a group.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class SynopsisAnswerTests
{
    /// <summary>
    /// The services per method, approximately, read from a table of sketches, in fewer rows of states than the exact count,
    /// whose states are keyed by the service; both give the bits of their rows, at one lane and four.
    /// </summary>
    [Fact]
    public async Task AnApproximateDistinctCountMergesTheSketchesOfItsTables()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await FilledAsync(store);
        Func<Scan<Request>, Vorticity.Aggregation> sketched = scan => scan.GroupBy(r => r.Method).Select(g => (g.Key, g.ApproximateCountDistinct(r => r.Service)));
        Func<Scan<Request>, Vorticity.Aggregation> keyed = scan => scan.GroupBy(r => r.Method).Select(g => (g.Key, g.CountDistinct(r => r.Service)));
        (List<string> on, ScanMetrics metrics) = await GroupsAsync(sketched(dataset.Scan<Request>().With(new ScanOptions { UseSynopses = true })));
        (List<string> lanes, _) = await GroupsAsync(sketched(dataset.Scan<Request>().With(new ScanOptions { UseSynopses = true, DegreeOfParallelism = 4 })));
        (List<string> off, _) = await GroupsAsync(sketched(dataset.Scan<Request>().With(new ScanOptions { UseSynopses = false })));
        (List<string> exact, ScanMetrics states) = await GroupsAsync(keyed(dataset.Scan<Request>().With(new ScanOptions { UseSynopses = true })));
        (List<string> rows, _) = await GroupsAsync(keyed(dataset.Scan<Request>().With(new ScanOptions { UseSynopses = false })));
        ScanPlan plan = await sketched(dataset.Scan<Request>().With(new ScanOptions { UseSynopses = true })).ExplainAsync(Ct);
        await foreach (PositionedObject held in dataset.Snapshot.WalkAsync(null, 0, long.MaxValue, null, Ct))
        {
            foreach ((Vorticity.Indexes.SynopsisTableInfo table, _) in held.Entry.Contents?.Tables ?? [])
            {
                Console.Out.Write($"TABLE {table.Kind} by {table.Bucket} keys [{string.Join(",", table.Keys)}] distinct [{string.Join(",", table.Distinct)}] {table.Rows} rows\n");
            }
        }
        Console.Out.Write(
            $"DISTINCT SKETCHES: {metrics.SynopsisRows} table rows merged against {states.SynopsisRows} keyed by the service: " +
            $"{string.Join(" | ", plan.States.Select(step => $"{step.Read} {step.Detail} x{step.Count}"))}\n");
        Assert.Equal(off, on);
        Assert.Equal(off, lanes);
        Assert.Equal(rows, exact);
        Assert.Contains(plan.States, step => step.Detail.StartsWith("Sketches by", StringComparison.Ordinal));
        Assert.True(metrics.SynopsisRows * 4 < states.SynopsisRows, $"{metrics.SynopsisRows} table rows merged, {states.SynopsisRows} keyed by the service");
    }

    /// <summary>
    /// The chunks of a table of sketches lay their distinct sketches in a region of their own, past their rows': a question of
    /// percentiles per method reads none of those regions, an approximate count of services per method reads them, and both
    /// answer the bits their rows give.
    /// </summary>
    [Fact]
    public async Task APercentileReadsNoDistinctSketchAndADistinctCountReadsThem()
    {
        string directory = CacheDirectory();
        try
        {
            await using MemoryObjectStore store = new MemoryObjectStore();
            List<(UInt128 Uid, ulong Offset)> asked = [];
            SynopsisCache cache = new SynopsisCache(directory, 64 << 20);
            cache.Asked = (uid, region) =>
            {
                lock (asked)
                {
                    asked.Add((uid, region.Offset));
                }
            };

            await using VortexSession session = VortexSession.Create(options => options.SynopsisCache = cache);
            await using VortexDataset dataset = await FilledAsync(store, session: session);

            // Every chunk of a table that holds distinct sketches lays them apart, and only those.
            HashSet<(UInt128, ulong)> sketches = [];
            await foreach (PositionedObject held in dataset.Snapshot.WalkAsync(null, 0, long.MaxValue, null, Ct))
            {
                if (held.Entry.Contents is not { } contents)
                {
                    continue;
                }

                await dataset.LoadSynopsisListsAsync([(held.Entry, contents, null)], Ct);
                foreach ((SynopsisTableInfo table, IReadOnlyList<TableChunk> chunks) in contents.Tables)
                {
                    foreach (TableChunk chunk in chunks)
                    {
                        Assert.Equal(table.Distinct.Length > 0, chunk.Distinct is not null);
                        if (chunk.Distinct is { } distinct)
                        {
                            Assert.True(distinct.Offset >= chunk.Region.Offset + chunk.Region.Length);
                            sketches.Add((held.Entry.Uid, distinct.Offset));
                        }
                    }
                }
            }

            Assert.NotEmpty(sketches);
            foreach ((string name, Func<Scan<Request>, Vorticity.Aggregation> ask, bool reads) in new (string, Func<Scan<Request>, Vorticity.Aggregation>, bool)[]
            {
                ("percentiles", scan => scan.GroupBy(r => r.Method).Select(g => (g.Key, g.Count(), g.Quantile(r => r.Duration, 0.99))), false),
                ("distinct services", scan => scan.GroupBy(r => r.Method).Select(g => (g.Key, g.ApproximateCountDistinct(r => r.Service))), true),
            })
            {
                asked.Clear();
                (List<string> on, ScanMetrics metrics) = await GroupsAsync(ask(dataset.Scan<Request>().With(new ScanOptions { UseSynopses = true })));
                int sketched = asked.Count(sketches.Contains);
                Console.Out.Write($"DISTINCT APART {name}: {asked.Count} regions asked, {sketched} of distinct sketches, {metrics.SynopsisRows} table rows.\n");
                (List<string> off, _) = await GroupsAsync(ask(dataset.Scan<Request>().With(new ScanOptions { UseSynopses = false })));
                Assert.Equal(off, on);
                Assert.True(metrics.SynopsisRows > 0);
                Assert.Equal(reads, sketched > 0);
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>
    /// The hosts per method but one request's, approximately, on hours whose budget keeps the hosts' distinct sketches but
    /// no table keyed by the host. No sketch can take the request's rows out, so an object whose identifier's index finds
    /// no row of the request answers from its sketches as if the filter did not name it, and the object that holds the
    /// request reads its rows. A request every object holds sends every object to its rows; one no object holds leaves
    /// every table to answer. Every answer gives the bits of its rows, at one lane and four.
    /// </summary>
    [Fact]
    public async Task ADistinctCountThatLeavesARequestOutTakesTheSketchesOfTheObjectsWithoutIt()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await HostedAsync(store);
        Dictionary<string, ScanMetrics> asked = [];
        foreach (string left in new[] { "r3-22", "nowhere", "hot" })
        {
            Func<Scan<Hosted>, Vorticity.Aggregation> sketched = scan => scan.Where(r => r.RequestId != left)
                .GroupBy(r => r.Method).Select(g => (g.Key, g.ApproximateCountDistinct(r => r.Host)));
            (List<string> on, ScanMetrics metrics) = await GroupsAsync(sketched(dataset.Scan<Hosted>().With(new ScanOptions { UseSynopses = true })));
            (List<string> lanes, _) = await GroupsAsync(sketched(dataset.Scan<Hosted>().With(new ScanOptions { UseSynopses = true, DegreeOfParallelism = 4 })));
            (List<string> off, ScanMetrics rows) = await GroupsAsync(sketched(dataset.Scan<Hosted>().With(new ScanOptions { UseSynopses = false })));
            ScanPlan plan = await sketched(dataset.Scan<Hosted>().With(new ScanOptions { UseSynopses = true })).ExplainAsync(Ct);
            Console.Out.Write(
                $"ABSENT {left}: {metrics.SynopsisRows} table rows and {metrics.Rows} rows, against {rows.Rows}: " +
                $"{string.Join(" | ", plan.States.Select(step => $"{step.Read} {step.Detail} x{step.Count}"))}\n");
            Assert.Equal(off, on);
            Assert.Equal(off, lanes);
            Assert.Contains(plan.States, step => step.Read == "absent");
            Assert.Contains(plan.States, step => step.Detail.StartsWith("Sketches by", StringComparison.Ordinal));
            asked[left] = metrics;
        }

        Assert.Equal(0, asked["hot"].SynopsisRows);
        Assert.True(asked["r3-22"].SynopsisRows > 0, "no sketch merged");
        Assert.True(asked["r3-22"].Rows < asked["hot"].Rows, $"{asked["r3-22"].Rows} rows read against {asked["hot"].Rows}");
        Assert.True(asked["nowhere"].Rows < asked["r3-22"].Rows, $"{asked["nowhere"].Rows} rows read against {asked["r3-22"].Rows}");
    }

    /// <summary>
    /// The requests of <see cref="StampedAsync"/> with a host of 400, each id its own or one in thirty-seven "hot", under a
    /// budget of the data's bytes: the hosts' distinct sketches kept, no table keyed by the host.
    /// </summary>
    private static async Task<VortexDataset> HostedAsync(Vorticity.IO.IObjectStore store)
    {
        VortexDataset dataset = await VortexDataset.CreateAsync(store, Hosted.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Hosted.At)],
            Identifiers = [nameof(Hosted.RequestId)],
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour, SliceTargetRows = 100 },
            Write = new VortexWriteOptions { Indexes = IndexPolicy.None.WithBudgetPerMille(1_000) },
        }, Ct);
        Random random = new Random(53);
        for (int part = 0; part < 16; part++)
        {
            Hosted[] rows = [.. Enumerable.Range(0, 400).Select(i => new Hosted(
                Noon.AddSeconds((part * 400 + i) * 1.25),
                Services[random.Next(Services.Length)],
                Methods[random.Next(Methods.Length)],
                random.Next(4) switch { 0 => 200L, 1 => 201L, 2 => 404L, _ => 500L },
                random.NextDouble() * 1_000,
                random.NextInt64(1, 1L << 40),
                $"host-{random.Next(400)}",
                i % 37 == 0 ? "hot" : $"r{part}-{i}"))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Hosted>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }, Ct) is not null)
        {
        }

        return dataset;
    }

    [VortexRecord]
    public partial record struct Hosted(DateTimeOffset At, string Service, string Method, long Status, double Duration, long Bytes, string Host, string RequestId);
}
