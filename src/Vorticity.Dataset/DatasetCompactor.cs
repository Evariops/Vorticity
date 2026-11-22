using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Indexes;
using Vorticity.Layouts;
using Vorticity.RowEncoding;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Types.Variants;
using Vorticity.Writing;

namespace Vorticity.Dataset;

/// <summary>What one compaction did.</summary>
public sealed record CompactionResult
{
    /// <summary>The version it created, or the one it found when it was abandoned.</summary>
    public ulong Version { get; init; }

    /// <summary>The level it read.</summary>
    public int FromLevel { get; init; }

    /// <summary>The level it wrote.</summary>
    public int ToLevel { get; init; }

    /// <summary>Why it ran.</summary>
    public CompactionTrigger Trigger { get; init; }

    /// <summary>Whether it merged or concatenated.</summary>
    public CompactionStyle Style { get; init; }

    /// <summary>The objects it read.</summary>
    public long ObjectsIn { get; init; }

    /// <summary>The objects it put into the level it wrote, those it moved included.</summary>
    public long ObjectsOut { get; init; }

    /// <summary>
    /// The objects it moved into the level above whole, without rewriting them: an object that
    /// overlapped nothing there, whose entry changes level and whose bytes stay where they are.
    /// </summary>
    public long ObjectsMoved { get; init; }

    /// <summary>The rows it rewrote; none for an object it moved.</summary>
    public long Rows { get; init; }

    /// <summary>The bytes it read, the denominator of write amplification; none for an object it moved.</summary>
    public long BytesIn { get; init; }

    /// <summary>The bytes it wrote; none for an object it moved.</summary>
    public long BytesOut { get; init; }

    /// <summary>
    /// The most bytes its blocks held at once, its inputs' and its writer's, under the session's jobs' budget
    /// (<see cref="VortexSessionOptions.JobMemoryBudget"/>), which they never pass: the ranges of a consolidation side by side
    /// together; none for a job that moved its objects whole.
    /// </summary>
    public long MemoryReserved { get; init; }

    /// <summary>Of <see cref="MemoryReserved"/>, the most its inputs' blocks held, and its writer's, each at its own moment.</summary>
    internal (long Inputs, long Writer) MemoryReservedBy { get; init; }

    /// <summary>
    /// What the inputs a merge of the job holds open at once were counted to hold together at most
    /// (<see cref="DatasetCompactor.Peak"/>), at the rows of the batches it read them in; 0 for a job that merged nothing.
    /// </summary>
    internal long InputsPlanned { get; init; }

    /// <summary>
    /// Each scan of an input the job's merges opened: the window it was counted at (<see cref="DatasetCompactor.Window"/>),
    /// of which what the object holds open, and what its account held at most. What tells a window that counts too little,
    /// or far too much.
    /// </summary>
    internal IReadOnlyList<(long Window, long Open, long Held)> InputWindows { get; init; } = [];

    /// <summary>
    /// The times the job began its objects again under a doubled bound, an object having passed its inputs' bytes times the
    /// bound (<see cref="ObjectPastBoundException"/>): each the whole of its writing done again.
    /// </summary>
    internal int BoundRestarts { get; init; }

    /// <summary>
    /// The most ranges of the key the job merged at once, as many as the budget admitted and the lanes free let run: a piece
    /// of a consolidation, the ranges below its cut the slices of its one stream; 1 for any other job, which writes one
    /// stream of objects.
    /// </summary>
    public int RangesAtOnce { get; init; } = 1;

    /// <summary>
    /// The bytes it put on the local disk rather than hold: the copies of its inputs it read from there, and the runs of its
    /// objects' indexes and of its partition's postings for a consolidation past what memory held of them; none for a job
    /// of small inputs whose runs all fit.
    /// </summary>
    public long ScratchBytes { get; init; }

    /// <summary>
    /// What the commit made of it: applied, or abandoned because another compaction took an input
    /// first, in which case its outputs are left for vacuum.
    /// </summary>
    public OperationOutcome Outcome { get; init; }

    /// <summary>Whether it was a piece of a consolidation, which leaves the partition's next keys in level 0 for the next job.</summary>
    internal bool Piece { get; init; }

    /// <summary>
    /// Whether its merge ended short of room, at the key it reached, and it committed what it had merged, a piece of
    /// itself: the rows it left in its inputs are the next job's.
    /// </summary>
    internal bool StoppedShort { get; init; }

    /// <summary>What ended its merge short of room, the reservation not granted or the refusal of a block no reserve covers, which names what asked for it; null for a job that ran to its end.</summary>
    internal string? StoppedBy { get; init; }

    /// <summary>
    /// How the end of the object its merge was writing was judged when it ended short of room: on a whole key, by the
    /// bound of the input being opened or by the next key of the input whose batch found no room, read in its file, and
    /// the object sealed; or not, and why, the object dropped. Null for a job that ran to its end.
    /// </summary>
    internal string? StoppedHow { get; init; }

    /// <summary>
    /// The times an arena of its writers gave back the blocks it keeps from one use to the next, the job not holding what
    /// its writer held then (<see cref="JobMemory.HoldsWriter"/>); 0 for a job whose writer stayed within its share.
    /// </summary>
    internal long WriterArenasGivenBack { get; init; }

    /// <summary>
    /// The blocks the pool kept that the process's work took while the job ran and the process's limit left its work no
    /// room for them, and those of them taken past the sixteenth a block may count past
    /// (<see cref="JobMemory.PoolBlocksPastRoom"/>): refused, when a block the heap held already was asked as new memory.
    /// </summary>
    internal (long PastRoom, long PastMargin) PoolBlocksPastRoom { get; init; }

    /// <summary>
    /// What its writer was counted to hold, which its inputs left it as they reserved (<see cref="JobMemory.WriterPrice"/>):
    /// what its blocks held may pass the jobs' budget by what its writer held past it, and by no more.
    /// </summary>
    internal long WriterCounted { get; init; }

    /// <summary>
    /// What its parts' reserves did (<see cref="MemoryReserve"/>): the reservations not granted at once, work not started
    /// ahead among them, the times one had the job give back what it had started ahead, and the bytes reserved.
    /// </summary>
    internal (long HeldBack, long MadeRoom, long Raised) Reserves { get; init; }

    /// <summary>
    /// The calls its rows reached the writers of its objects in: a block's map each, a run of a block or more cut in maps too,
    /// or a batch of a concatenation. A diagnostic of how its merge cuts them.
    /// </summary>
    internal long WriterBatches { get; init; }

    /// <summary>Of <see cref="WriterBatches"/>, those the writers ingested on their lanes rather than on the job's thread.</summary>
    internal long WriterBatchesAcross { get; init; }

    /// <summary>The batches the writers staged and sent through on a lane of their own while the merge went on.</summary>
    internal long WriterBatchesFlown { get; init; }

    /// <summary>Of <see cref="WriterBatchesFlown"/>, the blocks the writers sent through their line, several at once, each column's work on them in order.</summary>
    internal long WriterBlocksOnLine { get; init; }

    /// <summary>The chunks of the file the blocks on the writers' lines ended, each emitted before the next block ingested.</summary>
    internal long WriterChunksEndedOnLine { get; init; }

    /// <summary>Of <see cref="WriterChunksEndedOnLine"/>, those cut column by column, each column emitted once cut while the blocks after it went through.</summary>
    internal long WriterChunksCutOnLine { get; init; }

    /// <summary>The ends of groups of rows the job asked its writers for, each to read what an object held (<see cref="VortexFileWriter.EndGroupAsync"/>).</summary>
    internal int GroupEnds { get; init; }

    /// <summary>
    /// The bytes of data of each object it wrote, by its key: what the object's size counts (<see cref="DatasetLayout.ObjectBytes"/>),
    /// its statistics, zone maps, indexes and footer apart.
    /// </summary>
    internal ImmutableDictionary<string, long> ObjectDataBytes { get; init; } = ImmutableDictionary<string, long>.Empty;

    /// <summary>
    /// Of <see cref="WriterBlocksOnLine"/>, those that took their rows of a variant column from what the inputs took of
    /// them as their own shreddings hold them (<see cref="DatasetOptions.TakesVariants"/>), and read none.
    /// </summary>
    internal long WriterBlocksOfRunsTaken { get; init; }

    /// <summary>
    /// Of <see cref="WriterBlocksOfRunsTaken"/>, those whose values of the column, gathered not put back together, were put
    /// back whole before the block was read: a block the writer's line did not cut column by column, or whose column no
    /// chain took, or whose keys are read from its rows.
    /// </summary>
    internal long WriterBlocksPutBack { get; init; }

    /// <summary>
    /// The times an item of a block counted in <see cref="WriterBlocksOfRunsTaken"/> put the column's values back together
    /// in an arena of its own: a census near its most keys, a gathering whose chain a cut left broken.
    /// </summary>
    internal long WriterColumnsPutBackApart { get; init; }

    /// <summary>
    /// The chunks of a variant column the job's inputs handed over as their shreddings hold them, their rows not put back
    /// together; those they set out to take and put back together after all, each decoded twice, after which the input's
    /// scan takes no chunk more; and, of those, the ones taken whole but not byte for byte.
    /// </summary>
    internal (long Held, long Refused, long Inexact) InputVariantChunks { get; init; }

    /// <summary>The inputs' batches its merge took over from their scans and kept until its writers copied their runs.</summary>
    internal long InputWindowsTaken { get; init; }

    /// <summary>The inputs' batches its merge copied to keep, not taken over from their scans.</summary>
    internal long InputWindowsCopied { get; init; }

    /// <summary>The inputs' batches whose keys its merge's thread encoded, rather than the decode that made them.</summary>
    internal long InputWindowsKeyedByMerge { get; init; }

    /// <summary>The slices of the key its merge was cut in (<see cref="SlicedMerge"/>), 0 for a merge in one piece.</summary>
    internal int MergeSlices { get; init; }

    /// <summary>Of <see cref="MergeSlices"/>, those merged ahead on lanes of their own rather than by the job's flow.</summary>
    internal int MergeSlicesAhead { get; init; }

    /// <summary>
    /// The windows its inputs decoded ahead past their own two, shared by its merges' inputs and taken by those it read
    /// (<see cref="Scanning.DecodeAhead"/>): the input it drains decoded on several lanes.
    /// </summary>
    internal long InputWindowsAhead { get; init; }

    /// <summary>The waits for the store its objects made with the job's lane given back (<see cref="Execution.HeldLane"/>).</summary>
    internal int StoreWaitsWithoutLane { get; init; }

    /// <summary>
    /// The units its flows did not start ahead for want of room in its memory (<see cref="JobMemory.HeldBack"/>): windows its
    /// inputs did not decode ahead, slices its merge did not merge ahead, blocks its writers did not send through at once.
    /// </summary>
    internal (int Reads, int Slices, int Flights) AdvancesHeldBack { get; init; }

    /// <summary>The inputs its first pass counted from their path directories alone, reading none of their rows (<see cref="Indexes.VariantDirectory.ValueBytes"/>).</summary>
    internal int InputsToldByDirectories { get; init; }

    /// <summary>The inputs its first pass counted in parts of their rows side by side, and the parts (<see cref="DatasetOptions.CensusPartRows"/>).</summary>
    internal (int Inputs, int Parts) CensusParts { get; init; }
}

