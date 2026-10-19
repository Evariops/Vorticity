// Aggregates on the tool path: a group by over columns named as text gives the answers the typed path gives, from
// the same plan, the synopses included.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Tests.Writing;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class SynopsisAnswerTests
{
    /// <summary>
    /// Questions asked by column names known only at run time give the bits the typed questions give, and read the
    /// synopses as they do: a count and a sum by service under a status, the extremes and mean by method over a
    /// window, the aggregates of every row.
    /// </summary>
    [Fact]
    public async Task TheToolPathAnswersAsTheTypedOne()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await FilledAsync(store);
        DateTimeOffset from = Noon.AddHours(1);
        DateTimeOffset to = Noon.AddHours(2);
        long low = TimeSlicer.InstantOf(from, TimeUnit.Microseconds);
        long high = TimeSlicer.InstantOf(to, TimeUnit.Microseconds);
        (string Name, Func<Vorticity.Scan> Tool, Func<Vorticity.Aggregation> Typed)[] questions =
        [
            ("count and sum by service under a status",
                () => dataset.Scan().Where(VortexExpr.Parse("Status = 200")).GroupBy("Service").Select("Service", "count()", "sum(Bytes)"),
                () => dataset.Scan<Request>().Where(r => r.Status == 200).GroupBy(r => r.Service).Select(g => (g.Key, g.Count(), g.Sum(r => r.Bytes)))),
            ("extremes and mean by method over an hour",
                () => dataset.Scan().Where(VortexExpr.Parse($"At >= {low} and At < {high}")).GroupBy("Method").Select("Method", "min(Bytes)", "max(Duration)", "avg(Duration)", "count()"),
                () => dataset.Scan<Request>().Where(r => r.At >= from && r.At < to).GroupBy(r => r.Method)
                    .Select(g => (g.Key, g.Min(r => r.Bytes), g.Max(r => r.Duration), g.Average(r => r.Duration), g.Count()))),
            ("two keys",
                () => dataset.Scan().GroupBy("Service", "Method").Select("Service", "Method", "count()", "sum(Duration)"),
                () => dataset.Scan<Request>().GroupBy(r => (r.Service, r.Method)).Select(g => (g.Key.Service, g.Key.Method, g.Count(), g.Sum(r => r.Duration)))),
        ];

        foreach ((string name, Func<Vorticity.Scan> tool, Func<Vorticity.Aggregation> typed) in questions)
        {
            Vorticity.Scan result = tool();
            List<string> rows = [];
            await foreach (RecordBatch batch in result.ToBatchesAsync(Ct))
            {
                using (batch)
                {
                    Values.DescribeRows(batch, rows);
                }
            }

            rows.Sort(StringComparer.Ordinal);
            (List<string> expected, ScanMetrics typedMetrics) = await GroupsAsync(typed());
            Console.Out.Write($"TOOL {name}: {result.Metrics.SynopsisRows} table rows, typed {typedMetrics.SynopsisRows}.\n");
            Assert.Equal(expected, rows);
            Assert.Equal(typedMetrics.SynopsisRows, result.Metrics.SynopsisRows);
            Assert.True(result.Metrics.SynopsisRows > 0, $"'{name}' folded no table");
        }

        // The aggregates of every row, as a scan of one row whose columns the texts name.
        Vorticity.Scan whole = dataset.Scan().Aggregate("count()", "sum(Bytes)", "max(Bytes)");
        await foreach (BatchView batch in whole)
        {
            Assert.Equal(1, batch.RowCount);
            Assert.Equal(["count()", "sum(Bytes)", "max(Bytes)"], batch.Schema.Select(field => field.Name));
            Assert.Equal(16 * 400, batch.Column<long>(0)[0]);
        }

        Assert.Throws<ArgumentException>(() => dataset.Scan().GroupBy("Service").Select("Method"));
        Assert.Throws<ArgumentException>(() => dataset.Scan().GroupBy("Service").Select("median(Bytes)"));
        Assert.Throws<ArgumentException>(() => dataset.Scan().GroupBy("Service").Select("count()", "count()"));
        Assert.Throws<VortexSchemaException>(() => dataset.Scan().GroupBy("Missing"));
        Assert.Throws<VortexSchemaException>(() => dataset.Scan().GroupBy("Service").Select("sum(Service)"));
    }
}
