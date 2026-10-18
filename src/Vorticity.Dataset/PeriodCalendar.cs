using System;
using System.Collections.Generic;

namespace Vorticity.Dataset;

/// <summary>A change of a dataset's period length, from an instant on, counted in the instant column's unit.</summary>
/// <param name="From">The first instant the new length cuts, a boundary of both the old length and the new one.</param>
/// <param name="Length">The calendar unit periods span from then on.</param>
internal readonly record struct PeriodChange(long From, SliceWidth Length);

/// <summary>
/// Where a dataset's periods start and end: a calendar unit, and the changes of it the dataset made,
/// each from a boundary of both the unit before it and its own, so that no period straddles a change.
/// </summary>
/// <remarks>
/// The units are nested, a minute in an hour in a day in a month, so the next boundary of the coarser
/// of two units is a boundary of both. A change takes effect from the first such boundary past the
/// newest instant the dataset holds, so that the periods already written keep their bounds.
/// </remarks>
internal sealed class PeriodCalendar
{
    private readonly SliceWidth _first;
    private readonly IReadOnlyList<PeriodChange> _changes;
    private readonly TimeUnit _unit;

    internal PeriodCalendar(SliceWidth first, IReadOnlyList<PeriodChange> changes, TimeUnit unit)
    {
        _first = first;
        _changes = changes;
        _unit = unit;
    }

    /// <summary>The calendar a header records, over an instant counted in <paramref name="unit"/>: a day for a header that names no length.</summary>
    internal static PeriodCalendar Of(CommitHeader header, TimeUnit unit) =>
        new PeriodCalendar(header.PeriodLength ?? SliceWidth.Day, header.PeriodChanges, unit);

    /// <summary>The changes the dataset made, in order.</summary>
    internal IReadOnlyList<PeriodChange> Changes => _changes;

    /// <summary>The unit of the instant column.</summary>
    internal TimeUnit Unit => _unit;

    /// <summary>The length that cuts <paramref name="instant"/>.</summary>
    internal SliceWidth LengthAt(long instant)
    {
        SliceWidth length = _first;
        foreach (PeriodChange change in _changes)
        {
            if (change.From > instant)
            {
                break;
            }

            length = change.Length;
        }

        return length;
    }

    /// <summary>The start of the period holding <paramref name="instant"/>.</summary>
    internal long Floor(long instant) => TimeSlicer.Floor(instant, _unit, LengthAt(instant));

    /// <summary>The start of the period after the one starting at <paramref name="start"/>.</summary>
    internal long Next(long start) => TimeSlicer.Next(start, _unit, LengthAt(start));

    /// <summary>
    /// The change to <paramref name="length"/> past <paramref name="newest"/>: from the first boundary of
    /// the coarser of it and the length that cuts the newest instant; null when that is the length already.
    /// </summary>
    internal PeriodChange? ChangeTo(SliceWidth length, long newest)
    {
        SliceWidth current = LengthAt(newest);
        if (current == length)
        {
            return null;
        }

        SliceWidth coarser = length > current ? length : current;
        return new PeriodChange(TimeSlicer.Next(TimeSlicer.Floor(newest, _unit, coarser), _unit, coarser), length);
    }
}
