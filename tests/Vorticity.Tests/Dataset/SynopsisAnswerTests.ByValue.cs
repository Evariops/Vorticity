// A table of a column's values per slice lies in the order of its hours, then of its values, then of its slices, each
// chunk saying the least and the greatest value it holds: a question of one value reads the chunks that may hold it.
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
    /// The objects' tables cut in chunks of 8 rows: a count of one service of five per ten minutes reads the services'
    /// values per slice, and only the chunks that may hold the service, less than half the bytes the count of every service
    /// per ten minutes reads from every chunk; both give the bits of the rows.
    /// </summary>
    [Fact]
    public async Task AQuestionOfOneValueReadsTheChunksThatMayHoldIt()
    {
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);
        await using VortexDataset dataset = await DenseAsync(store);
        Func<Scan<Request>, Vorticity.Aggregation>[] questions =
        [
            scan => scan.Where(r => r.Service == "cart").GroupBy(r => r.At.Bucket(TimeSpan.FromMinutes(10))).Select(g => (g.Key, g.Count(), g.Sum(r => r.Bytes))),
            scan => scan.GroupBy(r => (r.Service, r.At.Bucket(TimeSpan.FromMinutes(10)))).Select(g => (g.Key.Service, g.Key.Item2, g.Count(), g.Sum(r => r.Bytes))),
        ];
        long[] bytes = new long[questions.Length];
        for (int q = 0; q < questions.Length; q++)
        {
            (List<string> on, ScanMetrics metrics) = await GroupsAsync(questions[q](dataset.Scan<Request>().With(new ScanOptions { UseSynopses = true })));
            store.Reset();
            (List<string> again, _) = await GroupsAsync(questions[q](dataset.Scan<Request>().With(new ScanOptions { UseSynopses = true })));
            bytes[q] = store.BytesRead;
            (List<string> off, _) = await GroupsAsync(questions[q](dataset.Scan<Request>().With(new ScanOptions { UseSynopses = false })));
            ScanPlan plan = await questions[q](dataset.Scan<Request>().With(new ScanOptions { UseSynopses = true })).ExplainAsync(Ct);
            Console.Out.Write($"BY VALUE {q}: {metrics.SynopsisRows} table rows and {metrics.Rows} rows, {bytes[q]} bytes read: {string.Join(" | ", plan.States.Select(step => $"{step.Read} {step.Detail} x{step.Count}"))}\n");
            Assert.Equal(off, on);
            Assert.Equal(on, again);
            Assert.True(metrics.SynopsisRows > 0, "no state folded");
            Assert.Contains(plan.States, step => step.Detail.StartsWith("Values by Slice", StringComparison.Ordinal));
        }

        Assert.True(bytes[0] * 2 < bytes[1], $"one service read {bytes[0]} bytes, every service {bytes[1]}");
    }
}

public sealed partial class SynopsisAnswerTests
{
    /// <summary>
    /// Two hours and a quarter of requests from noon, two a second, appended in sixteen parts and compacted, in slices of
    /// a minute: a slice holds some 120 rows, five services among them, whose values per slice the tables keep, cut in
    /// chunks of 8 rows.
    /// </summary>
    private static async Task<VortexDataset> DenseAsync(Vorticity.IO.IObjectStore store)
    {
        VortexDataset dataset = await VortexDataset.CreateAsync(store, Request.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Request.At)],
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour, SliceTargetRows = 100 },
            Write = new VortexWriteOptions { Indexes = IndexPolicy.None.WithBudgetPerMille(10_000) },
            SynopsisChunking = (8, 256),
        }, Ct);
        Random random = new Random(67);
        for (int part = 0; part < 16; part++)
        {
            Request[] rows = [.. Enumerable.Range(0, 1_000).Select(i => Drawn(random, Noon.AddSeconds(((part * 1_000) + i) * 0.5)))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Request>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }, Ct) is not null)
        {
        }

        return dataset;
    }
}
