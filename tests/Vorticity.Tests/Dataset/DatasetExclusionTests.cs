// A value of a declared identifier left out, id != v: an object's counts answer the question less the counts of the rows
// that hold the value, which the identifier's postings find with no read of the object's other rows.
//
// THE ORACLE IS THE ROWS: counts, sums, means and distinct values under a value left out, present in one object or in
// none, answer as the rows do; a host whose one row is left out leaves the distinct hosts; and the counts read less than
// the rows take, a fraction of it when no object holds the value (QP-5).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetExclusionTests
{
    private static readonly DateTimeOffset Noon = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private const string Solo = "r-solo";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AValueLeftOutIsTakenFromTheCountsAsTheRowsLeaveItOut()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store);
        foreach (string left in (string[])[Solo, "r-000123", "r-absent"])
        {
            string filter = $"RequestId != '{left}'";
            foreach ((string[] keys, string[] results) in (ValueTuple<string[], string[]>[])
                [
                    (["Service"], ["Service", "count()"]),
                    (["Service"], ["Service", "count()", "sum(Size)", "avg(Latency)"]),
                    ([], ["count_distinct(Host)"]),
                    (["Status"], ["Status", "count()"]),
                    (["Service"], ["Service", "min(Latency)"]),
                ])
            {
                List<string> rows = await DatasetPlannerTests.AskAsync(dataset, keys, results, filter, synopses: false);
                List<string> counted = await DatasetPlannerTests.AskAsync(dataset, keys, results, filter, synopses: true);
                Assert.NotEmpty(rows);
                Assert.True(rows.SequenceEqual(counted), $"{string.Join(", ", results)} under {filter}: {string.Join(" | ", counted)} through the counts, {string.Join(" | ", rows)} through the rows");
            }
        }

        // The solo host's one row left out, it leaves the distinct hosts: thirteen, then twelve.
        Assert.Equal(["13"], await DatasetPlannerTests.AskAsync(dataset, [], ["count_distinct(Host)"], "RequestId != 'r-absent'", synopses: true));
        Assert.Equal(["12"], await DatasetPlannerTests.AskAsync(dataset, [], ["count_distinct(Host)"], $"RequestId != '{Solo}'", synopses: true));

        // Every object answers by its counts, less the rows its postings find.
        ScanPlan plan = await dataset.Scan<Call>().With(new ScanOptions { UseSynopses = true }).Where(c => c.RequestId != Solo)
            .GroupBy(c => c.Service).Select(g => (g.Key, g.Count())).ExplainAsync(Ct);
        Assert.NotEmpty(plan.States);
        Assert.All(plan.States, step => Assert.Contains("its postings find", step.Detail, StringComparison.Ordinal));
    }

    /// <summary>
    /// Counts and sums by service but one request's (QP-5): each object reads its counts and the identifier's postings, a
    /// fraction of its rows' bytes, and the object that holds the request the chunks of the identifier, the counted column
    /// and the measure that hold its row, here every row's, one chunk a column: less than the rows all the same.
    /// </summary>
    [Theory]
    [InlineData("r-absent", 4)]
    [InlineData(Solo, 1)]
    public async Task AValueLeftOutReadsLessThanTheRows(string left, int fewer)
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await (await CreateAsync(memory)).DisposeAsync();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        long[] bytes = new long[2];
        long[] requests = new long[2];
        foreach (bool synopses in (bool[])[false, true])
        {
            await using VortexSession session = VortexSession.Create(_ => { });
            await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session }, Ct);
            store.Reset();
            await DatasetPlannerTests.AskAsync(dataset, ["Service"], ["Service", "count()", "sum(Size)"], $"RequestId != '{left}'", synopses);
            (bytes[synopses ? 1 : 0], requests[synopses ? 1 : 0]) = (store.BytesRead, store.Requests);
        }

        Console.WriteLine($"EXCLUSION counts by service but {left}: {bytes[1]:N0} bytes in {requests[1]} requests through the counts, {bytes[0]:N0} in {requests[0]} through the rows");
        Assert.True(bytes[1] * fewer < bytes[0], $"{bytes[1]} bytes through the counts, {bytes[0]} through the rows");
    }

    /// <summary>
    /// Four services, each 20 000 calls over two hours from noon, from three hosts of its own, each with a request of its
    /// own, keyed by the service then the instant, the request declared an identifier; and one call from a host of its own.
    /// </summary>
    private static async Task<VortexDataset> CreateAsync(MemoryObjectStore store)
    {
        Random random = new Random(0xD9);
        List<Call> rows = [];
        for (int service = 0; service < 4; service++)
        {
            for (int i = 0; i < 20_000; i++)
            {
                rows.Add(new Call(
                    $"svc-{service}",
                    Noon.AddTicks(random.NextInt64(TimeSpan.FromHours(2).Ticks)),
                    $"h-{service}-{random.Next(3)}",
                    $"r-{(service * 20_000) + i:d6}",
                    (short)(random.Next(20) switch { < 14 => 200, < 17 => 404, < 19 => 500, _ => 503 }),
                    random.Next(4_000) / 4.0,
                    random.Next(1_000_000)));
            }
        }

        rows.Add(new Call("svc-2", Noon.AddMinutes(61), "h-solo", Solo, 200, 12.5, 4_096));
        random.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(rows));
        VortexDataset dataset = await VortexDataset.CreateAsync(store, Call.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Call.Service), nameof(Call.At)],
            Clustering = new ClusteringOptions { ObjectBytes = 256 << 10 },
            Identifiers = [nameof(Call.RequestId)],
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
    public partial record struct Call(string Service, DateTimeOffset At, string Host, string RequestId, short Status, double Latency, long Size);
}
