using System;
using System.Collections.Generic;
using Vorticity.Indexes;

namespace Vorticity.Dataset;

/// <summary>The calendar units a time slice spans, in UTC, from the finest.</summary>
internal enum SliceWidth : byte
{
    Second,
    Minute,
    Hour,
    Day,
    Month,
}

/// <summary>
/// Cuts rows sorted by their instant into time slices sized by their volume: the coarsest calendar
/// unit whose slices hold no more than a target number of rows on average, and, inside it, any unit a
/// burst fills past twice the target cut again into finer units, so that every slice stays near its
/// target.
/// </summary>
/// <remarks>
/// A slice is the finest grain of time a question reads without reading rows, and the most rows an
/// edge of a window reads, so its target weighs the edges' cost against the runs a smaller slice adds.
/// That constant is measured once the runs exist; until then <see cref="DefaultTargetRows"/> is the
/// one the design's example gives, a minute of 5 × 10⁴ rows a second.
/// </remarks>
internal static class TimeSlicer
{
    /// <summary>The rows a slice aims at until the cost of edges against runs is measured: a minute of the design's example.</summary>
    internal const long DefaultTargetRows = 3_000_000;

    /// <summary>A unit past which a burst is cut again: twice the target.</summary>
    private const long BurstFactor = 2;

    /// <summary>
    /// The slices of rows whose instants, counted in <paramref name="unit"/>, are
    /// <paramref name="instants"/>, in row order.
    /// </summary>
    /// <exception cref="ArgumentException">The instants are not in ascending order.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The target is not positive.</exception>
    internal static List<TimeSlice> Cut(ReadOnlySpan<long> instants, TimeUnit unit, long targetRows = DefaultTargetRows)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetRows);
        for (int i = 1; i < instants.Length; i++)
        {
            if (instants[i] < instants[i - 1])
            {
                throw new ArgumentException($"Row {i} comes before row {i - 1} in time: slices cut rows sorted by their instant.", nameof(instants));
            }
        }

        List<TimeSlice> slices = [];
        if (!instants.IsEmpty)
        {
            Cut(instants, 0, unit, WidthFor(instants, unit, targetRows), targetRows, slices);
        }

        return slices;
    }

    /// <summary>
    /// The coarsest width whose units holding rows hold at most <paramref name="targetRows"/> of them
    /// at the median; a second when even a second holds more. The median, not the mean, so that a
    /// burst is cut finer rather than setting the grain of every other unit.
    /// </summary>
    internal static SliceWidth WidthFor(ReadOnlySpan<long> instants, TimeUnit unit, long targetRows)
    {
        List<int> counts = [];
        for (SliceWidth width = SliceWidth.Month; width > SliceWidth.Second; width--)
        {
            counts.Clear();
            int row = 0;
            while (row < instants.Length)
            {
                long end = Next(Floor(instants[row], unit, width), unit, width);
                int stop = row + LowerBound(instants[row..], end);
                counts.Add(stop - row);
                row = stop;
            }

            counts.Sort();
            if (counts[counts.Count / 2] <= targetRows)
            {
                return width;
            }
        }

        return SliceWidth.Second;
    }

    /// <summary>Cuts the rows at <paramref name="width"/>, a unit past the burst bound cut again one width finer.</summary>
    private static void Cut(ReadOnlySpan<long> instants, long firstRow, TimeUnit unit, SliceWidth width, long target, List<TimeSlice> into)
    {
        int row = 0;
        while (row < instants.Length)
        {
            long start = Floor(instants[row], unit, width);
            long end = Next(start, unit, width);
            int stop = row + LowerBound(instants[row..], end);
            int count = stop - row;
            if (count > BurstFactor * target && width > SliceWidth.Second)
            {
                Cut(instants[row..stop], firstRow + row, unit, width - 1, target, into);
            }
            else
            {
                into.Add(new TimeSlice(start, end, firstRow + row, count));
            }

            row = stop;
        }
    }

    /// <summary>The first index of <paramref name="sorted"/> at or past <paramref name="value"/>.</summary>
    private static int LowerBound(ReadOnlySpan<long> sorted, long value)
    {
        int low = 0;
        int high = sorted.Length;
        while (low < high)
        {
            int middle = (int)((uint)(low + high) >> 1);
            if (sorted[middle] < value)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>The start of the unit of <paramref name="width"/> holding <paramref name="value"/>, in UTC.</summary>
    internal static long Floor(long value, TimeUnit unit, SliceWidth width)
    {
        if (width != SliceWidth.Month)
        {
            long length = Length(unit, width);
            return TemporalUnits.FloorDiv(value, length) * length;
        }

        DateTime at = DateTime.UnixEpoch.AddTicks(Ticks(value, unit));
        return Count(new DateTime(at.Year, at.Month, 1, 0, 0, 0, DateTimeKind.Utc), unit);
    }

    /// <summary>The start of the unit after the one <paramref name="start"/> begins.</summary>
    internal static long Next(long start, TimeUnit unit, SliceWidth width)
    {
        if (width != SliceWidth.Month)
        {
            return checked(start + Length(unit, width));
        }

        DateTime at = DateTime.UnixEpoch.AddTicks(Ticks(start, unit));
        return Count(at.AddMonths(1), unit);
    }

    /// <summary>A width in seconds; a month as thirty days, which only an estimate of a slice count reads.</summary>
    private static double Seconds(SliceWidth width) => width switch
    {
        SliceWidth.Second => 1,
        SliceWidth.Minute => 60,
        SliceWidth.Hour => 3_600,
        SliceWidth.Day => 86_400,
        _ => 30 * 86_400,
    };

    /// <summary>A width of a fixed length, in <paramref name="unit"/>.</summary>
    private static long Length(TimeUnit unit, SliceWidth width) =>
        checked((long)(Seconds(width) * UnitsPerSecond(unit)));

    private static double UnitsPerSecond(TimeUnit unit) => unit switch
    {
        TimeUnit.Nanoseconds => 1e9,
        TimeUnit.Microseconds => 1e6,
        TimeUnit.Milliseconds => 1e3,
        TimeUnit.Seconds => 1,
        _ => 1.0 / 86_400,
    };

    /// <summary>An instant counted in <paramref name="unit"/> as ticks since the epoch.</summary>
    private static long Ticks(long value, TimeUnit unit) =>
        unit == TimeUnit.Nanoseconds ? TemporalUnits.FloorDiv(value, 100) : checked(value * TemporalUnits.TicksPer(unit));

    /// <summary>A UTC time as a count of <paramref name="unit"/> since the epoch.</summary>
    private static long Count(DateTime utc, TimeUnit unit)
    {
        long ticks = utc.Ticks - DateTime.UnixEpoch.Ticks;
        return unit == TimeUnit.Nanoseconds ? checked(ticks * 100) : TemporalUnits.FloorDiv(ticks, TemporalUnits.TicksPer(unit));
    }
}
