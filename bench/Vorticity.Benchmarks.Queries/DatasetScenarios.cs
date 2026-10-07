using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Vorticity.Dataset;

// The bench reads datasets, whose API is experimental (VX0001): an assembly marked so itself takes it.
[assembly: Experimental("VXBENCH")]

namespace Vorticity.Benchmarks.Queries;

/// <summary>A query over a dataset of the readings, read against the same query over their file.</summary>
/// <param name="Name">The name the table prints.</param>
/// <param name="Query">The query, its answer a number the work cannot skip.</param>
/// <param name="ProbeEvery">The answers between two samples of the live memory.</param>
internal sealed record DatasetScenario(string Name, Func<VortexDataset, Run, Task<long>> Query, int ProbeEvery = 1);

/// <summary>
/// The queries of the datasets' stage (PLAN-QUERIES-STREAMING.md, 6e): the readings as one object,
/// as sixteen, and as sixteen with an eighth of every object deleted. The first batch of a scan is
/// to stay flat from one object to sixteen; a group by on sixteen at a degree of many lanes within
/// 15 % of the file's.
/// </summary>
internal static class DatasetScenarios
{
    internal static IEnumerable<(string Dataset, DatasetScenario Scenario)> All()
    {
        foreach ((string dataset, string label) in new[] { ("readings-1", "1 object"), ("readings-16", "16 objects"), ("readings-16-deleted", "16 objects, deletions") })
        {
            yield return (dataset, new DatasetScenario($"dataset group by city, count avg, {label}", GroupByCityAsync));
            yield return (dataset, new DatasetScenario($"dataset first batch, full scan, {label}", FirstBatchAsync));
        }

        // A million groups over sixteen objects, against `hc random 1e6 small, count sum` over their file.
        yield return ($"spread-random-{HighCardinality.SmallRows}-16", new DatasetScenario("dataset hc random 1e6 small, count sum, 16 objects", SpreadTotalsAsync, 16));
    }

    private static async Task<long> SpreadTotalsAsync(VortexDataset dataset, Run run)
    {
        long rows = 0;
        await foreach (Columns<KeyTotal> groups in run.Track(dataset.Scan<Spread>()
            .GroupBy(s => s.K6)
            .Select(g => (g.Key, g.Count(), g.Sum(s => s.Value))))
            .As<KeyTotal>())
        {
            run.Answer();
            rows += Scenarios.Sum(groups.Column<long>(1).Values);
        }

        return rows;
    }

    private static async Task<long> GroupByCityAsync(VortexDataset dataset, Run run)
    {
        long rows = 0;
        await foreach (Columns<CityStats> groups in run.Track(dataset.Scan<Reading>()
            .GroupBy(r => r.City)
            .Select(g => (g.Key, g.Count(), g.Average(r => r.Celsius))))
            .As<CityStats>())
        {
            run.Answer();
            rows += Scenarios.Sum(groups.Column<long>(1).Values);
        }

        return rows;
    }

    private static async Task<long> FirstBatchAsync(VortexDataset dataset, Run run)
    {
        await foreach (Columns<Reading> batch in dataset.Scan<Reading>())
        {
            run.Answer();
            return batch.RowCount;
        }

        return 0;
    }
}