/// <summary>Runs one compaction: a k-way merge of the inputs on the sort key when there is
/// one, a concatenation otherwise.</summary>
internal static partial class DatasetCompactor
{
    /// <summary>Reads the job's inputs and replaces them by the objects it writes; the dataset moves
    /// to the version the commit creates.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The job has no input.</exception>
    /// <exception cref="VortexUnsupportedException">
    /// The sort key is composite and one of its columns holds nulls in an input: its run holds
    /// no such tuple, so no permuted read keeps every row.
    /// </exception>
    /// <exception cref="DatasetIntegrityException">The outputs do not hold the inputs' rows.</exception>
    public static async ValueTask<CompactionResult> RunAsync(
        VortexDataset dataset, CompactionJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(job);
        if (job.Inputs.Count == 0)
        {
            throw new ArgumentException("A compaction reads at least one object.", nameof(job));
        }

        // Its reads keep nothing in the session's segment cache: each object once, which questions would not find there
        // again.
        using SegmentCache.PassingScope passing = SegmentCache.Pass();

        // One activity over the job, its commit's as a child; a job counted however it ends.
        Activity? activity = DatasetTelemetry.StartCompaction(job, dataset.Location);
        CompactionResult result;
        try
        {
            result = job.Trigger == CompactionTrigger.Fragments
                ? await BundleAsync(dataset, job, cancellationToken).ConfigureAwait(false)
                : await RewriteAsync(dataset, job, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            DatasetTelemetry.CompactionFailed(activity, job.Trigger, failure);
            throw;
        }

        DatasetTelemetry.CompactionEnded(activity, result);
        return result;
    }

    /// <summary>
    /// The job's rows read, merged or concatenated, written, and its replacement committed, every block it rents under its
    /// own memory (<see cref="JobMemory"/>). A merge short of room it cannot go on without ends at the key it reached and
    /// the job commits what it merged, a piece of itself. One that may commit nothing of what it took, an object of a
    /// level past 0 taken in part, or nothing taken yet, throws <see cref="JobStoppedException"/> once its objects are
    /// gone and its blocks given back: the handle keeps what each of its inputs held open as its file said it
    /// (<see cref="KeepOpen"/>), and its next plan counts them at that.
    /// </summary>
    private static async ValueTask<CompactionResult> RewriteAsync(
        VortexDataset dataset, CompactionJob job, CancellationToken cancellationToken)
    {
        // What the job holds at least, reserved before it reads anything, while it holds nothing: when another job, a table's
        // of the same process, holds the memory, it waits for it, and runs at its width, where a block refused at its start
        // gave it up and the next plan narrowed it. Nothing reserved when nothing under the jobs' budget holds what would
        // make room: the job starts all the same, and its inputs reserve as they read.
        QueryMemoryBudget jobs = QueryMemoryBudget.JobsOf(dataset.Session);
        MemoryGrant? grant = await jobs.ReserveAsync(Math.Min(LeastBytes(dataset, job), JobBudget(dataset)), cancellationToken).ConfigureAwait(false);
        JobMemory memory;
        try
        {
            memory = new JobMemory(dataset, grant);
        }
        catch
        {
            grant?.Release();
            throw;
        }

        // The job's own flow computes, its merge first: it holds a lane of the session's for as long as it runs, taken once
        // its memory is, so that a job waiting for memory holds no lane. Everything it starts beside it takes a lane free now
        // or runs on this one, and never waits for one, which this lane, held by the waiter, would never free.
        // Its objects' sinks give the lane back while they wait for the store, and take it again after (HeldLane).
        Execution.LaneBudget lanes = dataset.Session.Lanes;
        Execution.HeldLane lane;
        try
        {
            lane = new Execution.HeldLane(lanes, await lanes.EnterAsync(cancellationToken).ConfigureAwait(false));
        }
        catch
        {
            await memory.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        memory.Lane = lane;
        try
        {
            return await RewriteUnderAsync(dataset, job, memory, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is ReserveExhaustedException or VortexMemoryException && !cancellationToken.IsCancellationRequested)
        {
            // Ended short of room with nothing it may commit, a reservation not granted or a block no reserve covers
            // refused, in its merge or in a pass before it: what its inputs held open, read in their files as it went, is
            // what the next plan counts them at.
            ReserveExhaustedException? stop = failure as ReserveExhaustedException;
            bool learned = KeepOpen(dataset, memory, stop);
            (long heldBack, long madeRoom, _) = memory.Reserves;

            // A refusal says itself what its budget, the process and the process's pool held then.
            string refusal = (stop?.InnerException ?? failure).Message;
            (long pastRoom, long pastMargin) = memory.PoolBlocksPastRoom;
            throw new JobStoppedException(job, memory.Peak, memory.Jobs.CeilingBytes, failure)
            {
                Learned = learned,
                Reason = string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"{(stop is { InnerException: null } ? $"a reservation of {stop.Bytes >> 10} KiB not granted" : $"a block no reservation covers refused [{refusal}]")}, inputs {memory.InputsPeak >> 20} MiB, writer {memory.WriterPeak >> 20} MiB, {heldBack} reservations held back, room made {madeRoom} times, {pastRoom} blocks of the pool taken past the process's room and {pastMargin} past its margin{(memory.Slices?.Invoke() is { } slices ? ", " + slices : "")}"),
            };
        }
        finally
        {
            memory.Lane = null;
            lane.Exit();
            await memory.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Each scan of an input the job's merges opened, for its result: the window it was counted at, of which what its
    /// object holds open, and what its account held at most (<see cref="CompactionResult.InputWindows"/>).
    /// </summary>
    private static (long Window, long Open, long Held)[] WindowsOf(VortexDataset dataset, DatasetSchema schema, JobMemory memory)
    {
        (ObjectEntry Entry, long Peak, int BatchRows, MemoryReserve? Reserve)[] opened = memory.OpenedInputs();
        (long, long, long)[] windows = new (long, long, long)[opened.Length];
        for (int i = 0; i < windows.Length; i++)
        {
            (ObjectEntry entry, long peak, int rows, _) = opened[i];
            windows[i] = (Window(dataset, schema, entry, Math.Max(rows, 1)), OpenBytes(dataset, schema, entry), peak);
        }

        return windows;
    }

    /// <summary>
    /// Keeps, for each input a job opened, what its scan held open when that is more than the handle counted it at
    /// (<see cref="OpenBytes"/>): what it reserved as it read it in the object's file and took, its account's peak, less
    /// the batches its window counts; and, for the input whose reservation ended the job short of room,
    /// <paramref name="stop"/>'s, what it asked for past what it held. The next plan counts the object at that
    /// (<see cref="VortexDataset.MeasuredOpenBytes"/>), which chooses a job that holds: an object whose entry says nothing,
    /// written before entries said what a reader holds, is counted at what its file says once a job read it.
    /// </summary>
    /// <returns>Whether an input held more than it was counted at.</returns>
    private static bool KeepOpen(VortexDataset dataset, JobMemory memory, ReserveExhaustedException? stop)
    {
        DatasetSchema schema = dataset.Snapshot.Schema;
        bool learned = false;
        foreach ((ObjectEntry entry, long peak, int rows, MemoryReserve? reserve) in memory.OpenedInputs())
        {
            long open = peak - (BatchesHeld * Math.Min(entry.PhysicalRows, Math.Max(rows, 1)) * DecodedRowBytes(dataset, entry));
            if (stop is not null && reserve is not null && ReferenceEquals(stop.Reserve, reserve))
            {
                open = Math.Max(open, peak + stop.Bytes);
            }

            if (open > OpenBytes(dataset, schema, entry))
            {
                dataset.LearnOpenBytes(entry.Key, open);
                learned = true;
            }
        }

        return learned;
    }

    /// <summary>The job's rows read, merged or concatenated, written, and its replacement committed, under <paramref name="memory"/>.</summary>
    private static async ValueTask<CompactionResult> RewriteUnderAsync(
        VortexDataset dataset, CompactionJob job, JobMemory memory, CancellationToken cancellationToken)
    {
        DateTimeOffset started = dataset.Options.TimeProvider.GetUtcNow();
        if (job.Moves && await MoveAsync(dataset, job, cancellationToken).ConfigureAwait(false) is { } moved)
        {
            return moved;
        }

        // Every input is read as this schema and every output written in it: a compaction is also
        // how objects of an earlier schema come to hold the current one.
        // A leveled job merges on the key, and so does a tiered one of a dataset with a key: its merges and its
        // consolidations; a tiered one of a dataset without concatenates.
        DatasetSchema schema = dataset.Snapshot.Schema;
        bool merge = job.Style is CompactionStyle.Leveled || (job.Style is CompactionStyle.Tiered && schema.Keyed);

        // A consolidation of more than an object's size runs its first piece alone, and commits it (DatasetCompactor.Pieces).
        CompactionJob whole = job;
        Piece? piece = merge ? await PieceAsync(dataset, schema, job, cancellationToken).ConfigureAwait(false) : null;
        HashSet<string>? partly = null;
        if (piece is not null)
        {
            job = piece.Job;
            partly = new HashSet<string>(StringComparer.Ordinal);
            foreach (CompactionInput input in job.Inputs)
            {
                if (input.Level == 0)
                {
                    partly.Add(input.Entry.Key);
                }
            }
        }

        // Inputs large enough are read through the local scratch, each copied once as the first pass asks for it; those a
        // piece reads a part of, from the store, the range it reads.
        memory.ReadThroughScratch(job, partly);
        if (merge)
        {
            Check(schema);
        }

        // Every input open before the passes, side by side, and held until the job ends.
        InputLeases opened = await InputLeases.OpenAsync(dataset, job, memory, cancellationToken).ConfigureAwait(false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable openedScope = opened.ConfigureAwait(false);

        // The key the reads seek by, whose first column's summaries bound a level's ranges and the late rows.
        DatasetKey? key = dataset.Key;

        // A job of a dataset with a key whose objects aim at a mebibyte or more writes them with a table of contents and the
        // sparse index of the key's indexed prefix, which it names: what a lookup of an identifier and a filter on the key
        // read without an open of the object's footer. Appends write neither, nor shredded variants.
        bool indexed = merge && job.TargetBytes >= dataset.Options.ContentsObjectBytes;

        // Such a job over a schema with a variant column counts, in a first pass, the paths of each partition value's
        // rows, and its objects of a value shred what those rows pay for; each object then lists its paths and the groups
        // of its rows that hold them, counted as its rows are written.
        IReadOnlyList<ValueShredding>? shreddings = null;
        if (indexed && VariantColumns(schema.DType).Length > 0)
        {
            using Activity? planning = DatasetTelemetry.StartCompactionPass("plan");
            shreddings = await PlanValuesAsync(dataset, schema, job, memory, piece?.Ranges, cancellationToken).ConfigureAwait(false);
        }

        // A merge writes each column every input writes against an object dictionary against their union, sorted, when the
        // writer prices it below a dictionary a chunk: the dictionaries go on through the merges.
        IReadOnlyList<(int Field, ObjectDictionary Dictionary, long RowBytes)> unions = [];
        if (merge)
        {
            using Activity? uniting = DatasetTelemetry.StartCompactionPass("unions");
            unions = await UnionsAsync(dataset, schema, job, memory, cancellationToken).ConfigureAwait(false);
        }

        // A leveled exit of level 0 keeps the rows that came late apart, in an object of level 0, while they weigh little
        // against the objects of its destination they fall among (LateRows): it reads its sources alone, and those
        // objects stand. Past that weight, it runs again as the merge with them it was planned as.
        LateRows? late = job.Style is CompactionStyle.Leveled && schema.Keyed ? LateRows.Of(job, key) : null;
        CompactionJob ran = late is null ? job : LateRows.Sources(job);

        // A consolidation hands its identifiers' postings and where its columns' values lie to the index of its partition
        // (D13, spec D, 7), which it writes over the one before for the objects that one covers still: its own objects keep
        // no postings of their identifiers.
        using PartitionPostings? postings = merge && indexed && job.Trigger == CompactionTrigger.Consolidation
            ? new PartitionPostings(
                schema.Identifiers, dataset.UnitBlocksOf(schema.DType, schema.Identifiers), ColumnNames(schema), dataset.Store is SealedObjectStore { Seals: true })
            : null;

        // Each object it streams is announced to the store as at most what the job reads, times what a job
        // writes a byte at worst; one that runs past it starts the job again under a bound twice as large. The objects the
        // job's own flow writes give its lane back while they wait for the store.
        ObjectStream OutputsOf(CompactionJob ran, int doublings, Execution.HeldLane? lane) => new ObjectStream(
            dataset, schema, ran.TargetBytes, ran.FirstRow, inKeyOrder: merge, rowBound: ran.Rows,
            ran.ToLevel, ObjectBound.Of(ran.Bytes, doublings, dataset.Options.BoundOutPerIn), endsBySize: !ran.OneObject || doublings > 0)
        {
            Lane = lane,
            FirstBytesPerRow = (double)ran.Bytes / Math.Max(ran.Rows, 1),
            RowsByValue = merge ? RowsByValueOf(schema.Cut, ran) : null,
            Room = memory.WriterRoom,
            Budget = memory.Writer,
            Reserve = memory.WriterReserve,
            Keys = indexed ? schema.Tree : null,
            Prefix = Math.Min(schema.Layout?.Layout.Prefix ?? 0, schema.Layout?.Components.Count ?? 0),
            Skip = schema.Partitioned ? 1 : 0,
            Shreddings = shreddings,
            Values = schema.Cut,
            Dictionaries = unions,
            SortedColumn = schema.SortedColumn,
            Arrival = Latest(ran),
            Postings = postings,
        };

        // A merge of several objects' worth, a consolidation's or a level's, runs as one stream of objects cut in slices of the
        // key, merged ahead on the lanes free (SlicedMerge), its writer sending several blocks through at once: the same
        // objects at any degree. It ran in ranges of the key side by side, as many as the session's lanes, each writing
        // objects of its own, so that one consolidation wrote 12 objects on one lane and 14 on four (2026-11-17).
        // The destination's objects, in the key's order: those of its one stream.
        ObjectStream[] streams;
        ObjectStream? lateOutputs = null;
        long rows;
        int atOnce = 1;
        int openAtOnce = 0;
        int boundRestarts = 0;
        int batchRows = 0;
        long writer = WriterCost(dataset, schema, job, shreddings);
        memory.WriterPrice = writer;
        Handed? handed = null;
        bool stopped = false;

        // What the inputs' batches weigh decoded, which the handle keeps for the next job's plan.
        RowMeter meter = new RowMeter();

        // A job writes one stream of objects, each ended where its data reaches the size it aims at. Ranges of the key, each
        // writing objects of its own side by side, were given up on 2026-11-21: on an M4 Pro at degree 14 they gave no time
        // back, took up to 15 % more processor, and each range ended its last object where its rows ended, at 61 to 66 % of
        // the size aimed at.
        Activity? writing = DatasetTelemetry.StartCompactionPass("write");
        try
        {
            // A job's inputs reserve what they hold as they read it (JobMemory), and its merge ends at a key when a reservation
            // it cannot go on without is not granted. Its inputs' batches are sized here to what the budget leaves once its
            // writer and its open inputs are counted: a choice of rows that has every chance to hold, not what keeps it.
            // A merge of slices' worth is cut in slices of the key, merged side by side on lanes free and taken in order into
            // one stream (SlicedMerge): its batches priced for the slices ahead it may merge, whatever the degree. A piece of
            // several ranges below its cut takes them as its slices, each counting what it takes of each input.
            KeyRanges? slices = null;
            int sliceCount = 0;
            if (piece is { Merged: > 1 })
            {
                slices = piece.Ranges;
                sliceCount = piece.Merged;
            }
            else if (merge && piece is null && KeyRanges.SlicesOf(dataset, ran) >= 2)
            {
                using Activity? cutting = DatasetTelemetry.StartCompactionPass("slices");
                slices = await KeyRangesAsync(dataset, schema, ran, KeyRanges.SlicesOf(dataset, ran), cancellationToken).ConfigureAwait(false);
            }

            // The inputs' batches, from what the inputs open at once hold of the room the writer leaves: for the slice taken
            // and as many of those priced ahead of it as that room holds at the shortest batches, so that slices merge ahead
            // where there is room and the batches are not cut short for slices that will not.
            long share = JobBudget(dataset) - writer;
            (long least, _) = Peak(dataset, schema, ran.Inputs, merge, LeastBatchRows);
            int merges = slices is null ? 1 : (int)Math.Clamp(share / Math.Max(least, 1), 1, 1 + SlicesAheadPriced);
            batchRows = InputBatchRows(dataset, schema, ran, merge, merges, share);
            memory.InputsPlanned = Peak(dataset, schema, ran.Inputs, merge, batchRows).Bytes;

            // A merge of a tiered dataset that ends short of room commits what it took of each input, a piece of itself
            // (StoppedAt): its flow counts what it hands its outputs of each. A leveled exit, whose commit moves its
            // level's pointer and keeps its late rows apart, commits whole or not at all.
            handed = merge && late is null && job.Style is CompactionStyle.Tiered ? new Handed(ran.Inputs.Count) : null;
            for (int doublings = 0; ; doublings++)
            {
                lateOutputs = late is null ? null : new ObjectStream(
                    dataset, schema, ran.Bytes, ran.FirstRow, inKeyOrder: true, rowBound: ran.Rows,
                    0, ObjectBound.Of(ran.Bytes, doublings, dataset.Options.BoundOutPerIn), endsBySize: false)
                {
                    Arrival = Latest(ran),
                    Budget = memory.Writer,
                    Reserve = memory.WriterReserve,
                    Lane = memory.Lane,
                    Room = memory.WriterRoom,
                };
                ObjectStream outputs = OutputsOf(ran, doublings, memory.Lane);
                try
                {
                    // A piece reads its first range alone, below its cut, and counts what it takes of each input.
                    if (piece is not null)
                    {
                        Array.Clear(piece.Emitted);
                    }

                    handed?.Reset();

                    rows = merge
                        ? await MergeAsync(
                            dataset, schema, ran, outputs, late, lateOutputs, batchRows, meter, memory, cancellationToken,
                            slices is null ? piece?.Ranges : null, 0, piece?.Emitted, writer, slices, sliceCount, handed).ConfigureAwait(false)
                        : await ConcatenateAsync(dataset, schema, ran, outputs, memory, cancellationToken).ConfigureAwait(false);
                    if (handed is { Stop: { } stop })
                    {
                        // Ended short of room: the object being written is sealed when it ends on a whole key, the rows
                        // left in the inputs starting at another; dropped otherwise, the job committing what its sealed
                        // objects hold, each ended where its key did. What it took of an object of a level past 0 in
                        // part, or nothing it may commit, leaves the job nothing to commit: its objects go, and the
                        // handle's next plan counts its inputs at what their files said as they were read.
                        long[]? took = handed.Clean && Commits(ran, handed.Rows) ? handed.Rows : null;
                        if (took is not null)
                        {
                            await outputs.FinishAsync(cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            await outputs.AbandonAsync().ConfigureAwait(false);
                            took = Commits(ran, handed.AtRoll) ? handed.AtRoll : null;
                        }

                        if (took is null)
                        {
                            List<string> unnamed = [];
                            foreach (WrittenObject made in outputs.Written)
                            {
                                unnamed.Add(made.Entry.Key);
                            }

                            await dataset.DeleteUnnamedAsync(unnamed, cancellationToken).ConfigureAwait(false);
                            throw stop;
                        }

                        piece = StoppedAt(ran, piece, took);
                        job = ran = piece.Job;
                        rows = Sum(piece.Emitted);
                        stopped = true;
                    }
                    else
                    {
                        await outputs.FinishAsync(cancellationToken).ConfigureAwait(false);
                    }

                    if (lateOutputs is not null)
                    {
                        await lateOutputs.FinishAsync(cancellationToken).ConfigureAwait(false);
                    }

                    streams = [outputs];
                    atOnce = piece is not null ? outputs.SlicesAtOnce : 1;
                    openAtOnce = slices is null ? 0 : slices.MostInputs * outputs.SlicesAtOnce;
                    break;
                }
                catch (ObjectPastBoundException) when (doublings < ObjectBound.MostDoublings)
                {
                    await AbandonAsync(outputs, lateOutputs).ConfigureAwait(false);
                    boundRestarts++;
                }
                catch (LateRows.Overflow)
                {
                    // The late rows weigh too much to stand apart: the exit as planned, merged with the objects they meet.
                    await AbandonAsync(outputs, lateOutputs).ConfigureAwait(false);
                    late = null;
                    lateOutputs = null;
                    ran = job;
                    doublings = -1;
                }
                catch
                {
                    await AbandonAsync(outputs, lateOutputs).ConfigureAwait(false);
                    throw;
                }
            }
        }
        finally
        {
            writing?.Dispose();
        }

        // A piece that took no row of level 0, its cut where none lies: it would rewrite the objects of level 1 below the cut
        // and leave level 0 as it was, piece after piece, a loop that never ends. The partition is consolidated in one piece,
        // now and after.
        if (piece is not null && !stopped && TookOfLevelZero(piece) == 0)
        {
            // What it wrote of level 1, which no version names, goes now.
            List<string> written = [];
            foreach (ObjectStream outputs in streams)
            {
                await outputs.AbandonAsync().ConfigureAwait(false);
                foreach (WrittenObject made in outputs.Written)
                {
                    written.Add(made.Entry.Key);
                }
            }

            await dataset.DeleteUnnamedAsync(written, cancellationToken).ConfigureAwait(false);

            dataset.ConsolidateWhole(CompactionPolicy.PartitionOf(dataset, whole));
            return await RewriteUnderAsync(dataset, whole, memory, cancellationToken).ConfigureAwait(false);
        }

        // A piece writes what it took of its inputs, which is what the merge emitted of each.
        long read = piece is null ? ran.Rows : Sum(piece.Emitted);
        if (rows != read)
        {
            throw new DatasetIntegrityException(
                $"The compaction read {read} row(s) from {ran.Inputs.Count} object(s) and wrote " +
                $"{rows}: a rewrite that loses rows is the one failure it must not have.");
        }

        // What the job read of its inputs as it went, which the handle's next plan counts them at: what a decoded row
        // weighs, the bytes a second it reads and writes, and, for a job ended short of room, what each object it opened
        // held open, those it leaves in the dataset with a part of their rows taken. A job that ran to its end keeps none:
        // what an input the merge drained held at most counts the batches it decoded ahead, which a plan must not.
        long reserved = memory.Peak;
        dataset.LearnDecodedRowBytes(meter.BytesPerRow);
        dataset.LearnJobRate(ran.Bytes, dataset.Options.TimeProvider.GetUtcNow() - started);
        if (handed?.Stop is { } shortOf)
        {
            KeepOpen(dataset, memory, shortOf);
        }

        // A piece takes out the objects of level 1 it read and those of level 0 it emptied, and puts back at level 0 those it
        // took some rows of, those rows marked; the inputs it took nothing of stay out of its commit.
        (List<(int Level, ReadOnlyMemory<byte> Key)> Consumed, List<(int Level, ReadOnlyMemory<byte> Key, ObjectEntry Entry)> Marked, List<ObjectEntry> Read, List<string> Gone)? taken =
            piece is null ? null : PieceCommit(piece);
        List<(int Level, ReadOnlyMemory<byte> Key)> consumed = taken?.Consumed ?? [];
        if (taken is null)
        {
            foreach (CompactionInput input in ran.Inputs)
            {
                consumed.Add((input.Level, input.Key));
            }
        }

        // The late rows' objects go back to level 0, the others to the destination, past every object there they met.
        List<(int Level, ReadOnlyMemory<byte> Key, ObjectEntry Entry)> produced = [.. taken?.Marked ?? []];
        List<CompactionInput> filled = [];
        DateTimeOffset? firstPut = null;
        long? generation = null;
        for (int i = 0; i < (lateOutputs?.Written.Count ?? 0); i++)
        {
            produced.Add((lateOutputs!.Levels[i], lateOutputs.Written[i].Key, lateOutputs.Written[i].Entry));
            firstPut = DatasetOperation.Earlier(firstPut, lateOutputs.Written[i].StartedAt);
            generation = DatasetOperation.Lower(generation, lateOutputs.Written[i].KeyGeneration);
        }

        foreach (ObjectStream outputs in streams)
        {
            for (int i = 0; i < outputs.Written.Count; i++)
            {
                WrittenObject written = outputs.Written[i];
                produced.Add((outputs.Levels[i], written.Key, written.Entry));
                filled.Add(new CompactionInput(outputs.Levels[i], written.Key, written.Entry));
                firstPut = DatasetOperation.Earlier(firstPut, written.StartedAt);
                generation = DatasetOperation.Lower(generation, written.KeyGeneration);
            }
        }

        // What each input was read as: a delete that marked rows in one meanwhile leaves it under its
        // key, and outputs worked out from the rows before the delete would bring them back.
        List<ObjectEntry> expected = taken?.Read ?? new List<ObjectEntry>(ran.Inputs.Count);
        if (taken is null)
        {
            foreach (CompactionInput input in ran.Inputs)
            {
                expected.Add(input.Entry);
            }
        }

        // A piece's objects take over the part of level 1 they fill with those of level 1 it read, which lie below its cut.
        LevelRange? pieceRange = null;
        if (piece is not null)
        {
            List<CompactionInput> span = [.. filled];
            foreach (CompactionInput input in ran.Inputs)
            {
                if (input.Level >= 1)
                {
                    span.Add(input);
                }
            }

            // A merge ended short of room whose objects stay in level 0, a merge of a size class, takes over no part of it.
            pieceRange = span.Count == 0 || job.ToLevel == 0 ? null : LevelRange.Of(job.ToLevel, span, key);
        }

        DatasetOperation.PartitionIndexChange? partitionIndex = null;
        if (postings is not null)
        {
            // The index's object is put after the outputs, which began first: it counts only when there are none.
            firstPut ??= dataset.Options.TimeProvider.GetUtcNow();
            generation ??= dataset.KeyGeneration;
            using Activity? indexing = DatasetTelemetry.StartCompactionPass("index");
            partitionIndex = await PartitionIndexAsync(dataset, schema, ran, streams, postings, cancellationToken).ConfigureAwait(false);
        }

        DatasetOperation.ReplaceObjects replacement = new DatasetOperation.ReplaceObjects(consumed, produced)
        {
            Expected = expected,
            Pointer = job.Stop.IsEmpty ? null : (job.FromLevel, job.Stop),

            // Apart, the outputs take over the part of the destination they fill alone, which no object it holds may meet.
            Range = piece is not null ? pieceRange : late is null ? job.Range : filled.Count == 0 ? null : LevelRange.Of(job.ToLevel, filled, key),
            PartitionIndex = partitionIndex,
            FirstPutAt = firstPut,
            KeyGeneration = generation,
        };

        // A commit lost throws away what the job wrote: it rebases for as long as the job took to write it, and leaves the
        // job abandoned rather than failed when every attempt lost, for the next plan to take its inputs up again.
        CommitResult commit = await dataset
            .CommitJobAsync([replacement], dataset.Options.TimeProvider.GetUtcNow() - started, cancellationToken).ConfigureAwait(false);

        // Once the commit is made, and never before: the inputs it took out go when no retained version reads them, and
        // the outputs of a replacement it did not apply, which no version names, go now.
        OperationOutcome outcome = commit.Outcomes.Count > 0 ? commit.Outcomes[0] : OperationOutcome.Applied;
        if (outcome == OperationOutcome.Applied)
        {
            // Of a piece, only the inputs it emptied: those it marked stay in level 0, the same objects.
            List<string> replaced = new List<string>(ran.Inputs.Count + 1);
            if (taken is { } pieceTaken)
            {
                replaced.AddRange(pieceTaken.Gone);
            }
            else
            {
                foreach (CompactionInput input in ran.Inputs)
                {
                    replaced.Add(input.Entry.Key);
                }
            }

            // What the handle kept of what these objects hold open goes with them; an object a piece marked and left in
            // level 0 keeps it, which the plan of what is left of it counts it at. And a writer halved for a job as wide
            // gathers its whole share again.
            dataset.ForgetOpenBytes(replaced);
            dataset.ForgetTakenThrough(replaced);
            dataset.JobCommitted(ran.Inputs.Count);

            // The rows a piece left in level 0 lie past the last key it wrote, its objects ending where a key does: kept for
            // the plan of what is left, which then leaves the objects this one wrote at their size (PieceAsync). The last
            // object of a piece that ran to its cut, or of a merge that sealed it as it ended short of room, is what was
            // left of its rows, short of its size: the next piece takes it with its own, as it did. A merge ended short
            // of room that dropped the object it was writing committed objects ended by their size alone, the last too.
            if (taken is { Marked.Count: > 0 } left && job.ToLevel >= 1)
            {
                bool allAtSize = stopped && handed is { Clean: false };
                ReadOnlyMemory<byte> last = default;
                ReadOnlyMemory<byte> before = default;
                foreach (ObjectStream outputs in streams)
                {
                    foreach (WrittenObject written in outputs.Written)
                    {
                        ReadOnlyMemory<byte> ends = written.Entry.LastKey;
                        if (last.IsEmpty || ends.Span.SequenceCompareTo(last.Span) > 0)
                        {
                            before = last;
                            last = ends;
                        }
                        else if (before.IsEmpty || ends.Span.SequenceCompareTo(before.Span) > 0)
                        {
                            before = ends;
                        }
                    }
                }

                ReadOnlyMemory<byte> atSize = allAtSize ? last : before;
                foreach ((_, _, ObjectEntry entry) in left.Marked)
                {
                    if (!atSize.IsEmpty)
                    {
                        dataset.NoteTakenThrough(entry.Key, entry.Deletions.LeadingRun, atSize.Span);
                    }
                }
            }

            if (partitionIndex?.Previous is { } previousIndex)
            {
                replaced.Add(previousIndex);
            }

            dataset.NoteRetired(commit.Version, commit.KnownAt, [.. replaced]);
        }
        else
        {
            // The objects the job wrote, which no version names: never an input it put back marked, which another holds.
            List<string> unnamed = [];
            foreach (CompactionInput output in filled)
            {
                unnamed.Add(output.Entry.Key);
            }

            for (int i = 0; i < (lateOutputs?.Written.Count ?? 0); i++)
            {
                unnamed.Add(lateOutputs!.Written[i].Entry.Key);
            }

            await dataset.DeleteUnnamedAsync(unnamed, cancellationToken).ConfigureAwait(false);
        }

        await dataset.DeleteRetiredAsync(cancellationToken).ConfigureAwait(false);

        int objectsOut = lateOutputs?.Written.Count ?? 0;
        long bytesOut = lateOutputs?.Bytes ?? 0;
        long scratch = (lateOutputs?.ScratchBytes ?? 0) + (postings?.ScratchBytes ?? 0) + memory.CopiedBytes;
        long batches = lateOutputs?.Batches ?? 0;
        long across = lateOutputs?.BatchesAcross ?? 0;
        long flown = lateOutputs?.BatchesFlown ?? 0;
        long onLine = lateOutputs?.BlocksOnLine ?? 0;
        long chunksOnLine = lateOutputs?.ChunksEndedOnLine ?? 0;
        long chunksCut = lateOutputs?.ChunksCutOnLine ?? 0;
        long runsTaken = lateOutputs?.BlocksOfRunsTaken ?? 0;
        long putBack = lateOutputs?.BlocksPutBack ?? 0;
        long putBackApart = lateOutputs?.ColumnsPutBackApart ?? 0;
        (long Held, long Refused, long Inexact) variantChunks = lateOutputs?.VariantChunks ?? default;
        long windowsTaken = 0;
        long windowsCopied = 0;
        long windowsKeyed = 0;
        int slicesCut = 0;
        int slicesAhead = 0;
        int groupEnds = lateOutputs?.GroupEnds ?? 0;
        ImmutableDictionary<string, long>.Builder dataBytes = ImmutableDictionary.CreateBuilder<string, long>(StringComparer.Ordinal);
        foreach (WrittenObject made in lateOutputs?.Written ?? [])
        {
            dataBytes[made.Entry.Key] = made.DataBytes;
        }

        long windowsAhead = lateOutputs?.InputWindowsAhead ?? 0;
        foreach (ObjectStream outputs in streams)
        {
            groupEnds += outputs.GroupEnds;
            foreach (WrittenObject made in outputs.Written)
            {
                dataBytes[made.Entry.Key] = made.DataBytes;
            }

            windowsAhead += outputs.InputWindowsAhead;
            objectsOut += outputs.Written.Count;
            bytesOut += outputs.Bytes;
            scratch += outputs.ScratchBytes;
            batches += outputs.Batches;
            across += outputs.BatchesAcross;
            flown += outputs.BatchesFlown;
            onLine += outputs.BlocksOnLine;
            chunksOnLine += outputs.ChunksEndedOnLine;
            chunksCut += outputs.ChunksCutOnLine;
            runsTaken += outputs.BlocksOfRunsTaken;
            putBack += outputs.BlocksPutBack;
            putBackApart += outputs.ColumnsPutBackApart;
            variantChunks = (variantChunks.Held + outputs.VariantChunks.Held, variantChunks.Refused + outputs.VariantChunks.Refused, variantChunks.Inexact + outputs.VariantChunks.Inexact);
            windowsTaken += outputs.WindowsTaken;
            windowsCopied += outputs.WindowsCopied;
            windowsKeyed += outputs.WindowsKeyedHere;
            slicesCut += outputs.Slices;
            slicesAhead += outputs.SlicesAhead;
        }

        return new CompactionResult
        {
            Version = commit.Version,
            FromLevel = job.FromLevel,
            ToLevel = job.ToLevel,
            Trigger = job.Trigger,
            Style = job.Style,
            ObjectsIn = ran.Inputs.Count,
            ObjectsOut = objectsOut,
            Rows = rows,
            BytesIn = ran.Bytes,
            BytesOut = bytesOut,
            MemoryReserved = reserved,
            MemoryReservedBy = (memory.InputsPeak, memory.WriterPeak),
            InputsPlanned = memory.InputsPlanned,
            InputWindows = WindowsOf(dataset, schema, memory),
            BoundRestarts = boundRestarts,
            RangesAtOnce = atOnce,
            ScratchBytes = scratch,
            Outcome = outcome,
            Piece = piece is { ToTheEnd: false },
            StoppedShort = stopped,
            StoppedBy = handed?.Stop?.Message,
            StoppedHow = handed?.Stop is null ? null : handed.Why,
            WriterArenasGivenBack = memory.ArenasGivenBack,
            PoolBlocksPastRoom = memory.PoolBlocksPastRoom,
            WriterCounted = memory.WriterPrice,
            Reserves = memory.Reserves,
            WriterBatches = batches,
            WriterBatchesAcross = across,
            WriterBatchesFlown = flown,
            WriterBlocksOnLine = onLine,
            WriterChunksEndedOnLine = chunksOnLine,
            WriterChunksCutOnLine = chunksCut,
            GroupEnds = groupEnds,
            ObjectDataBytes = dataBytes.ToImmutable(),
            WriterBlocksOfRunsTaken = runsTaken,
            WriterBlocksPutBack = putBack,
            WriterColumnsPutBackApart = putBackApart,
            InputVariantChunks = variantChunks,
            InputWindowsTaken = windowsTaken,
            InputWindowsCopied = windowsCopied,
            InputWindowsKeyedByMerge = windowsKeyed,
            MergeSlices = slicesCut,
            MergeSlicesAhead = slicesAhead,
            InputWindowsAhead = windowsAhead,
            StoreWaitsWithoutLane = memory.Lane?.GivenBack ?? 0,
            AdvancesHeldBack = memory.HeldBack,
            InputsToldByDirectories = memory.InputsToldByDirectories,
            CensusParts = memory.CensusParts,
        };
    }

    /// <summary>
    /// The index of the partition a consolidation fills (D13), over the one the version holds: the postings of its outputs'
    /// identifiers, which they handed to <paramref name="postings"/>, and those of the objects the index before covers that
    /// the job leaves.
    /// </summary>
    private static async ValueTask<DatasetOperation.PartitionIndexChange> PartitionIndexAsync(
        VortexDataset dataset, DatasetSchema schema, CompactionJob job, ObjectStream[] streams, PartitionPostings postings, CancellationToken cancellationToken)
    {
        byte[] partition = schema.Partitioned ? PartitionOf(schema, job.Inputs[0]) : [];
        PartitionIndexEntry? previous = await dataset.Snapshot.PartitionIndexAsync(partition, cancellationToken).ConfigureAwait(false);
        HashSet<UInt128> consumed = [];
        foreach (CompactionInput input in job.Inputs)
        {
            consumed.Add(input.Entry.Uid);
        }

        List<WrittenObject> outputs = [];
        foreach (ObjectStream stream in streams)
        {
            outputs.AddRange(stream.Written);
        }

        PartitionIndexEntry entry = await postings.WriteAsync(dataset, partition, outputs, previous, consumed, cancellationToken).ConfigureAwait(false);
        return new DatasetOperation.PartitionIndexChange(entry, previous?.ObjectKey);
    }

    /// <summary>The names of <paramref name="schema"/>'s top-level columns, by place: what the index of a partition names its located columns by.</summary>
    private static string[] ColumnNames(DatasetSchema schema)
    {
        string[] names = new string[schema.Columns.Count];
        for (int field = 0; field < names.Length; field++)
        {
            names[field] = schema.Columns[field].Name;
        }

        return names;
    }

    /// <summary>Abandons what a job's streams wrote: its outputs, and its late rows' when it kept them apart.</summary>
    private static async ValueTask AbandonAsync(ObjectStream outputs, ObjectStream? lateOutputs)
    {
        await outputs.AbandonAsync().ConfigureAwait(false);
        if (lateOutputs is not null)
        {
            await lateOutputs.AbandonAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A trivial move: each input's entry taken out of its level and put, byte for byte, into the level
    /// above, so that no row is read or written; a leveled job's one object, where nothing overlaps it,
    /// a tiered job's objects as large as the level above aims at. Null when an object holds an earlier
    /// schema, which only a rewrite brings to the current one: the whole job is then rewritten. The
    /// objects' tails, which their opens read to say so, are the move's requests, a wave of them side by
    /// side (<see cref="OpenedAhead"/>).
    /// </summary>
    /// <remarks>
    /// The commit checks what a merge's does: the entries are still the ones planned, and nothing came
    /// into the part of the level above that their bounds cover. An index fragment attached to an object
    /// in between goes up with it, since the object is not rewritten to fold it.
    /// </remarks>
    private static async ValueTask<CompactionResult?> MoveAsync(VortexDataset dataset, CompactionJob job, CancellationToken cancellationToken)
    {
        // The objects opened a wave at a time side by side, each only to read the schema it holds: one at a time, a job that
        // moves a run of many objects paid a round trip each.
        for (int from = 0; from < job.Inputs.Count; from += OpenedAhead)
        {
            if (await WrittenEarlierAsync(dataset, job.Inputs, from, Math.Min(OpenedAhead, job.Inputs.Count - from), cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
        }

        // Each object moves whole, its entry taken out of one tree and put into the other byte for byte.
        List<(int Level, ReadOnlyMemory<byte> Key)> consumed = new List<(int, ReadOnlyMemory<byte>)>(job.Inputs.Count);
        List<(int Level, ReadOnlyMemory<byte> Key, ObjectEntry Entry)> produced = new List<(int, ReadOnlyMemory<byte>, ObjectEntry)>(job.Inputs.Count);
        List<ObjectEntry> expected = new List<ObjectEntry>(job.Inputs.Count);
        foreach (CompactionInput input in job.Inputs)
        {
            consumed.Add((input.Level, input.Key));
            produced.Add((job.ToLevel, input.Key, input.Entry));
            expected.Add(input.Entry);
        }

        DatasetOperation.ReplaceObjects move = new DatasetOperation.ReplaceObjects(consumed, produced)
        {
            Expected = expected,
            Pointer = job.Stop.IsEmpty ? null : (job.FromLevel, job.Stop),
            Range = job.Range,
        };
        CommitResult commit = await dataset.CommitAsync([move], CommitKind.Compaction, cancellationToken).ConfigureAwait(false);
        return new CompactionResult
        {
            Version = commit.Version,
            FromLevel = job.FromLevel,
            ToLevel = job.ToLevel,
            Trigger = job.Trigger,
            Style = job.Style,
            ObjectsIn = job.Inputs.Count,
            ObjectsOut = job.Inputs.Count,
            ObjectsMoved = job.Inputs.Count,
            Outcome = commit.Outcomes[0],
        };
    }

    /// <summary>The objects a move opens side by side at most, to read the schema each holds.</summary>
    private const int OpenedAhead = 32;

    /// <summary>
    /// Whether one of the <paramref name="count"/> inputs from <paramref name="from"/> holds an earlier schema than the
    /// version's: each opened, all side by side, and let go once read; the first failure to open is thrown once all are.
    /// </summary>
    private static async ValueTask<bool> WrittenEarlierAsync(
        VortexDataset dataset, IReadOnlyList<CompactionInput> inputs, int from, int count, CancellationToken cancellationToken)
    {
        Task<ObjectLease>[] opening = new Task<ObjectLease>[count];
        for (int i = 0; i < count; i++)
        {
            opening[i] = dataset.RentAsync(inputs[from + i].Entry, cancellationToken).AsTask();
        }

        bool earlier = false;
        try
        {
            await ((Task)Task.WhenAll(opening)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            for (int i = 0; i < count; i++)
            {
                ObjectLease lease = await opening[i].ConfigureAwait(false);
                earlier |= dataset.Snapshot.Schema.ColumnsOf(lease.File.DType, inputs[from + i].Entry.Key) is not null;
            }
        }
        finally
        {
            foreach (Task<ObjectLease> open in opening)
            {
                if (open.IsCompletedSuccessfully)
                {
                    await (await open.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        return earlier;
    }

    /// <summary>
    /// Refuses a leveled compaction of a dataset whose layout states no key: it merges on the tree's order, every input's
    /// rows read in their own, which is the key's, nulls last.
    /// </summary>
    private static void Check(DatasetSchema schema)
    {
        if (schema.Tree is null || !schema.Keyed)
        {
            throw new VortexUnsupportedException(
                "sort key",
                ComponentKind.Feature,
                "A leveled compaction merges on the layout's key, and this dataset declares none. Compact it tiered, which concatenates.");
        }
    }

    /// <summary>
    /// The union of the object dictionaries the job's inputs write each column against, sorted, with the bytes the column's
    /// values take over the job's rows, about: a text's average value a row, a number's width. A column some input writes
    /// in its chunks, or whose union passes what a dictionary holds, has none. Each input's dictionaries are read with no
    /// open of it, from its table of contents.
    /// </summary>
    private static async ValueTask<IReadOnlyList<(int Field, ObjectDictionary Dictionary, long RowBytes)>> UnionsAsync(
        VortexDataset dataset, DatasetSchema schema, CompactionJob job, JobMemory memory, CancellationToken cancellationToken)
    {
        int fields = schema.DType.FieldCount;
        List<ObjectDictionary>?[] held = new List<ObjectDictionary>?[fields];
        long[] rowBytes = new long[fields];
        bool first = true;
        foreach (CompactionInput input in job.Inputs)
        {
            if (input.Entry.Contents is not { Dictionaries.Count: > 0 } contents || contents.Dictionaries.Count != fields)
            {
                return [];
            }

            ObjectDictionary?[] dictionaries = await dataset.DictionariesAsync(input.Entry, contents, cancellationToken).ConfigureAwait(false);
            for (int field = 0; field < fields; field++)
            {
                if (dictionaries[field] is not { } dictionary)
                {
                    held[field] = null;
                    continue;
                }

                if (first)
                {
                    held[field] = [];
                }

                if (held[field] is { } list)
                {
                    list.Add(dictionary);
                    DType type = schema.DType.GetField(field);
                    rowBytes[field] += type.Kind == DTypeKind.Primitive
                        ? input.Entry.PhysicalRows * type.PType.ByteWidth()
                        : input.Entry.PhysicalRows * dictionary.ValueBytes / Math.Max(dictionary.Count, 1);
                }
            }

            first = false;
        }

        List<(int, ObjectDictionary, long)> unions = [];
        for (int field = 0; field < fields; field++)
        {
            if (held[field] is not { Count: > 0 } list)
            {
                continue;
            }

            // Reset in the finally below whatever happens: the job's inputs' account.
            CanonicalArena arena = new CanonicalArena(64, memory.InputsAccount);
            try
            {
                DType type = schema.DType.GetField(field);
                (CanonicalArena, int)[] values = new (CanonicalArena, int)[list.Count];
                for (int i = 0; i < values.Length; i++)
                {
                    values[i] = (arena, list[i].ValuesNode(arena, type));
                }

                if (ObjectDictionary.Union(values, out _) is { } union)
                {
                    unions.Add((field, union, rowBytes[field]));
                }
            }
            finally
            {
                arena.Reset();
            }
        }

        return unions;
    }

    /// <summary>When the latest rows a job reads arrived: what its outputs say of theirs.</summary>
    private static long Latest(CompactionJob job)
    {
        long latest = 0;
        foreach (CompactionInput input in job.Inputs)
        {
            latest = Math.Max(latest, input.Entry.Arrival);
        }

        return latest;
    }

    /// <summary>
    /// Fragment compaction: each object's fragments bundled into one, index bytes only, swapped for
    /// it in one commit. The swap matches by content, so a fragment an indexer attached meanwhile
    /// survives the rebase.
    /// </summary>
    private static async ValueTask<CompactionResult> BundleAsync(
        VortexDataset dataset, CompactionJob job, CancellationToken cancellationToken)
    {
        List<DatasetOperation> operations = [];
        long bytesIn = 0;
        long bytesOut = 0;
        foreach (CompactionInput input in job.Inputs)
        {
            List<ReadOnlyMemory<byte>> fragments = new List<ReadOnlyMemory<byte>>(input.Entry.Fragments.Count);
            foreach (PageReference reference in input.Entry.Fragments)
            {
                fragments.Add(await dataset.ReadFragmentAsync(reference, cancellationToken).ConfigureAwait(false));
                bytesIn += reference.Length;
                operations.Add(new DatasetOperation.DropFragment(input.Key, reference) { Level = input.Level });
            }

            byte[] bundle = FragmentBundle.Pack(fragments);
            bytesOut += bundle.Length;
            operations.Add(new DatasetOperation.AddFragment(input.Key, input.Entry.Uid, bundle) { Level = input.Level });
        }

        CommitResult commit = await dataset.CommitAsync(operations, CommitKind.Compaction, cancellationToken).ConfigureAwait(false);

        // The bundles are the one operation per object that writes something.
        OperationOutcome outcome = OperationOutcome.Applied;
        for (int i = 0; i < operations.Count; i++)
        {
            if (operations[i] is DatasetOperation.AddFragment && commit.Outcomes[i] != OperationOutcome.Applied)
            {
                outcome = commit.Outcomes[i];
                break;
            }
        }

        return new CompactionResult
        {
            Version = commit.Version,
            FromLevel = job.FromLevel,
            ToLevel = job.ToLevel,
            Trigger = job.Trigger,
            Style = job.Style,
            ObjectsIn = job.Inputs.Count,
            ObjectsOut = job.Inputs.Count,
            Rows = 0,
            BytesIn = bytesIn,
            BytesOut = bytesOut,
            Outcome = outcome,
        };
    }

    /// <summary>
    /// An input's rows in its own order, the key's, as <paramref name="schema"/>'s columns, or those of them
    /// <paramref name="fields"/> names: its own scan, each batch reshaped when it was written under another schema; with
    /// <paramref name="rows"/>, those rows of it alone, which a range of the key reads.
    /// </summary>
    private static IAsyncEnumerable<RecordBatch> RowsOfAsync(
        ObjectLease lease, ObjectEntry entry, DatasetSchema schema, CancellationToken cancellationToken, int[]? fields = null, RowRange? rows = null,
        int batchRows = 0, Buffers.MemoryAccount? account = null, int prefetch = 0, Scanning.BatchHandover? handover = null,
        (string Column, VariantTake Take)[]? takes = null)
    {
        // A job's input reads and decodes under the job's inputs' account (JobMemory), which never passes the jobs' budget.
        // Its batches hold its rows at most: an input of 1 924 rows in a merge of batches of 16 384 held 566 KB, a batch's
        // worth of each column, where its job priced it at its rows.
        batchRows = batchRows > 0 ? (int)Math.Clamp(rows?.Length ?? entry.PhysicalRows, 1, batchRows) : batchRows;
        VortexFile file = lease.File;
        FieldMask projection = fields is null ? FieldMask.All : Layouts.FieldMasks.Of(fields);

        // Every reader of a job's input runs on a lane of the session's, the job's or one its census took: the batches it
        // asks ahead decode on lanes free now beside it, and on its own flow when none is.
        ObjectColumns? columns = schema.ColumnsOf(file.DType, entry.Key);
        ObjectReadPlan? read = columns?.Read(null, projection);
        ScanBuilder scan = Ranged(Batched(file.ScanBuilder().WithAccount(account).WithPrefetch(prefetch).WithConsumerLane(), batchRows), rows);
        if (read is { } plan)
        {
            scan.ProjectMask(plan.Shape.Source).WithEncodings(false, false);
        }
        else if (fields is not null)
        {
            scan.ProjectMask(projection);
        }

        // A merge's input hands its batches over, each shaped by its decode, on the lane that decoded it: its deleted rows
        // left out, its columns reshaped as the version holds them. Wrappers did both on the merge's thread.
        if (handover is not null)
        {
            if (entry.HasDeletions)
            {
                scan.WithCompaction(false).WithEncodings(false, false);
            }

            if (entry.HasDeletions || read is not null)
            {
                handover.State = new InputShaping(entry.HasDeletions ? entry.Deletions : null, read?.Shape);
            }
            else if (takes is not null)
            {
                // Batches handed over as their decode made them: what it took of a variant column's rows stays beside them.
                handover.Taken = TakenOf(file, takes);
            }

            return scan.WithHandover(handover).ExecuteAsync();
        }

        IAsyncEnumerable<RecordBatch> live = LiveRowsAsync(scan, entry, cancellationToken);
        return read is { } reshaped ? ObjectColumns.ReshapeAsync(live, reshaped.Shape, null, 0, compact: true, cancellationToken) : live;
    }

    /// <summary>The chunks of <paramref name="file"/>'s columns <paramref name="takes"/> names that a scan of it takes as their shredding holds them; null for none.</summary>
    private static TakenVariants? TakenOf(VortexFile file, (string Column, VariantTake Take)[] takes)
    {
        (int Field, VariantTake Take)[] columns = new (int, VariantTake)[takes.Length];
        int count = 0;
        foreach ((string column, VariantTake take) in takes)
        {
            if (file.DType.IndexOfField(column) is >= 0 and int field)
            {
                columns[count++] = (field, take);
            }
        }

        return count == 0 ? null : TakenVariants.Of(file, columns.AsSpan(0, count));
    }

    /// <summary>The scan of <paramref name="rows"/> alone, or of every row.</summary>
    private static ScanBuilder Ranged(ScanBuilder scan, RowRange? rows) => rows is { } range ? scan.Rows(range) : scan;

    /// <summary>The scan in batches of <paramref name="rows"/> rows, each decoded on its own, or of the scan's own size when 0.</summary>
    private static ScanBuilder Batched(ScanBuilder scan, int rows) => rows > 0 ? scan.WithMaxBatchRows(rows).WithWindowRows(rows) : scan;

    /// <summary>
    /// The job's inputs opened before its passes, as many at once as the session reads at once, and held until the job ends:
    /// each object is read once, whichever pass asks for it first, in one request when it fits the read that opens it. The
    /// census and the merge read each of 3 305 small inputs one after the other, twice: 6 749 requests, 148 s at 20 ms a
    /// request. Held open, an input costs what the job's price counts for it already.
    /// </summary>
    private sealed class InputLeases : IAsyncDisposable
    {
        private readonly ObjectLease?[] _leases;

        private InputLeases(ObjectLease?[] leases) => _leases = leases;

        /// <summary>Opens every input of <paramref name="job"/> of two inputs or more; none of a job of one.</summary>
        internal static async ValueTask<InputLeases> OpenAsync(VortexDataset dataset, CompactionJob job, JobMemory memory, CancellationToken cancellationToken)
        {
            InputLeases opened = new InputLeases(new ObjectLease?[job.Inputs.Count < 2 ? 0 : job.Inputs.Count]);
            try
            {
                // Opens, which wait on the store: beside the session's degree, as many at once as it reads.
                await Execution.Waits.ForAsync(
                    opened._leases.Length,
                    dataset.Session.Options.MaxConcurrentReads,
                    async (i, token) => opened._leases[i] = await memory.RentAsync(job.Inputs[i].Entry, token).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);
                return opened;
            }
            catch
            {
                await opened.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            for (int i = 0; i < _leases.Length; i++)
            {
                if (_leases[i] is { } lease)
                {
                    _leases[i] = null;
                    await lease.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>The decoded bytes and the rows of the batches a merge's inputs held: what a row weighs decoded, measured.</summary>
    private sealed class RowMeter
    {
        private long _bytes;
        private long _rows;

        /// <summary>Counts <paramref name="batch"/>, every buffer its nodes hold.</summary>
        internal void Add(RecordBatch batch)
        {
            Interlocked.Add(ref _bytes, batch.Arena.ByteSize(batch.RootIndex));
            Interlocked.Add(ref _rows, batch.RowCount);
        }

        /// <summary>The bytes of a row, over every batch counted; 0 before any row.</summary>
        internal long BytesPerRow
        {
            get
            {
                long rows = Interlocked.Read(ref _rows);
                return rows == 0 ? 0 : Interlocked.Read(ref _bytes) / rows;
            }
        }
    }

    /// <summary>The batches of <paramref name="batches"/> as they come, each counted by <paramref name="meter"/>.</summary>
    private static async IAsyncEnumerable<RecordBatch> MeteredAsync(
        IAsyncEnumerable<RecordBatch> batches, RowMeter meter, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (RecordBatch batch in batches.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            meter.Add(batch);
            yield return batch;
        }
    }

    /// <summary>
    /// An input's rows through <paramref name="scan"/>, in their order, without the rows it deleted: a compaction is also
    /// where a deletion vector ends, its outputs holding only the live rows.
    /// </summary>
    private static IAsyncEnumerable<RecordBatch> LiveRowsAsync(ScanBuilder scan, ObjectEntry entry, CancellationToken cancellationToken) =>
        entry.HasDeletions
            ? LiveRows.WithoutDeletedAsync(scan.WithCompaction(false).WithEncodings(false, false).ExecuteAsync(), entry.Deletions, compact: true, 0, cancellationToken)
            : scan.ExecuteAsync();

    /// <summary>Each input's rows, in its own order, one after another.</summary>
    private static async ValueTask<long> ConcatenateAsync(
        VortexDataset dataset, DatasetSchema schema, CompactionJob job, ObjectStream outputs, JobMemory memory, CancellationToken cancellationToken)
    {
        long rows = 0;
        foreach (CompactionInput input in job.Inputs)
        {
            // An object holds one partition's rows, which lead its tree key before its position: one of another rolls the
            // object, the inputs coming in the tree's order.
            if (schema.Partitioned)
            {
                byte[] partition = input.Key.Span[..^(VortexDataset.UidBytes + sizeof(long))].ToArray();
                if (outputs.Partition is { } held && !held.AsSpan().SequenceEqual(partition))
                {
                    await outputs.RollAsync(cancellationToken).ConfigureAwait(false);
                }

                outputs.Partition = partition;
            }

            ObjectLease lease = await memory.RentAsync(input.Entry, cancellationToken).ConfigureAwait(false);
            await using (lease.ConfigureAwait(false))
            {
                await foreach (RecordBatch batch in RowsOfAsync(lease, input.Entry, schema, cancellationToken, account: memory.InputsAccount)
                    .WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    await outputs.RollIfFullAsync(cancellationToken).ConfigureAwait(false);
                    await outputs.WriteAsync(batch, cancellationToken).ConfigureAwait(false);
                    rows += batch.RowCount;
                }
            }
        }

        return rows;
    }

    /// <summary>
    /// The k-way merge, one run of rows at a time; with <paramref name="ranges"/>, of the rows of range
    /// <paramref name="range"/> alone.
    /// </summary>
    private static async ValueTask<long> MergeAsync(
        VortexDataset dataset,
        DatasetSchema schema,
        CompactionJob job,
        ObjectStream outputs,
        LateRows? late,
        ObjectStream? lateOutputs,
        int batchRows,
        RowMeter meter,
        JobMemory memory,
        CancellationToken cancellationToken,
        KeyRanges? ranges = null,
        int range = 0,
        long[]? emitted = null,
        long writer = 0,
        KeyRanges? slices = null,
        int sliceCount = 0,
        Handed? handed = null)
    {
        // The slices of the merge: the first sliceCount ranges of the cut, every one when it names none.
        sliceCount = slices is null ? 0 : sliceCount > 0 ? sliceCount : slices.Count;
        long[]?[]? sliceEmitted = slices is not null && emitted is not null ? new long[]?[sliceCount] : null;

        // The writers keep the runs where they lie in the inputs' batches, and note them in one ledger, which the merge reads
        // to give a batch back once every run on it is copied: the merge's thread neither copies runs nor waits for copies.
        // The variant columns the job writes under one shredding: its inputs take their rows as their own shreddings hold
        // them, which its writers take their blocks' runs from in place of reading the rows put back together, for a
        // dataset that asks it (DatasetOptions.TakesVariants).
        (string Column, VariantTake Take)[]? takes = dataset.Options.TakesVariants && outputs.Shreddings is [{ Shredded.Count: > 0 } only]
            ? [.. only.Shredded.Select(static shredded => (shredded.Key, new VariantTake(shredded.Value)))]
            : null;
        Writing.RunLedger ledger = new Writing.RunLedger { Takes = takes is null ? null : [.. takes.Select(static held => held.Take)] };
        List<KeyOrderedMerge> made = [];

        // What the inputs a merge of the job holds open at once were counted to hold, at these batches: what a slice is
        // admitted at, and what the work ahead leaves the inputs still to open.
        long planned = Peak(dataset, schema, job.Inputs, merged: true, batchRows).Bytes;

        // The windows the inputs the merge reads decode ahead past their own, on the session's lanes but the merge's: a
        // window is admitted while the inputs' share holds it past what the inputs still to open may take, and what the
        // jobs' budget leaves the job, less it, still holds what the writer was priced at past what it holds now
        // (JobMemory.AdmitsRead). The batches stay those of the inputs' splits, so the bytes written are the same.
        int lanes = dataset.Session.Lanes.Lanes - 1;
        memory.WriterPrice = Math.Max(memory.WriterPrice, writer);
        Scanning.DecodeAhead? ahead = lanes <= 0 ? null : new Scanning.DecodeAhead(lanes, memory.AdmitsRead);
        SlicedMerge? sliced = null;
        KeyOrderedMerge MergeOf(int slice, Action<KeyOrderedMerge.Window>? retiring, Buffers.MemoryAccount? account)
        {
            // A slice counts what it takes of each input apart, slices merging at once (KeyOrderedMerge.Emitted).
            long[]? counts = sliceEmitted is null ? emitted : sliceEmitted[slice] = new long[emitted!.Length];

            // The merge the job's flow drives, the only one or the slice taken in its turn, has the writers let go of the
            // inputs it is done with before it opens one that would not hold beside them: what it holds with that input
            // passes what its inputs open at once were counted to hold. A slice merged ahead does not drive the writers.
            Buffers.CountedAccount? counted = account as Buffers.CountedAccount;
            async ValueTask LetGoAsync(ObjectEntry next)
            {
                if ((counted?.Held ?? memory.Inputs.ReservedBytes) + Window(dataset, schema, next, batchRows) <= planned)
                {
                    return;
                }

                await outputs.LetGoOfInputsAsync(cancellationToken).ConfigureAwait(false);
                if (lateOutputs is not null)
                {
                    await lateOutputs.LetGoOfInputsAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            // What an input of the merge asks when its reserve does not hold what it cannot go on without: the job gives back
            // what it started ahead. The merge the job's flow drives asks it there; a slice merged ahead asks through its
            // runs, and is answered once the flow takes it and comes to them (SlicedMerge.AskRoomAsync).
            Func<int, ValueTask<bool>> makeRoom = retiring is null
                ? memory.MakeRoomAsync
                : level => sliced!.AskRoomAsync(slice, level);
            KeyOrderedMerge one = InputsMerged(
                dataset, schema, job, batchRows, cancellationToken, ranges: slices ?? ranges, range: slices is null ? range : slice, meter: meter,
                memory: memory, emitted: counts, ledger: ledger, retiring: retiring, ahead: ahead, account: account, takes: takes,
                letGo: retiring is null ? LetGoAsync : null, makeRoom: makeRoom);
            lock (made)
            {
                made.Add(one);
            }

            return one;
        }

        // A slice merges ahead on a lane of its own while the room left holds its inputs open at once at their windows, or
        // what the slice taken held at its first run when more, past what the writer may take (JobMemory.AdmitsMerge);
        // otherwise the job's flow merges it in its turn, the same runs.
        sliced = slices is null
            ? null
            : new SlicedMerge(sliceCount, MergeOf, ledger, dataset.Session.Lanes, cancellationToken)
            {
                MostAhead = SlicesAheadPriced,
                Price = planned,
                SliceInputs = slices.MostInputs,
                Inputs = memory.InputsAccount,
                Admits = memory.AdmitsMerge,
                MakeRoom = memory.MakeRoomAsync,
                OwnRuns = async () =>
                {
                    await outputs.OwnRunsAsync(cancellationToken).ConfigureAwait(false);
                    if (lateOutputs is not null)
                    {
                        await lateOutputs.OwnRunsAsync(cancellationToken).ConfigureAwait(false);
                    }
                },
            };
        IMergedRuns merge = sliced is null ? MergeOf(0, null, null) : sliced;
        memory.Slices = sliced is null ? null : sliced.Report;

        // What the job gives back when a reservation it cannot go on without is not granted, a level at a time, its
        // writers' part then the merge's own: at 0, the writers copy the runs they keep of the inputs and the merge lets
        // go of the windows and the inputs done; at 1, the blocks in flight on the writers' lines land; at 2, the slices
        // merged ahead are given up and the batches decoded ahead given back; at 3, the writers end their group of rows,
        // each column writing what it gathered. The first three change nothing of what the job writes; the last ends a
        // group where the job, everything it started ahead given back, holds what its rows and its budget alone decide:
        // a chunk more a column, the same rows. On the job's flow, between two runs.
        memory.GiveBack = async level =>
        {
            switch (level)
            {
                case 0:
                    await outputs.LetGoOfInputsAsync(cancellationToken).ConfigureAwait(false);
                    if (lateOutputs is not null)
                    {
                        await lateOutputs.LetGoOfInputsAsync(cancellationToken).ConfigureAwait(false);
                    }

                    break;
                case 1:
                    await outputs.LandAsync().ConfigureAwait(false);
                    if (lateOutputs is not null)
                    {
                        await lateOutputs.LandAsync().ConfigureAwait(false);
                    }

                    break;
                case 2:
                    break;
                case 3:
                    await outputs.EndGroupAsync(cancellationToken).ConfigureAwait(false);
                    if (lateOutputs is not null)
                    {
                        await lateOutputs.EndGroupAsync(cancellationToken).ConfigureAwait(false);
                    }

                    break;
                default:
                    return false;
            }

            await merge.GiveBackAsync(level).ConfigureAwait(false);
            return true;
        };

        // What the inputs give back ahead of the writer when a start of the writer's asks, on this flow, between two runs:
        // the slices merged ahead and the batches decoded ahead.
        memory.GiveBackAhead = () => merge.GiveBackAsync(2);

        // What the work started ahead holds, which the inputs still to open are not counted to have taken yet.
        memory.AheadHeld = () => (ahead?.Bytes ?? 0) + (sliced?.AheadHeld ?? 0);
        ValueCut? values = schema.Cut;
        long rows = 0;
        outputs.Ledger = ledger;
        if (lateOutputs is not null)
        {
            lateOutputs.Ledger = ledger;
        }

        try
        {
            await using (merge.ConfigureAwait(false))
            {
                try
                {
                    rows = await MergeRunsAsync(merge, outputs, late, lateOutputs, values, handed, cancellationToken).ConfigureAwait(false);

                    // The merge ends: its writers copy what they still keep of its inputs, which then go.
                    await outputs.LetGoOfInputsAsync(cancellationToken).ConfigureAwait(false);
                    if (lateOutputs is not null)
                    {
                        await lateOutputs.LetGoOfInputsAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (ReserveExhaustedException stop) when (handed is not null)
                {
                    // Short of room it cannot go on without, everything started ahead given back: the merge ends at the
                    // key it reached, every run it gave handed to the writers. Whether the object being written ends on a
                    // whole key is read while the merge still holds its inputs, but for the next key of the one input
                    // whose next batch found no room, read in its file once they are gone (NextKeyAsync). The writers
                    // then copy what they keep of the inputs, which go with the merge, and stay whole for the job to
                    // seal or drop the object.
                    StopVerdict verdict = outputs.HoldsRows ? merge.Stopped(stop, outputs.EndsBelow) : StopVerdict.Whole("no object was being written");
                    handed.Clean = verdict.Clean;
                    handed.Why = verdict.Why;
                    handed.ToRead = verdict.NextOf is null ? null : verdict;
                    handed.Stop = stop;
                    await outputs.LetGoOfInputsAsync(cancellationToken).ConfigureAwait(false);
                    rows = 0;
                    foreach (long taken in handed.Rows)
                    {
                        rows += taken;
                    }
                }
                catch (VortexMemoryException refused) when (handed is not null && !cancellationToken.IsCancellationRequested)
                {
                    // A block no reserve covers was refused, the job's budget full: a merge's keys, a writer's arenas. The
                    // merge ends there too, but nothing says the object being written is whole: the writers read the
                    // inputs' batches no more once their rows in flight are through, and the job commits the objects it
                    // had sealed, each ended where its key did.
                    await outputs.SettleAsync().ConfigureAwait(false);
                    handed.Clean = false;
                    handed.Why = "a block no reservation covers was refused";
                    handed.ToRead = null;
                    handed.Stop = new ReserveExhaustedException(refused.ShortOfRoom, refused);
                    rows = 0;
                }
                catch
                {
                    // Failed, the writers read the inputs' batches no more once their rows in flight are through.
                    await outputs.SettleAsync().ConfigureAwait(false);
                    if (lateOutputs is not null)
                    {
                        await lateOutputs.SettleAsync().ConfigureAwait(false);
                    }

                    throw;
                }
            }

            // The merge gave its inputs back. One key was left to read: the next row's of the input whose next batch
            // found no room, every other row left lying past the last key written. Past it too, the object being
            // written ends on a whole key, and is sealed; at it, or unread, it is dropped as before.
            if (handed is { Stop: not null, ToRead: { NextOf: { } entry } pending })
            {
                (bool read, byte[]? next) = await NextKeyAsync(dataset, schema, memory, entry, pending.NextRow, cancellationToken).ConfigureAwait(false);
                handed.ToRead = null;
                handed.Clean = read && (next is null || outputs.EndsBelow(next));
                handed.Why = handed.Clean ? $"{pending.Why}, its next key read past the last one written"
                    : read ? $"{pending.Why}, its next row holding the key the last run ended on"
                    : $"{pending.Why}, its next key not read";
            }
        }
        finally
        {
            memory.GiveBack = null;
            memory.GiveBackAhead = null;
            outputs.Ledger = null;
            lock (made)
            {
                foreach (KeyOrderedMerge one in made)
                {
                    outputs.WindowsTaken += one.WindowsTaken;
                    outputs.WindowsCopied += one.WindowsCopied;
                    outputs.WindowsKeyedHere += one.WindowsKeyedHere;
                }
            }

            foreach ((_, VariantTake take) in takes ?? [])
            {
                (long held, long refused, long inexact) = outputs.VariantChunks;
                outputs.VariantChunks = (held + take.ChunksHeld, refused + take.ChunksRefused + take.ChunksInexact, inexact + take.ChunksInexact);
            }

            if (sliced is not null)
            {
                outputs.SlicesAhead += sliced.SlicesAhead;
                outputs.Slices += sliceCount;
                outputs.SlicesAtOnce = Math.Max(outputs.SlicesAtOnce, sliced.PeakAtOnce);
            }

            // What each slice took of each input, counted apart since slices merge at once, added up once every one is done.
            for (int slice = 0; sliceEmitted is not null && slice < sliceEmitted.Length; slice++)
            {
                for (int input = 0; sliceEmitted[slice] is { } took && input < took.Length; input++)
                {
                    emitted![input] += took[input];
                }
            }

            outputs.InputWindowsAhead += ahead?.Taken ?? 0;
            if (lateOutputs is not null)
            {
                lateOutputs.Ledger = null;
            }
        }

        return rows;
    }

    /// <summary>The merge's runs, one after another, into the objects they fall in: the rows that came late apart.</summary>
    private static async ValueTask<long> MergeRunsAsync(
        IMergedRuns merge, ObjectStream outputs, LateRows? late, ObjectStream? lateOutputs, ValueCut? values, Handed? handed,
        CancellationToken cancellationToken)
    {
        long rows = 0;
        {
            while (await merge.MoveNextAsync().ConfigureAwait(false))
            {
                int count = merge.CurrentRows;

                // What the flow hands of each input, for a merge that may end short of room and commit what it took.
                long[]? took = handed?.Rows;
                int input = took is null ? 0 : (int)merge.CurrentInput;

                // The rows that came late lead the merge, keys ascending: those of the run go to level 0, kept apart.
                // An object a partition value, so that each lies within the rows of its value, which a merge of neighbours
                // there takes in with them.
                int start = late?.End(merge, 0, count) ?? 0;
                if (start > 0)
                {
                    late!.Add(start);
                    for (int at = 0, end; at < start; at = end)
                    {
                        end = values is null ? start : values.EndOfValue(merge, at, start);
                        if (values is not null && lateOutputs!.HoldsOtherValue(values, merge.KeyAt(at)))
                        {
                            await lateOutputs.RollAsync(cancellationToken).ConfigureAwait(false);
                            late.Opens(lateOutputs.Written.Count + 1);
                        }

                        await lateOutputs!.WriteAsync(merge, at, end, cancellationToken).ConfigureAwait(false);
                    }
                }

                // An object holds the rows of one partition value: a run of several is written in parts, one a value,
                // and a part of another value than the object's seals it. A key has one value, so no part splits a key.
                for (int at = start, end; at < count; at = end)
                {
                    end = values is null ? count : values.EndOfValue(merge, at, count);

                    // An object the rows handed over since its last reading may bring to its size is read: it is full or it goes
                    // on, the next reading placed by what a row weighed since this one.
                    if (outputs.AsksReading)
                    {
                        await outputs.ReadSizeAsync(cancellationToken).ConfigureAwait(false);
                    }

                    // A run that goes on with the key the object ends on, past its size: the rows of that key stay with it,
                    // and the next part starts where the key changes. An input runs through the rows of a key it shares
                    // with others, into the next key, so that a key repeated across the inputs changes inside runs: without
                    // the split, a key of one text column rolled no object, 18 000 rows for 16 KiB aimed at.
                    if (outputs.WantsRoll && outputs.HoldsKey(merge.KeyAt(at)))
                    {
                        end = EndOfKey(merge, at, end);
                    }
                    else if (outputs.WantsRoll || (values is not null && outputs.HoldsOtherValue(values, merge.KeyAt(at))))
                    {
                        // Never split a key across two objects: two outputs sharing a boundary value
                        // would overlap, and a level wants key-disjoint objects.
                        await outputs.RollAsync(cancellationToken).ConfigureAwait(false);
                        handed?.Rolled();
                    }

                    // The runs are cut where the next reading of the object they go to is asked, so that it falls there whatever
                    // runs the merge hands, that object started first. A run that started an object went in whole, to where the
                    // merge's run ended, which the inputs' batches decide: an object of a line's blocks was read at 20 blocks
                    // where its writer had landed what 8 named (2026-11-21).
                    outputs.Opens(merge.KeyAt(at));
                    if (outputs.RowsToReading < end - at)
                    {
                        end = at + (int)outputs.RowsToReading;
                    }

                    await outputs.WriteAsync(merge, at, end, cancellationToken).ConfigureAwait(false);
                    if (took is not null)
                    {
                        took[input] += end - at;
                    }
                }

                rows += count;
            }
        }

        return rows;
    }

    /// <summary>
    /// The rows the inputs of <paramref name="job"/> hold of each partition value of <paramref name="cut"/>, by the first key
    /// of the value's first input, in the keys' order: what the objects of one value can take at most, their deleted rows
    /// counted. Null without a cut, or when an input holds several values, whose rows no one value can be told.
    /// </summary>
    internal static IReadOnlyList<(byte[] Key, long Rows)>? RowsByValueOf(ValueCut? cut, CompactionJob job)
    {
        if (cut is null)
        {
            return null;
        }

        List<(byte[] Key, long Rows)> inputs = new List<(byte[] Key, long Rows)>(job.Inputs.Count);
        foreach (CompactionInput input in job.Inputs)
        {
            if (!cut.HoldsOne(input))
            {
                return null;
            }

            inputs.Add((input.Key.Span[..^VortexDataset.UidBytes].ToArray(), input.Entry.Rows));
        }

        inputs.Sort(static (left, right) => left.Key.AsSpan().SequenceCompareTo(right.Key));
        List<(byte[] Key, long Rows)> values = [];
        foreach ((byte[] key, long rows) in inputs)
        {
            if (values.Count > 0 && cut.Same(values[^1].Key, key))
            {
                values[^1] = (values[^1].Key, values[^1].Rows + rows);
            }
            else
            {
                values.Add((key, rows));
            }
        }

        return values;
    }

    /// <summary>
    /// The first row from <paramref name="from"/> to <paramref name="to"/> of the merge's run whose key is not the key of
    /// row <paramref name="from"/>, or <paramref name="to"/>: found by halves, the run's keys ascending.
    /// </summary>
    private static int EndOfKey(IMergedRun merge, int from, int to)
    {
        if (merge.KeyAt(from).SequenceEqual(merge.KeyAt(to - 1)))
        {
            return to;
        }

        // Row `low` holds the key of row `from`, row `high` another.
        int low = from;
        int high = to - 1;
        while (high - low > 1)
        {
            int middle = low + ((high - low) >> 1);
            if (merge.KeyAt(from).SequenceEqual(merge.KeyAt(middle)))
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        return high;
    }

    /// <summary>
    /// The batches a merge's input decodes ahead of the one the merge reads: one, decoded on a lane of its scan while the merge
    /// takes the runs of the batch before, so that the merge's thread chooses runs rather than decodes them. A merge's thread
    /// decoded every input's columns, their variants rebuilt, a tenth of a consolidation's processor on the chain every
    /// lane of the writer waited for (2026-11-16). An input then holds two batches, which its measured price counts.
    /// </summary>
    private const int InputPrefetch = 1;

    /// <summary>
    /// The k-way merge of a job's inputs on the tree's order, the partition leading the key (<see cref="DatasetSchema.Tree"/>).
    /// Offered by leaf key, an exact lower bound, so an input opens only once it could hold the next row; the job's own
    /// order breaks ties among inputs holding the same key.
    /// </summary>
    /// <remarks>
    /// With <paramref name="fields"/>, each input reads those fields of <paramref name="schema"/> alone, the ones the
    /// partition and the key read among them, and the rows merged hold them alone. With <paramref name="ranges"/>, the
    /// merge holds the rows of range <paramref name="range"/> alone: each input its keys meet reads the rows the range
    /// names of it, and leaves out those outside the range. With <paramref name="only"/>, it holds that input's rows alone,
    /// their keys beside them. Each input reads batches of <paramref name="batchRows"/>, which <paramref name="meter"/>
    /// counts when given, and decodes <see cref="InputPrefetch"/> of them ahead of the merge, under an account of its own
    /// that counts in <paramref name="account"/>, a slice's, or in the job's inputs' when none is given: what each input
    /// holds is measured apart (<see cref="JobMemory.OpenInput"/>). <paramref name="letGo"/> is what the merge calls before
    /// it opens an input beside inputs done whose windows the writers still read (<see cref="KeyOrderedMerge.LetGo"/>).
    /// </remarks>
    private static KeyOrderedMerge InputsMerged(
        VortexDataset dataset,
        DatasetSchema schema,
        CompactionJob job,
        int batchRows,
        CancellationToken cancellationToken,
        int[]? fields = null,
        KeyRanges? ranges = null,
        int range = 0,
        int only = -1,
        RowMeter? meter = null,
        JobMemory? memory = null,
        long[]? emitted = null,
        Writing.RunLedger? ledger = null,
        Action<KeyOrderedMerge.Window>? retiring = null,
        Scanning.DecodeAhead? ahead = null,
        Buffers.MemoryAccount? account = null,
        (string Column, VariantTake Take)[]? takes = null,
        Func<ObjectEntry, ValueTask>? letGo = null,
        Func<int, ValueTask<bool>>? makeRoom = null)
    {
        account ??= memory?.InputsAccount;
        List<(ReadOnlyMemory<byte> Key, MergeObject Object)> inputs = new(job.Inputs.Count);
        Dictionary<string, RowRange>? rows = ranges is null ? null : new Dictionary<string, RowRange>(StringComparer.Ordinal);
        for (int i = 0; i < job.Inputs.Count; i++)
        {
            CompactionInput input = job.Inputs[i];
            if (only >= 0 && i != only)
            {
                continue;
            }

            if (ranges is not null)
            {
                RowRange read = ranges.RowsOf(i, range);
                if (read.IsEmpty)
                {
                    continue;
                }

                rows![input.Entry.Key] = read;
            }

            inputs.Add((input.Key, new MergeObject(input.Entry, VortexDataset.OrderOf(input.Key), i)));
        }

        inputs.Sort(static (left, right) => left.Key.Span.SequenceCompareTo(right.Key.Span));
        return new KeyOrderedMerge(
            dataset.Snapshot,
            inputs.Select(static held => held.Object).ToAsyncEnumerable(),
            schema.Tree!,

            // Equal keys in the order of the job's inputs, as a read promises: an input that ran through the keys equal to
            // another's put its rows first or not depending on where its batch ended, and the batches' rows follow what the
            // job measured, so the same rows compacted twice wrote objects whose rows of one key came in another order.
            rankedTies: true,
            // An input's batches, of the rows the job counted from its inputs' entries, under an account of the input's own.
            // Under a ledger, its scan hands its batches over, their keys encoded by their decode.
            (lease, entry, handover) =>
            {
                // Its scan reserves what it is about to hold before it takes it, a chunk's segments, what a read of the chunk
                // decodes whole, a batch, when it hands its batches over: a step of such a scan can be run again. A first
                // pass's scan counts what it holds and reserves nothing.
                Buffers.MemoryAccount? own = account;
                if (memory is not null && account is not null)
                {
                    (own, MemoryReserve? reserve) = memory.OpenInput(entry, account, batchRows, handover is null ? null : makeRoom);
                    if (handover is not null)
                    {
                        handover.Reserve = reserve;
                    }
                }

                // What a batch of its scan is reserved at before one was measured: its rows at what its entry says a row
                // weighs decoded, its key encoded beside it; nothing for an object whose entry says nothing, whose first
                // batch takes what the budget holds.
                if (handover is not null && batchRows > 0 && entry is { DecodedBytes: > 0, PhysicalRows: > 0 })
                {
                    handover.WindowBytes = Math.Min(entry.PhysicalRows, batchRows) * (DecodedRowBytes(dataset, entry) + entry.LastKey.Length);
                }
                IAsyncEnumerable<RecordBatch> batches = RowsOfAsync(
                    lease, entry, schema, cancellationToken, fields, rows?[entry.Key], batchRows, own, InputPrefetch, handover, takes);
                return meter is null ? batches : MeteredAsync(batches, meter, cancellationToken);
            },
            opened: null,
            cancellationToken,
            ranges?.Low(range) ?? default,
            ranges?.High(range) ?? default)
        {
            Account = account ?? QueryMemoryBudget.JobsOf(dataset.Session).Account,
            Rent = memory is null ? null : memory.RentAsync,
            Emitted = emitted,
            Ledger = ledger,
            Retiring = retiring,
            Ahead = ledger is null ? null : ahead,
            LetGo = letGo,
        };
    }

    /// <summary>
    /// The rows a merge's flow handed its outputs of each input of the job, by its place among them, and the same as its
    /// last object was sealed: what a merge ended short of room commits of each input (<see cref="StoppedAt"/>). Counted
    /// by the flow that takes the runs, a run at a time: a slice merged ahead counts what it merged, not what was taken.
    /// </summary>
    private sealed class Handed(int inputs)
    {
        /// <summary>The rows handed over of each input so far.</summary>
        internal long[] Rows { get; } = new long[inputs];

        /// <summary>The rows handed over of each input when the outputs last sealed an object; null before one was.</summary>
        internal long[]? AtRoll { get; private set; }

        /// <summary>What ended the merge short of room; null for a merge that ran to its end.</summary>
        internal ReserveExhaustedException? Stop { get; set; }

        /// <summary>Whether, ended short of room, the merge's outputs end on a whole key (<see cref="IMergedRuns.Stopped"/>, <see cref="NextKeyAsync"/>).</summary>
        internal bool Clean { get; set; }

        /// <summary>What ended the merge and how its outputs' end was judged, for the job's result.</summary>
        internal string? Why { get; set; }

        /// <summary>The input whose next key is still to read once the merge gave its inputs back, every other row left lying past the last key written; null for none.</summary>
        internal StopVerdict? ToRead { get; set; }

        /// <summary>The outputs seal the object they write: what was handed over so far is in sealed objects.</summary>
        internal void Rolled() => Array.Copy(Rows, AtRoll ??= new long[Rows.Length], Rows.Length);

        /// <summary>Nothing handed over yet: the job's write starts, or starts again.</summary>
        internal void Reset()
        {
            Array.Clear(Rows);
            AtRoll = null;
            Stop = null;
            Clean = false;
            Why = null;
            ToRead = null;
        }
    }

    /// <summary>
    /// The key of the first row left of <paramref name="entry"/>'s object from row <paramref name="row"/> of its file
    /// on, deleted rows passed over: what tells a merge ended short of room, the next batch of that input not read,
    /// whether the object it was writing ends on a whole key. Read through the columns of the key alone
    /// (<see cref="SortKey.ColumnsOf"/>), once the merge gave its inputs back: the batch that found no room held every
    /// column, a variant's dictionary decoded whole among them, where the key's hold a chunk of each of theirs.
    /// </summary>
    /// <returns>
    /// Whether the key could be read, and the key, null when no row is left. Not read when a value of the key is a key of
    /// a variant column, whose read takes the column's chunk, or when the read itself found no room.
    /// </returns>
    private static async ValueTask<(bool Read, byte[]? Key)> NextKeyAsync(
        VortexDataset dataset, DatasetSchema schema, JobMemory memory, ObjectEntry entry, long row, CancellationToken cancellationToken)
    {
        if (schema.Tree is not { } key || key.ColumnsOf(schema.DType) is not { } columns)
        {
            return (false, null);
        }

        if (row >= entry.PhysicalRows)
        {
            return (true, null);
        }

        try
        {
            ObjectLease lease = await memory.RentAsync(entry, cancellationToken).ConfigureAwait(false);
            await using (lease.ConfigureAwait(false))
            {
                IAsyncEnumerable<RecordBatch> left = RowsOfAsync(
                    lease, entry, schema, cancellationToken, columns, RowRange.FromLength(row, entry.PhysicalRows - row), LeastBatchRows,
                    memory.InputsAccount);
                await foreach (RecordBatch batch in left.WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    if (batch.RowCount == 0)
                    {
                        continue;
                    }

                    using RowKeys keys = key.Encode(batch);
                    return (true, keys.Row(0).ToArray());
                }
            }

            return (true, null);
        }
        catch (VortexMemoryException) when (!cancellationToken.IsCancellationRequested)
        {
            // The read found no room either: nothing says where the next row's key lies.
            return (false, null);
        }
    }

    /// <summary>
    /// Whether a job ended short of room may commit what it took of its inputs, <paramref name="took"/> rows of each: it
    /// took rows, some of level 0 when it reads some; each object of a level past 0 whole or not at all, a level's objects
    /// being disjoint by key; and it took part of no input that carries the counts of rows a delete marked, which the marks
    /// of a part taken would leave behind (<see cref="PieceCommit"/>).
    /// </summary>
    private static bool Commits(CompactionJob job, long[]? took)
    {
        if (took is null)
        {
            return false;
        }

        long ofLevelZero = 0;
        long any = 0;
        bool readsLevelZero = false;
        for (int i = 0; i < job.Inputs.Count; i++)
        {
            CompactionInput input = job.Inputs[i];
            any += took[i];
            if (input.Level >= 1)
            {
                if (took[i] != 0 && took[i] != input.Entry.Rows)
                {
                    return false;
                }

                continue;
            }

            readsLevelZero = true;
            ofLevelZero += took[i];
            if (took[i] > 0 && took[i] < input.Entry.Rows && input.Entry.HasNegatives)
            {
                return false;
            }
        }

        return any > 0 && (!readsLevelZero || ofLevelZero > 0);
    }

    /// <summary>
    /// What a job ended short of room commits as a piece of itself: the inputs it took rows of, each read from where the
    /// job read it, and what it took of each (<see cref="Piece"/>). The inputs it took nothing of stay out of the commit,
    /// as they were.
    /// </summary>
    private static Piece StoppedAt(CompactionJob job, Piece? piece, long[] took)
    {
        List<CompactionInput> inputs = [];
        List<long> starts = [];
        List<long> emitted = [];
        for (int i = 0; i < job.Inputs.Count; i++)
        {
            if (took[i] > 0)
            {
                inputs.Add(job.Inputs[i]);
                starts.Add(piece?.StartOf(i) ?? 0);
                emitted.Add(took[i]);
            }
        }

        Piece stopped = new Piece(
            new CompactionJob(job.FromLevel, job.ToLevel, job.Style, job.Trigger, inputs, job.TargetBytes, job.FirstRow), null!, [.. starts], toTheEnd: false);
        emitted.CopyTo(stopped.Emitted);
        return stopped;
    }

    /// <summary>
    /// The first pass of a job whose objects carry a sparse index of their keys, over a schema with a variant column: the paths
    /// of each partition value's rows counted, in the order of the values, and what each variant column shreds in the objects
    /// of each chosen from them (<see cref="ShreddingChoice"/>), the keys the dataset declares shredded wherever a row holds
    /// one. Without a partition value, the job's rows are counted together.
    /// </summary>
    /// <remarks>
    /// What a value's rows hold is the same whatever order they come in, so each input is counted apart, side by side on the
    /// session's lanes, and the counts of a value are its inputs' added up (<see cref="PathCensus.Merge"/>): a merge of the
    /// inputs counted every row on one thread, and took a fifth of a tier merge of log records. An input's counts are added
    /// to its values' once it and the inputs before it are counted, in the inputs' order whatever the lanes' pace, and its
    /// censuses go back to <see cref="Censuses"/> for the next input to count in: what the job holds is its values' keys,
    /// where every input's census, kept to the end, made the job's keys as many times over as it has inputs. A large input
    /// that no directory tells is counted in parts of its rows (<see cref="CensusPartsAsync"/>), side by side as the inputs
    /// are and added in their order, which makes the census the input's rows counted one after the other make.
    /// </remarks>
    private static async ValueTask<IReadOnlyList<ValueShredding>> PlanValuesAsync(
        VortexDataset dataset, DatasetSchema schema, CompactionJob job, JobMemory memory, KeyRanges? piece, CancellationToken cancellationToken)
    {
        int[] variants = VariantColumns(schema.DType);
        ValueCut? values = schema.Cut;
        int textBound = dataset.Options.Write.StringBoundBytes;

        // What the lanes count, in the inputs' order: each input whole, or a large one in parts of its rows (CensusPartsAsync).
        using CensusParts plan = await CensusPartsAsync(dataset, schema, job, variants, memory, piece, cancellationToken).ConfigureAwait(false);
        List<CensusPart> parts = plan.Parts;
        List<ValueCount>?[] counts = new List<ValueCount>?[parts.Count];
        List<(byte[] First, PathCensus[] Paths)> added = [];
        List<int> refused = [];
        int next = 0;
        ValueCount? pending = null;
        Lock gate = new Lock();

        // As many lanes as the room the jobs' budget leaves holds what a lane holds as it counts an input: the input open,
        // its batch at what the handle's last job measured, and a slice of a wide shredding. Their blocks are the job's.
        int censusRows = CensusRows(dataset, job);
        long perLane = File.VortexReadOptions.DefaultVariantSliceBytes;
        foreach (CompactionInput input in job.Inputs)
        {
            perLane = Math.Max(perLane, Window(dataset, schema, input.Entry, censusRows) + File.VortexReadOptions.DefaultVariantSliceBytes);
        }

        int most = Math.Min(Math.Max(1, dataset.Session.Options.MaxDegreeOfParallelism), parts.Count);
        int admitted = (int)Math.Clamp(memory.Room / perLane, 1, most);

        // The job's own lane, and beside it the lanes of the session's that are free now (LaneBudget), as many as the room
        // admits: the job holds the session's degree with its writers' helpers and every other job's, where the census took
        // its own threads past it. Each lane given back as it finds no part left.
        await Execution.LaneWorkers.ForAsync(
            dataset.Session.Lanes,
            parts.Count,
            admitted,
            async (part, token) =>
            {
                List<ValueCount> counted;
                try
                {
                    counted = await CountAsync(dataset, schema, job, parts[part].Input, variants, memory, piece, token, parts[part].Rows, parts[part].Told).ConfigureAwait(false);
                }
                catch (VortexMemoryException) when (!token.IsCancellationRequested)
                {
                    // Refused a block by the process beside the inputs the other lanes hold: counted again alone once they
                    // are done, rather than the job failed (raph, 2026-11-16: short of memory, a work waits). The blocks
                    // the count held went back as it unwound.
                    lock (gate)
                    {
                        refused.Add(part);
                    }

                    return;
                }

                lock (gate)
                {
                    counts[part] = counted;
                    Drain();
                }
            },
            cancellationToken).ConfigureAwait(false);

        // The parts refused beside the others, each counted alone on the job's thread, in their order.
        refused.Sort();
        foreach (int part in refused)
        {
            counts[part] = await CountAsync(dataset, schema, job, parts[part].Input, variants, memory, piece, cancellationToken, parts[part].Rows, parts[part].Told).ConfigureAwait(false);
            Drain();
        }

        // The values in the key's order, the partition leading it: each a range of keys, which any of its first keys places.
        added.Sort(static (left, right) => left.First.AsSpan().SequenceCompareTo(right.First));
        List<PathCensus[]> censuses = [.. added.Select(static value => value.Paths)];

        List<ValueShredding> shreddings = new List<ValueShredding>(censuses.Count);
        foreach (PathCensus[] paths in censuses)
        {
            ImmutableDictionary<string, VariantShredding> shredded = ImmutableDictionary<string, VariantShredding>.Empty;
            ImmutableDictionary<string, int> chunks = ImmutableDictionary<string, int>.Empty;
            for (int v = 0; v < variants.Length; v++)
            {
                string name = schema.DType.GetFieldName(variants[v]);
                if (ShreddingChoice.Choose(paths[v], required: IdentifierKeys(schema, variants[v])) is { } chosen)
                {
                    shredded = shredded.SetItem(name, chosen);
                    chunks = chunks.SetItem(name, ShreddingChoice.ChunkTargetBytes(paths[v]));
                }
            }

            shreddings.Add(new ValueShredding(shredded, chunks));
        }

        return shreddings;

        // The parts counted, added to their values in the parts' order up to the first not counted yet. The parts of an input
        // counted in parts make its census first, each added after the one before, then the paths its directories tell, as a
        // count of the whole input adds them after its rows; the input's census then goes to its value.
        void Drain()
        {
            for (; next < counts.Length && counts[next] is { } ready; counts[next++] = null)
            {
                CensusPart part = parts[next];
                if (part.Rows is null)
                {
                    foreach (ValueCount count in ready)
                    {
                        Add(count);
                    }

                    continue;
                }

                foreach (ValueCount count in ready)
                {
                    if (pending is null)
                    {
                        pending = count;
                        continue;
                    }

                    for (int v = 0; v < variants.Length; v++)
                    {
                        pending.Paths[v].Merge(count.Paths[v]);
                    }

                    Censuses.Return(count.Paths);
                }

                if (part.Last && pending is not null)
                {
                    if (plan.Take(next) is { } directories)
                    {
                        for (int v = 0; v < variants.Length; v++)
                        {
                            if (directories[v] is { } directory)
                            {
                                pending.Paths[v].AddPaths(directory);
                                directory.Dispose();
                            }
                        }
                    }

                    Add(pending);
                    pending = null;
                }
            }
        }

        // An input's counts of a value added to its value's, censuses of their own, and given back for the next input.
        void Add(ValueCount count)
        {
            int at = added.Count - 1;
            while (at >= 0 && values is not null && !values.Same(added[at].First, count.First))
            {
                at--;
            }

            if (at < 0)
            {
                added.Add((count.First, [.. variants.Select(_ => new PathCensus(textBound))]));
                at = added.Count - 1;
            }

            for (int v = 0; v < variants.Length; v++)
            {
                added[at].Paths[v].Merge(count.Paths[v]);
            }

            Censuses.Return(count.Paths);
        }
    }

    /// <summary>
    /// A unit of the first pass: an input whole, or <see cref="Rows"/> of it, with the variant columns its directories tell,
    /// whose rows are then counted alone (<see cref="PathCensus.AddRows"/>); <see cref="Last"/> for an input's last part.
    /// </summary>
    private readonly record struct CensusPart(int Input, RowRange? Rows, bool[]? Told, bool Last);

    /// <summary>
    /// The units of the first pass (<see cref="CensusPart"/>), and the directories of each input counted in parts, held by its
    /// last part until its census adds the paths they tell; those not taken go back as it is disposed.
    /// </summary>
    private sealed class CensusParts : IDisposable
    {
        private readonly Dictionary<int, VariantDirectory.DirectoryBody?[]> _directories = [];

        internal List<CensusPart> Parts { get; } = [];

        /// <summary>Holds <paramref name="directories"/> for part <paramref name="part"/>.</summary>
        internal void Hold(int part, VariantDirectory.DirectoryBody?[] directories) => _directories[part] = directories;

        /// <summary>The directories part <paramref name="part"/> holds, now the caller's to dispose; null for none.</summary>
        internal VariantDirectory.DirectoryBody?[]? Take(int part) => _directories.Remove(part, out VariantDirectory.DirectoryBody?[]? taken) ? taken : null;

        public void Dispose()
        {
            foreach (VariantDirectory.DirectoryBody?[] directories in _directories.Values)
            {
                foreach (VariantDirectory.DirectoryBody? directory in directories)
                {
                    directory?.Dispose();
                }
            }

            _directories.Clear();
        }
    }

    /// <summary>
    /// What the first pass counts on its lanes, in the inputs' order: each input whole, or in parts of its rows when it holds
    /// <see cref="DatasetOptions.CensusPartRows"/> twice or more, a single value, no deleted row, and a variant column its
    /// directories do not tell; an input they tell whole is counted from them alone. Each part is whole chunks of its first
    /// variant column and at least <see cref="DatasetOptions.CensusPartRows"/> rows, so that no chunk is decoded twice, and the
    /// cuts are the file's and the options' alone, the same at any degree. The parts' censuses added in their order, then the paths the directories tell, are the input's while its
    /// paths hold fewer than <see cref="PathCensus.MaxKeys"/> keys, and past them the same at any degree
    /// (<see cref="PathCensus.Merge"/>).
    /// </summary>
    private static async ValueTask<CensusParts> CensusPartsAsync(
        VortexDataset dataset, DatasetSchema schema, CompactionJob job, int[] variants, JobMemory memory, KeyRanges? piece, CancellationToken cancellationToken)
    {
        CensusParts plan = new CensusParts();
        List<CensusPart> parts = plan.Parts;
        long least = Math.Max(1, dataset.Options.CensusPartRows);
        try
        {
            for (int input = 0; input < job.Inputs.Count; input++)
            {
                CompactionInput held = job.Inputs[input];
                long rows = held.Entry.PhysicalRows;
                if (piece is not null || variants.Length == 0 || rows < 2 * least || held.Entry.HasDeletions
                    || (schema.Cut is { } values && !values.HoldsOne(held)))
                {
                    parts.Add(new CensusPart(input, null, null, true));
                    continue;
                }

                long[] starts;
                VariantDirectory.DirectoryBody?[] directories = new VariantDirectory.DirectoryBody?[variants.Length];
                ObjectLease lease = await memory.RentAsync(held.Entry, cancellationToken).ConfigureAwait(false);
                await using (lease.ConfigureAwait(false))
                {
                    if (schema.Holds(held.Entry))
                    {
                        directories = await DirectoriesAsync(lease, held.Entry, variants, cancellationToken).ConfigureAwait(false);
                    }

                    // Told whole when every column's directory keeps its values' bytes too, as the count decides.
                    starts = Array.TrueForAll(directories, static directory => directory is { ValueBytes: >= 0 })
                        ? []
                        : lease.File.ChunkStartsOf(schema.DType.GetFieldName(variants[0]));
                }

                int first = parts.Count;
                long begin = 0;
                for (int chunk = 1; chunk < starts.Length - 1; chunk++)
                {
                    if (starts[chunk] - begin >= least && rows - starts[chunk] >= least)
                    {
                        parts.Add(new CensusPart(input, new RowRange(begin, starts[chunk]), null, false));
                        begin = starts[chunk];
                    }
                }

                if (parts.Count == first)
                {
                    // Told whole, or of one part: counted whole, its directories read again by its count.
                    foreach (VariantDirectory.DirectoryBody? directory in directories)
                    {
                        directory?.Dispose();
                    }

                    parts.Add(new CensusPart(input, null, null, true));
                    continue;
                }

                parts.Add(new CensusPart(input, new RowRange(begin, rows), null, true));
                memory.CensusParts = (memory.CensusParts.Inputs + 1, memory.CensusParts.Parts + parts.Count - first);
                bool[] told = [.. directories.Select(static directory => directory is not null)];
                for (int part = first; part < parts.Count; part++)
                {
                    parts[part] = parts[part] with { Told = told };
                }

                plan.Hold(parts.Count - 1, directories);
            }

            return plan;
        }
        catch
        {
            plan.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The censuses a job's first pass counts its inputs in (<see cref="PlanValuesAsync"/>), kept from one input to the next
    /// and from one job to the next with their trees of paths: a census an input, its tree as large as the input's keys, was
    /// made new for every input and kept to the job's end, 1.4 bytes a row of a consolidation of S1's logs. At most
    /// <see cref="Most"/> are kept, each of at most twice <see cref="PathCensus.MaxKeys"/> keys.
    /// </summary>
    private static class Censuses
    {
        private const int Most = 32;
        private static readonly Lock Gate = new Lock();
        private static readonly Stack<PathCensus> Kept = new Stack<PathCensus>();

        /// <summary>A census of each of <paramref name="columns"/> variant columns, text bounds cut to <paramref name="textBound"/> bytes, counting nothing.</summary>
        internal static PathCensus[] Rent(int columns, int textBound)
        {
            PathCensus[] censuses = new PathCensus[columns];
            for (int c = 0; c < columns; c++)
            {
                PathCensus? kept = null;
                lock (Gate)
                {
                    if (Kept.Count > 0 && Kept.Peek().StringBoundBytes == textBound)
                    {
                        kept = Kept.Pop();
                    }
                }

                censuses[c] = kept ?? new PathCensus(textBound);
            }

            return censuses;
        }

        /// <summary>Takes <paramref name="censuses"/> back, cleared, those the pool has room for.</summary>
        internal static void Return(PathCensus[] censuses)
        {
            foreach (PathCensus census in censuses)
            {
                if (census.HeldKeys > 2 * PathCensus.MaxKeys)
                {
                    continue;
                }

                census.Clear();
                lock (Gate)
                {
                    if (Kept.Count < Most)
                    {
                        Kept.Push(census);
                    }
                }
            }
        }
    }

    /// <summary>
    /// The paths of input <paramref name="input"/>'s rows counted for <see cref="PlanValuesAsync"/>, a value at a time in the
    /// key's order: of an input that holds one value, or of a dataset with none, its variant columns alone, read through; of
    /// one that holds several, the key's columns too, the input merged alone so that its keys tell its values apart. With
    /// <paramref name="rows"/>, a part of an input of one value (<see cref="CensusPartsAsync"/>), those rows alone, the columns
    /// <paramref name="toldColumns"/> marks counted by their rows and bytes only, their paths left to its directories.
    /// </summary>
    private static async ValueTask<List<ValueCount>> CountAsync(
        VortexDataset dataset, DatasetSchema schema, CompactionJob job, int input, int[] variants, JobMemory memory, KeyRanges? piece,
        CancellationToken cancellationToken, RowRange? rows = null, bool[]? toldColumns = null)
    {
        int textBound = dataset.Options.Write.StringBoundBytes;
        CompactionInput held = job.Inputs[input];
        ValueCut? values = schema.Cut;
        List<ValueCount> counts = [];
        if (values is null || values.HoldsOne(held))
        {
            PathCensus[] paths = Censuses.Rent(variants.Length, textBound);
            ObjectLease lease = await memory.RentAsync(held.Entry, cancellationToken).ConfigureAwait(false);
            await using (lease.ConfigureAwait(false))
            {
                // A column whose directory tells its paths is not parsed: its rows give their count and their values' bytes
                // alone, and the directory the rest. A piece reads a part of its inputs, which their directories do not tell.
                VariantDirectory.DirectoryBody?[] directories = piece is null && rows is null && schema.Holds(held.Entry)
                    ? await DirectoriesAsync(lease, held.Entry, variants, cancellationToken).ConfigureAwait(false)
                    : new VariantDirectory.DirectoryBody?[variants.Length];
                try
                {
                    // Every column's directory keeps its rows' value bytes too: none of the input is read, its rows put back
                    // together from their shredding only to be sized, a log's attributes from some four hundred keys a row.
                    bool told = true;
                    foreach (VariantDirectory.DirectoryBody? directory in directories)
                    {
                        told &= directory is { ValueBytes: >= 0 };
                    }

                    if (told)
                    {
                        for (int v = 0; v < variants.Length; v++)
                        {
                            paths[v].AddCounts(directories[v]!.Rows, directories[v]!.ValueBytes);
                        }

                        memory.ToldByDirectories();
                    }
                    else
                    {
                        // In batches of the rows its lane was priced at (CensusRows): read at the scan's own size, a lane of
                        // log records held a window of their attributes rebuilt, and the census of a tier merge of 23 inputs was
                        // refused at 671 MiB of 672, twice, before a job of 5 ran (2026-11-16).
                        await foreach (RecordBatch batch in RowsOfAsync(lease, held.Entry, schema, cancellationToken, variants, rows ?? piece?.RowsOf(input, 0), CensusRows(dataset, job), memory.InputsAccount)
                            .WithCancellation(cancellationToken).ConfigureAwait(false))
                        {
                            for (int v = 0; v < variants.Length; v++)
                            {
                                if (directories[v] is null && toldColumns?[v] != true)
                                {
                                    paths[v].Add(batch, v);
                                }
                                else
                                {
                                    paths[v].AddRows(batch, v);
                                }
                            }
                        }
                    }

                    for (int v = 0; v < variants.Length; v++)
                    {
                        if (directories[v] is { } directory)
                        {
                            paths[v].AddPaths(directory);
                        }
                    }
                }
                finally
                {
                    foreach (VariantDirectory.DirectoryBody? directory in directories)
                    {
                        directory?.Dispose();
                    }
                }
            }

            if (paths[0].Rows > 0)
            {
                counts.Add(new ValueCount(held.Key.Span[..^VortexDataset.UidBytes].ToArray(), input, paths));
            }
            else
            {
                Censuses.Return(paths);
            }

            return counts;
        }

        int[] counted = Counted(schema);
        int[] read = [.. variants.Select(field => Array.BinarySearch(counted, field))];
        byte[]? last = null;
        KeyOrderedMerge merge = InputsMerged(dataset, schema, job, CensusRows(dataset, job), cancellationToken, counted, ranges: piece, only: input, memory: memory);
        await using (merge.ConfigureAwait(false))
        {
            while (await merge.MoveNextAsync().ConfigureAwait(false))
            {
                RecordBatch run = merge.Current;
                for (int at = 0, end; at < run.RowCount; at = end)
                {
                    // A value starts the input's rows, or follows the last counted where the key's value changes.
                    if (counts.Count == 0 || !values.Same(last, merge.KeyAt(at)))
                    {
                        counts.Add(new ValueCount(merge.KeyAt(at).ToArray(), input, Censuses.Rent(variants.Length, textBound)));
                    }

                    end = values.EndOfValue(merge, at, run.RowCount);
                    RecordBatch part = at == 0 && end == run.RowCount ? run : run.Window(at, end - at);
                    for (int v = 0; v < variants.Length; v++)
                    {
                        counts[^1].Paths[v].Add(part, read[v]);
                    }

                    last = merge.KeyAt(end - 1).ToArray();
                }
            }
        }

        return counts;
    }

    /// <summary>The paths of one input's rows of one partition value, counted: the value's first key there, the input, and each variant column's census.</summary>
    private sealed record ValueCount(byte[] First, int Input, PathCensus[] Paths);

    /// <summary>
    /// The path directory of each of the <paramref name="variants"/> columns of the input <paramref name="entry"/> names, read
    /// from <paramref name="lease"/>, the job's own copy of it or its read of the store, when it tells every count a shredding
    /// is chosen from (<see cref="PathCensus.Tells"/>); null for a column whose directory does not, or that has none. Counting
    /// the rows of a merge of 66 appends of metric records again took a seventh of its processor, for what each append's
    /// directory says (2026-11-16).
    /// </summary>
    private static async ValueTask<VariantDirectory.DirectoryBody?[]> DirectoriesAsync(
        ObjectLease lease, ObjectEntry entry, int[] variants, CancellationToken cancellationToken)
    {
        VariantDirectory.DirectoryBody?[] directories = new VariantDirectory.DirectoryBody?[variants.Length];

        // A copy written again lays its chunks its own way, where the regions of the object's table of contents are not.
        if (entry.Contents?.PathDirectories is not { Count: > 0 } regions || lease.IsCopy)
        {
            return directories;
        }

        IO.ISegmentReader file = lease.File;

        try
        {
            for (int v = 0; v < variants.Length; v++)
            {
                foreach ((int column, IndexSegment region) in regions)
                {
                    if (column != variants[v])
                    {
                        continue;
                    }

                    using Buffers.SegmentOwner bytes = await file
                        .ReadAsync(new Serialization.Schemas.SegmentSpec(region.Offset, region.Length, region.AlignmentExponent, 0, 0), cancellationToken)
                        .ConfigureAwait(false);
                    if (region.Holds(bytes.Buffer.Span) && VariantDirectory.Open(bytes.Buffer.Span) is { } body)
                    {
                        if (PathCensus.Tells(body))
                        {
                            directories[v] = body;
                        }
                        else
                        {
                            body.Dispose();
                        }
                    }

                    break;
                }
            }
        }
        catch
        {
            foreach (VariantDirectory.DirectoryBody? directory in directories)
            {
                directory?.Dispose();
            }

            throw;
        }

        return directories;
    }

    /// <summary>
    /// The fields of <paramref name="schema"/> a first pass counts, ascending: those the partition and the key read and the
    /// variant columns, what it reads of its inputs.
    /// </summary>
    private static int[] Counted(DatasetSchema schema)
    {
        SortedSet<int> fields = [.. VariantColumns(schema.DType)];
        if (schema.Layout is { } plan)
        {
            foreach (LayoutColumn read in plan.Components)
            {
                fields.Add(read.Field);
            }

            if (plan.Partition is { } partition)
            {
                fields.Add(partition.Field);
            }
        }

        return [.. fields];
    }

    /// <summary>
    /// What the objects of one partition value of a job shred (<see cref="PlanValuesAsync"/>): each variant column's
    /// shredding, with its chunk target.
    /// </summary>
    internal sealed record ValueShredding(ImmutableDictionary<string, VariantShredding> Shredded, ImmutableDictionary<string, int> VariantChunks);

    /// <summary>
    /// The keys of the variant column <paramref name="field"/> the dataset declares as identifiers or as shredded paths,
    /// each with the class it is read in, which every object shreds wherever a row holds one; null for none. A key
    /// declared both ways keeps its identifier's class, the one its index holds.
    /// </summary>
    private static Dictionary<string, VariantClass>? IdentifierKeys(DatasetSchema schema, int field)
    {
        Dictionary<string, VariantClass>? keys = null;
        foreach (string declared in schema.ShreddedPaths)
        {
            if (VortexDataset.ShreddedPath(declared, schema.DType) is { } path && path.Field == field)
            {
                (keys ??= new Dictionary<string, VariantClass>(StringComparer.Ordinal))[path.Key] = path.Class;
            }
        }

        foreach (string identifier in schema.Identifiers)
        {
            if (VortexDataset.IdentifierPath(identifier, schema.DType) is { } path && path.Field == field)
            {
                (keys ??= new Dictionary<string, VariantClass>(StringComparer.Ordinal))[path.Key] = path.Class;
            }
        }

        return keys;
    }

    /// <summary>The top-level variant columns of <paramref name="schema"/>.</summary>
    private static int[] VariantColumns(Vorticity.Types.DType schema)
    {
        List<int> variants = [];
        for (int field = 0; field < schema.FieldCount; field++)
        {
            if (schema.GetField(field).Kind == Vorticity.Types.DTypeKind.Variant)
            {
                variants.Add(field);
            }
        }

        return [.. variants];
    }

    /// <summary>The objects a compaction writes, rolled at the destination level's size.</summary>
    private sealed class ObjectStream
    {
        private readonly VortexDataset _dataset;
        private readonly DatasetSchema _schema;
        private readonly bool _inKeyOrder;
        private readonly long _target;
        private readonly long _rowBound;
        private readonly long _byteBound;
        private readonly List<WrittenObject> _written = [];
        private readonly List<int> _levels = [];
        private byte[] _lastKey = [];
        private int _lastKeyLength = -1;
        private ObjectOutput? _output;

        // The tree's key of the first row of the object being written, which its tree key leads with; null before one.
        private byte[]? _firstKey;
        private SortedRows? _sorted;
        private long _rows;
        private long _firstRow;
        private readonly int _level;

        // The rows the job reads, `rowBound`, bound every object it writes, and so the length of their chunks;
        // its bytes, `byteBound`, bound what the store sizes the parts of one it streams from. An object ends at `target`
        // bytes of data when `endsBySize`.
        internal ObjectStream(
            VortexDataset dataset, DatasetSchema schema, long target, long firstRow, bool inKeyOrder, long rowBound, int level, long byteBound, bool endsBySize)
        {
            _dataset = dataset;
            _schema = schema;
            _inKeyOrder = inKeyOrder;
            _target = target;
            _firstRow = firstRow;
            _rowBound = rowBound;
            _level = level;
            _byteBound = byteBound;
            _endsBySize = endsBySize;
            _startRow = firstRow;
            _slack = (long)(target * dataset.Options.ObjectEndSlack);
            _drift = dataset.Options.RowWeightDrift;
        }

        // Whether the job ends an object as it reaches the size: not when its plan chose its inputs to make one object
        // (CompactionJob.OneObject), unless its objects ran past the bound of its inputs, which it then starts again under.
        private readonly bool _endsBySize;

        /// <summary>The objects it has sealed.</summary>
        internal IReadOnlyList<WrittenObject> Written => _written;

        /// <summary>The level each object it has sealed goes to, in the order of <see cref="Written"/>.</summary>
        internal IReadOnlyList<int> Levels => _levels;

        internal long Bytes
        {
            get
            {
                long bytes = 0;
                foreach (WrittenObject written in _written)
                {
                    bytes += written.Entry.Bytes;
                }

                return bytes;
            }
        }

        /// <summary>The bytes the writers of the objects it has sealed spilled to disk, their indexes' runs past their memory.</summary>
        internal long ScratchBytes { get; private set; }

        /// <summary>The batches it handed its writers, one a call: what a merge cuts its rows into before they are written.</summary>
        internal long Batches { get; private set; }

        /// <summary>Of <see cref="Batches"/>, those the writers of the objects it sealed ingested on their lanes.</summary>
        internal long BatchesAcross { get; private set; }

        /// <summary>The batches the writers of the objects it sealed sent through on a lane of their own while it went on.</summary>
        internal long BatchesFlown { get; private set; }

        /// <summary>Of <see cref="BatchesFlown"/>, the blocks those writers sent through their line, several at once.</summary>
        internal long BlocksOnLine { get; private set; }

        /// <summary>The chunks of the file the blocks on those writers' lines ended.</summary>
        internal long ChunksEndedOnLine { get; private set; }

        /// <summary>Of <see cref="ChunksEndedOnLine"/>, those cut column by column, the flights after them going through meanwhile.</summary>
        internal long ChunksCutOnLine { get; private set; }

        /// <summary>Of <see cref="BlocksOnLine"/>, those that took their rows of a variant column from what the inputs took of them.</summary>
        internal long BlocksOfRunsTaken { get; private set; }

        /// <summary>Of <see cref="BlocksOfRunsTaken"/>, those whose values of the column were put back together whole before the block was read.</summary>
        internal long BlocksPutBack { get; private set; }

        /// <summary>The times an item of such a block put the column's values back together in an arena of its own.</summary>
        internal long ColumnsPutBackApart { get; private set; }

        /// <summary>
        /// The chunks of a variant column the inputs of the merges into this stream handed over as their shreddings hold
        /// them, those they set out to take and put back together after all, and, of those, the ones taken whole but not
        /// byte for byte (<see cref="VariantTake"/>).
        /// </summary>
        internal (long Held, long Refused, long Inexact) VariantChunks { get; set; }

        /// <summary>The inputs' batches the merges into this stream took over, and those they copied to keep.</summary>
        internal long WindowsTaken { get; set; }

        /// <inheritdoc cref="WindowsTaken"/>
        internal long WindowsCopied { get; set; }

        /// <summary>The inputs' batches whose keys the merges' thread encoded rather than their decode.</summary>
        internal long WindowsKeyedHere { get; set; }

        /// <summary>The slices the merges into this stream were cut in, and those merged ahead on lanes of their own.</summary>
        internal int Slices { get; set; }

        /// <inheritdoc cref="Slices"/>
        internal int SlicesAhead { get; set; }

        /// <summary>The most slices of its merges merging at once, the one taken and those ahead (<see cref="SlicedMerge.PeakAtOnce"/>); 1 for none.</summary>
        internal int SlicesAtOnce { get; set; } = 1;

        /// <summary>The windows the merges' inputs decoded ahead past their own two (<see cref="Scanning.DecodeAhead"/>).</summary>
        internal long InputWindowsAhead { get; set; }

        /// <summary>
        /// Whether the object being written holds its size, as the job last read it (<see cref="ReadSizeAsync"/>): within the
        /// size less its slack (<see cref="DatasetOptions.ObjectEndSlack"/>). Never for a job whose plan chose its inputs to
        /// make one object (<see cref="CompactionJob.OneObject"/>), which then weighs what the merge writes of them.
        /// </summary>
        /// <remarks>
        /// No estimate ends an object: a tier merge of five objects of 370 KB, 1.85 MB together under a size of 2 MiB, wrote
        /// five objects of 370 KB again when an object was ended on its writer's estimate of its size, each at 2 MiB of a
        /// variant column's rows that encoded to a sixth, and the plan took them again without end (2026-11-20).
        /// </remarks>
        internal bool WantsRoll => _output is not null && _full;

        /// <summary>
        /// Whether the rows handed over reach where the object's next reading is asked (<see cref="NextReading"/>): the job
        /// then reads what the object holds (<see cref="ReadSizeAsync"/>).
        /// </summary>
        /// <remarks>
        /// A reading is aimed at the object's size, at what a row weighed in the stream's last object read, or at what a row
        /// of the job's inputs weighs for its first: one reading an object as a rule, which ends every column's chunk there,
        /// and another when the rows weighed less than that. Readings aimed short of the size by a quarter of the gap left,
        /// to be sure of landing under it, left every column two chunks more an object, which a question on one value read
        /// 1.7 times the bytes of (2026-11-21).
        /// <para>
        /// Where what a row weighs moves along the keys, no aim by the rows follows it: log records sorted by service went
        /// from 3.7 to 33.8 bytes a row, and objects aimed at 64 MiB ended at 125 and 211 % of it; on appends of the same
        /// records, four million steady rows came before rows of 31.6 bytes, and an object ended at 241 %. Read a quarter of
        /// an object's rows apart at the latest, those objects ended at 97 to 106 %, and the questions of a bench read 3 002
        /// requests cold where they read 2 632. An object of a line's blocks is read by what its blocks name instead
        /// (<see cref="ReadsNow"/>): those log objects end at 99 to 107 % in 15 ends where the quarters asked 27, and the
        /// questions read 2 644 requests, every answer the same (2026-11-21).
        /// </para>
        /// </remarks>
        internal bool AsksReading => _rows >= NextReading;

        /// <summary>
        /// The rows the object being written takes before its next reading (<see cref="AsksReading"/>), which the job cuts its
        /// runs at, so that the reading is asked there whatever runs its merge hands the rows in; <see cref="long.MaxValue"/>
        /// for none.
        /// </summary>
        /// <remarks>
        /// A reading due within the object's first block, for a size a block outweighs, cuts no run: it falls where the run it
        /// is due in ends, as an object of such a size ended at the first run past its size.
        /// </remarks>
        internal long RowsToReading =>
            NextReading is long next && next != long.MaxValue && _output is { } output && next >= output.Writer.BlockRows ? next - _rows : long.MaxValue;

        /// <summary>
        /// The rows of the object being written its next reading is asked at: where its writer's grid ends a group
        /// (<see cref="VortexFileWriter.GroupRows"/>), or before, in an object of a line's blocks at the end of the first
        /// flight where what its blocks name says it is due (<see cref="ReadsNow"/>), looked at on each, and in a smaller one
        /// where the rows at what a row weighs reach the object's size; on a block's boundary. <see cref="long.MaxValue"/>
        /// for an object that does not end by its size or is full.
        /// </summary>
        /// <remarks>
        /// Asked as the merge's runs came, a reading fell at rows that hung on where an input's batch ended, which the job's
        /// memory decides, and the objects ended at other rows (2026-11-21).
        /// </remarks>
        private long NextReading
        {
            get
            {
                if (_output is not { } output || !_endsBySize || _full)
                {
                    return long.MaxValue;
                }

                // Read on the writer's grid of groups at least, so that no estimate spans more than a group of rows.
                long block = Math.Max(1, output.Writer.BlockRows);
                long grid = output.Writer.GroupRows;
                long next = grid > 0 ? ((_rowsRead / grid) + 1) * grid : long.MaxValue;

                // In an object of a line's blocks, at the end of the first flight where what its blocks name says it is due
                // (ReadsNow), looked at on each, which follows a weight that moves along the keys with no chunk ended before.
                if (output.Writer.Weights is { } weights)
                {
                    long boundary = (_rows + block - 1) / block * block;
                    return Math.Min(next, boundary > _rows ? boundary : ReadsNow(output.Writer, weights, block, next) ? _rows : _rows + block);
                }

                // None by the weight of the rows when those the object can take at most, the job's left or its partition
                // value's, a drift heavier than the last reading says, leave it short of its size: it closes before it fills.
                if (_bytesPerRow <= 0 || (_dataRead + ((_rows - _rowsRead + RowsLeft) * _bytesPerRow * (1 + _drift))) < _target - _slack)
                {
                    return next;
                }

                // On a block's boundary, where every row handed over is in the file; within an object's first block for a size a
                // block outweighs, where the rows past the file's whole blocks count at what a row weighs. At the object's mean:
                // a stretch of a few blocks weighs its chunks' fixed cost, which read objects short of their size again and
                // again.
                double rows = (_target - _dataRead) / _bytesPerRow;
                if (rows < (long.MaxValue >> 2))
                {
                    long byWeight = _rowsRead + Math.Max((long)Math.Ceiling(rows), 1);
                    next = Math.Min(next, byWeight < block ? byWeight : (byWeight + block - 1) / block * block);
                }

                return next;
            }
        }

        /// <summary>
        /// Reads what the object being written holds, exact: a group of rows ends there, every column writing what it holds
        /// (<see cref="VortexFileWriter.EndGroupAsync"/>), on a block's boundary, where every row handed over is in the file.
        /// Full once its data reaches its size less its slack, and sealed when its next row starts another key, its last
        /// chunks the ones that end wrote; otherwise what a row weighs in it places the next reading. The size counts the
        /// object's data, never its end: its statistics, zone maps, indexes and footer, which its index budget keeps or gives
        /// up whole at its end (<see cref="DatasetLayout.ObjectBytes"/>).
        /// </summary>
        /// <remarks>
        /// A reading without ending a chunk counted at their columns' rates what the columns still gathered, which no chunk
        /// had measured: a column's chunk aimed at a mebibyte holds an object of a few hundred kilobytes whole, and light rows
        /// read at the rate of heavier ones before them ended objects at a third of their size (2026-11-21).
        /// </remarks>
        internal async ValueTask ReadSizeAsync(CancellationToken cancellationToken)
        {
            if (_output is not { } output)
            {
                return;
            }

            // Every row handed over, the block's map's among them, which stays where it is.
            // What the object holds, exact: what a row weighs in it from here is its own mean, which no estimate enters.
            GroupEnd end = await output.Writer.EndGroupAsync(cancellationToken).ConfigureAwait(false);
            if (end.Rows > 0)
            {
                _bytesPerRow = (double)end.DataBytes / end.Rows;
            }

            // The rows past the file's whole blocks, which wait for the chunks after, at what a row weighs.
            long data = end.DataBytes + (long)((_rows - end.Rows) * _bytesPerRow);

            // What the blocks named, every flight landed. A reading asked where what a row named drifted starts a window there,
            // whose rows the next reading measures (ReadsNow); any other measures the data a byte named takes over the window,
            // which places the readings after, and the bytes a row named, which counts the rows flown since the last flight
            // known (NamedNow). Over a window, an object's stretches count what ending their groups wrote past their rows: over
            // the stretch since the last reading alone, the last stretch of objects of steady rows took 0.30 bytes of data a
            // byte named where their whole took 0.264, and the next object was read at 87 % of its size, then again
            // (2026-11-21).
            if (output.Writer.Weights is { } weights)
            {
                long named = weights.Through(output.Writer.Flown - 1);
                long block = Math.Max(1, output.Writer.BlockRows);
                if (_dataPerNamed > 0 && Drifts(output.Writer, weights, block))
                {
                    _windowData = data;
                    _windowNamed = named;
                    _windowRows = _rows;
                    _measuring = true;
                    _firstWindow = false;
                }
                else if (named > _windowNamed && data > _windowData && _rows > _windowRows)
                {
                    _dataPerNamed = (double)(data - _windowData) / (named - _windowNamed);
                    _namedPerRow = (double)(named - _windowNamed) / (_rows - _windowRows);
                    _windowSpan = named - _windowNamed;
                    _measuring = false;
                }

                _namedRead = named;
            }

            _rowsRead = _rows;
            _dataRead = data;
            _full = data >= _target - _slack;
            GroupEnds++;
        }

        /// <summary>
        /// Whether the object being written, on a flight's end, is read there (<see cref="NextReading"/>), the end of its
        /// writer's next group at row <paramref name="grid"/>. Before the stream measured what its rows' data takes a byte
        /// named, or past a drift, once its last <see cref="VortexFileWriter.FlightDepth"/> blocks known lie past the window's
        /// start, which they measure. Otherwise once what it holds from its last reading, at what a byte named took over the
        /// window measured, reaches its size; once what a row names drifts (<see cref="Drifts"/>); or, in the stream's first
        /// window, once its blocks named since the window's start reach <see cref="WindowGrowth"/> times the window measured.
        /// The first window's readings wait for the grid when they can (<see cref="WaitsForGrid"/>).
        /// </summary>
        /// <remarks>
        /// What a byte named takes moves along the keys even where what a row names does not, which a window a few times
        /// smaller than what it places says little of: rows of three keys took 0.084 bytes of data a byte named over the
        /// first 4 % of an object and 0.096 over the whole, and an object placed from the first ended at 114 % of its size.
        /// Past the stream's first window, a window spans an object or the rows a drift started, and a reading extrapolates
        /// from it however far: grown four times at a time after every window, the readings of a consolidation of 60 million
        /// log records rose from 24 to 30, each drift then read four times more. Such rows of a test, placed from 2 048 rows
        /// past a drift, end an object at 124 % of its size (2026-11-21).
        /// </remarks>
        private bool ReadsNow(VortexFileWriter writer, Writing.BlockWeights weights, long flightRows, long grid)
        {
            if (_dataPerNamed <= 0)
            {
                return PastWindow(writer, flightRows) && !WaitsForGrid(grid);
            }

            if (_measuring)
            {
                return PastWindow(writer, flightRows);
            }

            double named = NamedNow(writer, weights, flightRows);
            return _dataRead + ((named - _namedRead) * _dataPerNamed) >= _target
                || Drifts(writer, weights, flightRows)
                || (_firstWindow && named - _windowNamed >= WindowGrowth * (double)_windowSpan && !WaitsForGrid(grid));
        }

        /// <summary>
        /// Whether a reading of the stream's first window waits for the writer's next end of a group at row
        /// <paramref name="grid"/>, which writes nothing more: while the rows up to it, at what a row weighs and a drift more,
        /// leave the object short of its size.
        /// </summary>
        /// <remarks>
        /// Read from its eighth block, its first window cut every chunk of a consolidation of 200 million metric records
        /// three times more in its first object, all before the writer's first end of a group, at four million rows.
        /// </remarks>
        private bool WaitsForGrid(long grid) =>
            grid != long.MaxValue && _dataRead + ((grid - _rowsRead) * _bytesPerRow * (1 + _drift)) < _target - _slack;

        // Whether the window measured is the stream's first, from its first row, no object sealed and no drift met.
        private bool _firstWindow = true;

        /// <summary>The times its window the bytes named a reading extrapolates to at most (<see cref="ReadsNow"/>).</summary>
        private const int WindowGrowth = 4;

        /// <summary>
        /// What the blocks of the object being written name as its rows stand: counted through the flight
        /// <see cref="VortexFileWriter.FlightDepth"/> behind the last flown, which its writer landed, as many at any degree;
        /// the rows flown since, and those not flown, at what a row of the blocks known since the last reading named, or of
        /// the window measured when none is.
        /// </summary>
        private double NamedNow(VortexFileWriter writer, Writing.BlockWeights weights, long flightRows)
        {
            long known = writer.Flown - writer.FlightDepth;
            long rowsKnown = known * flightRows;
            if (known > 0 && rowsKnown > _rowsRead)
            {
                long through = weights.Through(known - 1);
                return through + ((_rows - rowsKnown) * ((double)(through - _namedRead) / (rowsKnown - _rowsRead)));
            }

            return _namedRead + ((_rows - _rowsRead) * _namedPerRow);
        }

        /// <summary>
        /// Whether what a row names drifted by more than a drift from the window the data a byte named takes was measured over
        /// (<see cref="ReadSizeAsync"/>): over the last <see cref="VortexFileWriter.FlightDepth"/> blocks known, all past the
        /// last reading, which measured the window with those before them. The rows then are other rows, which a window
        /// started at the reading measures.
        /// </summary>
        private bool Drifts(VortexFileWriter writer, Writing.BlockWeights weights, long flightRows)
        {
            if (_measuring || _namedPerRow <= 0 || !KnownPast(writer, flightRows, _rowsRead))
            {
                return false;
            }

            int window = writer.FlightDepth;
            long known = writer.Flown - window;
            double recent = (double)(weights.Through(known - 1) - weights.Through(known - 1 - window)) / (window * flightRows);
            return Math.Abs((recent / _namedPerRow) - 1) > _drift;
        }

        /// <summary>
        /// Whether the last <see cref="VortexFileWriter.FlightDepth"/> blocks known all lie past the start of the window, which
        /// they measure.
        /// </summary>
        private bool PastWindow(VortexFileWriter writer, long flightRows) => KnownPast(writer, flightRows, _windowRows);

        /// <summary>
        /// Whether the last <see cref="VortexFileWriter.FlightDepth"/> blocks known all lie past row <paramref name="row"/> of
        /// the object being written.
        /// </summary>
        private static bool KnownPast(VortexFileWriter writer, long flightRows, long row)
        {
            int window = writer.FlightDepth;
            long known = writer.Flown - window;
            return known >= window && (known - window) * flightRows >= row;
        }

        // The share of the size an object may hold less or more (DatasetOptions.ObjectEndSlack), in bytes; the share by which
        // a row's weight may drift between two readings (DatasetOptions.RowWeightDrift).
        private readonly long _slack;
        private readonly double _drift;

        // What the last reading found the object being written to hold, the rows handed over then, and whether it was full;
        // none before a reading.
        private long _dataRead;
        private long _rowsRead;
        private bool _full;

        // What a row weighs in the object last read, its data over its rows at that reading, the stream's next object going
        // by it until its own first reading; before any, what a row of the job's inputs weighs (FirstBytesPerRow).
        private double _bytesPerRow;

        // Where the writer of an object of a line's blocks counts what each of its blocks names (BlockWeights), the stream's
        // own, given to each such object's writer; what its blocks named at the last reading, 0 before one. The data a byte
        // named took and the bytes a row named, over the stream's last window measured, 0 before one: from the object's
        // start, or from the reading where what a row named drifted, to the last reading; and the bytes named over it. The
        // data, the bytes named and the rows at the window's start in the object being written; and whether the window
        // started at a drift, its rows not measured yet.
        private Writing.BlockWeights? _weights;
        private long _namedRead;
        private double _dataPerNamed;
        private double _namedPerRow;
        private long _windowSpan;
        private long _windowData;
        private long _windowNamed;
        private long _windowRows;
        private bool _measuring;

        // The stream's first row, which the rows it handed over count from.
        private readonly long _startRow;

        /// <summary>
        /// The rows the object being written takes at most before it closes whatever its size: the job's rows not handed over
        /// yet, and those its partition value's inputs hold (<see cref="RowsByValue"/>), its own handed over apart.
        /// </summary>
        private long RowsLeft => Math.Min(Math.Max(0, _rowBound - (_firstRow - _startRow + _rows)), _valueRowsLeft);

        /// <summary>
        /// The rows the job's inputs hold of each partition value, by the first key of the value's first input, in the keys'
        /// order: what the objects of one value take at most; null when the job is not cut by value or an input holds
        /// several (<see cref="DatasetCompactor.RowsByValueOf"/>).
        /// </summary>
        internal IReadOnlyList<(byte[] Key, long Rows)>? RowsByValue { get; init; }

        // The value of RowsByValue the rows being written hold, and its rows not yet handed over; long.MaxValue when unknown.
        private int _valueAt = -1;
        private long _valueRowsLeft = long.MaxValue;

        /// <summary>Counts <paramref name="rows"/> rows of key <paramref name="key"/> against its partition value's (<see cref="RowsLeft"/>).</summary>
        private void CountValue(ReadOnlySpan<byte> key, int rows)
        {
            if (Values is not { } values || RowsByValue is not { } byValue)
            {
                return;
            }

            if (_valueAt < 0 || !values.Same(byValue[_valueAt].Key, key))
            {
                // The values come in the keys' order: the next one past those done, or none known.
                int at = Math.Max(_valueAt, 0);
                while (at < byValue.Count && !values.Same(byValue[at].Key, key) && byValue[at].Key.AsSpan().SequenceCompareTo(key) < 0)
                {
                    at++;
                }

                bool found = at < byValue.Count && values.Same(byValue[at].Key, key);
                _valueAt = found ? at : _valueAt;
                _valueRowsLeft = found ? byValue[at].Rows : long.MaxValue;
            }

            _valueRowsLeft = _valueRowsLeft == long.MaxValue ? long.MaxValue : _valueRowsLeft - rows;
        }

        /// <summary>What a row of the job's inputs weighs: what places a stream's first reading.</summary>
        internal double FirstBytesPerRow
        {
            init => _bytesPerRow = value;
        }

        /// <summary>The ends of groups of rows the job asked its objects' writers for, each to read what an object held (<see cref="ReadSizeAsync"/>).</summary>
        internal int GroupEnds { get; private set; }

        /// <summary>Whether the last row written carries this key, which must not be split.</summary>
        internal bool HoldsKey(ReadOnlySpan<byte> key) =>
            _lastKeyLength >= 0 && key.SequenceEqual(_lastKey.AsSpan(0, _lastKeyLength));

        /// <summary>Whether the object being written holds rows of another partition value than this key's.</summary>
        internal bool HoldsOtherValue(ValueCut values, ReadOnlySpan<byte> key) =>
            _lastKeyLength >= 0 && !values.Same(_lastKey.AsSpan(0, _lastKeyLength), key);

        /// <summary>
        /// The key the rows come in the order of, whose indexed prefix (<see cref="Prefix"/>) each object keeps the sparse
        /// index of and cuts its blocks on (<see cref="SortedRows"/>); null for objects written without them, which keep no
        /// table of contents.
        /// </summary>
        internal SortKey? Keys { get; init; }

        /// <summary>The components of <see cref="Keys"/> the objects index: the layout's indexed prefix.</summary>
        internal int Prefix { get; init; }

        /// <summary>The components that lead <see cref="Keys"/> before the key's own: the partition's, which an object's index leaves out.</summary>
        internal int Skip { get; init; }

        /// <summary>When the latest rows the job reads arrived, which its objects say of theirs; 0 for when they are sealed.</summary>
        internal long Arrival { get; init; }

        /// <summary>
        /// The partition the object being concatenated holds, row-encoded, which leads its tree key before its position;
        /// null for none, or for objects in a key's order, whose keys lead with it.
        /// </summary>
        internal byte[]? Partition { get; set; }

        /// <summary>
        /// What the objects of each partition value shred, in the order of the values, which the rows come in; null for
        /// objects that shred nothing, and list no path.
        /// </summary>
        internal IReadOnlyList<ValueShredding>? Shreddings { get; init; }

        /// <summary>The partition value the objects are cut by, which tells <see cref="Shreddings"/> apart; null for one.</summary>
        internal ValueCut? Values { get; init; }

        /// <summary>
        /// The object dictionaries each object declares to its writer before its first row, each column's with the bytes its
        /// values take over the job's rows: the union of the inputs' (<see cref="UnionsAsync"/>).
        /// </summary>
        internal IReadOnlyList<(int Field, ObjectDictionary Dictionary, long RowBytes)> Dictionaries { get; init; } = [];

        /// <summary>The column the key's first component reads as it is, whose dictionary is priced as sorted; -1 for none.</summary>
        internal int SortedColumn { get; init; } = -1;

        /// <summary>
        /// The postings of the index of the partition the objects fill, which their identifiers' hashed postings hand their
        /// hashes to; null for objects that keep their own.
        /// </summary>
        internal PartitionPostings? Postings { get; init; }

        /// <summary>
        /// The budget each object's writer and sink rent under: the job's writer's (<see cref="JobMemory.Writer"/>), which never
        /// passes the jobs' budget; null for the jobs' budget itself.
        /// </summary>
        internal QueryMemoryBudget? Budget { get; init; }

        /// <summary>
        /// What each object's writer reserves in before it takes a copy whose size it counted (<see cref="JobMemory.WriterReserve"/>);
        /// null for a writer that reserves nothing.
        /// </summary>
        internal MemoryReserve? Reserve { get; init; }

        /// <summary>
        /// The lane of the flow that writes the objects, the job's own, which each object's sink gives back while it waits for
        /// the store (<see cref="ObjectSegmentSink.Lane"/>); null for a flow that keeps its lane, a range's worker.
        /// </summary>
        internal Execution.HeldLane? Lane { get; init; }

        /// <summary>
        /// What the job tells each object's writer of its memory (<see cref="JobMemory.WriterRoom"/>): whether it admits a
        /// block more in flight, a chunk more in emission, a leaf more laid out, and whether it holds what the writer
        /// holds; null for a writer on its own.
        /// </summary>
        internal Writing.WriteRoom? Room { get; init; }

        /// <summary>
        /// Whether the last row written lies strictly below <paramref name="key"/>: what tells a merge ended short of room
        /// that the rows it has yet to give start at another key than the one the object ends on
        /// (<see cref="IMergedRuns.Stopped"/>). True for an object that holds no row.
        /// </summary>
        internal bool EndsBelow(ReadOnlySpan<byte> key) =>
            _lastKeyLength < 0 || _lastKey.AsSpan(0, _lastKeyLength).SequenceCompareTo(key) < 0;

        /// <summary>Whether an object is being written, which holds a row or more.</summary>
        internal bool HoldsRows => _output is not null && _lastKeyLength >= 0;

        /// <summary>
        /// Has the object's writer land the blocks it has in flight, then end the chunks it has in emission, none written
        /// before its place: what a job short of room gives back of its writer's work ahead.
        /// </summary>
        internal async ValueTask LandAsync()
        {
            if (_output is { } output)
            {
                await output.Writer.LandAsync().ConfigureAwait(false);
                await output.Writer.EmissionsEndAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Has the object's writer end its group of rows where it is, every column writing what it gathered
        /// (<see cref="VortexFileWriter.EndGroupAsync"/>): what a job short of room gives back last, once everything it
        /// started ahead is given back, so that where the group ends follows from its rows and its budget alone. The
        /// object holds a chunk more a column, and the same rows.
        /// </summary>
        internal async ValueTask EndGroupAsync(CancellationToken cancellationToken)
        {
            if (_output is { } output)
            {
                await output.Writer.EndGroupAsync(cancellationToken).ConfigureAwait(false);
                GroupEnds++;
            }
        }

        /// <summary>
        /// Writes rows <paramref name="from"/> to <paramref name="to"/> of the merge's run, which the object takes next in the
        /// key's order: the first starts the next partition value's shredding when they start an object of another value,
        /// and the object's sparse index and blocks take them.
        /// </summary>
        internal async ValueTask WriteAsync(IMergedRun merge, int from, int to, CancellationToken cancellationToken)
        {
            Opens(merge.KeyAt(from));
            ObjectOutput output = _output!;
            if (Keys is { } keys)
            {
                (_sorted ??= new SortedRows(keys, Prefix, skip: Skip)).Take(merge, from, to, output.Writer.BlockRows);
            }

            _firstKey ??= merge.KeyAt(from).ToArray();
            _lastKeyLength = SortedRows.Keep(ref _lastKey, merge.KeyAt(to - 1));

            // The rows where they lie in the input's batch, into the map of the block they fall in, the run that crosses a block
            // cut there: the writer takes a block's map at once, keeps a window on its runs and copies them as the block goes
            // through its line; the merge keeps the batch until the ledger says every run on it is copied. A run of a block or
            // more goes the same way, a block's map at a time: a level's objects, whose keys do not interleave, give runs of
            // whole batches, which went to the writer whole, past its line, their chunks ingested in turns of its lanes.
            (Vorticity.Arrays.CanonicalArena arena, int root, int row, double rowBytes) = merge.CurrentRun;
            int rows = to - from;
            int at = row + from;
            _rows += rows;
            CountValue(merge.KeyAt(from), rows);
            VortexFileWriter writer = output.Writer;
            int staged = writer.MapRows;
            if (staged == 0)
            {
                await HandMapAsync(writer, cancellationToken).ConfigureAwait(false);
                Batches++;
                await writer.WriteRunAloneAsync(arena, root, at, rows, cancellationToken).ConfigureAwait(false);
                return;
            }

            // A map completes the block the writer stages: a slice's end hands it a short one (OwnRunsAsync).
            while (rows > 0)
            {
                int take = Math.Min(rows, staged - writer.StagingRows - _map.Rows);
                _map.Add(arena, root, at, take, rowBytes);
                _ledger?.Note(_map, ref _map.FirstRun);
                at += take;
                rows -= take;
                if (writer.StagingRows + _map.Rows >= staged)
                {
                    await HandMapAsync(writer, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Has the object's writer copy the runs it keeps of the merge's inputs and has not sent through yet, the block's map
        /// handed to it short first: what a slice's end asks, so that the slice's merge and its inputs go before the next slice
        /// opens its own (<see cref="SlicedMerge.OwnRuns"/>). The block's rows stay the same, the maps after completing it.
        /// </summary>
        internal async ValueTask OwnRunsAsync(CancellationToken cancellationToken)
        {
            if (_output is not { } output)
            {
                return;
            }

            await HandMapAsync(output.Writer, cancellationToken).ConfigureAwait(false);
            output.Writer.OwnStaged();
        }

        /// <summary>Hands the block's map to <paramref name="writer"/>, full or short, when it holds rows.</summary>
        private ValueTask HandMapAsync(VortexFileWriter writer, CancellationToken cancellationToken)
        {
            if (_map.Rows == 0)
            {
                return ValueTask.CompletedTask;
            }

            Batches++;
            return writer.WriteBlockAsync(_map, cancellationToken);
        }

        // The runs of the block the merge fills, which the writer of the object being written takes once the block is full.
        private readonly Writing.BlockMap _map = new Writing.BlockMap();

        /// <summary>
        /// The runs the merge's writers keep where they lie in its inputs: each object's writer notes its own there, which the
        /// merge reads to give its inputs' batches back. Null for a stream whose rows are its own.
        /// </summary>
        internal Writing.RunLedger? Ledger
        {
            get => _ledger;
            set
            {
                _ledger = value;
                if (_output is { } output)
                {
                    output.Writer.Ledger = value;
                }
            }
        }

        private Writing.RunLedger? _ledger;

        /// <summary>
        /// Hands the writer of the object being written the block's map, short, and has it copy every row it keeps in the
        /// merge's inputs, as the merge ends.
        /// </summary>
        internal async ValueTask LetGoOfInputsAsync(CancellationToken cancellationToken)
        {
            if (_output is { } output)
            {
                await HandMapAsync(output.Writer, cancellationToken).ConfigureAwait(false);
                await output.Writer.LetGoOfInputsAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Waits for the rows the object's writer has in flight, whatever becomes of them, as a failed merge ends; the block's
        /// map, which reads the inputs, goes.
        /// </summary>
        internal ValueTask SettleAsync()
        {
            _map.Clear();
            _map.FirstRun = -1;
            return _output is { } output ? output.Writer.SettleAsync() : ValueTask.CompletedTask;
        }

        /// <summary>Writes <paramref name="batch"/>'s rows after those written: in no key's order, they index nothing.</summary>
        internal async ValueTask WriteAsync(RecordBatch batch, CancellationToken cancellationToken)
        {
            ObjectOutput output = Open();

            // The blocks the rows before these and these themselves closed, declared before their rows are written.
            _rows += batch.RowCount;
            Batches++;
            await output.Writer.WriteBatchAsync(batch, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// The blocks an object's writer sends through its line at once (<see cref="VortexFileWriter.FlightDepth"/>):
        /// <see cref="VortexFileWriter.FlightsAhead"/> for an object aimed at <see cref="VortexFileWriter.LineObjectBlocks"/>
        /// blocks or more, at the bytes a row of the job's inputs holds; one block for a smaller one, which makes no line.
        /// Read from the job alone, the same at any degree.
        /// </summary>
        private int FlightDepthOf(int blockRows)
        {
            if (_rowBound <= 0 || _byteBound <= 0 || blockRows <= 0)
            {
                return 1;
            }

            double blockBytes = (double)_byteBound / _rowBound * blockRows;
            return _target / blockBytes >= VortexFileWriter.LineObjectBlocks ? VortexFileWriter.FlightsAhead : 1;
        }

        /// <summary>
        /// The object being written, started when none is: one with a sparse index keeps a table of contents, and in a
        /// partition value's rows shreds what they pay for, its writer counting its paths as it takes its rows, for its path
        /// directory, at the grain of its granules: counted here run by run, they held a merge's thread a fortieth of its
        /// time, beside a writer that counts them a block at a time on its lanes (2026-11-16).
        /// </summary>
        private ObjectOutput Open()
        {
            if (_output is { } output)
            {
                return output;
            }

            ValueShredding? shredding = Shreddings is { } shreddings ? shreddings[Math.Clamp(_value, 0, shreddings.Count - 1)] : null;
            output = _dataset.StartObjectUnder(
                _schema, _inKeyOrder, _rowBound, contents: Keys is not null, synopses: Keys is not null, shredded: shredding?.Shredded,
                variantChunks: shredding?.VariantChunks, byteBound: _byteBound, countsPaths: shredding is not null, postings: Postings, budget: Budget,
                lane: Lane, room: Room, reserve: Reserve);
            _output = output;
            output.Writer.Ledger = Ledger;
            output.Writer.FlightDepth = FlightDepthOf(output.Writer.BlockRows);

            // An object of a line's blocks that ends by its size has its writer count what each block names, which places its
            // readings (NextReading): its flights a block each, so that they end where its readings may fall.
            if (_endsBySize && output.Writer.FlightDepth > 1 && output.Writer.MapRows == output.Writer.BlockRows)
            {
                (_weights ??= new Writing.BlockWeights()).Reset();
                output.Writer.Weights = _weights;
            }

            Postings?.BlocksOf(VortexDataset.Uid(output.Identity), output.Writer.BlockRows);
            foreach ((int field, ObjectDictionary dictionary, long rowBytes) in Dictionaries)
            {
                output.Writer.TryDeclareDictionary(field, dictionary, _rowBound, rowBytes, sorted: field == SortedColumn);
            }
            return output;
        }

        // The place among the shreddings of the value being written and its first key.
        private int _value = -1;
        private byte[]? _valueKey;

        /// <summary>
        /// Starts the object the rows of key <paramref name="key"/> go to when none is: what the job cuts their run for
        /// (<see cref="RowsToReading"/>) before it writes them.
        /// </summary>
        internal void Opens(ReadOnlySpan<byte> key)
        {
            Starts(key);
            _ = Open();
        }

        /// <summary>
        /// Records the first key of the rows written next, which starts the next partition value's shredding when they
        /// start an object of another value.
        /// </summary>
        private void Starts(ReadOnlySpan<byte> key)
        {
            if (Shreddings is not null && _output is null && (_value < 0 || (Values is { } values && !values.Same(_valueKey, key))))
            {
                _value++;
                _valueKey = key.ToArray();
            }
        }

        /// <summary>
        /// Seals the object being written once it holds its size, read first when the rows handed over since the last
        /// reading may bring it there (<see cref="AsksReading"/>).
        /// </summary>
        internal async ValueTask RollIfFullAsync(CancellationToken cancellationToken)
        {
            if (AsksReading)
            {
                await ReadSizeAsync(cancellationToken).ConfigureAwait(false);
            }

            if (WantsRoll)
            {
                await RollAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>Seals the object being written and starts the next one.</summary>
        internal async ValueTask RollAsync(CancellationToken cancellationToken)
        {
            if (_output is not { } output)
            {
                return;
            }

            // The object is this method's from here: whatever fails gives it up, its upload included when it streams, since
            // nothing else holds it any more. Its sparse index closes at its last key, and the keys of its first and last rows
            // in the tree's order are its own when it took them in that order.
            if (_inKeyOrder && _schema.Keyed && _firstKey is not null && _lastKeyLength >= 0)
            {
                output.Keys = (_firstKey, _lastKey[.._lastKeyLength]);
            }

            output.Arrival = Arrival;
            output.Partition = _inKeyOrder ? null : Partition;

            // The block's map goes to the object it falls in, short, before the object is sealed.
            await HandMapAsync(output.Writer, cancellationToken).ConfigureAwait(false);
            _output = null;
            _firstKey = null;
            _lastKeyLength = -1;
            WrittenObject? written;
            try
            {
                await using (output.Writer.ConfigureAwait(false))
                {
                    // The rows in flight land before the key index is declared to the writer that takes them.
                    await output.Writer.LandAsync().ConfigureAwait(false);
                    _sorted?.Finish(output.Writer);
                    await output.Writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
                    ScratchBytes += output.Writer.ScratchBytes;
                    BatchesAcross += output.Writer.BatchesIngestedAcross;
                    BatchesFlown += output.Writer.BatchesFlown;
                    BlocksOnLine += output.Writer.BlocksOnLine;
                    ChunksEndedOnLine += output.Writer.ChunksEndedOnLine;
                    ChunksCutOnLine += output.Writer.ChunksCutOnLine;
                    BlocksOfRunsTaken += output.Writer.BlocksOfRunsTaken;
                    BlocksPutBack += output.Writer.BlocksPutBack;
                    ColumnsPutBackApart += output.Writer.ColumnsPutBackApart;
                }

                written = await _dataset.SealAsync(output, _rows, _firstRow, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _sorted?.Clear();
                output.Sink.Discard();
                Postings?.Drop(VortexDataset.Uid(output.Identity));
                throw;
            }

            // What the object's taps still hold goes to the scratch, once its writer has noted every row.
            if (Postings is { } postings)
            {
                if (written is not null)
                {
                    await postings.CloseAsync(VortexDataset.Uid(output.Identity), cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    postings.Drop(VortexDataset.Uid(output.Identity));
                }
            }

            if (written is { } made)
            {
                _written.Add(made);
                _levels.Add(_level);
            }

            _firstRow += _rows;
            _rows = 0;
            _dataRead = 0;
            _rowsRead = 0;
            _namedRead = 0;
            _windowData = 0;
            _windowNamed = 0;
            _windowRows = 0;
            _firstWindow = false;
            _full = false;
        }

        /// <summary>Seals whatever is left.</summary>
        internal ValueTask FinishAsync(CancellationToken cancellationToken) => RollAsync(cancellationToken);

        /// <summary>Drops the object being written: a failed compaction leaves no commit behind.</summary>
        internal async ValueTask AbandonAsync()
        {
            if (_output is not { } output)
            {
                return;
            }

            _output = null;
            _firstKey = null;
            _sorted?.Clear();
            Postings?.Drop(VortexDataset.Uid(output.Identity));
            await output.DisposeAsync().ConfigureAwait(false);
        }
    }
}
