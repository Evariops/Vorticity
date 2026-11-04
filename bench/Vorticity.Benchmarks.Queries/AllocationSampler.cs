using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Linq;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// The bytes the process allocates, by type, as the runtime's allocation ticks sample them: a tick about every 100 KB
/// allocated, naming the type of the object that crossed it and the bytes since the one before. Over a few rounds of a
/// question that allocates megabytes, what each type takes of them; a repeated call of a few hundred bytes aliases with
/// the ticks, which the runtime's randomized samples would not.
/// </summary>
internal sealed class AllocationSampler : EventListener
{
    // Made before the listener's own constructor runs, which the runtime's event source calls back into.
    private ConcurrentDictionary<string, long>? _bytes;

    internal AllocationSampler() => _bytes ??= new ConcurrentDictionary<string, long>(StringComparer.Ordinal);

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == "Microsoft-Windows-DotNETRuntime")
        {
            // The GC keyword at verbose: GCAllocationTick.
            EnableEvents(eventSource, EventLevel.Verbose, (EventKeywords)0x1);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (eventData.EventName?.StartsWith("GCAllocationTick", StringComparison.Ordinal) != true || eventData.PayloadNames is not { } names
            || eventData.Payload is not { } payload)
        {
            return;
        }

        int type = names.IndexOf("TypeName");
        int amount = names.IndexOf("AllocationAmount64");
        if (type < 0 || amount < 0)
        {
            return;
        }

        string name = payload[type] as string ?? "?";
        long bytes = Convert.ToInt64(payload[amount], CultureInfo.InvariantCulture);
        (_bytes ??= new ConcurrentDictionary<string, long>(StringComparer.Ordinal)).AddOrUpdate(name, bytes, (_, total) => total + bytes);
    }

    /// <summary>The types sampled, the most bytes first, each with its bytes over <paramref name="asks"/> asks: the first <paramref name="top"/>.</summary>
    internal IEnumerable<string> Lines(int asks, int top)
    {
        Dictionary<string, long> bytes = new Dictionary<string, long>(_bytes!, StringComparer.Ordinal);
        long total = bytes.Values.Sum();
        yield return string.Create(CultureInfo.InvariantCulture, $"    allocated: {total / 1e6 / asks:F2} MB an ask, sampled");
        foreach ((string type, long held) in bytes.OrderByDescending(pair => pair.Value).Take(top))
        {
            yield return string.Create(CultureInfo.InvariantCulture, $"      {held / 1e6 / asks,8:F3} MB  {held * 100.0 / total,5:F1} %  {type}");
        }
    }
}
