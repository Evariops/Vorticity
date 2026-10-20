using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Vorticity.Dataset;

/// <summary>What a commit was made for, as the dataset's telemetry names it.</summary>
internal enum CommitKind
{
    /// <summary>The first version, which a creation makes.</summary>
    Create = 0,

    /// <summary>An object appended.</summary>
    Append = 1,

    /// <summary>A file already in the store, taken as an object.</summary>
    Import = 2,

    /// <summary>Objects removed or replaced by the caller.</summary>
    Replace = 3,

    /// <summary>Rows deleted.</summary>
    Delete = 4,

    /// <summary>Rows changed.</summary>
    Update = 5,

    /// <summary>The schema changed.</summary>
    Schema = 6,

    /// <summary>Index fragments attached to objects.</summary>
    Index = 7,

    /// <summary>A compaction's replacement, or its bundling of fragments.</summary>
    Compaction = 8,

    /// <summary>Pages and fragments moved out of sparse commit objects.</summary>
    Repack = 9,

    /// <summary>The data key changed.</summary>
    Rekey = 10,

    /// <summary>The offsets of sources no writer takes any more forgotten.</summary>
    Sources = 11,
}

/// <summary>How a commit ended, as the dataset's telemetry names it.</summary>
internal enum CommitEnd
{
    /// <summary>A version was created.</summary>
    Applied = 0,

    /// <summary>No operation applied, so no version was created.</summary>
    NothingToDo = 1,

    /// <summary>The commit lost every attempt it was allowed.</summary>
    Lost = 2,

    /// <summary>The caller cancelled it.</summary>
    Cancelled = 3,

    /// <summary>The store or the operations raised.</summary>
    Failed = 4,
}

/// <summary>
/// The dataset's instruments, on the library's meter and activity source: an activity per commit,
/// per compaction and per vacuum, and counters a dashboard sums over a process.
/// </summary>
/// <remarks>
/// A dataset's location is a tag of its activities and never of a counter: a process may hold a hundred
/// thousand datasets, and a tag per location would make a series per dataset. Each instrument is
/// touched once per commit, compaction or vacuum, never per batch of rows, and costs a check when
/// nothing listens: a counter's tags are not even built, and an activity is null.
/// </remarks>
internal static class DatasetTelemetry
{
    private static readonly Counter<long> Commits = VortexTelemetry.Meter.CreateCounter<long>(
        "vortex.dataset.commits", "{commit}", "Commits of datasets, by operation and outcome.");

    private static readonly Counter<long> Attempts = VortexTelemetry.Meter.CreateCounter<long>(
        "vortex.dataset.commit_attempts", "{attempt}", "Conditional creations of a version that commits sent, by operation; those past the commits are rebases.");

    private static readonly Counter<long> Compactions = VortexTelemetry.Meter.CreateCounter<long>(
        "vortex.dataset.compactions", "{compaction}", "Compactions of datasets, by trigger and outcome.");

    private static readonly Counter<long> CompactionBytesRead = VortexTelemetry.Meter.CreateCounter<long>(
        "vortex.dataset.compaction.bytes_read", "By", "Bytes the compactions that ran to their commit read, by trigger.");

    private static readonly Counter<long> CompactionBytesWritten = VortexTelemetry.Meter.CreateCounter<long>(
        "vortex.dataset.compaction.bytes_written", "By", "Bytes the compactions that ran to their commit wrote, by trigger.");

    private static readonly Counter<long> VacuumDeleted = VortexTelemetry.Meter.CreateCounter<long>(
        "vortex.dataset.vacuum.objects_deleted", "{object}", "Objects vacuums deleted.");

    private static readonly Histogram<double> Lateness = VortexTelemetry.Meter.CreateHistogram<double>(
        "vortex.dataset.lateness", "s", "How far behind the newest instant a handle appended the oldest row of each append to a dataset of periods came.");

    /// <summary>Records how late an append's oldest row came: what tells a lateness horizon too short.</summary>
    private static readonly Counter<long> RowsPastHorizon = VortexTelemetry.Meter.CreateCounter<long>(
        "vortex.dataset.rows_past_horizon", "{row}", "Rows a dataset by periods took later than its lateness horizon, kept in the late level beside their period.");

    /// <summary>Rows level 0's exit sent to the late level for a period already closed.</summary>
    internal static void PastHorizon(long rows) => RowsPastHorizon.Add(rows);

    internal static void Late(double seconds)
    {
        if (Lateness.Enabled)
        {
            Lateness.Record(seconds);
        }
    }

    private static readonly Histogram<long> Partitions = VortexTelemetry.Meter.CreateHistogram<long>(
        "vortex.dataset.partitions", "{partition}", "The values of its partition columns a dataset holds rows of, at each plan of its compaction.");

