using System;
using System.Collections.Generic;
using Vorticity.Compute;
using Vorticity.Indexes;

namespace Vorticity.Dataset;

/// <summary>
/// The time slices of rows that come in the order of their instants, cut as they come, where a first pass counted every row
/// before the first was written (<see cref="TimeSlicer"/>): each slice a unit of one calendar width, chosen from the rows'
/// pace, the coarsest whose units hold at most twice the target at that pace, as <see cref="TimeSlicer"/>'s median holds
/// it for rows at a steady pace. A unit whose rows pass twice the target, a burst, ends its slice at the end of the unit a
/// width finer that holds the row past the bound, and its rows after it go to slices a width finer until the unit ends,
/// each cut again as it is, down to a second, which holds what it holds.
/// </summary>
/// <remarks>
/// <para>
/// For rows at a steady pace the slices are those a count of every row gives. A burst is cut where the stream meets it,
/// not over its whole unit as a count of the unit cuts it, so its first slice runs from its unit's start: what a slice
/// holds stays near its target either way. A unit a width finer that a burst ended a slice in counts the rows it holds
/// from the batch that crossed the bound, those of the batches before it in the unit aside: a slice of a burst nested
/// in a burst holds twice the bound at most before it is cut again.
/// </para>
/// <para>
/// A stream that judges its width holds its first rows in its first slice until it has seen the rows the object's order
/// is judged from, whose pace, their rows over the seconds they span, a second at least, chooses the width: the first
/// slice ends with the unit of that width that holds the last of them. An object of fewer is one slice, its width the
/// pace of all its rows, judged on every row as their count would judge it; a pace read off a batch of a few rows, or
/// a first unit its rows start near the end of, would not be. A span of a second at least reads a burst of one instant
/// as a slow pace, whose slices a burst cuts finer as the rows come, rather than a fast one, whose slices nothing makes
/// coarser.
/// </para>
/// </remarks>
internal sealed class SliceStream
{
    /// <summary>The bound past which a unit is a burst: twice the target, as <see cref="TimeSlicer"/>'s.</summary>
    private const long BurstFactor = 2;

    private readonly TimeUnit _unit;
    private readonly long _target;
    private readonly long _least;
    private readonly List<TimeSlice> _slices = [];

    // The width the slices are cut at, once the first rows chose it.
    private SliceWidth _width;
    private bool _judged;

    // The slice being filled, its start and its end; the width the slices of its unit of the stream's width take, a finer
    // one once a burst was cut in it, and that unit's end; by width, the end of the unit of it a burst was cut in, past
    // which the slices go back to its width; the rows of the slice before the unit a burst ended it in, which the bound
    // leaves out; the object's row the slice starts at.
    private long _start;
    private long _end;
    private bool _open;
    private SliceWidth _current;
    private long _unitEnd = long.MinValue;
    private readonly long[] _burstEnds = new long[(int)SliceWidth.Month + 1];
    private long _base;
    private long _firstRow;

    // The instants of the first row, while the width is not judged, and of the last row taken, which the next must not
    // come before.
    private long _first;
    private long _last = long.MinValue;

    /// <summary>The slices of rows counted in <paramref name="unit"/>, cut at <paramref name="width"/>, a burst past twice <paramref name="target"/> finer.</summary>
    internal SliceStream(TimeUnit unit, SliceWidth width, long target)
        : this(unit, target)
    {
        Judge(width, 0);
    }

