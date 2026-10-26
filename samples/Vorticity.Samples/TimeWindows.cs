using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;

namespace Vorticity.Samples;

/// <summary>A request as a dataset clustered by day holds it.</summary>
[VortexRecord]
public partial record struct Request(DateTimeOffset At, string Service, string Method, short Status, string RequestId, long Bytes);

/// <summary>A service's count and bytes over a window.</summary>
[VortexRecord]
public partial record struct ServiceTotals(string Service, long Count, long Bytes);

/// <summary>A log line, kept for as long as its retention says.</summary>
[VortexRecord]
public partial record struct Logged(string Retention, DateTimeOffset At, string Message);

/// <summary>
/// The time-window part of the datasets page: a dataset whose objects each hold a day, questions that read the rows their
/// window reaches, a request found through its identifier, a window asked day by day newest first, the objects'
/// directories kept on the local disk, and rows of two retentions kept in a dataset each and expired on their own.
/// </summary>
internal static class TimeWindows
{
    private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly string[] Services = ["api", "auth", "billing", "cart", "search"];

    internal static async Task RunAsync()
    {
        await using FileObjectStore files = new FileObjectStore(Demo.Path("time-windows"));
        await using CountingObjectStore store = new CountingObjectStore(files);
        string needle;
        await using (VortexDataset dataset = await VortexDataset.CreateAsync(store, Request.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Request.At)],
            Clustering = new ClusteringOptions { Value = ClusteringValue.Truncate(CalendarUnit.Day) },
            Identifiers = [nameof(Request.RequestId)],
        }))
        {
            // Three days of a request every five seconds, appended ten minutes at a time, then compacted.
            Random random = new Random(7);
            for (int part = 0; part < 3 * 24 * 6; part++)
            {
                Request[] rows = [.. Enumerable.Range(0, 120).Select(i => new Request(
                    Start.AddSeconds((part * 120 + i) * 5),
                    Services[random.Next(Services.Length)],
                    random.Next(4) == 0 ? "POST" : "GET",
                    random.Next(20) == 0 ? (short)500 : (short)200,
                    $"req-{part}-{i}",
                    random.NextInt64(100, 100_000)))];
                await using ObjectDraft draft = dataset.StartObject();
                await draft.Writer.WriteAsync<Request>(rows);
                await dataset.AppendAsync(draft);
            }

            while (await dataset.CompactAsync() is not null)
            {
            }

            needle = "req-200-7";
            Console.WriteLine($"{dataset.RowCount} requests over three days in {dataset.ObjectCount} objects");

            DateTimeOffset from = Start;
            DateTimeOffset to = Start.AddDays(2);
            Aggregation question = dataset.Scan<Request>()
                .Where(r => r.At >= from && r.At < to)
                .GroupBy(r => r.Service).Select(g => (g.Key, g.Count(), g.Sum(r => r.Bytes)));
            List<ServiceTotals> totals = await question.As<ServiceTotals>().ToRecordsAsync().ToListAsync();
            Console.WriteLine(
                $"two days by service: {string.Join(", ", totals.OrderBy(t => t.Service, StringComparer.Ordinal).Select(t => $"{t.Service} {t.Count}"))}; " +
                $"{question.Metrics.Rows} rows read");

            // A window inside a day reads the blocks its instants reach, and no other.
            Aggregation quarter = dataset.Scan<Request>()
                .Where(r => r.At >= to.AddMinutes(-15) && r.At < to)
                .GroupBy(r => r.Service).Select(g => (g.Key, g.Count(), g.Sum(r => r.Bytes)));
            List<ServiceTotals> last = await quarter.As<ServiceTotals>().ToRecordsAsync().ToListAsync();
            Console.WriteLine($"the last quarter of an hour of the second day: {last.Sum(t => t.Count)} requests; blocks decoded {quarter.Metrics.BlocksDecoded}, pruned {quarter.Metrics.BlocksPruned}");

            // One request found through its identifier's hashed postings.
            Scan<Request> lookup = dataset.Scan<Request>().Where(r => r.RequestId == needle);
            List<Request> found = await lookup.ToRecordsAsync().ToListAsync();
            Console.WriteLine($"{needle}: {found.Count} row, at {found[0].At:MM-dd HH:mm:ss}, {lookup.Metrics.BlocksPruned} blocks pruned");

            // The newest day first, then the one before: each part's answer is final for its day.
            await foreach (ValuePart<DateTimeOffset, List<ServiceTotals>> part in dataset.AskByValuesAsync(from, to, async (start, end, ct) =>
                await dataset.Scan<Request>().Where(r => r.At >= start && r.At < end)
                    .GroupBy(r => r.Service).Select(g => (g.Key, g.Count(), g.Sum(r => r.Bytes)))
                    .As<ServiceTotals>().ToRecordsAsync(ct).ToListAsync(ct)))
            {
                Console.WriteLine($"part {part.From:MM-dd} to {part.To:MM-dd}: {part.Answer.Sum(t => t.Count)} requests");
            }
        }

        // A session that keeps on the local disk the directories the objects' tables of contents declare, which a read with
        // no open of an object, such as a lookup in a large one, then finds there.
        await using (VortexSession session = VortexSession.Create(options =>
            options.SynopsisCache = new SynopsisCache(Demo.Path("synopsis-cache"), capacityBytes: 64L << 20)))
        await using (VortexDataset cached = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session }))
        {
            long warmed = await cached.WarmAsync<Request>(r => r.At >= Start);
            List<ServiceTotals> again = await TwoDaysAsync(cached);
            Console.WriteLine($"warmed {warmed} bytes of directories; two days by service again: {again.Count} services");
        }

        await RetentionAsync();
    }

    /// <summary>The count and bytes per service over the first two days.</summary>
    private static async Task<List<ServiceTotals>> TwoDaysAsync(VortexDataset dataset) =>
        await dataset.Scan<Request>().Where(r => r.At >= Start && r.At < Start.AddDays(2))
            .GroupBy(r => r.Service).Select(g => (g.Key, g.Count(), g.Sum(r => r.Bytes))).As<ServiceTotals>().ToRecordsAsync().ToListAsync();

    /// <summary>
    /// Rows kept for different lengths of time: a dataset per retention, each expired on its own, its objects an hour's,
    /// dropped unread once the expiry's bound passes them.
    /// </summary>
    private static async Task RetentionAsync()
    {
        await using IObjectStore debugStore = new FileObjectStore(Demo.Path("retention-debug"));
        await using IObjectStore infoStore = new FileObjectStore(Demo.Path("retention-info"));
        DatasetOptions options = new DatasetOptions
        {
            ClusteringKey = [nameof(Logged.At)],
            Clustering = new ClusteringOptions { Value = ClusteringValue.Truncate(CalendarUnit.Hour) },
        };
        await using VortexDataset debug = await VortexDataset.CreateAsync(debugStore, Logged.Schema, options);
        await using VortexDataset info = await VortexDataset.CreateAsync(infoStore, Logged.Schema, options);
        for (int hour = 0; hour < 3 * 24; hour++)
        {
            Logged[] rows = [.. Enumerable.Range(0, 60).Select(i => new Logged(i % 3 == 0 ? "info" : "debug", Start.AddHours(hour).AddMinutes(i), $"line {hour}-{i}"))];
            await AppendAsync(debug, [.. rows.Where(r => r.Retention == "debug")]);
            await AppendAsync(info, [.. rows.Where(r => r.Retention == "info")]);
        }

        while (await debug.CompactAsync() is not null)
        {
        }

        long before = await debug.Scan<Logged>().CountAsync();
        DateTimeOffset cutoff = Start.AddDays(2);
        RowChangeResult expired = await debug.ExpireAsync<Logged>(r => r.At < cutoff);
        long kept = await debug.Scan<Logged>().CountAsync();
        long untouched = await info.Scan<Logged>().CountAsync();
        Console.WriteLine($"debug lines expired before {cutoff:MM-dd}: {before} then {kept}, {expired.Rows} dropped; info lines untouched: {untouched}");
    }

    /// <summary>Appends <paramref name="rows"/> to <paramref name="dataset"/> as one object.</summary>
    private static async Task AppendAsync(VortexDataset dataset, Logged[] rows)
    {
        await using ObjectDraft draft = dataset.StartObject();
        await draft.Writer.WriteAsync<Logged>(rows);
        await dataset.AppendAsync(draft);
    }
}
