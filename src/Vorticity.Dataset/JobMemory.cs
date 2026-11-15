using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;

namespace Vorticity.Dataset;

/// <summary>
/// The memory of one compaction job: the blocks its inputs hold, their reads, their batches and the merge's keys, and those
/// its writer holds, its arenas, its builders and the sinks of its objects. Each under a hard budget of its own, both under
/// the job's, under the jobs' budget of the session: a block none of them can take past the jobs' ceiling is refused, and
/// the job gives way rather than count it past, as a lane's growth would be (<see cref="QueryMemoryBudget.Hard"/>). And
/// the copies of its inputs in the local scratch, which it reads them from once they are large enough
/// (<see cref="JobInputs"/>).
/// </summary>
/// <remarks>
/// The job learns from it what an input and its writer held at most, which the next plan of the handle sizes a job by
/// (<see cref="VortexDataset.LearnJobCosts"/>): a measure, where a model of four constants was 1.4 to 3.1 times off on
/// three jobs of real metric records.
/// </remarks>
internal sealed class JobMemory : IAsyncDisposable
{
    private readonly VortexDataset _dataset;
    private readonly WorkAccount _inputs;
    private JobInputs? _copies;

    /// <summary>The memory of a job of <paramref name="dataset"/>, under its session's jobs' budget.</summary>
    internal JobMemory(VortexDataset dataset)
    {
        _dataset = dataset;
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

    /// <summary>The bytes of its inputs it copied to the scratch; none for a job that reads them from the store.</summary>
    internal long CopiedBytes => _copies?.CopiedBytes ?? 0;

    /// <summary>
    /// Reads the inputs of <paramref name="job"/> through the local scratch when they are large enough
    /// (<see cref="JobInputs.FromBytes"/>), each copied once as the first pass asks for it; but those it reads a part of,
    /// <paramref name="partly"/>, from the store, a range of their rows each time.
    /// </summary>
    /// <exception cref="VortexMemoryException">No scratch on a disk, or not room enough there for the job's inputs.</exception>
    internal void ReadThroughScratch(CompactionJob job, IReadOnlySet<string>? partly = null)
    {
        long whole = 0;
        foreach (CompactionInput input in job.Inputs)
        {
            whole += partly is not null && partly.Contains(input.Entry.Key) ? 0 : input.Entry.Bytes;
        }

        _partly = partly;
        _copies ??= JobInputs.For(_dataset, job, whole, _inputs);
    }

    // The inputs the job reads a part of, from the store rather than from a copy; null for none.
    private IReadOnlySet<string>? _partly;

    /// <summary>A lease of the job's input <paramref name="entry"/>: from its copy in the scratch, or from the store.</summary>
    internal ValueTask<ObjectLease> RentAsync(ObjectEntry entry, CancellationToken cancellationToken) =>
        _copies is { } copies && _partly?.Contains(entry.Key) != true
            ? copies.RentAsync(entry, cancellationToken)
            : _dataset.RentAsync(entry, cancellationToken);

    /// <summary>
    /// Closes the copies and removes them, gives back what the inputs' blocks still hold, and has the pool let go of what it
    /// keeps past what the jobs' budget leaves now: the job's blocks went back to it, of every size the job asked for.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_copies is { } copies)
        {
            _copies = null;
            await copies.DisposeAsync().ConfigureAwait(false);
        }

        _inputs.Dispose();
        Jobs.KeepPoolWithin();
    }
}
