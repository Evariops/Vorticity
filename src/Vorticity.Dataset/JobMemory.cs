using System;
using Vorticity.Buffers;

namespace Vorticity.Dataset;

/// <summary>
/// The memory of one compaction job: the blocks its inputs hold, their reads, their batches and the merge's keys, and those
/// its writer holds, its arenas, its builders and the sinks of its objects. Each under a hard budget of its own, both under
/// the job's, under the jobs' budget of the session: a block none of them can take past the jobs' ceiling is refused, and
/// the job gives way rather than count it past, as a lane's growth would be (<see cref="QueryMemoryBudget.Hard"/>).
/// </summary>
/// <remarks>
/// The job learns from it what an input and its writer held at most, which the next plan of the handle sizes a job by
/// (<see cref="VortexDataset.LearnJobCosts"/>): a measure, where a model of four constants was 1.4 to 3.1 times off on
/// three jobs of real metric records.
/// </remarks>
internal sealed class JobMemory : IDisposable
{
    private readonly WorkAccount _inputs;

    /// <summary>The memory of a job of <paramref name="dataset"/>, under its session's jobs' budget.</summary>
    internal JobMemory(VortexDataset dataset)
    {
        Jobs = QueryMemoryBudget.JobsOf(dataset.Session);
        Job = Jobs.HardChild();
        Inputs = Job.HardChild();
        Writer = Job.HardChild();
        _inputs = new WorkAccount(Inputs, "compaction's inputs");
    }

    /// <summary>The jobs' budget the job reserves in.</summary>
    internal QueryMemoryBudget Jobs { get; }

    /// <summary>The job's own budget, its inputs' and its writer's together.</summary>
    internal QueryMemoryBudget Job { get; }

    /// <summary>What its inputs rent under: their reads, their batches, the merge's keys and windows.</summary>
    internal QueryMemoryBudget Inputs { get; }

    /// <summary>What its writer rents under: each object's writer and its sink.</summary>
    internal QueryMemoryBudget Writer { get; }

    /// <summary>The account its inputs' scans and the merge rent under, which the job closes at its end.</summary>
    internal MemoryAccount InputsAccount => _inputs;

    /// <summary>Whether a block was refused to the job: its budget did not hold it.</summary>
    internal bool Refused => Job.WasRefused || Inputs.WasRefused || Writer.WasRefused;

    /// <summary>The most bytes its blocks held at once.</summary>
    internal long Peak => Job.PeakBytes;

    /// <summary>The most bytes its inputs' blocks held at once.</summary>
    internal long InputsPeak => Inputs.PeakBytes;

    /// <summary>The most bytes its writer's blocks held at once.</summary>
    internal long WriterPeak => Writer.PeakBytes;

    /// <summary>
    /// Gives back what the inputs' blocks still hold, and has the pool let go of what it keeps past what the jobs' budget
    /// leaves now: the job's blocks went back to it, of every size the job asked for.
    /// </summary>
    public void Dispose()
    {
        _inputs.Dispose();
        Jobs.KeepPoolWithin();
    }
}
