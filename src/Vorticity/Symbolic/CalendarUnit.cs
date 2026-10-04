namespace Vorticity;

/// <summary>
/// A unit of the calendar a time is truncated to: <c>r.At.Truncate(CalendarUnit.Day)</c>. Not
/// <see cref="TimeUnit"/>, which is the unit a timestamp is stored in.
/// </summary>
public enum CalendarUnit : byte
{
    /// <summary>The start of the minute.</summary>
    Minute,

    /// <summary>The start of the hour.</summary>
    Hour,

    /// <summary>Midnight.</summary>
    Day,

    /// <summary>Midnight of the week's Monday, as ISO 8601 starts a week.</summary>
    Week,

    /// <summary>Midnight of the month's first day.</summary>
    Month,

    /// <summary>Midnight of the first day of January, April, July or October.</summary>
    Quarter,

    /// <summary>Midnight of the first of January.</summary>
    Year,
}