    /// <summary>
    /// Records the partitions a plan found a dataset to hold: one append's values are bounded, a dataset's
    /// over its life are not, and this is what shows them pile up.
    /// </summary>
    internal static void PartitionsHeld(int partitions)
    {
        if (Partitions.Enabled)
        {
            Partitions.Record(partitions);
        }
    }

    private static readonly Histogram<double> PeriodLoad = VortexTelemetry.Meter.CreateHistogram<double>(
        "vortex.dataset.period.load", "By/s", "The bytes per second of data time of each period a seal wrote, with the period length that load calls for.");

    /// <summary>
    /// Records the load a seal weighed and the length it calls for: the measure the length's rule is
    /// built on, which a deployment reads to see whether the rule's constants fit it.
    /// </summary>
    internal static void PeriodWeighed(double bytesPerSecond, SliceWidth length)
    {
        if (PeriodLoad.Enabled)
        {
            PeriodLoad.Record(bytesPerSecond, new KeyValuePair<string, object?>("length", length.ToString()));
        }
    }

    /// <summary>Starts the activity of one commit, or returns null when nothing listens.</summary>
    internal static Activity? StartCommit(CommitKind kind, string? location)
    {
        Activity? activity = VortexTelemetry.Source.StartActivity("vortex.dataset.commit", ActivityKind.Internal);
        if (activity is not null)
        {
            activity.SetTag("vortex.operation", NameOf(kind));
            if (location is not null)
            {
                activity.SetTag("vortex.location", location);
            }
        }

        return activity;
    }

    /// <summary>Counts one conditional creation a commit sends.</summary>
    internal static void Attempted(CommitKind kind)
    {
        if (Attempts.Enabled)
        {
            Attempts.Add(1, new KeyValuePair<string, object?>("vortex.operation", NameOf(kind)));
        }
    }

    /// <summary>
    /// Counts a commit by how it ended, and tags and ends its activity; <paramref name="attempts"/> is
    /// 0 when the commit raised before it could say.
    /// </summary>
    internal static void CommitEnded(Activity? activity, CommitKind kind, CommitEnd end, int attempts, ulong version)
    {
        if (Commits.Enabled)
        {
            Commits.Add(
                1,
                new KeyValuePair<string, object?>("vortex.operation", NameOf(kind)),
                new KeyValuePair<string, object?>("vortex.outcome", NameOf(end)));
        }

        if (activity is null)
        {
            return;
        }

        activity.SetTag("vortex.outcome", NameOf(end));
        if (attempts > 0)
        {
            activity.SetTag("vortex.attempts", attempts);
        }

        if (end is CommitEnd.Applied or CommitEnd.NothingToDo)
        {
            activity.SetTag("vortex.version", (long)version);
        }
        else
        {
            activity.SetStatus(ActivityStatusCode.Error, NameOf(end));
        }

        activity.Dispose();
    }

    /// <summary>Starts the activity of one compaction job, or returns null when nothing listens.</summary>
    internal static Activity? StartCompaction(CompactionJob job, string? location)
    {
        Activity? activity = VortexTelemetry.Source.StartActivity("vortex.dataset.compaction", ActivityKind.Internal);
        if (activity is not null)
        {
            activity.SetTag("vortex.trigger", NameOf(job.Trigger));
            activity.SetTag("vortex.level.from", job.FromLevel);
            activity.SetTag("vortex.level.to", job.ToLevel);
            activity.SetTag("vortex.objects_in", job.Inputs.Count);
            activity.SetTag("vortex.bytes_in", job.Bytes);
            if (location is not null)
            {
                activity.SetTag("vortex.location", location);
            }
        }

        return activity;
    }

    /// <summary>
    /// Starts the activity of one pass of a compaction job, a child of the job's, or returns null when nothing
    /// listens: <c>plan</c>, the first pass of a job that writes sorted slices, which cuts them from the rows,
    /// or <c>write</c>, the pass that reads the rows again and writes the outputs.
    /// </summary>
    internal static Activity? StartCompactionPass(string pass) =>
        VortexTelemetry.Source.StartActivity($"vortex.dataset.compaction.{pass}", ActivityKind.Internal);

