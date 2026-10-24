using System.Diagnostics;
using System.Diagnostics.Metrics;
using Vorticity.Scanning;

namespace Vorticity;

/// <summary>
/// The names a listener subscribes to: the <see cref="Meter"/> of the process-wide counters and the
/// <see cref="ActivitySource"/> of one activity per scan and per write.
/// </summary>
/// <remarks>
/// <para>
/// The meter counts <c>vortex.scan.rows</c>, <c>vortex.scan.requests</c>,
/// <c>vortex.scan.bytes_requested</c>, <c>vortex.scan.blocks_decoded</c>,
/// <c>vortex.scan.blocks_pruned</c>, <c>vortex.cache.hits</c>, <c>vortex.cache.misses</c> and
/// <c>vortex.write.bytes</c>, with no tags. A scan's activity carries its plan's figures as tags.
/// Without a listener, neither costs anything but a check; the numbers of one scan are also on its
/// <c>Metrics</c>.
/// </para>
/// <para>
/// The dataset package counts on the same meter <c>vortex.dataset.commits</c>, by operation and
/// outcome, <c>vortex.dataset.commit_attempts</c>, by operation, <c>vortex.dataset.compactions</c>,
/// by trigger and outcome, <c>vortex.dataset.compaction.bytes_read</c> and
/// <c>vortex.dataset.compaction.bytes_written</c>, by trigger, and
/// <c>vortex.dataset.vacuum.objects_deleted</c>. Its activities are <c>vortex.dataset.commit</c>,
/// <c>vortex.dataset.compaction</c> and <c>vortex.dataset.vacuum</c>, which carry the dataset's
/// location as a tag when the handle was opened at one. A location never tags a counter, which would
/// make a series per dataset.
/// </para>
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
    /// <summary>The library's meter, which the dataset package's instruments share.</summary>
    internal static readonly Meter Meter = new Meter(VortexDiagnostics.MeterName);

    internal static readonly ActivitySource Source = new ActivitySource(VortexDiagnostics.ActivitySourceName);

    private static readonly Counter<long> Rows = Meter.CreateCounter<long>("vortex.scan.rows", "{row}", "Rows delivered by scans.");
    private static readonly Counter<long> Requests = Meter.CreateCounter<long>("vortex.scan.requests", "{request}", "Segment requests made by scans.");
    private static readonly Counter<long> BytesRequested = Meter.CreateCounter<long>("vortex.scan.bytes_requested", "By", "Bytes requested by scans.");
    private static readonly Counter<long> BlocksDecoded = Meter.CreateCounter<long>("vortex.scan.blocks_decoded", "{block}", "Blocks decoded by scans.");
    private static readonly Counter<long> BlocksPruned = Meter.CreateCounter<long>("vortex.scan.blocks_pruned", "{block}", "Blocks skipped by statistics, zone maps or indexes.");
    private static readonly Counter<long> CacheHits = Meter.CreateCounter<long>("vortex.cache.hits", "{segment}", "Segments served by a session's cache.");
    private static readonly Counter<long> CacheMisses = Meter.CreateCounter<long>("vortex.cache.misses", "{segment}", "Segments a session's cache did not hold.");
    private static readonly Counter<long> WriteBytes = Meter.CreateCounter<long>("vortex.write.bytes", "By", "Bytes written by writers.");
    private static readonly Counter<long> SynopsisHits = Meter.CreateCounter<long>("vortex.synopsis_cache.hits", "{region}", "Regions a synopsis cache served from the local disk.");
    private static readonly Counter<long> SynopsisMisses = Meter.CreateCounter<long>("vortex.synopsis_cache.misses", "{region}", "Regions a synopsis cache did not hold.");
    private static readonly Counter<long> SynopsisPeerHits = Meter.CreateCounter<long>("vortex.synopsis_cache.peer_hits", "{region}", "Regions a synopsis cache served from the directory of another cache of its shared root, counted among its hits.");
    private static readonly Counter<long> SynopsisDiskBytes = Meter.CreateCounter<long>("vortex.synopsis_cache.bytes_from_disk", "By", "Bytes a synopsis cache served from the local disk.");
    private static readonly Counter<long> SynopsisStoreBytes = Meter.CreateCounter<long>("vortex.synopsis_cache.bytes_from_store", "By", "Bytes of regions a synopsis cache was handed from the store.");
    private static readonly Counter<long> SynopsisEvictions = Meter.CreateCounter<long>("vortex.synopsis_cache.evictions", "{region}", "Regions a synopsis cache dropped past its capacity.");
    private static readonly Counter<long> SynopsisRefused = Meter.CreateCounter<long>("vortex.synopsis_cache.refused", "{region}", "Regions a synopsis cache refused, larger than its capacity.");
    private static readonly Counter<long> SynopsisCorrupt = Meter.CreateCounter<long>("vortex.synopsis_cache.corrupt", "{region}", "Regions whose file on the disk failed its checksum, dropped and read again.");
    private static readonly UpDownCounter<long> SynopsisHeld = Meter.CreateUpDownCounter<long>("vortex.synopsis_cache.held", "By", "Bytes the synopsis caches hold on the local disk.");

    /// <summary>Starts the activity of one scan, or returns null when nothing listens.</summary>
    /// <remarks>
    /// The activity is a child of whatever the caller had current, and the caller keeps that one
    /// current: a scan starts where its enumerator is built and ends inside an awaited step, whose
    /// execution context does not flow back, so an activity left current would outlive its stop on
    /// the caller's side and parent every span the caller opened after the scan.
    /// </remarks>
    internal static Activity? StartScan(string kind) => Start("vortex.scan." + kind);

    private static Activity? Start(string name)
    {
        Activity? ambient = Activity.Current;
        Activity? started = Source.StartActivity(name, ActivityKind.Internal);
        if (started is not null)
        {
            Activity.Current = ambient;
        }

        return started;
    }

    /// <summary>Adds what a finished scan did to the counters, and to its activity.</summary>
    internal static void ScanEnded(ScanCounters metrics, Activity? activity)
    {
        // Each instrument on its own: a listener may enable some and not others, and an Add to an
        // instrument nobody listens to returns at once.
        Rows.Add(metrics.Rows);
        Requests.Add(metrics.SegmentRequests);
        BytesRequested.Add(metrics.BytesRequested);
        BlocksDecoded.Add(metrics.BlocksDecoded);
        BlocksPruned.Add(metrics.BlocksPruned);

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

    /// <summary>A region a synopsis cache served from the disk.</summary>
    internal static void SynopsisHit(long bytes)
    {
        SynopsisHits.Add(1);
        SynopsisDiskBytes.Add(bytes);
    }

    internal static void SynopsisMiss() => SynopsisMisses.Add(1);

    /// <summary>A region a synopsis cache read from the directory of another cache of its shared root.</summary>
    internal static void SynopsisPeerHit() => SynopsisPeerHits.Add(1);

    /// <summary>A region the store sent a synopsis cache.</summary>
    internal static void SynopsisFetched(long bytes) => SynopsisStoreBytes.Add(bytes);

    internal static void SynopsisEvicted() => SynopsisEvictions.Add(1);

    internal static void SynopsisRefusal() => SynopsisRefused.Add(1);

    internal static void SynopsisCorruption() => SynopsisCorrupt.Add(1);

    /// <summary>Bytes a synopsis cache came to hold, or no longer holds when negative.</summary>
    internal static void SynopsisHolds(long bytes) => SynopsisHeld.Add(bytes);

    /// <summary>Starts the activity of one write, or returns null when nothing listens.</summary>
    internal static Activity? StartWrite() => Start("vortex.write");

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
