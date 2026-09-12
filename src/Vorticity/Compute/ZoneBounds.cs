// What one zone of one column says about itself - docs/08-semantics.md §1 and §2.
//
// Copied OUT of the canonical arena on purpose. The zones are decoded once per scan, but the arena
// they land in is reset at every batch boundary (ScanContext.ResetBatch), so anything the pruner
// keeps has to stop being an arena index before the first batch runs. Per zone that is two values
// and two counts, so the copy is measured in kilobytes for a file with thousands of zones.
//
// The bounds are held as FilterLiteral, the same tagged union a filter's constants use, so that
// comparing a bound against a constant is the same code path as comparing a value against one, and
// the signed/unsigned rules cannot drift between the two.
//
// PRECISION IS CARRIED, NOT ASSUMED. `vortex.bounded_min` and `vortex.bounded_max` are Inexact:
// the true minimum is at or above the stored one, the true maximum at or below it. docs/08 §1 makes
// that a conservative bound rather than an approximation, so range pruning stays legal while the
// `min == max` equality shortcut does not -- and IsExact is what the pruner has to consult before
// reaching for the latter.
using System;
using Vorticity.Expressions;

namespace Vorticity.Compute;

/// <summary>One column's summary of one zone.</summary>
internal readonly struct ZoneBounds
{
    /// <summary>Nothing is known about this zone; every predicate must assume it may match.</summary>
    internal static ZoneBounds Unknown => default;

    private ZoneBounds(
        FilterLiteral min, bool hasMin, FilterLiteral max, bool hasMax, bool exact,
        long nullCount, bool hasNullCount)
    {
        Min = min;
        HasMin = hasMin;
        Max = max;
        HasMax = hasMax;
        IsExact = exact;
        NullCount = nullCount;
        HasNullCount = hasNullCount;
    }

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
    /// <c>vortex.bounded_min</c> / <c>vortex.bounded_max</c>.
    /// </summary>
    internal bool IsExact { get; }

    /// <summary>How many of the zone's rows are null.</summary>
    internal long NullCount { get; }

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
    internal static ZoneBounds Create(
        FilterLiteral min, bool hasMin, FilterLiteral max, bool hasMax, bool exact,
        long nullCount, bool hasNullCount) =>
        new ZoneBounds(min, hasMin, max, hasMax, exact, nullCount, hasNullCount);
}
