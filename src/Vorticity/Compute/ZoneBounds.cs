using System;
using Vorticity.Expressions;

namespace Vorticity.Compute;

/// <summary>
/// One column's summary of one zone, held as values rather than as indices into the arena the
/// zones were decoded in: that arena is reset at every batch boundary, so a pruner that kept
/// indices into it would be reading freed memory from the second batch on. The bounds are
/// <see cref="FilterLiteral"/>s, the same tagged union a filter's constants use, so that comparing
/// a bound against a constant runs the code that compares a value against one and the signed and
/// unsigned rules cannot drift apart.
/// </summary>
internal readonly struct ZoneBounds
{
    /// <summary>Nothing is known about this zone; every predicate must assume it may match.</summary>
    internal static ZoneBounds Unknown => default;

    // The counts are ints so the struct stays 64 bytes: two literals of 24 bytes, two counts and
    // five flags fit one cache line exactly, where two long counts would spill past it and cost
    // eight more bytes for every zone of the file. A zone is one batch of rows, so a count that
    // does not fit an int is not a count this reader will ever meet, and Create treats one as
    // "not recorded", which prunes and proves less and is never wrong.
    private readonly int _nullCount;
    private readonly int _nanCount;

    private ZoneBounds(
        FilterLiteral min, bool hasMin, FilterLiteral max, bool hasMax, bool exact,
        int nullCount, bool hasNullCount, int nanCount, bool hasNanCount)
    {
        Min = min;
        HasMin = hasMin;
        Max = max;
        HasMax = hasMax;
        IsExact = exact;
        _nullCount = nullCount;
        HasNullCount = hasNullCount;
        _nanCount = nanCount;
        HasNanCount = hasNanCount;
    }

    /// <summary>
    /// How many of the zone's rows are NaN. The bounds exclude those rows and no comparison ever
    /// selects one, so a proof that counts a whole block has to subtract them.
    /// </summary>
    internal long NanCount => _nanCount;

    /// <summary>Whether <see cref="NanCount"/> was recorded.</summary>
    internal bool HasNanCount { get; }

    /// <summary>A lower bound on the zone's values.</summary>
    internal FilterLiteral Min { get; }

    /// <summary>Whether <see cref="Min"/> was recorded.</summary>
    internal bool HasMin { get; }

    /// <summary>An upper bound on the zone's values.</summary>
    internal FilterLiteral Max { get; }

    /// <summary>Whether <see cref="Max"/> was recorded.</summary>
    internal bool HasMax { get; }

    /// <summary>
    /// Whether the bounds are the true extremes rather than conservative ones. False for
    /// <c>vortex.bounded_min</c> / <c>vortex.bounded_max</c>, whose true minimum is at or above
    /// the stored one and true maximum at or below it. A conservative bound still rules a range
    /// out soundly, since it only widens the interval; what it does not license is the shortcut
    /// that reads <c>min == max</c> as "the zone holds one value", so anything resting on equality
    /// must consult this first.
    /// </summary>
    internal bool IsExact { get; }

    /// <summary>How many of the zone's rows are null.</summary>
    internal long NullCount => _nullCount;

    /// <summary>Whether <see cref="NullCount"/> was recorded.</summary>
    internal bool HasNullCount { get; }

    /// <summary>Builds a zone summary.</summary>
    /// <param name="min">The lower bound, or default when absent.</param>
    /// <param name="hasMin">Whether a lower bound was recorded.</param>
    /// <param name="max">The upper bound, or default when absent.</param>
    /// <param name="hasMax">Whether an upper bound was recorded.</param>
    /// <param name="exact">Whether the bounds are exact extremes.</param>
    /// <param name="nullCount">The null count, when recorded.</param>
    /// <param name="hasNullCount">Whether a null count was recorded.</param>
    /// <param name="nanCount">The NaN count, when recorded.</param>
    /// <param name="hasNanCount">Whether a NaN count was recorded.</param>
    internal static ZoneBounds Create(
        FilterLiteral min, bool hasMin, FilterLiteral max, bool hasMax, bool exact,
        long nullCount, bool hasNullCount, long nanCount = 0, bool hasNanCount = false)
    {
        // A count that does not fit an int is taken as not recorded rather than truncated.
        bool nulls = hasNullCount && nullCount >= 0 && nullCount <= int.MaxValue;
        bool nans = hasNanCount && nanCount >= 0 && nanCount <= int.MaxValue;
        return new ZoneBounds(
            min, hasMin, max, hasMax, exact,
            nulls ? (int)nullCount : 0, nulls,
            nans ? (int)nanCount : 0, nans);
    }
}
