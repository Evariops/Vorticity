using System;
using System.Globalization;

namespace Vorticity.Dataset;

/// <summary>
/// A compaction job whose merge ended short of room with nothing the job may commit: it had taken no row yet, or part of an
/// object it takes whole or not at all. Its objects are gone and its blocks given back; the handle that ran it kept what
/// each of its inputs held open as its file said it (<see cref="DatasetCompactor"/>), and the caller plans again, which
/// counts them at that, rather than run the same job twice.
/// </summary>
/// <remarks>
/// A merge short of room that took rows it may commit commits them, a piece of itself, and throws nothing: this is what is
/// left of a job given up, for the kinds of job that cannot commit part way.
/// </remarks>
internal sealed class JobStoppedException : Exception
{
    /// <summary>A job of <paramref name="job"/>'s inputs ended short of room once its blocks held <paramref name="held"/> under a jobs' budget of <paramref name="budget"/>.</summary>
    internal JobStoppedException(CompactionJob job, long held, long budget, Exception inner)
        : base(
            string.Create(
                CultureInfo.InvariantCulture,
                $"A compaction of {job.Inputs.Count} objects and {job.Rows:N0} rows ended short of room before it could commit a row, its blocks holding {held:N0} bytes at most under a jobs' budget of {budget:N0}."),
            inner)
    {
        Job = job;
        Held = held;
        Budget = budget;
    }

    /// <summary>The job that ended.</summary>
    internal CompactionJob Job { get; }

    /// <summary>The reservation that was not granted, and what its inputs and its writer held then, for the job's activity.</summary>
    internal string? Reason { get; init; }

    /// <summary>
    /// Whether the handle kept, of an input of the job, that it holds more open than it was counted at
    /// (<see cref="VortexDataset.LearnOpenBytes"/>): the next plan then counts it so, and takes fewer inputs open at once.
    /// </summary>
    internal bool Learned { get; init; }

    /// <summary>The most its blocks held.</summary>
    internal long Held { get; }

    /// <summary>The jobs' budget it ran under.</summary>
    internal long Budget { get; }
}
