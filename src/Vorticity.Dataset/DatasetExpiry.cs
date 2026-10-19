using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Expressions;
using Vorticity.IO;
using Vorticity.Scanning;

namespace Vorticity.Dataset;

/// <summary>
/// Expiry of a dataset of periods: every object whose rows all lie before a cutoff, aligned down to a
/// period's start, removed in one commit from what the leaves say, reading none of them.
/// </summary>
/// <remarks>
/// Every object above level 0 holds one period, so a cutoff at a period's start leaves none
/// straddling it, and an object's maximum instant, which its summary bounds from above, says whether
/// it lies wholly before. An object of level 0 that straddles the cutoff stays until its exit cuts it
/// by period, and the next expiry takes it. A row thus outlives its retention by up to a period.
/// </remarks>
internal static class DatasetExpiry
{
    private const int Attempts = 8;

    /// <summary>Removes what lies before the start of the period holding <paramref name="before"/>, and returns that start.</summary>
    /// <exception cref="InvalidOperationException">The dataset declares no periods.</exception>
    internal static async ValueTask<DateTimeOffset> RunAsync(VortexDataset dataset, DateTimeOffset before, CancellationToken cancellationToken)
    {
        if (dataset.Snapshot.Header.Lateness is null || dataset.Key is not { } key)
        {
            throw new InvalidOperationException(
                "Expiry drops whole periods, and this dataset declares none (DatasetOptions.Periods): delete its rows instead.");
        }

        TimeUnit unit = PeriodOptions.InstantOf(key);
        string path = key.InstantPath;
        for (int attempt = 1; attempt <= Attempts; attempt++)
        {
            DatasetSnapshot version = dataset.Snapshot;
            long cutoff = PeriodCalendar.Of(version.Header, unit).Floor(TimeSlicer.InstantOf(before, unit));
            List<(int Level, ReadOnlyMemory<byte> Key)> expired = [];
            await foreach (PositionedObject held in version.WalkAsync(null, 0, long.MaxValue, null, cancellationToken).ConfigureAwait(false))
            {
                if (held.Entry.Summaries.TryGet(path, out ColumnSummary instant) && instant.HasMax
                    && instant.Max.Kind == FilterLiteralKind.Signed && instant.Max.SignedValue < cutoff)
                {
                    expired.Add((held.Level, held.TreeKey.ToArray()));
                }
            }

            if (expired.Count == 0)
            {
                return TimeSlicer.TimeOf(cutoff, unit);
            }

            // A compaction that took one of them first leaves its rows in its outputs: worked out again.
            CommitResult commit = await dataset
                .CommitAsync([new DatasetOperation.ReplaceObjects(expired, [])], CommitKind.Delete, cancellationToken)
                .ConfigureAwait(false);
            if (commit.Outcomes[0] == OperationOutcome.Applied)
            {
                return TimeSlicer.TimeOf(cutoff, unit);
            }

            await dataset.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        throw new ObjectStoreException(
            $"The expiry lost {Attempts} times to compactions that rewrote the objects it would remove; a coordinator " +
            "that serialises the dataset's writers is the answer to contention, not more attempts.");
    }
}
