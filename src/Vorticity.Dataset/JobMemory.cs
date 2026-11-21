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

    /// <summary>What each part of the job may hold, dealt from the jobs' budget as the job starts (<see cref="JobShares.Of"/>).</summary>
    internal JobShares Shares { get; private set; }

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
        Shares = JobShares.Of(Jobs.CeilingBytes);
    }

    /// <summary>The jobs' budget the job reserves in.</summary>
    internal QueryMemoryBudget Jobs { get; }

    /// <summary>The job's own budget, its inputs' and its writer's together.</summary>
    internal QueryMemoryBudget Job { get; }

    /// <summary>What its inputs rent under: their reads, their batches, the merge's keys and windows.</summary>
    internal QueryMemoryBudget Inputs { get; }

    /// <summary>What its writer rents under: each object's writer and its sink.</summary>
    internal QueryMemoryBudget Writer { get; }

    /// <summary>The inputs its first pass counted from their path directories alone, reading none of their rows.</summary>
    internal int InputsToldByDirectories => Volatile.Read(ref _toldByDirectories);

    private int _toldByDirectories;

    /// <summary>Notes an input its first pass counted from its directories alone, from the lane that counted it.</summary>
    internal void ToldByDirectories() => Interlocked.Increment(ref _toldByDirectories);

    /// <summary>The inputs its first pass counted in parts of their rows, side by side, and the parts.</summary>
    internal (int Inputs, int Parts) CensusParts { get; set; }

    /// <summary>The lane the job's own flow holds, which its objects' sinks give back while they wait for the store; null before it holds one.</summary>
    internal Execution.HeldLane? Lane { get; set; }

    /// <summary>The account its inputs' scans and the merge rent under, which the job closes at its end.</summary>
    internal MemoryAccount InputsAccount => _inputs;

    /// <summary>Whether a block was refused to the job: its budget did not hold it.</summary>
    internal bool Refused => Job.WasRefused || Inputs.WasRefused || Writer.WasRefused;

    /// <summary>
    /// What the inputs a merge of the job holds open at once were counted to hold at most, their windows along the keys
    /// (<see cref="DatasetCompactor.Peak"/>): what the work started ahead leaves the inputs the merge has yet to open
    /// (<see cref="StillToOpen"/>). 0 before the job sized its batches.
    /// </summary>
    internal long InputsPlanned { get; set; }

    /// <summary>
    /// What the work the job started ahead holds of its inputs' blocks now: the windows its inputs decode ahead of their own
    /// (<see cref="Scanning.DecodeAhead.Bytes"/>) and the slices merged ahead of the one taken; null for none.
    /// </summary>
    internal Func<long>? AheadHeld { get; set; }

    /// <summary>
    /// What the inputs the merge has yet to open may still take: what they were counted to hold at once
    /// (<see cref="InputsPlanned"/>), less what the inputs hold now that no work ahead holds. A unit ahead is admitted in
    /// what the inputs' share holds past it, so that an input the merge reaches finds the room its window was counted at.
    /// </summary>
    /// <remarks>
    /// A merge opens an input when it reaches its first key, and cannot go on without it. Admitted by what the inputs
    /// held alone, the windows decoded ahead of the inputs open took the room of those still to open.
    /// </remarks>
    private long StillToOpen =>
        Math.Max(0, InputsPlanned - Math.Max(0, Inputs.ReservedBytes - (AheadHeld?.Invoke() ?? 0)));

    // The inputs the job's merges opened, each with the account its scan rents under and the rows of its batches: what each
    // held at most, measured.
    private readonly List<(ObjectEntry Entry, CountedAccount Account, int BatchRows)> _opened = [];

    /// <summary>
    /// The account the scan of the job's input <paramref name="entry"/> rents under, read in batches of
    /// <paramref name="batchRows"/> rows: it counts in <paramref name="under"/>, the merge's or a slice's, and tells what
    /// the input holds (<see cref="OpenedInputs"/>).
    /// </summary>
    internal MemoryAccount OpenInput(ObjectEntry entry, MemoryAccount under, int batchRows)
    {
        CountedAccount account = new CountedAccount(under);
        lock (_opened)
        {
            _opened.Add((entry, account, batchRows));
        }

        return account;
    }

    /// <summary>
    /// The inputs the job's merges opened so far, each with what its scan held at most and the rows of its batches: a
    /// slice that reads an input opens it again, and it is listed once a scan.
    /// </summary>
    internal (ObjectEntry Entry, long Peak, int BatchRows)[] OpenedInputs()
    {
        lock (_opened)
        {
            (ObjectEntry, long, int)[] inputs = new (ObjectEntry, long, int)[_opened.Count];
            for (int i = 0; i < inputs.Length; i++)
            {
                inputs[i] = (_opened[i].Entry, _opened[i].Account.Peak, _opened[i].BatchRows);
            }

            return inputs;
        }
    }

    /// <summary>Which budget refused the job a block, and what its inputs and its writer held at most, in MiB: for its activity.</summary>
    internal string RefusalReason =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"refused by the {(Inputs.WasRefused ? "inputs" : Writer.WasRefused ? "writer" : "job")}, inputs {Inputs.PeakBytes >> 20} MiB, writer {Writer.PeakBytes >> 20} MiB, batches of {Sizing.BatchRows} rows, {Sizing.Open} open{(Slices?.Invoke() is { } slices ? ", " + slices : "")}");

    /// <summary>What the slices of the job's merge hold and were priced at, for its activity; null for a merge in one piece.</summary>
    internal Func<string>? Slices { get; set; }

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

    /// <summary>
    /// What the writer was priced at, or held at most so far when that is more, past what it holds now: which the reads and
    /// the merge ahead leave it. A writer's chunks come and go, and what it held once it takes again with the next.
    /// </summary>
    private long WriterKeep => Math.Max(0, Math.Max(WriterPrice, Writer.PeakBytes) - Writer.ReservedBytes);

    /// <summary>
    /// Whether the job admits a window of <paramref name="bytes"/> decoded ahead by an input of its merge, past those its
    /// window counts: the inputs' share holds it past what they hold and what the inputs still to open may take
    /// (<see cref="JobShares.MergeAhead"/>, <see cref="StillToOpen"/>), and the room left holds it and still what the writer
    /// may take. Counted when refused.
    /// </summary>
    internal bool AdmitsRead(long bytes) => Admits(bytes, Inputs, Shares.MergeAhead - StillToOpen, WriterKeep, ref _readsHeldBack);

    /// <summary>
    /// Whether the job admits a slice of its merge merged ahead on a lane of its own, which with the slices ahead already
    /// merging may still take <paramref name="bytes"/>: the inputs' share holds them past what they hold and what the inputs
    /// still to open may take, and the room left holds them and still what the writer may take. Counted when refused.
    /// </summary>
    internal bool AdmitsMerge(long bytes) => Admits(bytes, Inputs, Shares.MergeAhead - StillToOpen, WriterKeep, ref _slicesHeldBack);

    /// <summary>
    /// Whether the job admits one more block of <paramref name="bytes"/> in flight on its writer's line past the one that must
    /// go: the writer's share holds it past what the writer holds (<see cref="JobShares.Writer"/>), and the room left holds
    /// it. The writer comes first: nothing is kept downstream of it. Counted when refused.
    /// </summary>
    internal bool AdmitsWrite(long bytes) => Admits(bytes, Writer, Shares.Writer, 0, ref _flightsHeldBack);

    /// <summary>The units each flow did not start ahead for want of room: windows decoded, slices merged, blocks in flight.</summary>
    internal (int Reads, int Slices, int Flights) HeldBack =>
        (Volatile.Read(ref _readsHeldBack), Volatile.Read(ref _slicesHeldBack), Volatile.Read(ref _flightsHeldBack));

    private int _readsHeldBack;
    private int _slicesHeldBack;
    private int _flightsHeldBack;

    /// <summary>
    /// Whether <paramref name="part"/>, its share <paramref name="share"/>, holds <paramref name="bytes"/> more, and the room
    /// left holds them past <paramref name="keep"/>; a refusal counted in <paramref name="refused"/>.
    /// </summary>
    private bool Admits(long bytes, QueryMemoryBudget part, long share, long keep, ref int refused)
    {
        if (part.ReservedBytes + bytes <= share && Room - bytes >= keep)
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
