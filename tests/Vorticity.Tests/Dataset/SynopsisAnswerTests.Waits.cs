// A number whose values repeat but are many, a wait in whole milliseconds: a key or a count of the tables, and a measure
// of the tables it is no key of too, so that its mean and its extremes by another key come from the tables; a status of
// a few values stays a key alone.
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
    /// Two hours and a quarter of records a half second apart, in slices of 2 000 rows: a wait of 251 whole values, which
    /// repeat some eight times a slice, beside a route and a status. The mean and the longest wait by route, and by
    /// service under a status, come from the tables of every object, where an object whose wait the tables keyed or
    /// counted read its rows, and give the bits of the rows; a sum of the status by route, a code of four values the
    /// tables count, reads the rows.
    /// </summary>
    [Theory]
    [InlineData("mean and longest wait by route", true)]
    [InlineData("wait by service under a status", true)]
    [InlineData("status sum by route", false)]
    public async Task ANumberOfManyValuesThatRepeatIsAMeasureToo(string question, bool fromTables)
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await WaitsAsync(store);
        List<string>[] answers = new List<string>[2];
        ScanMetrics[] metrics = new ScanMetrics[2];
        foreach (bool synopses in new[] { true, false })
        {
            Scan<Attributed> scan = dataset.Scan<Attributed>().With(new ScanOptions { UseSynopses = synopses });
            (answers[synopses ? 0 : 1], metrics[synopses ? 0 : 1]) = await GroupsAsync(question switch
            {
                "mean and longest wait by route" => scan.GroupBy(r => r.Attributes["route"].AsString())
                    .Select(g => (g.Key, g.Average(r => r.Attributes["wait"].AsDouble()), g.Max(r => r.Attributes["wait"].AsDouble()))),
                "wait by service under a status" => scan.Where(r => r.Attributes["status"].AsInt64() == 500).GroupBy(r => r.Service)
                    .Select(g => (g.Key, g.Count(), g.Sum(r => r.Attributes["wait"].AsDouble()), g.Min(r => r.Attributes["wait"].AsDouble()))),
                _ => scan.GroupBy(r => r.Attributes["route"].AsString()).Select(g => (g.Key, g.Sum(r => r.Attributes["status"].AsInt64()))),
            });
        }

        Console.Out.Write($"WAITS {question}: {metrics[0].SynopsisRows} table rows and {metrics[0].Rows} rows, against {metrics[1].Rows} rows.\n");
        Assert.Equal(answers[1], answers[0]);
        Assert.NotEmpty(answers[0]);
        Assert.Equal(fromTables, metrics[0].SynopsisRows > 0);
        Assert.Equal(fromTables, metrics[0].Rows == 0);
    }

    /// <summary>
    /// Two hours and a quarter of records from noon, a half second apart, appended in sixteen parts and compacted: a route
    /// of nine, a status of four, a wait of 251 whole values. Slices of 2 000 rows, and every table kept.
    /// </summary>
    private static async Task<VortexDataset> WaitsAsync(Vorticity.IO.IObjectStore store)
    {
        VortexDataset dataset = await VortexDataset.CreateAsync(store, Attributed.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Attributed.At)],
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour, SliceTargetRows = 2_000 },
            Write = new VortexWriteOptions { Indexes = IndexPolicy.None.WithBudgetPerMille(10_000) },
        }, Ct);
        Random random = new Random(61);
        for (int part = 0; part < 16; part++)
        {
            Attributed[] rows = [.. Enumerable.Range(0, 1_000).Select(i => new Attributed(
                Noon.AddSeconds(((part * 1_000) + i) * 0.5),
                Services[random.Next(Services.Length)],
                Variant.Object(
                    ("route", Variant.From($"/r/{random.Next(9)}")),
                    ("status", Variant.From(random.Next(4) switch { 0 => 200L, 1 => 201L, 2 => 404L, _ => 500L })),
                    ("wait", Variant.From(Math.Round(random.NextDouble() * 250)))))
            )];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Attributed>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }, Ct) is not null)
        {
        }

        return dataset;
    }
}
