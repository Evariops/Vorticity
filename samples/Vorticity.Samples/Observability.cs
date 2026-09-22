using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.IO;

namespace Vorticity.Samples;

internal static class Observability
{
    internal static async Task RunAsync()
    {
        string path = await Demo.ReadingsAsync();
        ConcurrentDictionary<string, long> totals = new(StringComparer.Ordinal);
        using MeterListener meters = new MeterListener();
        meters.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == VortexDiagnostics.MeterName)
            {
                listener.EnableMeasurementEvents(instrument);
                Console.WriteLine($"instrument {instrument.Name} ({instrument.Unit}): {instrument.Description}");
            }
        };
        meters.SetMeasurementEventCallback<long>((instrument, value, tags, state) => totals.AddOrUpdate(instrument.Name, value, (_, total) => total + value));
        meters.Start();

        List<string> finished = [];
        using ActivityListener activities = new ActivityListener
        {
            ShouldListenTo = source => source.Name == VortexDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => finished.Add(
                $"{activity.OperationName} [{activity.Status}]: {string.Join(", ", activity.TagObjects.Select(tag => $"{tag.Key}={tag.Value}"))}"),
        };
        ActivitySource.AddActivityListener(activities);

        await using VortexSession session = VortexSession.Create(o => o.SegmentCache = new SegmentCache(64L * 1024 * 1024));
        await using (VortexFile file = await session.OpenAsync(new FileSegmentSource(path)))
        {
            for (int run = 1; run <= 2; run++)
            {
                Scan<Reading> scan = file.Scan<Reading>().Where(r => r.Day >= 900);
                long rows = 0;
                await foreach (Columns<Reading> batch in scan)
                {
                    rows += batch.RowCount;
                }

                ScanStatistics stats = scan.Statistics;
                Console.WriteLine($"scan {run}: {stats.Rows} rows in {stats.Batches} batches, {stats.Requests} requests, " +
                    $"{stats.BytesRequested} bytes, {stats.BlocksDecoded} blocks decoded, {stats.BlocksPruned} pruned, {stats.CacheHits} cache hits");
            }

            Scan untyped = file.Scan("City").Where($"City = {"Paris"}");
            long paris = await untyped.CountAsync();
            Console.WriteLine($"tool scan: {paris} rows, {untyped.Statistics.Requests} requests, {untyped.Statistics.BlocksDecoded} blocks decoded");
        }

        Console.WriteLine($"cache: {session.Options.SegmentCache!.Hits} hits, {session.Options.SegmentCache.Misses} misses, {session.Options.SegmentCache.Size} bytes held");

        await using (VortexFileWriter writer = session.CreateWriter<Reading>(Demo.Path("observed.vortex")))
        {
            await writer.WriteAsync<Reading>([new Reading(1, 21.5, "Paris"), new Reading(2, null, "Lyon")]);
            await writer.CompleteAsync();
        }

        await using (VortexFileWriter abandoned = session.CreateWriter<Reading>(Demo.Path("abandoned.vortex")))
        {
            await abandoned.WriteAsync<Reading>([new Reading(1, 21.5, "Paris")]);
            abandoned.Abandon();
        }

        foreach (string activity in finished)
        {
            Console.WriteLine($"activity {activity}");
        }

        foreach ((string name, long total) in totals.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"total {name} = {total}");
        }
    }
}
