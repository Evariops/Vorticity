// An approximate distinct count of a key deeper than the question's merges the distinct sketches a table of sketches
// holds of it, a row a group, where states keyed by it come a row for each of its values in a group.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
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
}
