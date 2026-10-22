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
}
