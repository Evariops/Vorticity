using System;
using System.Diagnostics.Tracing;
using System.Runtime.InteropServices;
using System.Threading;

namespace Vorticity.Benchmarks.Queries;

/// <summary>What the process has paid so far that a timer does not show: its page faults.</summary>
internal static class ProcessCounters
{
    /// <summary>
    /// The minor page faults of the process so far: a page touched for the first time, which a
    /// first query pays and the warm rounds hide. -1 where <c>getrusage</c> is not there.
    /// </summary>
    /// <remarks>
    /// <c>ru_minflt</c> follows the two timevals and four longs of <c>struct rusage</c>, at byte 64 on
    /// macOS and Linux alike.
    /// </remarks>
    internal static unsafe long MinorFaults()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return -1;
        }

        byte* usage = stackalloc byte[512];
        return getrusage(0, usage) == 0 ? *(long*)(usage + 64) : -1;
    }

    [DllImport("libc", EntryPoint = "getrusage")]
    private static extern unsafe int getrusage(int who, byte* usage);
}

/// <summary>
/// The bytes the process allocates in the large object heap while it listens, from the runtime's
/// allocation ticks: one each time about 100 KB of a kind of object was allocated, which says its
/// kind and its amount. Listening costs an event a tick, so the bench listens in its memory pass
/// alone, never in a timed round.
/// </summary>
internal sealed class LargeAllocations : EventListener
{
    private long _bytes;
    private volatile bool _listening;

    /// <summary>The large bytes counted since <see cref="Start"/>.</summary>
    internal long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>Counts from now on, from zero.</summary>
    internal void Start()
    {
        Interlocked.Exchange(ref _bytes, 0);
        _listening = true;
    }

    /// <summary>Stops counting, once the ticks already raised have arrived.</summary>
    internal long Stop()
    {
        // The runtime hands its events to listeners on a thread of its own, a moment later.
        Thread.Sleep(200);
        _listening = false;
        return Bytes;
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == "Microsoft-Windows-DotNETRuntime")
        {
            // The GC keyword at the verbose level carries the allocation ticks.
            EnableEvents(eventSource, EventLevel.Verbose, (EventKeywords)0x1);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (!_listening || eventData.EventName is null || !eventData.EventName.StartsWith("GCAllocationTick", StringComparison.Ordinal)
            || eventData.PayloadNames is not { } names || eventData.Payload is not { } payload)
        {
            return;
        }

        int kind = names.IndexOf("AllocationKind");
        int amount = names.IndexOf("AllocationAmount64");
        if (kind >= 0 && amount >= 0 && Convert.ToUInt32(payload[kind], System.Globalization.CultureInfo.InvariantCulture) == 1)
        {
            Interlocked.Add(ref _bytes, (long)Convert.ToUInt64(payload[amount], System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
