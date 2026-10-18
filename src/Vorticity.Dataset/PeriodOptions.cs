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
    /// The unit of the instant a dataset of periods leads its key with: its key's first column, a
    /// timestamp stored in UTC, or naive, which a dataset of periods reads as UTC.
    /// </summary>
    /// <exception cref="ArgumentException">The dataset declares no key, or its first column is not such a timestamp.</exception>
    internal static TimeUnit InstantOf(ClusteringKey? key)
    {
        if (key is null)
        {
            throw new ArgumentException(
                "A dataset compacted by periods declares a clustering key that leads with its instant, a timestamp in UTC.",
                nameof(DatasetOptions.Periods));
        }

        VortexType leading = VortexTypes.FromDType(key.LeadingType);
        if (leading.ExtensionId != ExtensionIds.Timestamp || (leading.TimeZone is { } zone && !TimeZones.IsUtc(zone)))
        {
            throw new ArgumentException(
                $"A dataset compacted by periods leads its clustering key with its instant, a timestamp in UTC, and '{key.Paths[0]}' is {leading}.",
                nameof(DatasetOptions.Periods));
        }

        return leading.Unit ?? TimeUnit.Microseconds;
    }
}
