// Distinct counts from the values the granules list (the query path plan, G3): a granule whose key holds one group gives
// that group each value its statistics list of the distinct column, as its rows would; a granule where the distinct column
// holds one value gives it to each group it lists. The distinct sketch ignores repeats, so its state is the rows'.
//
// THE ORACLE IS THE ROWS' PATH: every distinct count, exact or approximate, equals the one the rows give.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetGranuleDistinctTests
{
    private static readonly DateTime Start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// 400 000 requests of four services, sorted by the service, the host, then the instant, in two appends consolidated:
    /// 12 hosts, each in one of five regions, a host's rows in four granules or so, one host and one region in most. The
    /// distinct hosts per region, approximate and exact, are the rows' own; under a request left out, which the rows read
    /// every identifier for, they are answered by the granules' statistics, the request placed by its partition's index: 35
    /// granules of 49 settled, those where two hosts of two regions meet and the four the request may lie in read as rows.
    /// An object this small reads those whole chunks, a request being worth more than its bytes: what each path reads is
    /// printed, not held.
    /// </summary>
    [Fact]
    public async Task DistinctHostsPerRegionAreAnsweredFromTheGranules()
    {
        Random random = new Random(71);
        Request[] requests = [.. Enumerable.Range(0, 400_000).Select(i =>
        {
            int host = random.Next(12);
            return new Request(Start.AddMilliseconds(i * 61L), $"svc-{host % 4}", $"host-{host:D2}", $"region-{host % 5}", $"id-{i:D7}", 200 + random.Next(3));
        })];
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        DatasetOptions options = new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(Request.At)).Truncate(CalendarUnit.Day),
                SortKey = [LayoutValue.Column(nameof(Request.Service)), LayoutValue.Column(nameof(Request.Host)), LayoutValue.Column(nameof(Request.At))],
                IndexedPrefix = 2,
                ObjectBytes = 256 << 20,
                MergeFanout = 4,
                CalmAfter = null,
            },
            Identifiers = [nameof(Request.RequestId)],
            ContentsObjectBytes = 0,
            SheetObjectBytes = 0,
        };
        await using (VortexDataset writer = await VortexDataset.CreateAsync(store, Request.Schema, options, Ct))
        {
            foreach (Request[] half in (Request[][])[requests[..200_000], requests[200_000..]])
            {
                await using (ObjectDraft draft = writer.StartObject())
                {
                    await draft.Writer.WriteAsync<Request>(half, Ct);
                    await writer.AppendAsync(draft, Ct);
                }

                while (await writer.CompactAsync(cancellationToken: Ct) is not null)
                {
                }
            }

            await writer.ConsolidateAsync(Ct);
        }

        string left = requests[123_456].RequestId;
        foreach ((string label, bool granules, Func<VortexDataset, bool, Vorticity.Aggregation> ask) in new (string, bool, Func<VortexDataset, bool, Vorticity.Aggregation>)[]
        {
            ("approximate", false, static (d, synopses) => d.Scan<Request>().With(new ScanOptions { UseSynopses = synopses })
                .GroupBy(r => r.Region).Select(g => (g.Key, g.ApproximateCountDistinct(r => r.Host)))),
            ("exact", false, static (d, synopses) => d.Scan<Request>().With(new ScanOptions { UseSynopses = synopses })
                .GroupBy(r => r.Region).Select(g => (g.Key, g.CountDistinct(r => r.Host)))),
            ("approximate, but one request", true, (d, synopses) => d.Scan<Request>().With(new ScanOptions { UseSynopses = synopses })
                .Where(r => r.RequestId != left).GroupBy(r => r.Region).Select(g => (g.Key, g.ApproximateCountDistinct(r => r.Host)))),
            ("exact, but one request", true, (d, synopses) => d.Scan<Request>().With(new ScanOptions { UseSynopses = synopses })
                .Where(r => r.RequestId != left).GroupBy(r => r.Region).Select(g => (g.Key, g.CountDistinct(r => r.Host)))),
        })
        {
            long[] bytes = new long[2];
            List<string>[] answers = [[], []];
            foreach (bool synopses in (bool[])[false, true])
            {
                await using VortexDataset dataset = await VortexDataset.OpenAsync(store, options, Ct);
                store.Reset();
                Vorticity.Aggregation question = ask(dataset, synopses);
                answers[synopses ? 1 : 0] = [.. (await question.As<RegionHosts>().ToRecordsAsync(Ct).ToListAsync(Ct)).Select(static a => $"{a.Region}:{a.Hosts}").Order(StringComparer.Ordinal)];
                bytes[synopses ? 1 : 0] = store.BytesRead;
                if (synopses && granules)
                {
                    ScanPlan plan = await ask(dataset, true).ExplainAsync(Ct);
                    Assert.All(plan.Units, unit => Assert.True(unit.Alternative == "granules", $"{label}, {unit.Alternative}: {unit.Detail}; {string.Join("; ", unit.Rejected)}"));
                }
            }

            Console.WriteLine($"DISTINCT {label}: {bytes[1]} bytes through the granules, {bytes[0]} through the rows");
            Assert.Equal(5, answers[0].Count);
            Assert.Equal(answers[0], answers[1]);
        }
    }

    [VortexRecord]
    public partial record struct Request(DateTime At, string Service, string Host, string Region, string RequestId, int Status);

    [VortexRecord]
    public partial record struct RegionHosts(string? Region, long Hosts);
}
