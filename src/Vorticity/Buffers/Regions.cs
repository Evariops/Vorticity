using System;
using System.Collections.Generic;
using System.Numerics;

namespace Vorticity.Buffers;

/// <summary>
/// The regions of the collector's pinned heap, which the library's pinned arrays are sized to fill: a slab, an outsized
/// block. The collector gives a pinned array a region of its own when no free space and no end of a region of its heap
/// holds it, and takes the region back whole once nothing lives in it; an array that leaves room in its region shares it
/// with the next pinned arrays of that heap, which then keep it alive after the array is gone, a hole no collection
/// closes, since the pinned heap is never compacted.
/// </summary>
/// <remarks>
/// <para>
/// The rule, read in the collector's sources (<c>gc_heap::get_uoh_seg_size</c>, .NET 11): an array whose object takes
/// <c>S</c> bytes opens a region of <c>((S + 2 × 24 + page + R) / R) × R</c> bytes, a multiple of a large region <c>R</c>.
/// So an array of <c>k × R − page − 80</c> bytes opens a region of exactly <c>k × R</c>, and leaves at its end a page and
/// 16 bytes, which no array of a page or more fits. Measured on macOS arm64, under the workstation collector and the
/// server one: an array of 32 MiB less 16,464 bytes took a region of 32 MiB, eight bytes more a region of 64 MiB that the
/// next array of the same size shared (2026-11-25).
/// </para>
/// <para>
/// The first region of a heap's pinned heap is the collector's from its start, and holds the runtime's own pinned objects,
/// a few kilobytes: the first slab of a process may lie there beside them, measured under all three collectors, and leaves
/// a hole of its size once gone, which the next slab of that heap fills exactly. One region at most, which no churn grows.
/// </para>
/// <para>
/// A large region is eight basic ones. The collector picks the basic region at start, 4, 2 or 1 MiB, from the range it
/// reserves and the number of its heaps, unless the host sets it (<c>GCRegionSize</c>): it reports the range and the heaps,
/// and this class works the size out the same way.
/// </para>
/// </remarks>
internal static class Regions
{
    /// <summary>A large region, in basic ones: the collector's constant.</summary>
    private const int LargeFactor = 8;

    /// <summary>
    /// The basic regions the collector wants a heap to hold at start: one per young generation and one more, and a large
    /// region of each of the large and pinned heaps (<c>min_regions_per_heap</c>).
    /// </summary>
    private const int RegionsPerHeap = 3 + 1 + (2 * LargeFactor);

    /// <summary>The bytes of an object the rule counts past the array's, its header and twice the least object, rounded up to eight.</summary>
    private const int FitBytes = 80;

    /// <summary>The large region when the collector reports nothing to work it out from: its default.</summary>
    private const int DefaultLargeBytes = 32 << 20;

    /// <summary>The bytes of a large region of the collector's heaps: 32 MiB unless its heap is small for its number of heaps.</summary>
    internal static int LargeBytes { get; } = ReadLargeBytes();

    /// <summary>The bytes of a pinned array alone in a region of <paramref name="span"/> bytes, a multiple of <see cref="LargeBytes"/>: the region filled but for a page.</summary>
    internal static int ArrayBytes(long span) => checked((int)(span - Environment.SystemPageSize - FitBytes));

    /// <summary>The region a pinned array of at least <paramref name="bytes"/> bytes takes alone: the least multiple of <see cref="LargeBytes"/> whose array holds them.</summary>
    internal static long SpanFor(long bytes)
    {
        long region = LargeBytes;
        long regions = (bytes + Environment.SystemPageSize + FitBytes + region - 1) / region;
        return Math.Max(1, regions) * region;
    }

    /// <summary>The large region, worked out from what the collector reports, as it worked it out at start.</summary>
    private static int ReadLargeBytes()
    {
        IReadOnlyDictionary<string, object> settings = GC.GetConfigurationVariables();
        long basic = Number(settings, "GCRegionSize");
        if (basic <= 0)
        {
            long range = Number(settings, "GCRegionRange");
            long heaps = Math.Max(1, Number(settings, "HeapCount"));
            if (range <= 0)
            {
                return DefaultLargeBytes;
            }

            long most = range / 2 / heaps / RegionsPerHeap;
            basic = most >= 4 << 20 ? 4 << 20 : most >= 2 << 20 ? 2 << 20 : 1 << 20;
        }

        long large = basic * LargeFactor;
        return BitOperations.IsPow2(large) && large is >= 1 << 20 and <= 1 << 30 ? (int)large : DefaultLargeBytes;
    }

    /// <summary>The setting <paramref name="name"/> as a number, 0 when the collector reports none.</summary>
    private static long Number(IReadOnlyDictionary<string, object> settings, string name) =>
        settings.TryGetValue(name, out object? value) ? value switch
        {
            long number => number,
            ulong number => (long)Math.Min(number, long.MaxValue),
            int number => number,
            uint number => number,
            _ => 0,
        } : 0;
}
