// The runs of the sort key's whole indexed prefix in each granule (the query path plan, G2b): each run one value of every
// component, the service, the host and the hour, kept granule by granule with its rows as the rows come in the key's
// order; a question keyed by two of them, the service and a bucket of twelve hours, takes a granule's groups from them run
// by run where the granule holds two services and two buckets, which neither a column's listed values nor a component's
// values answer alone.
//
// THE ORACLE IS LINQ AND THE ROWS: every count is LINQ's; the runs of each granule are those its rows hold.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetPrefixRunsTests
{
    private static readonly DateTime Start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// 400 000 requests of a day, forty services of three hosts each, or of thirty, sorted by the service, the host, the hour,
    /// then the instant, the first three indexed, in two appends consolidated: a service's 10 000 requests lie in two
    /// granules or so, and where two services meet a granule holds the last hours of one and the first of the next, two
    /// services and two buckets of twelve hours. The object keeps the runs of the prefix each granule holds, those its rows
    /// hold, some 60 a granule, or 590 under thirty hosts, past the 256 values a granule lists; every request but one, per
    /// service and per twelve hours, is answered by the granules, those where two services meet by their runs, the
    /// request's own read as rows, its counts LINQ's.
    /// </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(30)]
    public async Task TwoKeysOfThePrefixAreAnsweredRunByRun(int hosts)
    {
        Random random = new Random(89);
        Request[] requests = [.. Enumerable.Range(0, 400_000).Select(i =>
        {
            int host = random.Next(40 * hosts);
            return new Request(Start.AddTicks(random.NextInt64(TimeSpan.TicksPerDay)), $"svc-{host / hosts:D2}", $"host-{host:D4}", $"id-{i:D7}");
        })];
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        DatasetOptions options = new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(Request.At)).Truncate(CalendarUnit.Day),
                SortKey =
                [
                    LayoutValue.Column(nameof(Request.Service)), LayoutValue.Column(nameof(Request.Host)),
                    LayoutValue.Column(nameof(Request.At)).Truncate(CalendarUnit.Hour), LayoutValue.Column(nameof(Request.At)),
                ],
                IndexedPrefix = 3,
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

            // The runs each granule holds, and their rows, are those its rows hold: a service, a host and an hour each.
            await foreach (PositionedObject held in writer.ScanBuilder().ObjectsAsync(Ct))
            {
                Assert.True(held.Entry.Contents!.KeyRunsOf(GranuleKeyRuns.WholePrefix) is not null, "the object keeps no runs of its prefix by granule");
                (_, _, GranuleKeyRuns?[] read) = await writer.ReadGranuleStatsAsync(held.Entry, [], [], [GranuleKeyRuns.WholePrefix], Ct);
                GranuleKeyRuns runs = read[0]!;
                List<Request> rows = await writer.Scan<Request>().ToRecordsAsync(Ct).Skip((int)held.FirstRow).Take((int)held.Entry.Rows).ToListAsync(Ct);
                Assert.Equal((rows.Count + 8191) / 8192, runs.Count);
                for (int g = 0; g < runs.Count; g++)
                {
                    long[] truth = [.. Runs(rows.Skip(g * 8192).Take(8192))];
                    (int first, int count) = runs.RunsOf(g);
                    Assert.Equal(truth, Enumerable.Range(first, count).Select(runs.RowsOf));
                }
            }
        }

        string left = requests[123_456].RequestId;
        TimeSpan half12 = TimeSpan.FromHours(12);
        string[] expected = [.. requests.Where(r => r.RequestId != left)
            .GroupBy(r => (r.Service, Bucket: r.At.Ticks / half12.Ticks)).Select(static g => $"{g.Key.Service}/{g.Key.Bucket}:{g.Count()}").Order(StringComparer.Ordinal)];
        long[] bytes = new long[2];
        long[] decoded = new long[2];
        foreach (bool synopses in (bool[])[false, true])
        {
            await using VortexDataset dataset = await VortexDataset.OpenAsync(store, options, Ct);
            store.Reset();
            Vorticity.Aggregation question = dataset.Scan<Request>().With(new ScanOptions { UseSynopses = synopses })
                .Where(r => r.RequestId != left).GroupBy(r => (r.Service, r.At.Bucket(half12))).Select(g => (g.Key.Service, g.Key.Item2, g.Count()));
            List<ServiceBucket> answers = await question.As<ServiceBucket>().ToRecordsAsync(Ct).ToListAsync(Ct);
            bytes[synopses ? 1 : 0] = store.BytesRead;
            decoded[synopses ? 1 : 0] = question.Metrics.BlocksDecoded;
            Assert.Equal(expected, answers.Select(a => $"{a.Service}/{a.Bucket.Ticks / half12.Ticks}:{a.Count}").Order(StringComparer.Ordinal));
            if (synopses)
            {
                ScanPlan plan = await dataset.Scan<Request>().Where(r => r.RequestId != left).GroupBy(r => (r.Service, r.At.Bucket(half12)))
                    .Select(g => (g.Key.Service, g.Key.Item2, g.Count())).ExplainAsync(Ct);
                Assert.All(plan.Units, unit => Assert.True(unit.Alternative == "granules", $"{unit.Alternative}: {unit.Detail}; {string.Join("; ", unit.Rejected)}"));
            }
        }

        // The four granules the request may lie in alone are read as rows, a few blocks: the granules where two services meet
        // are answered by their runs. Without them, every such granule read as rows, the granules weighed 5.55 MB and the
        // rows were chosen.
        Console.WriteLine($"PREFIX RUNS: {hosts} hosts a service, every request but one by service and twelve hours, {bytes[1]} bytes and {decoded[1]} blocks decoded through the granules, {bytes[0]} and {decoded[0]} through the rows");
        Assert.True(decoded[1] * 8 < decoded[0], $"{decoded[1]} blocks decoded through the granules, {decoded[0]} through the rows");
        Assert.True(bytes[1] < bytes[0], $"{bytes[1]} bytes through the granules, {bytes[0]} through the rows");

        // The rows of each run of the prefix in rows that follow the key: one value of the service, the host and the hour.
        static IEnumerable<long> Runs(IEnumerable<Request> rows)
        {
            (string, string, long)? open = null;
            long count = 0;
            foreach (Request row in rows)
            {
                (string, string, long) key = (row.Service, row.Host, row.At.Ticks / TimeSpan.TicksPerHour);
                if (open is { } held && held != key)
                {
                    yield return count;
                    count = 0;
                }

                open = key;
                count++;
            }

            if (count > 0)
            {
                yield return count;
            }
        }
    }

    [VortexRecord]
    public partial record struct Request(DateTime At, string Service, string Host, string RequestId);

    [VortexRecord]
    public partial record struct ServiceBucket(string? Service, DateTime Bucket, long Count);
}
