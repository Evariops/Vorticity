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
/// A slice is the finest grain of time a question reads without reading rows, the most rows an edge
/// of a window reads, and the rows a job writes together. A smaller slice makes edges cheaper, but
/// adds entries to the slice table. A target per object from the edges alone was measured and
/// dropped when slices were sorted by the synopsis columns of the time: it cut the bench's hours
/// into minutes, whose sorted columns took 16 % more bytes.
/// <see cref="DefaultTargetRows"/> stays a minute of the design's example, 5 × 10⁴ rows a second.
/// </remarks>
internal static class TimeSlicer
{
    /// <summary>The rows a slice aims at: a minute of the design's example.</summary>
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
        SecondCounts seconds = new SecondCounts(unit);
        seconds.Add(instants);
        return Cut(seconds.Seconds, unit, targetRows);
    }

    /// <summary>
    /// The slices of rows counted per second in <paramref name="seconds"/>, ascending: the same slices
    /// the rows' instants give, since no slice is finer than a second.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The target is not positive.</exception>
    internal static List<TimeSlice> Cut(ReadOnlySpan<SecondCount> seconds, TimeUnit unit, long targetRows = DefaultTargetRows)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetRows);
        List<TimeSlice> slices = [];
        if (!seconds.IsEmpty)
        {
            Cut(seconds, 0, unit, WidthFor(seconds, unit, targetRows), targetRows, slices);
        }

        return slices;
    }

    /// <summary>
    /// The coarsest width whose units holding rows hold at most twice <paramref name="targetRows"/> of
    /// them at the median, the bound past which a unit is a burst and cut again; a second when even a
    /// second holds more. The median, not the mean, so that a burst is cut finer rather than setting the
    /// grain of every other unit.
    /// </summary>
    /// <remarks>
    /// At the target itself, a minute of the design's rate that rows a little late from the next hour
    /// took past it went to seconds whole: every hour but the last of a day of the design's requests was
    /// sliced by the second, its tables by slice sixty times the rows, the hosts' and the endpoints' past
    /// the budget, and a window's edge read in seconds where a minute was meant.
    /// </remarks>
    internal static SliceWidth WidthFor(ReadOnlySpan<long> instants, TimeUnit unit, long targetRows)
    {
        SecondCounts seconds = new SecondCounts(unit);
        seconds.Add(instants);
        return WidthFor(seconds.Seconds, unit, targetRows);
    }

    private static SliceWidth WidthFor(ReadOnlySpan<SecondCount> seconds, TimeUnit unit, long targetRows)
    {
        List<long> counts = [];
        for (SliceWidth width = SliceWidth.Month; width > SliceWidth.Second; width--)
        {
            counts.Clear();
            int at = 0;
            while (at < seconds.Length)
            {
                long end = Next(Floor(seconds[at].Start, unit, width), unit, width);
                int stop = at + LowerBound(seconds[at..], end);
                counts.Add(RowsOf(seconds[at..stop]));
                at = stop;
            }

            counts.Sort();
            if (counts[counts.Count / 2] <= BurstFactor * targetRows)
            {
                return width;
            }
        }

        return SliceWidth.Second;
    }

    /// <summary>Cuts the rows at <paramref name="width"/>, a unit past the burst bound cut again one width finer.</summary>
    private static void Cut(ReadOnlySpan<SecondCount> seconds, long firstRow, TimeUnit unit, SliceWidth width, long target, List<TimeSlice> into)
    {
        int at = 0;
        while (at < seconds.Length)
        {
            long start = Floor(seconds[at].Start, unit, width);
            long end = Next(start, unit, width);
            int stop = at + LowerBound(seconds[at..], end);
            long count = RowsOf(seconds[at..stop]);
            if (count > BurstFactor * target && width > SliceWidth.Second)
            {
                Cut(seconds[at..stop], firstRow, unit, width - 1, target, into);
            }
            else
            {
                into.Add(new TimeSlice(start, end, firstRow, count));
            }

            firstRow += count;
            at = stop;
        }
    }

    private static long RowsOf(ReadOnlySpan<SecondCount> seconds)
    {
        long rows = 0;
        foreach (SecondCount second in seconds)
        {
            rows += second.Rows;
        }

        return rows;
    }

    /// <summary>The first index of <paramref name="seconds"/> whose start is at or past <paramref name="value"/>.</summary>
    private static int LowerBound(ReadOnlySpan<SecondCount> seconds, long value)
    {
        int low = 0;
        int high = seconds.Length;
        while (low < high)
        {
            int middle = (int)((uint)(low + high) >> 1);
            if (seconds[middle].Start < value)
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

    /// <summary>
    /// The width rows at a pace of <paramref name="perSecond"/> a second are cut at: the coarsest whose units hold at most
    /// twice <paramref name="targetRows"/> of them, which is the width <see cref="WidthFor(ReadOnlySpan{long}, TimeUnit, long)"/>
    /// gives rows at a steady pace, whose units all hold what the median does; a second when even a second holds more.
    /// </summary>
    internal static SliceWidth WidthAt(double perSecond, long targetRows)
    {
        for (SliceWidth width = SliceWidth.Month; width > SliceWidth.Second; width--)
        {
            if (perSecond * Seconds(width) <= BurstFactor * targetRows)
            {
                return width;
            }
        }

        return SliceWidth.Second;
    }

    /// <summary>The rows a unit of <paramref name="width"/> holds at a pace of <paramref name="perSecond"/> a second.</summary>
    internal static double RowsAt(double perSecond, SliceWidth width) => perSecond * Seconds(width);

    /// <summary>The seconds between two instants counted in <paramref name="unit"/>.</summary>
    internal static double SecondsBetween(long from, long to, TimeUnit unit) => (to - from) / UnitsPerSecond(unit);

    /// <summary>A width in seconds; a month as thirty days, which only an estimate of a slice count reads.</summary>
    private static double Seconds(SliceWidth width) => width switch
    {
        SliceWidth.Second => 1,
        SliceWidth.Minute => 60,
        SliceWidth.Hour => 3_600,
        SliceWidth.Day => 86_400,
        _ => 30 * 86_400,
    };

    /// <summary>A UTC time as an instant column counts it, in <paramref name="unit"/> since the epoch.</summary>
    internal static long InstantOf(DateTimeOffset at, TimeUnit unit) => Count(at.UtcDateTime, unit);

    /// <summary>An instant counted in <paramref name="unit"/> as a UTC time, to the tick.</summary>
    internal static DateTimeOffset TimeOf(long instant, TimeUnit unit) =>
        new DateTimeOffset(DateTime.UnixEpoch.AddTicks(Ticks(instant, unit)), TimeSpan.Zero);

    /// <summary>A span of time counted in <paramref name="unit"/>, as an instant column counts it.</summary>
    internal static long Units(TimeSpan span, TimeUnit unit) => checked((long)(span.TotalSeconds * UnitsPerSecond(unit)));

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

/// <summary>The rows of one second, its start counted in the instant's unit.</summary>
internal readonly record struct SecondCount(long Start, long Rows);

/// <summary>
/// The rows of each second that holds some, counted as sorted instants stream past: what slices are
/// cut from without keeping the instants, sixteen bytes a second holding rows, 1.4 MB for a day.
/// </summary>
internal sealed class SecondCounts(TimeUnit unit)
{
    private SecondCount[] _seconds = new SecondCount[16];
    private int _count;
    private long _last = long.MinValue;

    // The end of the last second counted, so that an instant inside it costs a comparison.
    private long _end = long.MinValue;

    /// <summary>The seconds counted so far, ascending.</summary>
    internal ReadOnlySpan<SecondCount> Seconds => _seconds.AsSpan(0, _count);

    /// <summary>The rows counted so far.</summary>
    internal long Rows { get; private set; }

    /// <summary>Counts <paramref name="instants"/>, which follow those counted before.</summary>
    /// <exception cref="ArgumentException">An instant comes before the one counted before it.</exception>
    internal void Add(ReadOnlySpan<long> instants)
    {
        for (int i = 0; i < instants.Length; i++)
        {
            long instant = instants[i];
            if (instant < _last)
            {
                throw new ArgumentException(
                    $"Row {Rows + i} comes before row {Rows + i - 1} in time: slices cut rows sorted by their instant.", nameof(instants));
            }

            _last = instant;
            if (instant < _end)
            {
                _seconds[_count - 1] = _seconds[_count - 1] with { Rows = _seconds[_count - 1].Rows + 1 };
                continue;
            }

            long start = TimeSlicer.Floor(instant, unit, SliceWidth.Second);
            _end = TimeSlicer.Next(start, unit, SliceWidth.Second);
            if (_count == _seconds.Length)
            {
                Array.Resize(ref _seconds, _count * 2);
            }

            _seconds[_count++] = new SecondCount(start, 1);
        }

        Rows += instants.Length;
    }
}
