// The correction of a synopsis table for the rows a question leaves out, held to the rows it takes out.
//
// WHAT IS HELD: leaving one request out of a count over two hours whose table keyed down to the host holds some
// twenty-seven thousand rows an hour allocates, warm, what leaving out a request no row holds allocates, give or take
// the rows the identifier's index reads for it: the table rows charged are found by their keys where they lie, and
// only their own states are copied, where a dictionary of every row of the table made a correction allocate what the
// table holds, 7.3 MB for one row taken out of these two. Both answer the bits their rows give.
//
// WHY SO MANY WARM ASKS: the first dozen pay the JIT's tiering, which in the walk over a table's rows allocated
// megabytes an ask; the least of a few asks past it is the steady state.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Vorticity.Tests.Scan;
using Vorticity.Tests.Writing;
using Xunit;

namespace Vorticity.Tests.Dataset;

[Collection(nameof(AllocationCollection))]
[Trait("Category", "RunsAlone")]
public sealed partial class SynopsisCorrectionTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] Services = ["api", "auth", "billing", "cart", "search"];

    private static readonly string[] Methods = ["GET", "POST", "PUT"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A count by method, status, service and host but one request's, over two sealed hours whose table keyed by all four
    /// answers it, and the minutes after them read as rows: the request's row comes out of its hour's table for what a
    /// request no row holds costs, within 256 KB.
    /// </summary>
    [Fact]
    public async Task ARowTakenOutOfATableAllocatesWhatItTakesOutNotWhatTheTableHolds()
    {
        ReleaseOnlyCeilings.Require();
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await HoursAsync(store);
        long held = 0;
        await foreach (PositionedObject entry in dataset.Snapshot.WalkAsync(null, 0, long.MaxValue, null, Ct))
        {
            foreach ((SynopsisTableInfo table, _) in entry.Entry.Contents?.Tables ?? [])
            {
                held += table.Kind == SynopsisTableKind.Coarse && table.Keys.Length == 4 ? table.Rows : 0;
            }
        }

        Assert.True(held >= 50_000, $"{held} rows in the tables keyed by the host");
        Dictionary<string, long> allocated = [];
        foreach (string left in new[] { "r3-22", "nowhere" })
        {
            Func<Scan<Hosted>, Vorticity.Aggregation> ask = scan => scan.Where(r => r.RequestId != left)
                .GroupBy(r => (r.Method, r.Status, r.Service, r.Host)).Select(g => (g.Key.Method, g.Key.Status, g.Key.Service, g.Key.Host, g.Count()));
            (List<string> answer, ScanMetrics metrics) = await GroupsAsync(ask(dataset.Scan<Hosted>().With(new ScanOptions { UseSynopses = true })));
            for (int warm = 0; warm < 40; warm++)
            {
                await GroupsAsync(ask(dataset.Scan<Hosted>().With(new ScanOptions { UseSynopses = true })));
            }

            long least = long.MaxValue;
            for (int round = 0; round < 5; round++)
            {
                long before = GC.GetTotalAllocatedBytes(precise: true);
                await GroupsAsync(ask(dataset.Scan<Hosted>().With(new ScanOptions { UseSynopses = true })));
                least = Math.Min(least, GC.GetTotalAllocatedBytes(precise: true) - before);
            }

            allocated[left] = least;
            Console.Out.Write(string.Create(
                CultureInfo.InvariantCulture, $"CORRECTION '{left}': {metrics.SynopsisRows} table rows, {metrics.Rows} rows, {least} bytes allocated warm.\n"));
            Assert.Equal(held, metrics.SynopsisRows);
            Assert.Equal((await GroupsAsync(ask(dataset.Scan<Hosted>().With(new ScanOptions { UseSynopses = false })))).Groups, answer);
        }

        long taken = allocated["r3-22"] - allocated["nowhere"];
        Assert.True(taken < 256 << 10, $"{taken} bytes to take a row out of {held} table rows");
    }

    /// <summary>The question's groups, each a line of its key and results, and what reading them did.</summary>
    private static async Task<(List<string> Groups, ScanMetrics Metrics)> GroupsAsync(Vorticity.Aggregation aggregation)
    {
        List<string> groups = [];
        IAsyncEnumerator<RecordBatch> batches = aggregation.Query.Batches(Ct);
        await using (batches)
        {
            while (await batches.MoveNextAsync())
            {
                Values.DescribeRows(batches.Current, groups);
            }
        }

        groups.Sort(StringComparer.Ordinal);
        return (groups, aggregation.Metrics);
    }

    /// <summary>
    /// Sixteen parts of 4,000 requests an eighth of a second apart, from noon on, compacted until nothing is due: two
    /// sealed hours and the minutes after them; a host of 4,000, and every request an id of its own, declared an
    /// identifier; a budget that keeps the table keyed by the host.
    /// </summary>
    private static async Task<VortexDataset> HoursAsync(Vorticity.IO.IObjectStore store)
    {
        VortexDataset dataset = await VortexDataset.CreateAsync(store, Hosted.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Hosted.At)],
            Identifiers = [nameof(Hosted.RequestId)],
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour, SliceTargetRows = 1_000 },
            Write = new VortexWriteOptions { Indexes = IndexPolicy.None.WithBudgetPerMille(10_000) },
        }, Ct);
        Random random = new Random(59);
        for (int part = 0; part < 16; part++)
        {
            Hosted[] rows = [.. Enumerable.Range(0, 4_000).Select(i => new Hosted(
                Noon.AddSeconds((part * 4_000 + i) * 0.125),
                Services[random.Next(Services.Length)],
                Methods[random.Next(Methods.Length)],
                random.Next(4) switch { 0 => 200L, 1 => 201L, 2 => 404L, _ => 500L },
                $"host-{random.Next(4_000)}",
                $"r{part}-{i}"))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Hosted>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }, Ct) is not null)
        {
        }

        return dataset;
    }

    [VortexRecord]
    public partial record struct Hosted(DateTimeOffset At, string Service, string Method, long Status, string Host, string RequestId);
}
