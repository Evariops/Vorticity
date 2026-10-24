// A day sealed as one object, cut by a key of time or a window into its hours: the hours kept whole come from its
// table of hours, where they came from the runs of every slice, and every question gives the bits of its rows.
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
    /// A day of a request every 20 s sealed as one object, in slices of a minute: a count by service per hour, and in a
    /// window of whole hours that cuts the day, read the hours from a table by the hour, where every slice's runs were
    /// read when the object's table could not say, and give the bits of the rows; a window that cuts an hour reads the
    /// hours it keeps from the table, and that hour's slices as rows, slices of three rows keeping no table of their own.
    /// </summary>
    [Fact]
    public async Task ACutDaysHoursComeFromItsTableOfHours()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        DateTimeOffset midnight = new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero);
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Request.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Request.At)],
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Day, SliceTargetRows = 20 },
            Write = new VortexWriteOptions { Indexes = IndexPolicy.None.WithBudgetPerMille(10_000) },
        }, Ct);
        Random random = new Random(43);
        for (int part = 0; part < 9; part++)
        {
            // Eight parts of the day, then a request of the next day past the lateness, which closes it.
            Request[] rows = part < 8
                ? [.. Enumerable.Range(0, 540).Select(i => Drawn(random, midnight.AddSeconds(((part * 540) + i) * 20)))]
                : [Drawn(random, midnight.AddDays(1).AddMinutes(10))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Request>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 1, Fanout = 100 }, Ct) is not null)
        {
        }

        DateTimeOffset from = midnight.AddHours(3);
        DateTimeOffset to = midnight.AddHours(21);
        Func<Scan<Request>, Vorticity.Aggregation>[] questions =
        [
            scan => scan.Where(r => r.At < midnight.AddDays(1)).GroupBy(r => (r.At.Truncate(CalendarUnit.Hour), r.Service)).Select(g => (g.Key.Item1, g.Key.Service, g.Count(), g.Sum(r => r.Bytes))),
            scan => scan.Where(r => r.At >= from && r.At < to).GroupBy(r => r.Service).Select(g => (g.Key, g.Count(), g.Max(r => r.Bytes))),
            scan => scan.Where(r => r.At >= from.AddMinutes(17.5) && r.At < to).GroupBy(r => r.Method).Select(g => (g.Key, g.Count())),
        ];
        for (int q = 0; q < questions.Length; q++)
        {
            (List<string> on, ScanMetrics metrics) = await GroupsAsync(questions[q](dataset.Scan<Request>().With(new ScanOptions { UseSynopses = true })));
            (List<string> off, ScanMetrics without) = await GroupsAsync(questions[q](dataset.Scan<Request>().With(new ScanOptions { UseSynopses = false })));
            ScanPlan plan = await questions[q](dataset.Scan<Request>().With(new ScanOptions { UseSynopses = true })).ExplainAsync(Ct);
            string reads = string.Join(" | ", plan.States.Select(step => $"{step.Read} {step.Detail} x{step.Count}"));
            Console.Out.Write($"CUT DAY {q}: {metrics.SynopsisRows} table rows and {metrics.Rows} rows against {without.Rows}: {reads}\n");
            Assert.Equal(off, on);
            Assert.NotEmpty(on);
            Assert.True(metrics.SynopsisRows > 0, "no state folded");
            Assert.Contains(plan.States, step => step.Detail.Contains("by Hour", StringComparison.Ordinal));
            Assert.DoesNotContain(plan.States, step => step.Detail.Contains("by Slice", StringComparison.Ordinal) && step.Count > 120);
            Assert.Equal(q == 2, metrics.EdgeRows > 0);
        }
    }

    /// <summary>A request at <paramref name="at"/>, its service, method, status, duration and bytes drawn by <paramref name="random"/>.</summary>
    private static Request Drawn(Random random, DateTimeOffset at) => new Request(
        at,
        Services[random.Next(Services.Length)],
        Methods[random.Next(Methods.Length)],
        random.Next(4) switch { 0 => 200L, 1 => 201L, 2 => 404L, _ => 500L },
        random.NextDouble() * 1_000,
        random.NextInt64(1, 1L << 40));
}
