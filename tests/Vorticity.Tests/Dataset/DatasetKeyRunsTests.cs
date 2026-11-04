// The values a component of the sort key takes in each granule (the query path plan, G2): a component that is a function of a
// column, the hour of an instant, counted granule by granule as the rows come in the key's order; a question keyed by a
// coarser function of the same column, a bucket of twelve hours, takes a granule's groups from them where the granule
// straddles two buckets, with no read of the instants.
//
// THE ORACLE IS LINQ AND THE ROWS: every count is LINQ's; the values of each granule are those its rows hold.
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

public sealed partial class DatasetKeyRunsTests
{
    private static readonly DateTime Start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// 400 000 requests of a day, two services of six hosts each, sorted by the service, the host, the hour, then the
    /// instant, the first three indexed, in two appends consolidated: a series of a host's 33 000 requests, 1 400 an hour,
    /// lies in five granules or so, those around noon and where two series meet holding two buckets of twelve hours. The
    /// object keeps the hours each granule holds, those its rows hold; every request but one, per service and per twelve
    /// hours, is answered by the granules, the straddling ones by their hours, the four the request may lie in read as rows,
    /// its counts LINQ's.
    /// </summary>
    [Fact]
    public async Task ABucketOfHoursIsAnsweredByTheHoursEachGranuleHolds()
    {
        Random random = new Random(83);
        Request[] requests = [.. Enumerable.Range(0, 400_000).Select(i =>
        {
            int host = random.Next(12);
            return new Request(Start.AddTicks(random.NextInt64(TimeSpan.TicksPerDay)), $"svc-{host % 2}", $"host-{host:D2}", $"id-{i:D7}");
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

            // The hours each granule holds, and their rows, are those its rows hold.
            await foreach (PositionedObject held in writer.ScanBuilder().ObjectsAsync(Ct))
            {
                Assert.True(held.Entry.Contents!.KeyRunsOf(2) is not null, "the object keeps no hours by granule");
                (_, _, GranuleKeyRuns?[] runs) = await writer.ReadGranuleStatsAsync(held.Entry, [], [], [2], Ct);
                GranuleKeyRuns hours = runs[0]!;
                List<Request> rows = await writer.Scan<Request>().ToRecordsAsync(Ct).Skip((int)held.FirstRow).Take((int)held.Entry.Rows).ToListAsync(Ct);
                for (int g = 0; g < hours.Count; g++)
                {
                    (_, ReadOnlyMemory<long> counted) = hours.Of(g);
                    long[] truth = [.. rows.Skip(g * 8192).Take(8192).GroupBy(static r => r.At.Ticks / TimeSpan.TicksPerHour).Select(static h => (long)h.Count()).Order()];
                    Assert.Equal(truth, counted.ToArray().Order());
                }
            }
        }

        string left = requests[123_456].RequestId;
        TimeSpan half12 = TimeSpan.FromHours(12);
        string[] expected = [.. requests.Where(r => r.RequestId != left)
            .GroupBy(r => (r.Service, Bucket: r.At.Ticks / half12.Ticks)).Select(static g => $"{g.Key.Service}/{g.Key.Bucket}:{g.Count()}").Order(StringComparer.Ordinal)];
        long[] bytes = new long[2];
        foreach (bool synopses in (bool[])[false, true])
        {
            await using VortexDataset dataset = await VortexDataset.OpenAsync(store, options, Ct);
            store.Reset();
            Vorticity.Aggregation question = dataset.Scan<Request>().With(new ScanOptions { UseSynopses = synopses })
                .Where(r => r.RequestId != left).GroupBy(r => (r.Service, r.At.Bucket(half12))).Select(g => (g.Key.Service, g.Key.Item2, g.Count()));
            List<ServiceBucket> answers = await question.As<ServiceBucket>().ToRecordsAsync(Ct).ToListAsync(Ct);
            bytes[synopses ? 1 : 0] = store.BytesRead;
            Assert.Equal(expected, answers.Select(a => $"{a.Service}/{a.Bucket.Ticks / half12.Ticks}:{a.Count}").Order(StringComparer.Ordinal));
            if (synopses)
            {
                ScanPlan plan = await dataset.Scan<Request>().Where(r => r.RequestId != left).GroupBy(r => (r.Service, r.At.Bucket(half12)))
                    .Select(g => (g.Key.Service, g.Key.Item2, g.Count())).ExplainAsync(Ct);
                Assert.All(plan.Units, unit => Assert.True(unit.Alternative == "granules", $"{unit.Alternative}: {unit.Detail}; {string.Join("; ", unit.Rejected)}"));
            }
        }

        Console.WriteLine($"KEY RUNS: every request but one by service and twelve hours, {bytes[1]} bytes through the granules, {bytes[0]} through the rows");
        Assert.True(bytes[1] * 2 < bytes[0], $"{bytes[1]} bytes through the granules, {bytes[0]} through the rows");
    }

    [VortexRecord]
    public partial record struct Request(DateTime At, string Service, string Host, string RequestId);

    [VortexRecord]
    public partial record struct ServiceBucket(string? Service, DateTime Bucket, long Count);
}