    /// <summary>
    /// The slices of rows counted in <paramref name="unit"/>, cut at the width the pace of the first <paramref name="least"/>
    /// rows calls for, which the first slice holds, a burst past twice <paramref name="target"/> finer.
    /// </summary>
    internal SliceStream(TimeUnit unit, long target, long least)
        : this(unit, target)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(least);
        _least = least;
    }

    private SliceStream(TimeUnit unit, long target)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(target);
        _unit = unit;
        _target = target;
    }

    /// <summary>The unit the slices' bounds are counted in.</summary>
    internal TimeUnit Unit => _unit;

    /// <summary>Whether the width is chosen: given, or judged from the first rows.</summary>
    internal bool Judged => _judged;

    /// <summary>The width the slices are cut at, a burst aside; once <see cref="Judged"/>.</summary>
    internal SliceWidth Width => _judged ? _width : throw new InvalidOperationException("The stream's width is judged from its first rows, which have not come.");

    /// <summary>The rows a second of the rows the width was judged from; 0 for a stream given its width.</summary>
    internal double Pace { get; private set; }

    /// <summary>The slices ended so far, in the order of their instants.</summary>
    internal IReadOnlyList<TimeSlice> Slices => _slices;

    /// <summary>
    /// What the slices of an object of <paramref name="rows"/> rows, 0 when unknown, say of it before its first slice is
    /// counted, once <see cref="Judged"/>: it spans hours when <paramref name="spansHours"/>, and its hours hold several
    /// slices when the stream cuts them finer.
    /// </summary>
    internal SliceExtent Extent(long rows, bool spansHours) => new SliceExtent(rows, spansHours, Width < SliceWidth.Hour);

    /// <summary>
    /// How many of <paramref name="instants"/>, the next rows in the order of their instants, the slice being filled takes,
    /// <paramref name="held"/> of its rows taken before, and whether it ends before the rows after them: at the first row at
    /// or past its end, or, its unit's rows past twice the target, at the end of the unit a width finer that holds the row
    /// past the bound. The first slice of a stream that judges its width takes every row until the rows it judges from.
    /// </summary>
    /// <exception cref="ArgumentException">A row comes before the one taken before it.</exception>
    internal (int Take, bool Ends) Take(ReadOnlySpan<long> instants, long held)
    {
        if (instants.IsEmpty)
        {
            return (0, false);
        }

        if (instants[0] < _last)
        {
            throw new ArgumentException($"A row at {instants[0]} came after a row at {_last}: slices cut rows sorted by their instant.", nameof(instants));
        }

        if (!_judged)
        {
            if (held == 0)
            {
                _first = instants[0];
            }

            if (held + instants.Length < _least)
            {
                return (Taken(instants, instants.Length), false);
            }

            // The pace of the rows up to the last the order is judged from chooses the width; the first slice ends with the
            // unit of that width that holds it, its rows before that unit left out of its bound.
            int judged = (int)(_least - held - 1);
            double pace = _least / Seconds(_first, instants[judged]);
            Judge(TimeSlicer.WidthAt(pace, _target), pace);
            long unit = TimeSlicer.Floor(instants[judged], _unit, _width);
            _unitEnd = TimeSlicer.Next(unit, _unit, _width);
            _start = TimeSlicer.Floor(_first, _unit, _width);
            _end = _unitEnd;
            _base = held + Before(instants, unit);
            _open = true;
        }
        else if (!_open)
        {
            Open(instants[0]);
        }

        int take = Before(instants, _end);
        while (_current > SliceWidth.Second)
        {
            // The rows the unit the slice ends with may still take: a burst ends the slice with the unit a width finer that
            // holds the row past the bound, and the slices of the rest of its unit are a width finer.
            long bound = (BurstFactor * _target) - (held - _base);
            if (take <= bound)
            {
                break;
            }

            long past = instants[(int)Math.Max(bound, 0)];
            _burstEnds[(int)_current] = TimeSlicer.Next(TimeSlicer.Floor(past, _unit, _current), _unit, _current);
            _current--;
            long unit = TimeSlicer.Floor(past, _unit, _current);
            _end = Math.Min(TimeSlicer.Next(unit, _unit, _current), _end);
            _base = held + Before(instants, unit);
            take = Before(instants, _end);
        }

        return (Taken(instants, take), take < instants.Length);
    }

    /// <summary>
    /// Ends the slice being filled, which took <paramref name="rows"/> rows: the slice it is. The first slice of a stream
    /// whose rows ended before those it judges from judges the width from the pace of all of them, and ends with the unit
    /// of that width that holds the last.
    /// </summary>
    internal TimeSlice Close(long rows)
    {
        if (!_judged)
        {
            double pace = rows / Seconds(_first, _last);
            Judge(TimeSlicer.WidthAt(pace, _target), pace);
            _start = TimeSlicer.Floor(_first, _unit, _width);
            _end = TimeSlicer.Next(TimeSlicer.Floor(_last, _unit, _width), _unit, _width);
        }

        TimeSlice slice = new TimeSlice(_start, _end, _firstRow, rows);
        _slices.Add(slice);
        _firstRow += rows;
        _open = false;
        return slice;
    }

    /// <summary>The first row of <paramref name="instants"/> before the one ahead of it, or -1 when they rise: the registers clear what they can, a walk the rest.</summary>
    internal static int Falls(ReadOnlySpan<long> instants)
    {
        bool repeats = false;
        return FallsFrom(instants, instants.Length < 2 ? 1 : PairOrder.Cleared<long, PairOrder.Rising>(instants, ref repeats));
    }

    /// <summary>The scalar twin of <see cref="Falls"/>, from row <paramref name="from"/>, the second or past it.</summary>
    internal static int FallsFrom(ReadOnlySpan<long> instants, int from)
    {
        for (int i = from; i < instants.Length; i++)
        {
            if (instants[i] < instants[i - 1])
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The <paramref name="take"/> rows of <paramref name="instants"/> taken, checked to rise, which their search took for
    /// granted: those after them are when they are taken. A slice whose end the next row is past takes none.
    /// </summary>
    private int Taken(ReadOnlySpan<long> instants, int take)
    {
        if (take > 0)
        {
            if (Falls(instants[..take]) is int row and >= 0)
            {
                throw new ArgumentException($"A row at {instants[row]} came after a row at {instants[row - 1]}: slices cut rows sorted by their instant.", nameof(instants));
            }

            _last = instants[take - 1];
        }

        return take;
    }

    /// <summary>Cuts the slices at <paramref name="width"/> from now on, the width rows at <paramref name="pace"/> a second call for.</summary>
    private void Judge(SliceWidth width, double pace)
    {
        _width = width;
        _current = width;
        Pace = pace;
        _judged = true;
    }

    /// <summary>The seconds from <paramref name="from"/> to <paramref name="to"/>, a second at least.</summary>
    private double Seconds(long from, long to) => Math.Max(TimeSlicer.SecondsBetween(from, to, _unit), 1);

    /// <summary>
    /// Opens the slice of the row at <paramref name="instant"/>: its unit, a burst's finer one inside the unit it cut, and
    /// the width of a unit a burst was cut in again once the row is past the unit's end, as a count cuts a burst's units
    /// that hold fewer rows at their own width.
    /// </summary>
    private void Open(long instant)
    {
        if (instant >= _unitEnd)
        {
            _current = _width;
            _unitEnd = TimeSlicer.Next(TimeSlicer.Floor(instant, _unit, _width), _unit, _width);
        }

        while (_current < _width && instant >= _burstEnds[(int)_current + 1])
        {
            _current++;
        }

        _start = TimeSlicer.Floor(instant, _unit, _current);
        _end = Math.Min(TimeSlicer.Next(_start, _unit, _current), _current < _width ? _burstEnds[(int)_current + 1] : _unitEnd);
        _base = 0;
        _open = true;
    }

    /// <summary>The rows of <paramref name="instants"/>, ascending, before <paramref name="end"/>.</summary>
    private static int Before(ReadOnlySpan<long> instants, long end)
    {
        if (instants[^1] < end)
        {
            return instants.Length;
        }

        int low = 0;
        int high = instants.Length;
        while (low < high)
        {
            int middle = (int)((uint)(low + high) >> 1);
            if (instants[middle] < end)
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
}

/// <summary>
/// What the slices of an object cut as its rows come say of it before its first slice is counted: the rows it is expected
/// to hold, 0 when unknown, whether they may span hours, and whether its hours hold several slices.
/// </summary>
internal readonly record struct SliceExtent(long Rows, bool SpansHours, bool CutsHours);
