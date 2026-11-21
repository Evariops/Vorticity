using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;

namespace Vorticity.Dataset;

/// <summary>
/// The memory of one compaction job: the blocks its inputs hold, their reads, their batches and the merge's keys, and those
/// its writer holds, its arenas, its builders and the sinks of its objects. Each under a budget of its own, both under the
/// job's, under the jobs' budget of the session. And the copies of its inputs in the local scratch, which it reads them from
/// once they are large enough (<see cref="JobInputs"/>).
/// </summary>
/// <remarks>
/// <para>
/// Nothing of a job is priced before it runs, and no block is refused it to give it up. Each part that takes memory by the
/// data reserves it just before it takes it, at a size it reads (<see cref="MemoryReserve"/>): an input, a chunk's segments
/// in its file's footer, what a read of the chunk decodes whole in the chunk's header, a batch at what the heaviest before
/// it held (<see cref="OpenInput"/>). Work started ahead, a window decoded ahead, a slice merged ahead, a block in flight,
/// asks without waiting and is not started where its room is not (<see cref="AdmitsRead"/>, <see cref="AdmitsMerge"/>,
/// <see cref="AdmitsWrite"/>).
/// </para>
/// <para>
/// A reservation the job cannot go on without is asked on the flow that drives the job, which first gives back what it
/// holds past what it needs to go on, a level at a time (<see cref="MakeRoomAsync"/>): at 0, its writers copy the runs
/// they keep of the inputs done, which go; at 1, the blocks in flight on its writers' lines land; at 2, the slices merged
/// ahead are given up and the batches decoded ahead given back; at 3, its writers end their group of rows, each column
/// writing what it gathered. Once nothing of that is left, the merge ends at the key it reached and the job commits what
/// it merged (<see cref="ReserveExhaustedException"/>): the object it was writing is sealed when every row left lies past
/// the last key written, which the next key of the input that found no room, read in its file, says
/// (<see cref="IMergedRuns.Stopped"/>).
/// </para>
/// <para>
/// The inputs' budget is never passed (<see cref="QueryMemoryBudget.Hard"/>), and an input reserves what it cannot go on
/// without only in the room that leaves the writer what it has yet to take (<see cref="AdmitsInput"/>): it is then an
/// input, on the job's flow, that finds no room first, where the merge can end at a key, and not a lane of the writer in
/// the middle of a chunk. A block of theirs no reserve covers, a window the merge copies, is refused past the budget,
/// and ends the merge where nothing says it ends on a whole key: the job commits the objects it had sealed, each ended
/// where its key did, and gives up the one it was writing. A job so ended before it sealed an object commits nothing,
/// and its handle plans again. Work started ahead is admitted by the process too, whose room the jobs' budget does not
/// follow (<see cref="ProcessHolds"/>).
/// </para>
/// <para>
/// The job's writer can wait on the job's flow alone, between two blocks or before a chunk leaves for its emission: a
/// lane of it is in the middle of a chunk it cannot leave half encoded. There it reserves the copies whose size it
/// counted, a block's rows as it is assembled and a chunk's as it leaves (<see cref="WriterReserve"/>), and, while its
/// share does not hold them past what it holds (<see cref="AdmitsWrite"/>), waits for what it has in the air: its blocks
/// in flight land, its chunks in emission end. A wait changes nothing of what it writes. What a lane takes past what was
/// reserved, a shredding's copies, an encoder's output, is counted past the writer's budget rather than refused, the
/// process bounding it (<see cref="QueryMemoryBudget.CountsPast"/>): refused one, it gave up the job and all it had
/// written. The writer makes up for it at its next wait, and with nothing left in the air goes on at what it holds at
/// least. While the job does not hold what the writer holds (<see cref="HoldsWriter"/>), the writer's arenas give back
/// the blocks they keep from one use to the next, and it lays out one leaf of a shredded chunk at a time.
/// </para>
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
        // The inputs' budget is never passed. The writer's, and the job's for it, count past their ceiling what the writer
        // could not reserve (QueryMemoryBudget.CountsPast): a writer cannot wait in the middle of a chunk.
        Job = grant is null ? Jobs.HardChild(countsPast: true) : Jobs.HardChild(grant, countsPast: true);
        Inputs = Job.HardChild();
        Writer = Job.HardChild(countsPast: true);
        _inputs = new WorkAccount(Inputs, "compaction's inputs");
        WriterReserve = new MemoryReserve(Writer);
        WriterRoom = new JobWriteRoom(this);
        Shares = JobShares.Of(Jobs.CeilingBytes);
    }

    /// <summary>
    /// What the job tells its writers of its memory (<see cref="Writing.WriteRoom"/>): whether it admits what a writer is
    /// about to start (<see cref="AdmitsWrite"/>), whether it holds what the writer holds (<see cref="HoldsWriter"/>),
    /// and what it gives back ahead of the writer when a start asks (<see cref="GiveBackAhead"/>).
    /// </summary>
    internal Writing.WriteRoom WriterRoom { get; }

    private sealed class JobWriteRoom(JobMemory memory) : Writing.WriteRoom
    {
        internal override bool Admits(long bytes) => memory.AdmitsWrite(bytes);

        internal override bool Holds => memory.HoldsWriter();

        internal override ValueTask MakeRoomAsync() =>
            memory.GiveBackAhead is { } giveBack ? giveBack() : ValueTask.CompletedTask;
    }

    /// <summary>
    /// Whether the job holds what its writer holds now: the writer within its share, the job within the jobs' budget,
    /// and the process within what it leaves its work (<see cref="ProcessHolds"/>). While it does, the writer's arenas
    /// keep their blocks idle for their next use; once it does not, each gives them back where it kept them
    /// (<see cref="VortexFileWriter.KeepsBlocks"/>). Counted when it does not.
    /// </summary>
    internal bool HoldsWriter()
    {
        if (Writer.ReservedBytes <= Shares.Writer && Jobs.ReservedBytes - Job.GrantLeft <= Jobs.CeilingBytes && ProcessHolds(0))
        {
            return true;
        }

        Interlocked.Increment(ref _arenasGivenBack);
        return false;
    }

    /// <summary>The times an arena of the job's writers gave its blocks back rather than keep them, the job not holding what its writer held.</summary>
    internal long ArenasGivenBack => Interlocked.Read(ref _arenasGivenBack);

    private long _arenasGivenBack;

    /// <summary>
    /// What the job's inputs give back of what they started ahead, for a start of the writer the job does not admit once
    /// the writer waited for all it had in the air: the slices merged ahead and the batches decoded ahead. Set by the
    /// merge under way, on whose flow the writer asks; null while none runs.
    /// </summary>
    internal Func<ValueTask>? GiveBackAhead { get; set; }

    /// <summary>
    /// Whether an input of the job may reserve <paramref name="bytes"/> more: the room the jobs' budget leaves the job
    /// holds them and still what the writer has yet to take (<see cref="WriterKeep"/>), what it was counted to hold, or
    /// held at most so far, past what it holds now. What an input cannot go on without is asked on the job's flow, where
    /// a merge that finds no room ends at a key and commits what it merged.
    /// </summary>
    /// <remarks>
    /// Admitted by the budget alone, the inputs of a consolidation of 60 million log records took 484 MiB of its 672
    /// while its writer held 190: the writer then took the 80 more its chunks need, past the budget, and it was a lane
    /// of the writer, in the middle of a chunk, that the process refused a block. The job lost the object it was writing,
    /// 8.5 s of merge, where its merge, an input short of room, would have ended at a key (2026-11-21).
    /// </remarks>
    private bool AdmitsInput(long bytes) => Room - bytes >= WriterKeep;

    /// <summary>
    /// Whether the process holds <paramref name="bytes"/> more now, by what the rest of its memory leaves its work
    /// (<see cref="QueryMemoryBudget.CanReserve"/>): what the jobs' budget does not say. The budget is a share of the
    /// process's limit, set once; what the process can still take moves with its heap and with the room its slabs keep
    /// free.
    /// </summary>
    /// <remarks>
    /// Work started ahead was admitted by the budget alone. Under 2 GiB, a consolidation of 60 million log records held
    /// 700 MiB of blocks within its budget of 672 while the rest of the process's memory held 1.2 to 1.35 GB: the
    /// process, past its ceiling by more than the sixteenth it lets a block count past, refused a writer's lane a block
    /// of 4 KiB to 64 KiB, in the middle of a chunk, and the job lost the object it was writing, 8 to 9 s of merge
    /// (2026-11-21).
    /// </remarks>
    private static bool ProcessHolds(long bytes) => QueryMemoryBudget.Process.CanReserve(Math.Max(bytes, 0));

    /// <summary>
    /// What the job's writers reserve in before they take a copy whose size they counted, on the job's flow: a block's rows
    /// as it is assembled, a chunk's as it leaves for its emission (<see cref="ReservingAccount"/>). One for the job: its
    /// objects are written one after the other.
    /// </summary>
    internal MemoryReserve WriterReserve { get; }

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
    /// what the inputs' share holds past it, so that an input the merge reaches is not kept waiting by work that could
    /// wait: what it was counted at chooses what is started ahead, and no longer what the job may hold.
    /// </summary>
    private long StillToOpen =>
        Math.Max(0, InputsPlanned - Math.Max(0, Inputs.ReservedBytes - (AheadHeld?.Invoke() ?? 0)));

    // The inputs the job's merges opened, each with the account its scan rents under, what it reserves in, when it
    // reserves, and the rows of its batches.
    private readonly List<(ObjectEntry Entry, CountedAccount Account, MemoryReserve? Reserve, int BatchRows)> _opened = [];

    /// <summary>
    /// The account the scan of the job's input <paramref name="entry"/> rents under, read in batches of
    /// <paramref name="batchRows"/> rows, and what it reserves in before it takes (<see cref="MemoryReserve"/>): the
    /// account counts in <paramref name="under"/>, the merge's or a slice's, and tells what the input holds
    /// (<see cref="OpenedInputs"/>). A reservation the input cannot go on without has the job give back what it started
    /// ahead through <paramref name="makeRoom"/>, the way of the merge that reads it: on the flow that drives the job for
    /// the merge that flow runs, through the runs of a slice merged ahead for that slice's
    /// (<see cref="SlicedMerge.AskRoomAsync"/>). Without one, a scan that hands no batch over and cannot run a step
    /// again, a first pass's, the account counts what the input holds and reserves nothing.
    /// </summary>
    internal (MemoryAccount Account, MemoryReserve? Reserve) OpenInput(ObjectEntry entry, MemoryAccount under, int batchRows, Func<int, ValueTask<bool>>? makeRoom)
    {
        MemoryReserve? reserve = makeRoom is null ? null : new MemoryReserve(Inputs) { MakeRoom = makeRoom, Admits = AdmitsInput };
        CountedAccount account = new CountedAccount(under, reserve);
        lock (_opened)
        {
            _opened.Add((entry, account, reserve, batchRows));
        }

        return (account, reserve);
    }

    /// <summary>
    /// The inputs the job's merges opened so far, each with what its scan held at most and the rows of its batches: a
    /// slice that reads an input opens it again, and it is listed once a scan.
    /// </summary>
    internal (ObjectEntry Entry, long Peak, int BatchRows, MemoryReserve? Reserve)[] OpenedInputs()
    {
        lock (_opened)
        {
            (ObjectEntry, long, int, MemoryReserve?)[] inputs = new (ObjectEntry, long, int, MemoryReserve?)[_opened.Count];
            for (int i = 0; i < inputs.Length; i++)
            {
                inputs[i] = (_opened[i].Entry, _opened[i].Account.Peak, _opened[i].BatchRows, _opened[i].Reserve);
            }

            return inputs;
        }
    }

    /// <summary>
    /// What the reserves of the job's inputs did together: the reservations not granted at once, the times one had the
    /// job give back what it started ahead, and the bytes reserved.
    /// </summary>
    internal (long HeldBack, long MadeRoom, long Raised) Reserves
    {
        get
        {
            long heldBack = 0;
            long madeRoom = 0;
            long raised = 0;
            lock (_opened)
            {
                foreach ((_, _, MemoryReserve? reserve, _) in _opened)
                {
                    heldBack += reserve?.HeldBack ?? 0;
                    madeRoom += reserve?.MadeRoom ?? 0;
                    raised += reserve?.Raised ?? 0;
                }
            }

            return (heldBack, madeRoom, raised);
        }
    }

    /// <summary>
    /// What the job's flow gives back of what it started ahead at a level, set by the merge under way: its writers' part,
    /// then the merge's own (<see cref="IMergedRuns.GiveBackAsync"/>); false for a level that gives nothing back. Null
    /// while no merge runs.
    /// </summary>
    internal Func<int, ValueTask<bool>>? GiveBack { get; set; }

    /// <summary>
    /// Has the job give back what it started ahead at <paramref name="level"/>, from 0: what a reservation it cannot go on
    /// without asks, level after level, until it is granted. On the flow that drives the job.
    /// </summary>
    /// <returns>False once no such level is left: the job holds what it needs to go on and nothing else.</returns>
    internal ValueTask<bool> MakeRoomAsync(int level) =>
        GiveBack is { } giveBack ? giveBack(level) : new ValueTask<bool>(false);

    /// <summary>What the slices of the job's merge hold and were counted at, for its activity; null for a merge in one piece.</summary>
    internal Func<string>? Slices { get; set; }

    /// <summary>The most bytes its blocks held at once.</summary>
    internal long Peak => Job.PeakBytes;

    /// <summary>The most bytes its inputs' blocks held at once.</summary>
    internal long InputsPeak => Inputs.PeakBytes;

    /// <summary>The most bytes its writer's blocks held at once.</summary>
    internal long WriterPeak => Writer.PeakBytes;

    /// <summary>The bytes of its inputs it copied to the scratch; none for a job that reads them from the store.</summary>
    internal long CopiedBytes => _copies?.CopiedBytes ?? 0;

    /// <summary>
    /// What the jobs' budget leaves the job now: what the jobs running hold taken off, but what is left of the job's own grant,
    /// which is its to use.
    /// </summary>
    internal long Room => Math.Max(0, Jobs.CeilingBytes - Jobs.ReservedBytes + Job.GrantLeft);

    /// <summary>
    /// The bytes the writer of the job was counted to hold (<see cref="DatasetCompactor.WriterCost"/>), which the flows
    /// upstream of it leave it before they take room ahead: what it does not hold yet of them is kept for it.
    /// </summary>
    internal long WriterPrice { get; set; }

    /// <summary>
    /// What the writer was counted to hold, or held at most so far when that is more, past what it holds now: which the
    /// reads and the merge ahead leave it. A writer's chunks come and go, and what it held once it takes again with the
    /// next.
    /// </summary>
    private long WriterKeep => Math.Max(0, Math.Max(WriterPrice, Writer.PeakBytes) - Writer.ReservedBytes);

    /// <summary>
    /// Whether the job admits a window of <paramref name="bytes"/> decoded ahead by an input of its merge, past those its
    /// window counts: the inputs' share holds it past what they hold and what the inputs still to open may take
    /// (<see cref="JobShares.MergeAhead"/>, <see cref="StillToOpen"/>), and the room left holds it and still what the writer
    /// may take. The input then reserves it, or does not start it. Counted when refused.
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
        // By the budget, and by the process, whose room the budget does not follow (ProcessHolds).
        if (part.ReservedBytes + bytes <= share && Room - bytes >= keep && ProcessHolds(bytes))
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
    /// Closes the copies and removes them, gives back what the inputs' blocks still hold and what its parts reserved and
    /// never took, and has the pool let go of what it keeps past what the jobs' budget leaves now: the job's blocks went
    /// back to it, of every size the job asked for.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_copies is { } copies)
        {
            _copies = null;
            await copies.DisposeAsync().ConfigureAwait(false);
        }

        GiveBack = null;
        GiveBackAhead = null;
        lock (_opened)
        {
            foreach ((_, _, MemoryReserve? reserve, _) in _opened)
            {
                reserve?.End();
            }
        }

        WriterReserve.End();
        _inputs.Dispose();

        // What is left of the job's grant goes back now, the waiting jobs served; what its blocks still hold, as they go.
        Job.CloseGrant();
        Jobs.KeepPoolWithin();
    }
}
