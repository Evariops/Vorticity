using System;
using Vorticity.Types;

namespace Vorticity.Dataset;

/// <summary>
/// How a dataset whose rows arrive in the order of their instant is compacted: by periods, each
/// sealed into one object once its end and the lateness horizon have passed
/// (<see cref="DatasetOptions.Periods"/>).
/// </summary>
/// <remarks>
/// The instant is the first column of the clustering key, a timestamp in UTC. The horizon is a
/// property of the sources, which only the application knows; the dataset reports the lateness it
/// sees, so that a horizon too short shows. Fixed when the dataset is created and carried by every
/// commit, which a library that does not know periods refuses.
/// </remarks>
public sealed record PeriodOptions
{
    private readonly TimeSpan _lateness;

    /// <summary>How late a row may come after the newest one, kept to the second, rounded up: zero by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The horizon is negative.</exception>
    public TimeSpan Lateness
    {
        get => _lateness;
        init
        {
            if (value < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "A lateness horizon is not negative.");
            }

            _lateness = TimeSpan.FromSeconds(Math.Ceiling(value.TotalSeconds));
        }
    }

    /// <summary>
    /// Refuses an append that holds a row later than the lateness horizon, a row of a period already closed, rather
    /// than keep it in the late level beside its period: false by default. Each handle's own, not recorded by the
    /// dataset. The dataset counts such rows either way, on its meter.
    /// </summary>
    public bool RefuseLateRows { get; init; }

    /// <summary>
    /// The rows a time slice aims at: <see cref="TimeSlicer.DefaultTargetRows"/>. Set by tests and the
    /// benches to cut slices of a few rows; each writer's own, since an object's slices are its own.
    /// </summary>
    internal long SliceTargetRows { get; init; } = TimeSlicer.DefaultTargetRows;

    private readonly SliceWidth _length = SliceWidth.Day;

    /// <summary>
    /// The calendar unit a period spans, in UTC: a day until the rule that weighs the store's largest
    /// streamed object, the footer's cap, a purge's cost and the retention picks it. Set by tests to
    /// reach a period's end in a few rows; recorded in the header, so every writer cuts the same.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The unit is finer than a minute.</exception>
    internal SliceWidth Length
    {
        get => _length;
        init
        {
            if (value is < SliceWidth.Minute or > SliceWidth.Month)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "A period spans a minute, an hour, a day or a month.");
            }

            _length = value;
        }
    }

    /// <summary>
    /// The unit of the instant a dataset of periods leads its key with: a timestamp stored in UTC, or
    /// naive, which a dataset of periods reads as UTC.
    /// </summary>
    /// <exception cref="ArgumentException">The dataset declares no key, or its instant column is not such a timestamp.</exception>
    internal static TimeUnit InstantOf(ClusteringKey? key)
    {
        if (key is null)
        {
            throw new ArgumentException(
                "A dataset compacted by periods declares a clustering key that leads with its instant, a timestamp in UTC.",
                nameof(DatasetOptions.Periods));
        }

        VortexType instant = VortexTypes.FromDType(key.InstantType);
        if (instant.ExtensionId != ExtensionIds.Timestamp || (instant.TimeZone is { } zone && !TimeZones.IsUtc(zone)))
        {
            throw new ArgumentException(
                $"A dataset compacted by periods leads its clustering key with its instant, a timestamp in UTC, and '{key.InstantPath}' is {instant}.",
                nameof(DatasetOptions.Periods));
        }

        return instant.Unit ?? TimeUnit.Microseconds;
    }
}

/// <summary>
/// The levels of a dataset compacted by periods. Level 0 holds the appends. The late level holds rows
/// older than the fresh object they would overlap, and rows of sealed periods: sorted objects that
/// overlap, under a ceiling as level 0's. The periods level holds one object per sealed period, and
/// the fresh levels, from <see cref="FirstFresh"/> up, the open period and the closed ones until their
/// seal, each level f times the one below; every one of them is key-disjoint.
/// </summary>
internal static class PeriodLevels
{
    /// <summary>The late level.</summary>
    internal const int Late = 1;

    /// <summary>The level of sealed periods.</summary>
    internal const int Periods = 2;

    /// <summary>The first fresh level, which level 0's exit writes the rows past every fresh object into.</summary>
    internal const int FirstFresh = 3;

    /// <summary>Whether a level is a fresh one.</summary>
    internal static bool IsFresh(int level) => level >= FirstFresh;
}
