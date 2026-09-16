// The `Vorticity` EventSource - docs/09-contracts.md §5, and the counters docs/12-index-reads.md
// §8.1 adds: whether an index earns its bytes, in production.
//
// ZERO-COST WITHOUT A LISTENER. Every hook is one `IsEnabled` test on a static; the counters exist
// only once a listener has asked for them, and the running totals are plain `long`s added with
// `Interlocked`. What `ScanMetrics` counts for one scan, this counts for the process.
using System;
using System.Diagnostics.Tracing;
using System.Threading;

namespace Vorticity.Diagnostics;

/// <summary>The library's counters, published as <c>EventCounters</c> under the name <c>Vorticity</c>.</summary>
[EventSource(Name = "Vorticity")]
internal sealed class VortexEventSource : EventSource
{
    /// <summary>The one instance.</summary>
    internal static readonly VortexEventSource Log = new VortexEventSource();

    private long _segmentsRequested;
    private long _bytesRequested;
    private long _zonesPruned;
    private long _zonesTotal;
    private long _indexRunsRead;
    private long _cursorSeeks;
    private long _cursorSteps;
    private long _countBlocksProven;
    private long _countBlocksDecoded;
    private long _keyOrderWindows;
    private long _keyOrderWindowSplits;

    private DiagnosticCounter[]? _counters;

    private VortexEventSource()
        : base(EventSourceSettings.EtwSelfDescribingEventFormat)
    {
    }

    /// <summary>Whether a listener is attached: the one test a hook makes.</summary>
    internal static bool On => Log.IsEnabled();

    internal long SegmentsRequested => Interlocked.Read(ref _segmentsRequested);

    internal long IndexRunsRead => Interlocked.Read(ref _indexRunsRead);

    internal long CursorSeeks => Interlocked.Read(ref _cursorSeeks);

    internal long CursorSteps => Interlocked.Read(ref _cursorSteps);

    internal long CountBlocksProven => Interlocked.Read(ref _countBlocksProven);

    internal long CountBlocksDecoded => Interlocked.Read(ref _countBlocksDecoded);

    internal long ZonesPruned => Interlocked.Read(ref _zonesPruned);

    internal long ZonesTotal => Interlocked.Read(ref _zonesTotal);

    internal long KeyOrderWindows => Interlocked.Read(ref _keyOrderWindows);

    /// <summary>A split registered its segments: <c>segments-requested</c>, <c>bytes-requested</c>.</summary>
    internal static void Requested(long segments, long bytes)
    {
        if (On)
        {
            Interlocked.Add(ref Log._segmentsRequested, segments);
            Interlocked.Add(ref Log._bytesRequested, bytes);
        }
    }

    /// <summary>A pruning pass refined a mask: <c>zones-pruned</c> of <c>zones-total</c>.</summary>
    internal static void Pruned(long pruned, long total)
    {
        if (On)
        {
            Interlocked.Add(ref Log._zonesPruned, pruned);
            Interlocked.Add(ref Log._zonesTotal, total);
        }
    }

    /// <summary>Index payload segments read: <c>index-runs-read</c>.</summary>
    internal static void RunsRead(long segments)
    {
        if (On)
        {
            Interlocked.Add(ref Log._indexRunsRead, segments);
        }
    }

    /// <summary>A cursor positioned by key or rank: <c>cursor-seeks</c>.</summary>
    internal static void Seek()
    {
        if (On)
        {
            Interlocked.Increment(ref Log._cursorSeeks);
        }
    }

    /// <summary>A cursor stepped: <c>cursor-steps</c>.</summary>
    internal static void Step()
    {
        if (On)
        {
            Interlocked.Increment(ref Log._cursorSteps);
        }
    }

    /// <summary>A count answered blocks from the zone maps, or decoded them.</summary>
    internal static void Counted(long proven, long decoded)
    {
        if (On)
        {
            Interlocked.Add(ref Log._countBlocksProven, proven);
            Interlocked.Add(ref Log._countBlocksDecoded, decoded);
        }
    }

    /// <summary>A key-ordered window read its splits: <c>key-order-windows</c>, <c>key-order-window-splits</c>.</summary>
    internal static void Window(int splits)
    {
        if (On)
        {
            Interlocked.Increment(ref Log._keyOrderWindows);
            Interlocked.Add(ref Log._keyOrderWindowSplits, splits);
        }
    }

    /// <inheritdoc/>
    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command != EventCommand.Enable || _counters is not null)
        {
            return;
        }

        _counters =
        [
            Counter("segments-requested", "Segments requested", () => SegmentsRequested),
            Counter("bytes-requested", "Bytes requested", () => Interlocked.Read(ref _bytesRequested)),
            Counter("zones-pruned", "Zones pruned", () => ZonesPruned),
            Counter("zones-total", "Zones considered", () => ZonesTotal),
            Counter("index-runs-read", "Index run segments read", () => IndexRunsRead),
            Counter("cursor-seeks", "Cursor seeks", () => CursorSeeks),
            Counter("cursor-steps", "Cursor steps", () => CursorSteps),
            Counter("count-blocks-proven", "Count blocks proven", () => CountBlocksProven),
            Counter("count-blocks-decoded", "Count blocks decoded", () => CountBlocksDecoded),
            Counter("key-order-windows", "Key-ordered windows", () => KeyOrderWindows),
            Counter("key-order-window-splits", "Splits read by key-ordered windows", () => Interlocked.Read(ref _keyOrderWindowSplits)),
        ];
    }

    private IncrementingPollingCounter Counter(string name, string display, Func<long> value) =>
        new IncrementingPollingCounter(name, this, () => value()) { DisplayName = display };

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing && _counters is not null)
        {
            foreach (DiagnosticCounter counter in _counters)
            {
                counter.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}
