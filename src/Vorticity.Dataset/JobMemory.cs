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

    /// <summary>
    /// The memory of a job of <paramref name="dataset"/>, under its session's jobs' budget: funded by <paramref name="grant"/>,
    /// what the job reserved before it started (<see cref="QueryMemoryBudget.ReserveAsync"/>), which its blocks take first.
    /// </summary>
    internal JobMemory(VortexDataset dataset, MemoryGrant? grant = null)
    {
        _dataset = dataset;
        Jobs = QueryMemoryBudget.JobsOf(dataset.Session);
        Job = grant is null ? Jobs.HardChild() : Jobs.HardChild(grant);
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

    /// <summary>The lane the job's own flow holds, which its objects' sinks give back while they wait for the store; null before it holds one.</summary>
    internal Execution.HeldLane? Lane { get; set; }

    /// <summary>The account its inputs' scans and the merge rent under, which the job closes at its end.</summary>
    internal MemoryAccount InputsAccount => _inputs;

    /// <summary>Whether a block was refused to the job: its budget did not hold it.</summary>
    internal bool Refused => Job.WasRefused || Inputs.WasRefused || Writer.WasRefused;

    /// <summary>
    /// The rows of the batches the inputs the merge opens next read, once the job measured what the open ones hold
    /// (<see cref="Reprice"/>); 0 before, when the plan's rows hold.
    /// </summary>
    internal int InputRows { get; private set; }

    /// <summary>
    /// Reprices the batches of the inputs a merge has yet to open, <paramref name="total"/> of them in all and
    /// <paramref name="open"/> open, read at <paramref name="rows"/> rows: what the open ones hold now, an input's share of it
    /// times the inputs to come, must leave the writer what it was priced at, <paramref name="writer"/>, under the job's
    /// ceiling; the rows halve until it does, down to <paramref name="least"/>. A job priced by a guess, or by a measure of one
    /// input of another shape, ran past the jobs' budget and was given up twice before a job of a fifth of its inputs ran
    /// (2026-11-16): narrowed here as its inputs open, it runs at its width.
    /// </summary>
    internal void Reprice(int open, int total, int rows, long writer, int least)
    {
        if (open <= 0 || open >= total)
        {
            return;
        }

        int now = InputRows > 0 ? InputRows : rows;
        long held = Inputs.ReservedBytes;
        long perInput = held / open;
        long room = Jobs.CeilingBytes - writer - held;
        long needed = perInput * (total - open);
        while (needed > room && now > least)
        {
            now /= 2;
            needed /= 2;
        }

        InputRows = Math.Max(now, least);
    }

    /// <summary>Which budget refused the job a block, and what its inputs and its writer held at most, in MiB: for its activity.</summary>
    internal string RefusalReason =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"refused by the {(Inputs.WasRefused ? "inputs" : Writer.WasRefused ? "writer" : "job")}, inputs {Inputs.PeakBytes >> 20} MiB, writer {Writer.PeakBytes >> 20} MiB, batches of {Sizing.BatchRows} rows, {Sizing.Open} open");

    /// <summary>The most bytes its blocks held at once.</summary>
    internal long Peak => Job.PeakBytes;

    /// <summary>The most bytes its inputs' blocks held at once.</summary>
    internal long InputsPeak => Inputs.PeakBytes;

    /// <summary>The most bytes its writer's blocks held at once.</summary>
    internal long WriterPeak => Writer.PeakBytes;

    /// <summary>
    /// The rows of an input's batch and the inputs open at once, once the job sized them; 0 before: what a job refused a
    /// block tells the next plan of what an input holds (<see cref="VortexDataset.NarrowJobs"/>).
    /// </summary>
    internal (int BatchRows, int Open) Sizing { get; set; }

    /// <summary>The bytes of its inputs it copied to the scratch; none for a job that reads them from the store.</summary>
    internal long CopiedBytes => _copies?.CopiedBytes ?? 0;

    /// <summary>
    /// What the jobs' budget leaves the job now: what the jobs running hold taken off, but what is left of the job's own grant,
    /// which is its to use.
    /// </summary>
    internal long Room => Math.Max(0, Jobs.CeilingBytes - Jobs.ReservedBytes + Job.GrantLeft);

    /// <summary>
    /// The bytes the writer of the job was priced at (<see cref="DatasetCompactor.WriterCost"/>), which the flows upstream of
    /// it leave it before they take room ahead: what it does not hold yet of them is kept for it.
    /// </summary>
    internal long WriterPrice { get; set; }

    /// <summary>What the writer was priced at past what it holds now, which the reads and the merge ahead leave it.</summary>
    private long WriterKeep => Math.Max(0, WriterPrice - Writer.ReservedBytes);

    /// <summary>
    /// Whether the job admits a window of <paramref name="bytes"/> decoded ahead by an input of its merge, past the two its
    /// price counts: the room left holds it and still what the writer may take. Counted when refused.
    /// </summary>
    internal bool AdmitsRead(long bytes) => Admits(bytes, WriterKeep, ref _readsHeldBack);

    /// <summary>
    /// Whether the job admits a slice of its merge of <paramref name="bytes"/> merged ahead on a lane of its own: the room left
    /// holds it and still what the writer may take. Counted when refused.
    /// </summary>
    internal bool AdmitsMerge(long bytes) => Admits(bytes, WriterKeep, ref _slicesHeldBack);

    /// <summary>
    /// Whether the job admits one more block of <paramref name="bytes"/> in flight on its writer's line past the one that must
    /// go: the room left holds the block its writer stages next. The writer comes first: nothing is kept downstream of it.
    /// Counted when refused.
    /// </summary>
    internal bool AdmitsWrite(long bytes) => Admits(bytes, 0, ref _flightsHeldBack);

    /// <summary>The units each flow did not start ahead for want of room: windows decoded, slices merged, blocks in flight.</summary>
    internal (int Reads, int Slices, int Flights) HeldBack =>
        (Volatile.Read(ref _readsHeldBack), Volatile.Read(ref _slicesHeldBack), Volatile.Read(ref _flightsHeldBack));

    private int _readsHeldBack;
    private int _slicesHeldBack;
    private int _flightsHeldBack;

    /// <summary>Whether the room left holds <paramref name="bytes"/> past <paramref name="keep"/>; a refusal counted in <paramref name="refused"/>.</summary>
    private bool Admits(long bytes, long keep, ref int refused)
    {
        if (Room - bytes >= keep)
        {
            return true;
        }

        Interlocked.Increment(ref refused);
        return false;
    }

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
        _copies ??= JobInputs.For(_dataset, job, whole);
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

        // What is left of the job's grant goes back now, the waiting jobs served; what its blocks still hold, as they go.
        Job.CloseGrant();
        Jobs.KeepPoolWithin();
    }
}
