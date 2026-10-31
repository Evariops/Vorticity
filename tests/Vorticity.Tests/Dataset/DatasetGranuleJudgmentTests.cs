// The granules judged one by one (the query path plan, G1): an object whose granules hold one value of a column in most of
// them, that column and another varying over the object, is answered by its granules' statistics, each granule judged by
// what it holds, where the object's summaries alone would read its rows.
//
// THE ORACLE IS LINQ AND THE ROWS' PATH: every answer is LINQ's; the granules' path reads less than the rows'.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetGranuleJudgmentTests
{
    private static readonly DateTime Start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// 640 000 calls of two services, four routes each, 200 pods, sorted by the service, the route, then the instant, in two
    /// appends consolidated, 79 granules: a route's 80 000 calls lie in ten granules or eleven, one value of the route in
    /// most. The pods of a route, a filter on the route and a key on the pod, both varying over the object, are answered by
    /// the granules: the route's statistics refute or prove the filter granule by granule, the pod's count its rows; the two
    /// granules that hold the route and another are read as rows. The table of contents says the rows of the granules where
    /// the route holds one value. The two granules read pull the hull of their columns' chunk, 221 KB of the 378 the rows
    /// read: the granules' path reads less, not yet its model.
    /// </summary>
    [Fact]
    public async Task APodsOfARouteAreAnsweredGranuleByGranule()
    {
        Random random = new Random(53);
        Call[] calls = [.. Enumerable.Range(0, 640_000).Select(i => new Call(
            Start.AddMilliseconds(i * 47L), $"svc-{i % 2}", $"/svc{i % 2}/r{(i / 2) % 4}", $"pod-{random.Next(200):D3}", (short)(random.Next(10) == 0 ? 500 : 200)))];
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Call.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(Call.At)).Truncate(CalendarUnit.Day),
                SortKey = [LayoutValue.Column(nameof(Call.Service)), LayoutValue.Column(nameof(Call.Route)), LayoutValue.Column(nameof(Call.At))],
                IndexedPrefix = 1,
                ObjectBytes = 256 << 20,
                MergeFanout = 4,
                CalmAfter = null,
            },
            ContentsObjectBytes = 0,
            SheetObjectBytes = 0,
        }, Ct);
        foreach (Call[] half in (Call[][])[calls[..320_000], calls[320_000..]])
        {
            await using (ObjectDraft draft = dataset.StartObject())
            {
                await draft.Writer.WriteAsync<Call>(half, Ct);
                await dataset.AppendAsync(draft, Ct);
            }

            while (await dataset.CompactAsync(cancellationToken: Ct) is not null)
            {
            }
        }

        await dataset.ConsolidateAsync(Ct);
        int route = dataset.Snapshot.Schema.DType.IndexOfField(nameof(Call.Route));
        long single = 0;
        long all = 0;
        await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(Ct))
        {
            single += held.Entry.Contents!.SingleRowsOf(route);
            all += held.Entry.Rows;
        }

        Assert.True(single >= all / 2, $"{single} of {all} rows in granules of one route");

        string[] expected = [.. calls.Where(static c => c.Route == "/svc1/r2").GroupBy(static c => c.Pod).Select(static g => $"{g.Key}:{g.Count()}").Order(StringComparer.Ordinal)];
        long[] bytes = new long[2];
        foreach (bool synopses in (bool[])[false, true])
        {
            await using VortexDataset fresh = await VortexDataset.OpenAsync(store, new DatasetOptions { ContentsObjectBytes = 0, SheetObjectBytes = 0 }, Ct);
            store.Reset();
            Vorticity.Aggregation question = fresh.Scan<Call>().With(new ScanOptions { UseSynopses = synopses })
                .Where(c => c.Route == "/svc1/r2").GroupBy(c => c.Pod).Select(g => (g.Key, g.Count()));
            List<PodCount> answers = await question.As<PodCount>().ToRecordsAsync(Ct).ToListAsync(Ct);
            bytes[synopses ? 1 : 0] = store.BytesRead;
            Assert.Equal(expected, answers.Select(static a => $"{a.Pod}:{a.Count}").Order(StringComparer.Ordinal));
            if (synopses)
            {
                ScanPlan plan = await fresh.Scan<Call>().Where(c => c.Route == "/svc1/r2").GroupBy(c => c.Pod).Select(g => (g.Key, g.Count())).ExplainAsync(Ct);
                Assert.All(plan.Units, static unit => Assert.True(unit.Alternative == "granules", $"{unit.Alternative}: {unit.Detail}; {string.Join("; ", unit.Rejected)}"));
            }
        }

        Console.WriteLine($"JUDGED: the pods of a route, {bytes[1]} bytes through the granules, {bytes[0]} through the rows");
        Assert.True(bytes[1] < bytes[0], $"{bytes[1]} bytes through the granules, {bytes[0]} through the rows");
    }

    [VortexRecord]
    public partial record struct Call(DateTime At, string Service, string Route, string Pod, short Status);

    [VortexRecord]
    public partial record struct PodCount(string? Pod, long Count);
}
