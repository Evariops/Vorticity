// The partials a question learns of the objects it reads as rows, its family's: the same columns, its filters that ask a
// column for values made keys, so that the question asked again, or with other values, answers from them.
//
// THE ORACLE IS THE ROWS: every question of a family, learned then answered from its partials, gives the rows' answer;
// asked again with another value, it reads nothing of the objects; and a handle opened later finds the partials in the
// session's cache.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Vorticity.Tests.Writing;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetLearnedPartialsTests
{
    private static readonly DateTimeOffset Noon = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Keys and results of a family, and the filters that ask it each a value of the same columns.
    private static readonly (string[] Keys, string[] Results, string[] Filters)[] Families =
    [
        (["Route"], ["Route", "count()", "sum(Size)"], ["Status = 404", "Status = 500", "Status in (500, 503)"]),
        (["Host"], ["Host", "count()", "avg(Latency)", "max(Size)"], ["Status = 200", "Status = 503"]),
        (["Service", "Route"], ["Service", "Route", "count()", "min(Latency)"], ["Status = 404 and Host = 'h-1-0'", "Status = 500 and Host = 'h-2-1'"]),
        ([], ["count()", "sum(Size)"], ["Status = 404", "Status = 200"]),
    ];

    [Fact]
    public async Task AFamilyLearnedAnswersEveryValueAsTheRowsDo()
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await (await CreateAsync(memory)).DisposeAsync();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions(), Ct);
        foreach ((string[] keys, string[] results, string[] filters) in Families)
        {
            for (int f = 0; f < filters.Length; f++)
            {
                List<string> rows = await DatasetPlannerTests.AskAsync(dataset, keys, results, filters[f], synopses: false);
                store.Reset();
                List<string> answered = await DatasetPlannerTests.AskAsync(dataset, keys, results, filters[f], synopses: true);
                Assert.NotEmpty(rows);
                Assert.True(rows.SequenceEqual(answered), $"{string.Join(", ", results)} under {filters[f]}: {string.Join(" | ", answered)} from the partials, {string.Join(" | ", rows)} through the rows");

                // Past the first value the family learned, the objects are answered from the partials: nothing is read.
                if (f > 0)
                {
                    Assert.True(store.BytesRead == 0, $"{filters[f]}: {store.BytesRead} bytes in {store.Requests} requests, where the partials hold the answer");
                }
            }
        }

        // The family is its columns, however the question is written: the typed question finds what the text one learned.
        ScanPlan plan = await dataset.Scan<Call>().With(new ScanOptions { UseSynopses = true }).Where(c => c.Status == 503)
            .GroupBy(c => c.Route).Select(g => (g.Key, g.Count(), g.Sum(c => c.Size))).ExplainAsync(Ct);
        Assert.NotEmpty(plan.States);
        Assert.All(plan.States, step => Assert.Equal(("partial", "the partial of the question's family it learned"), (step.Read, step.Detail)));
    }

    /// <summary>Quantiles and approximate distinct counts, whose states are sketches, are learned and answered as the rows give them.</summary>
    [Fact]
    public async Task SketchesAreLearnedToo()
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await (await CreateAsync(memory)).DisposeAsync();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions(), Ct);
        foreach (short status in (short[])[404, 500, 503])
        {
            List<string> rows = await AskAsync(dataset, status, synopses: false);
            store.Reset();
            Assert.Equal(rows, await AskAsync(dataset, status, synopses: true));
            Assert.True(status == 404 || store.BytesRead == 0, $"{store.BytesRead} bytes under {status}, where the partials hold the answer");
        }

        static async Task<List<string>> AskAsync(VortexDataset dataset, short status, bool synopses)
        {
            List<string> rows = [];
            await using (IAsyncEnumerator<RecordBatch> batches = dataset.Scan<Call>().With(new ScanOptions { UseSynopses = synopses }).Where(c => c.Status == status)
                .GroupBy(c => c.Route).Select(g => (g.Key, g.Quantile(c => c.Latency, 0.9), g.ApproximateCountDistinct(c => c.Host))).Query.Batches(Ct))
            {
                while (await batches.MoveNextAsync())
                {
                    Values.DescribeRows(batches.Current, rows);
                }
            }

            rows.Sort(StringComparer.Ordinal);
            return rows;
        }
    }

    /// <summary>
    /// A value of the key's first column, which an object of several services holds the rows of together: the family would
    /// read every service's rows where the question reads one's, so such an object is read as the question reads it.
    /// </summary>
    [Fact]
    public async Task AValueOfTheKeysFirstColumnIsNotLearnedWhereItWouldWidenTheRead()
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await (await CreateAsync(memory)).DisposeAsync();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        foreach (string service in (string[])["svc-1", "svc-2"])
        {
            await using VortexSession session = VortexSession.Create(_ => { });
            await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session }, Ct);
            string filter = $"Service = '{service}' and Status = 500";
            List<string> rows = await DatasetPlannerTests.AskAsync(dataset, ["Route"], ["Route", "count()"], filter, synopses: false);
            store.Reset();
            Assert.Equal(rows, await DatasetPlannerTests.AskAsync(dataset, ["Route"], ["Route", "count()"], filter, synopses: true));
            Assert.True(store.BytesRead > 0, "an object of several services is read as the question reads it");
        }
    }

    /// <summary>A handle opened later, on the same session's cache, answers a learned family with no read of the objects.</summary>
    [Fact]
    public async Task ThePartialsOutliveTheHandleInTheSessionsCache()
    {
        string directory = Directory.CreateTempSubdirectory("vortex-partials-").FullName;
        try
        {
            await using MemoryObjectStore memory = new MemoryObjectStore();
            await (await CreateAsync(memory)).DisposeAsync();
            await using CountingObjectStore store = new CountingObjectStore(memory);
            long[] bytes = new long[3];
            List<string>[] answers = new List<string>[3];
            for (int ask = 0; ask < 3; ask++)
            {
                // Twice on one cache, the second handle finding what the first learned; once with none, reading the rows.
                await using VortexSession session = ask < 2
                    ? VortexSession.Create(options => options.SynopsisCache = new SynopsisCache(directory, 64L << 20))
                    : VortexSession.Create(_ => { });
                await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session }, Ct);
                store.Reset();
                answers[ask] = await DatasetPlannerTests.AskAsync(dataset, ["Route"], ["Route", "count()", "sum(Size)"], ask == 0 ? "Status = 404" : "Status = 503", synopses: ask < 2);
                bytes[ask] = store.BytesRead;
            }

            Console.WriteLine($"PARTIALS learned {bytes[0]:N0} bytes, answered from the cache {bytes[1]:N0}, the rows {bytes[2]:N0}");
            Assert.Equal(answers[2], answers[1]);
            Assert.True(bytes[1] * 10 < bytes[2], $"{bytes[1]} bytes from the cache, {bytes[2]} through the rows");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Four services, each 20 000 calls over two hours from noon, from three hosts and on six routes of its own, keyed by the
    /// service then the instant, appended in parts and compacted.
    /// </summary>
    private static async Task<VortexDataset> CreateAsync(MemoryObjectStore store)
    {
        Random random = new Random(0xF1);
        List<Call> rows = [];
        for (int service = 0; service < 4; service++)
        {
            for (int i = 0; i < 20_000; i++)
            {
                rows.Add(new Call(
                    $"svc-{service}",
                    Noon.AddTicks(random.NextInt64(TimeSpan.FromHours(2).Ticks)),
                    $"h-{service}-{random.Next(3)}",
                    $"/svc{service}/r{random.Next(6)}",
                    (short)(random.Next(20) switch { < 14 => 200, < 17 => 404, < 19 => 500, _ => 503 }),
                    random.Next(4_000) / 4.0,
                    random.Next(1_000_000)));
            }
        }

        random.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(rows));
        VortexDataset dataset = await VortexDataset.CreateAsync(store, Call.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Call.Service), nameof(Call.At)],
            Clustering = new ClusteringOptions { ObjectBytes = 256 << 10 },
            ContentsObjectBytes = 1,
            SheetObjectBytes = 0,
        }, Ct);
        for (int from = 0; from < rows.Count; from += 20_000)
        {
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Call>(rows.Skip(from).Take(20_000).ToArray(), Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 1 }, Ct) is not null)
        {
        }

        return dataset;
    }

    [VortexRecord]
    public partial record struct Call(string Service, DateTimeOffset At, string Host, string Route, short Status, double Latency, long Size);
}
