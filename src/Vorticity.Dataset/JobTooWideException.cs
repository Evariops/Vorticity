using System;
using System.Globalization;

namespace Vorticity.Dataset;

/// <summary>
/// A compaction job whose memory was refused a block: its inputs and its writer needed more than the jobs' budget held. Its
/// outputs were given up and its blocks given back; the handle that ran it takes half as many inputs in its next plan
/// (<see cref="VortexDataset.NarrowJobs"/>), and the caller plans again rather than run the same job twice.
/// </summary>
internal sealed class JobTooWideException : Exception
{
    /// <summary>A job of <paramref name="job"/>'s inputs refused a block once its blocks held <paramref name="held"/> under a jobs' budget of <paramref name="budget"/>.</summary>
    internal JobTooWideException(CompactionJob job, long held, long budget, Exception inner)
        : base(
            string.Create(
                CultureInfo.InvariantCulture,
                $"A compaction of {job.Inputs.Count} objects and {job.Rows:N0} rows was refused a block once its blocks held {held:N0} bytes, under a jobs' budget of {budget:N0}."),
            inner)
    {
        Job = job;
        Held = held;
        Budget = budget;
    }

    /// <summary>
    /// A job of <paramref name="job"/>'s inputs given up before it read anything: the measure of an input, the handle's first,
    /// lets the jobs' budget of <paramref name="budget"/> hold <paramref name="fits"/> of them.
    /// </summary>
    internal JobTooWideException(CompactionJob job, int fits, long budget)
        : base(
            string.Create(
                CultureInfo.InvariantCulture,
                $"A compaction of {job.Inputs.Count} objects, priced by the measure of one, fits {fits} of them under a jobs' budget of {budget:N0}."))
    {
        Job = job;
        Budget = budget;
        Priced = true;
    }

    /// <summary>The job refused.</summary>
    internal CompactionJob Job { get; }

    /// <summary>The rows of an input's batch and the inputs it held open at once when refused; 0 before it sized them.</summary>
    internal (int BatchRows, int Open) Sizing { get; init; }

    /// <summary>Whether it was given up before it read anything, priced by its first input's measure.</summary>
    internal bool Priced { get; }

    /// <summary>What its blocks held when one was refused.</summary>
    internal long Held { get; }

    /// <summary>The jobs' budget it ran under.</summary>
    internal long Budget { get; }
}
