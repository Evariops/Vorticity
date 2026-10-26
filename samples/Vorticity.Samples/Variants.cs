using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;

namespace Vorticity.Samples;

/// <summary>An event whose attributes and body are variants: keys that are data.</summary>
[VortexRecord]
public partial record struct Event(DateTimeOffset At, Variant Attributes, Variant? Body);

/// <summary>A log row of a dataset, its attributes a variant its objects shred.</summary>
[VortexRecord]
public partial record struct LogRow(DateTimeOffset At, string Service, Variant Attributes);

/// <summary>A route and the rows that hold it.</summary>
[VortexRecord]
public partial record struct RouteCount(string? Route, long Count);

/// <summary>
/// Variant columns: a value walked by key, read as stored or converted, and, in a dataset clustered by day, the keys
/// its objects shred asked as columns are, from their typed columns.
/// </summary>
internal static class Variants
{
    private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    internal static async Task RunAsync()
    {
        Event e = new(
            DateTimeOffset.UtcNow,
            Variant.Object(("http.route", "/orders/{id}"), ("http.response.status_code", 502L)),
            Variant.FromJson("""{"message": "upstream timed out", "retries": [1, 2]}"""));

        long? status = e.Attributes["http.response.status_code"]?.AsInt64();   // 502
        string? text = e.Body?["message"]?.AsString();                         // "upstream timed out"
        Console.WriteLine($"status {status}, message \"{text}\", as text {e.Attributes["http.response.status_code"]?.AsString() ?? "null"}");

        await using IObjectStore store = new FileObjectStore(Demo.Path("variants"));
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, LogRow.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(LogRow.At)],
            Clustering = new ClusteringOptions { Value = ClusteringValue.Truncate(CalendarUnit.Day) },
        });

        // Three days of a line every five seconds from three routes, one in ten an error, appended ten minutes at a time.
        Random random = new Random(11);
        for (int part = 0; part < 3 * 24 * 6; part++)
        {
            LogRow[] rows = [.. Enumerable.Range(0, 120).Select(i => new LogRow(
                Start.AddSeconds((part * 120 + i) * 5),
                $"web-{random.Next(4)}",
                Variant.Object(
                    ("http.route", $"/orders/{random.Next(3)}"),
                    ("http.response.status_code", random.Next(10) == 0 ? 502L : 200L),
                    ("user.id", $"u-{random.Next(10_000)}"))))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<LogRow>(rows);
            await dataset.AppendAsync(draft);
        }

        while (await dataset.CompactAsync() is not null)
        {
        }

        DateTimeOffset to = Start.AddDays(2);
        var routes = dataset.Scan<LogRow>()
            .Where(r => r.At < to && r.Attributes["http.response.status_code"].AsInt64() >= 500)
            .GroupBy(r => r.Attributes["http.route"].AsString())
            .Select(g => (g.Key, g.Count()));
        List<RouteCount> counts = await routes.As<RouteCount>().ToRecordsAsync().ToListAsync();
        Console.WriteLine(
            $"errors by route over two days: " +
            $"{string.Join(", ", counts.OrderBy(c => c.Route, StringComparer.Ordinal).Select(c => $"{c.Route} {c.Count}"))}; " +
            $"{routes.Metrics.Rows} rows read");

        // The same kind of question as text, as a tool that learns its columns at run time asks it.
        Scan perService = dataset.Scan()
            .Where($"Attributes['http.response.status_code'] >= {500}")
            .GroupBy("Service")
            .Select("Service", "count()");
        long services = 0;
        long errors = 0;
        await foreach (BatchView groups in perService)
        {
            Column<long> counted = groups.Column<long>("count()");
            for (int row = 0; row < groups.RowCount; row++)
            {
                services++;
                errors += counted[row];
            }
        }

        Console.WriteLine($"errors by service, asked as text: {services} services, {errors} errors");
    }
}