    /// <summary>
    /// Counts a compaction that reached its commit, applied or abandoned, with what it read and wrote,
    /// and tags and ends its activity.
    /// </summary>
    internal static void CompactionEnded(Activity? activity, CompactionResult result)
    {
        string trigger = NameOf(result.Trigger);
        string outcome = NameOf(result.Outcome);
        if (Compactions.Enabled)
        {
            Compactions.Add(
                1,
                new KeyValuePair<string, object?>("vortex.trigger", trigger),
                new KeyValuePair<string, object?>("vortex.outcome", outcome));
        }

        if (CompactionBytesRead.Enabled)
        {
            CompactionBytesRead.Add(result.BytesIn, new KeyValuePair<string, object?>("vortex.trigger", trigger));
        }

        if (CompactionBytesWritten.Enabled)
        {
            CompactionBytesWritten.Add(result.BytesOut, new KeyValuePair<string, object?>("vortex.trigger", trigger));
        }

        if (activity is null)
        {
            return;
        }

        activity.SetTag("vortex.outcome", outcome);
        activity.SetTag("vortex.objects_out", result.ObjectsOut);
        activity.SetTag("vortex.objects_moved", result.ObjectsMoved);
        activity.SetTag("vortex.bytes_out", result.BytesOut);
        activity.SetTag("vortex.rows", result.Rows);
        activity.SetTag("vortex.version", (long)result.Version);
        activity.Dispose();
    }

    /// <summary>Counts a compaction that raised before its commit, and ends its activity as an error.</summary>
    internal static void CompactionFailed(Activity? activity, CompactionTrigger trigger, Exception failure)
    {
        string outcome = failure is OperationCanceledException ? "cancelled" : "failed";
        if (Compactions.Enabled)
        {
            Compactions.Add(
                1,
                new KeyValuePair<string, object?>("vortex.trigger", NameOf(trigger)),
                new KeyValuePair<string, object?>("vortex.outcome", outcome));
        }

        if (activity is not null)
        {
            activity.SetTag("vortex.outcome", outcome);
            activity.SetStatus(ActivityStatusCode.Error, failure.GetType().Name);
            activity.Dispose();
        }
    }

    /// <summary>Starts the activity of one vacuum, or returns null when nothing listens.</summary>
    internal static Activity? StartVacuum(string? location, bool dryRun)
    {
        Activity? activity = VortexTelemetry.Source.StartActivity("vortex.dataset.vacuum", ActivityKind.Internal);
        if (activity is not null)
        {
            activity.SetTag("vortex.dry_run", dryRun);
            if (location is not null)
            {
                activity.SetTag("vortex.location", location);
            }
        }

        return activity;
    }

    /// <summary>Counts what a vacuum deleted, a dry run's nothing, and tags and ends its activity.</summary>
    internal static void VacuumEnded(Activity? activity, VacuumResult result, bool dryRun)
    {
        if (!dryRun && VacuumDeleted.Enabled)
        {
            VacuumDeleted.Add(result.Deleted.Length);
        }

        if (activity is null)
        {
            return;
        }

        activity.SetTag("vortex.deleted", result.Deleted.Length);
        activity.SetTag("vortex.young", result.Young.Length);
        activity.SetTag("vortex.locked", result.Locked.Length);
        activity.SetTag("vortex.retained", result.Retained.Length);
        activity.Dispose();
    }

    /// <summary>Ends the activity of a vacuum that raised, as an error.</summary>
    internal static void VacuumFailed(Activity? activity, Exception failure)
    {
        if (activity is not null)
        {
            activity.SetStatus(ActivityStatusCode.Error, failure.GetType().Name);
            activity.Dispose();
        }
    }

    private static string NameOf(CommitKind kind) => kind switch
    {
        CommitKind.Create => "create",
        CommitKind.Append => "append",
        CommitKind.Import => "import",
        CommitKind.Replace => "replace",
        CommitKind.Delete => "delete",
        CommitKind.Update => "update",
        CommitKind.Schema => "schema",
        CommitKind.Index => "index",
        CommitKind.Compaction => "compaction",
        CommitKind.Repack => "repack",
        CommitKind.Rekey => "rekey",
        CommitKind.Sources => "sources",
        _ => "other",
    };

    private static string NameOf(CommitEnd end) => end switch
    {
        CommitEnd.Applied => "applied",
        CommitEnd.NothingToDo => "nothing_to_do",
        CommitEnd.Lost => "lost",
        CommitEnd.Cancelled => "cancelled",
        _ => "failed",
    };

    private static string NameOf(OperationOutcome outcome) => outcome switch
    {
        OperationOutcome.Applied => "applied",
        OperationOutcome.AlreadyThere => "already_there",
        OperationOutcome.Dropped => "dropped",
        _ => "abandoned",
    };

    private static string NameOf(CompactionTrigger trigger) => trigger switch
    {
        CompactionTrigger.LevelZeroCeiling => "level_zero_ceiling",
        CompactionTrigger.LevelSize => "level_size",
        CompactionTrigger.Fragments => "fragments",
        CompactionTrigger.Marks => "marks",
        CompactionTrigger.Erasure => "erasure",
        CompactionTrigger.Neighbours => "neighbours",
        _ => "none",
    };
}
