using System.Diagnostics;
using System.Diagnostics.Metrics;
using Vorticity.Scanning;

namespace Vorticity;

/// <summary>
/// The names a listener subscribes to: the <see cref="Meter"/> of the process-wide counters and the
/// <see cref="ActivitySource"/> of one activity per scan and per write.
/// </summary>
/// <remarks>
/// The meter counts <c>vortex.scan.rows</c>, <c>vortex.scan.requests</c>,
/// <c>vortex.scan.bytes_requested</c>, <c>vortex.scan.blocks_decoded</c>,
/// <c>vortex.scan.blocks_pruned</c>, <c>vortex.cache.hits</c>, <c>vortex.cache.misses</c> and
/// <c>vortex.write.bytes</c>, with no tags. A scan's activity carries its plan's figures as tags.
/// Without a listener, neither costs anything but a check; the numbers of one scan are also on its
/// <c>Statistics</c>.
/// </remarks>
public static class VortexDiagnostics
{
    /// <summary>The name of the library's <see cref="Meter"/>.</summary>
    public const string MeterName = "Vorticity";

    /// <summary>The name of the library's <see cref="ActivitySource"/>.</summary>
    public const string ActivitySourceName = "Vorticity";
}

/// <summary>The instruments behind <see cref="VortexDiagnostics"/>.</summary>
internal static class VortexTelemetry
{
    private static readonly Meter Meter = new Meter(VortexDiagnostics.MeterName);

    internal static readonly ActivitySource Source = new ActivitySource(VortexDiagnostics.ActivitySourceName);

    private static readonly Counter<long> Rows = Meter.CreateCounter<long>("vortex.scan.rows", "{row}", "Rows delivered by scans.");
    private static readonly Counter<long> Requests = Meter.CreateCounter<long>("vortex.scan.requests", "{request}", "Segment requests made by scans.");
    private static readonly Counter<long> BytesRequested = Meter.CreateCounter<long>("vortex.scan.bytes_requested", "By", "Bytes requested by scans.");
    private static readonly Counter<long> BlocksDecoded = Meter.CreateCounter<long>("vortex.scan.blocks_decoded", "{block}", "Blocks decoded by scans.");
    private static readonly Counter<long> BlocksPruned = Meter.CreateCounter<long>("vortex.scan.blocks_pruned", "{block}", "Blocks skipped by statistics, zone maps or indexes.");
    private static readonly Counter<long> CacheHits = Meter.CreateCounter<long>("vortex.cache.hits", "{segment}", "Segments served by a session's cache.");
    private static readonly Counter<long> CacheMisses = Meter.CreateCounter<long>("vortex.cache.misses", "{segment}", "Segments a session's cache did not hold.");
    private static readonly Counter<long> WriteBytes = Meter.CreateCounter<long>("vortex.write.bytes", "By", "Bytes written by writers.");

    /// <summary>Starts the activity of one scan, or returns null when nothing listens.</summary>
    internal static Activity? StartScan(string kind) => Source.StartActivity("vortex.scan." + kind, ActivityKind.Internal);

    /// <summary>Adds what a finished scan did to the counters, and to its activity.</summary>
    internal static void ScanEnded(ScanMetrics metrics, Activity? activity)
    {
        if (Rows.Enabled)
        {
            Rows.Add(metrics.Rows);
            Requests.Add(metrics.SegmentRequests);
            BytesRequested.Add(metrics.BytesRequested);
            BlocksDecoded.Add(metrics.BlocksDecoded);
            BlocksPruned.Add(metrics.BlocksPruned);
        }

        if (activity is not null)
        {
            activity.SetTag("vortex.rows", metrics.Rows);
            activity.SetTag("vortex.batches", metrics.Batches);
            activity.SetTag("vortex.requests", metrics.SegmentRequests);
            activity.SetTag("vortex.bytes_requested", metrics.BytesRequested);
            activity.SetTag("vortex.blocks_decoded", metrics.BlocksDecoded);
            activity.SetTag("vortex.blocks_pruned", metrics.BlocksPruned);
            activity.Dispose();
        }
    }

    internal static void CacheHit() => CacheHits.Add(1);

    internal static void CacheMiss() => CacheMisses.Add(1);

    internal static void Written(long bytes) => WriteBytes.Add(bytes);

    /// <summary>Starts the activity of one write, or returns null when nothing listens.</summary>
    internal static Activity? StartWrite() => Source.StartActivity("vortex.write", ActivityKind.Internal);

    /// <summary>Tags and ends the activity of a write that completed its file or gave it up.</summary>
    internal static void WriteEnded(Activity? activity, long rows, long bytes, bool completed)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag("vortex.rows", rows);
        activity.SetTag("vortex.bytes", bytes);
        activity.SetTag("vortex.completed", completed);
        if (!completed)
        {
            activity.SetStatus(ActivityStatusCode.Error, "abandoned");
        }

        activity.Dispose();
    }
}
