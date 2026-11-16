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

    /// <summary>The most ranges of the key it ran side by side, as many as the budget held; 1 for a job in one piece.</summary>
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

    /// <summary>The batches its rows reached the writers of its objects in, a call each: a diagnostic of how its merge cuts them.</summary>
    internal long WriterBatches { get; init; }

    /// <summary>Of <see cref="WriterBatches"/>, those the writers ingested on their lanes rather than on the job's thread.</summary>
    internal long WriterBatchesAcross { get; init; }
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

        // Its reads keep nothing in the session's segment cache, its measure's included: each object once, which questions
        // would not find there again.
        using SegmentCache.PassingScope passing = SegmentCache.Pass();

        // A handle that has measured no input yet measures one before its first merge, an input and a row; a tier merge
        // wider than the measure lets the budget hold is given up before it starts, counted as no compaction, and the next
        // plan prices its inputs by the measure, where the guess before it took them all and was refused twice.
        if (job.Trigger != CompactionTrigger.Fragments)
        {
            await PriceAsync(dataset, job, cancellationToken).ConfigureAwait(false);
        }

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
    /// own memory (<see cref="JobMemory"/>). A job whose memory was refused a block throws <see cref="JobTooWideException"/>
    /// once its outputs are given up and its blocks given back: the handle's next plan takes fewer inputs.
    /// </summary>
    /// <summary>
    /// Measures an input before the handle's first merge (<see cref="MeasureInputAsync"/>), and gives up a tier merge wider
    /// than the measure lets the jobs' budget hold: <see cref="JobTooWideException"/>, before the job starts.
    /// </summary>
    private static async ValueTask PriceAsync(VortexDataset dataset, CompactionJob job, CancellationToken cancellationToken)
    {
        bool merge = job.Style is CompactionStyle.Leveled || (job.Style is CompactionStyle.Tiered && dataset.Snapshot.Schema.Keyed);
        if (merge && (dataset.InputJobBytes == 0 || dataset.DecodedRowBytes == 0)
            && await MeasureInputAsync(dataset, job, cancellationToken).ConfigureAwait(false)
            && job.Trigger == CompactionTrigger.Tier
            && MemoryWidth(dataset, job.Inputs) is int fits && fits < job.Inputs.Count)
        {
            dataset.CapJobWidth(fits);
            throw new JobTooWideException(job, fits, JobBudget(dataset));
        }
    }

    private static async ValueTask<CompactionResult> RewriteAsync(
        VortexDataset dataset, CompactionJob job, CancellationToken cancellationToken)
    {
        JobMemory memory = new JobMemory(dataset);
        try
        {
            return await RewriteUnderAsync(dataset, job, memory, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (memory.Refused && failure is not JobTooWideException && !cancellationToken.IsCancellationRequested)
        {
            dataset.NarrowJobs(job.Inputs.Count, memory.InputsPeak, memory.Sizing, memory.WriterPeak);
            throw new JobTooWideException(job, memory.Peak, memory.Jobs.CeilingBytes, failure) { Sizing = memory.Sizing };
        }
        finally
        {
            await memory.DisposeAsync().ConfigureAwait(false);
        }
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
        // writes a byte at worst; one that runs past it starts the job again under a bound twice as large.
        ObjectStream OutputsOf(CompactionJob ran, int doublings) => new ObjectStream(
            dataset, schema, ran.TargetBytes, ran.FirstRow, inKeyOrder: merge, rowBound: ran.Rows,
            ran.ToLevel, ObjectBound.Of(ran.Bytes, doublings, dataset.Options.BoundOutPerIn))
        {
            Budget = memory.Writer,
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

        // A merge of several objects' worth runs in ranges of the key side by side, on the session's lanes, each reading of
        // its inputs the granules of its range alone (KeyRanges): a consolidation's, and a level's, which ran on one lane
        // whatever the session gave it, 336 ms for level 0's sixteen objects of 2 M rows at eight lanes, 93 ms in ranges.
        // Not one of a value, since each object of a range shreds the paths of the first.
        KeyRanges? ranges = null;
        if (late is null && merge && shreddings is not { Count: > 1 } && piece is null)
        {
            using Activity? cutting = DatasetTelemetry.StartCompactionPass("ranges");
            ranges = await KeyRangesAsync(dataset, schema, job, cancellationToken).ConfigureAwait(false);
        }

        // The destination's objects, in the key's order: those of one stream, or of one a range.
        ObjectStream[] streams;
        ObjectStream? lateOutputs = null;
        long rows;
        int atOnce = 1;
        int batchRows = 0;
        long writer = WriterCost(dataset, schema, job, shreddings);

        // What the inputs' batches weigh decoded, which the handle keeps for the next job's plan.
        RowMeter meter = new RowMeter();

        Activity? writing = DatasetTelemetry.StartCompactionPass("write");
        try
        {
            if (ranges is not null)
            {
                (streams, rows, atOnce, batchRows) =
                    await RangesAsync(dataset, schema, job, ranges, writer, meter, memory, doublings => OutputsOf(job, doublings), cancellationToken).ConfigureAwait(false);
            }
            else if (piece is { Merged: > 1 })
            {
                // A piece of several ranges below its cut merges them side by side, and counts what each took of each input.
                (streams, rows, atOnce, batchRows) = await RangesAsync(
                    dataset, schema, job, piece.Ranges, writer, meter, memory, doublings => OutputsOf(job, doublings), cancellationToken,
                    piece.Merged, piece.Emitted).ConfigureAwait(false);
            }
            else
            {
                // A job in one piece takes what its blocks hold, never past the jobs' budget: a block refused gives the job up,
                // and the handle's next plan takes fewer inputs. Its inputs' batches take what the budget leaves of it once its
                // writer and its open inputs are counted.
                int open = merge ? ran.Inputs.Count : 1;
                batchRows = InputBatchRows(dataset, schema, ran, open, JobBudget(dataset), writer);
                memory.Sizing = (batchRows, open);
                for (int doublings = 0; ; doublings++)
                {
                    lateOutputs = late is null ? null : new ObjectStream(
                        dataset, schema, ran.Bytes, ran.FirstRow, inKeyOrder: true, rowBound: ran.Rows,
                        0, ObjectBound.Of(ran.Bytes, doublings, dataset.Options.BoundOutPerIn))
                    {
                        Arrival = Latest(ran),
                        Budget = memory.Writer,
                    };
                    ObjectStream outputs = OutputsOf(ran, doublings);
                    try
                    {
                        // A piece reads its first range alone, below its cut, and counts what it takes of each input.
                        if (piece is not null)
                        {
                            Array.Clear(piece.Emitted);
                        }

                        rows = merge
                            ? await MergeAsync(
                                dataset, schema, ran, outputs, late, lateOutputs, batchRows, meter, memory, cancellationToken,
                                piece?.Ranges, 0, piece?.Emitted).ConfigureAwait(false)
                            : await ConcatenateAsync(dataset, schema, ran, outputs, memory, cancellationToken).ConfigureAwait(false);
                        await outputs.FinishAsync(cancellationToken).ConfigureAwait(false);
                        if (lateOutputs is not null)
                        {
                            await lateOutputs.FinishAsync(cancellationToken).ConfigureAwait(false);
                        }

                        streams = [outputs];
                        break;
                    }
                    catch (ObjectPastBoundException) when (doublings < ObjectBound.MostDoublings)
                    {
                        await AbandonAsync(outputs, lateOutputs).ConfigureAwait(false);
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
        }
        finally
        {
            writing?.Dispose();
        }

        // A piece that took no row of level 0, its cut where none lies: it would rewrite the objects of level 1 below the cut
        // and leave level 0 as it was, piece after piece, a loop that never ends. The partition is consolidated in one piece,
        // now and after.
        if (piece is not null && TookOfLevelZero(piece) == 0)
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

        // What the job measured, which the handle's next plan sizes a job by: what an input and the writer held at most.
        long reserved = memory.Peak;
        dataset.LearnDecodedRowBytes(meter.BytesPerRow);
        dataset.LearnJobRate(ran.Bytes, dataset.Options.TimeProvider.GetUtcNow() - started);
        if (merge)
        {
            KeyRanges? sideBySide = ranges ?? (piece is { Merged: > 1 } ? piece.Ranges : null);
            int open = sideBySide is null ? ran.Inputs.Count : Math.Max(1, sideBySide.MostInputs * atOnce);
            int width = 0;
            foreach (CompactionInput input in ran.Inputs)
            {
                width += input.Level == 0 ? 1 : 0;
            }

            dataset.LearnJobCosts(memory.InputsPeak / open, batchRows, memory.WriterPeak / atOnce, width);
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

            pieceRange = span.Count == 0 ? null : LevelRange.Of(job.ToLevel, span, key);
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
        foreach (ObjectStream outputs in streams)
        {
            objectsOut += outputs.Written.Count;
            bytesOut += outputs.Bytes;
            scratch += outputs.ScratchBytes;
            batches += outputs.Batches;
            across += outputs.BatchesAcross;
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
            RangesAtOnce = atOnce,
            ScratchBytes = scratch,
            Outcome = outcome,
            Piece = piece is { ToTheEnd: false },
            WriterBatches = batches,
            WriterBatchesAcross = across,
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
        int batchRows = 0, Buffers.MemoryAccount? account = null)
    {
        // A job's input reads and decodes under the job's inputs' account (JobMemory), which never passes the jobs' budget.
        // Its batches hold its rows at most: an input of 1 924 rows in a merge of batches of 16 384 held 566 KB, a batch's
        // worth of each column, where its job priced it at its rows.
        batchRows = batchRows > 0 ? (int)Math.Clamp(rows?.Length ?? entry.PhysicalRows, 1, batchRows) : batchRows;
        VortexFile file = lease.File;
        FieldMask projection = fields is null ? FieldMask.All : Layouts.FieldMasks.Of(fields);
        if (schema.ColumnsOf(file.DType, entry.Key) is not { } columns)
        {
            ScanBuilder whole = file.ScanBuilder().WithAccount(account);
            return LiveRowsAsync(Ranged(Batched(fields is null ? whole : whole.ProjectMask(projection), batchRows), rows), entry, cancellationToken);
        }

        ObjectReadPlan read = columns.Read(null, projection);
        return ObjectColumns.ReshapeAsync(
            LiveRowsAsync(Ranged(Batched(file.ScanBuilder().WithAccount(account).ProjectMask(read.Shape.Source).WithEncodings(false, false), batchRows), rows), entry, cancellationToken),
            read.Shape,
            null,
            0,
            compact: true,
            cancellationToken);
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
                ParallelOptions lanes = new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1, dataset.Session.Options.MaxConcurrentReads),
                    CancellationToken = cancellationToken,
                };
                await Parallel.ForAsync(0, opened._leases.Length, lanes, async (i, token) =>
                    opened._leases[i] = await memory.RentAsync(job.Inputs[i].Entry, token).ConfigureAwait(false)).ConfigureAwait(false);
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
        long[]? emitted = null)
    {
        KeyOrderedMerge merge = InputsMerged(dataset, schema, job, batchRows, cancellationToken, ranges: ranges, range: range, meter: meter, memory: memory, emitted: emitted);
        ValueCut? values = schema.Cut;
        long rows = 0;
        await using (merge.ConfigureAwait(false))
        {
            while (await merge.MoveNextAsync().ConfigureAwait(false))
            {
                RecordBatch run = merge.Current;

                // The rows that came late lead the merge, keys ascending: those of the run go to level 0, kept apart.
                // An object a partition value, so that each lies within the rows of its value, which a merge of neighbours
                // there takes in with them.
                int start = late?.End(merge, 0, run.RowCount) ?? 0;
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
                for (int at = start, end; at < run.RowCount; at = end)
                {
                    end = values is null ? run.RowCount : values.EndOfValue(merge, at, run.RowCount);

                    // A run that goes on with the key the object ends on, past its size: the rows of that key stay with it,
                    // and the next part starts where the key changes. An input runs through the rows of a key it shares
                    // with others, into the next key, so that a key repeated across the inputs changes inside runs: without
                    // the split, a key of one text column rolled no object, 18 000 rows for 16 KiB aimed at.
                    if (outputs.WantsRoll && outputs.HoldsKey(merge.KeyAt(at)))
                    {
                        end = EndOfKey(merge, at, end);
                    }

                    if ((outputs.WantsRoll && !outputs.HoldsKey(merge.KeyAt(at)))
                        || (values is not null && outputs.HoldsOtherValue(values, merge.KeyAt(at))))
                    {
                        // Never split a key across two objects: two outputs sharing a boundary value
                        // would overlap, and a level wants key-disjoint objects.
                        await outputs.RollAsync(cancellationToken).ConfigureAwait(false);
                    }

                    await outputs.WriteAsync(merge, at, end, cancellationToken).ConfigureAwait(false);
                }

                rows += run.RowCount;
            }
        }

        return rows;
    }

    /// <summary>
    /// The first row from <paramref name="from"/> to <paramref name="to"/> of the merge's run whose key is not the key of
    /// row <paramref name="from"/>, or <paramref name="to"/>: found by halves, the run's keys ascending.
    /// </summary>
    private static int EndOfKey(KeyOrderedMerge merge, int from, int to)
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
    /// counts when given.
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
        long[]? emitted = null)
    {
        Buffers.MemoryAccount? account = memory?.InputsAccount;
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
            rankedTies: false,
            (lease, entry) => meter is null
                ? RowsOfAsync(lease, entry, schema, cancellationToken, fields, rows?[entry.Key], batchRows, account)
                : MeteredAsync(RowsOfAsync(lease, entry, schema, cancellationToken, fields, rows?[entry.Key], batchRows, account), meter, cancellationToken),
            opened: null,
            cancellationToken,
            ranges?.Low(range) ?? default,
            ranges?.High(range) ?? default)
        {
            Account = account ?? QueryMemoryBudget.JobsOf(dataset.Session).Account,
            Rent = memory is null ? null : memory.RentAsync,
            Emitted = emitted,
        };
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
    /// where every input's census, kept to the end, made the job's keys as many times over as it has inputs.
    /// </remarks>
    private static async ValueTask<IReadOnlyList<ValueShredding>> PlanValuesAsync(
        VortexDataset dataset, DatasetSchema schema, CompactionJob job, JobMemory memory, KeyRanges? piece, CancellationToken cancellationToken)
    {
        int[] variants = VariantColumns(schema.DType);
        ValueCut? values = schema.Cut;
        int textBound = dataset.Options.Write.StringBoundBytes;
        List<ValueCount>?[] counts = new List<ValueCount>?[job.Inputs.Count];
        List<(byte[] First, PathCensus[] Paths)> added = [];
        int next = 0;
        Lock gate = new Lock();

        // As many lanes as the room the jobs' budget leaves holds what a lane holds as it counts an input: the input open,
        // its batch at what the handle's last job measured, and a slice of a wide shredding. Their blocks are the job's.
        int censusRows = CensusRows(dataset, job);
        long perLane = File.VortexReadOptions.DefaultVariantSliceBytes;
        foreach (CompactionInput input in job.Inputs)
        {
            perLane = Math.Max(perLane, InputCost(dataset, schema, input.Entry, censusRows) + File.VortexReadOptions.DefaultVariantSliceBytes);
        }

        int most = Math.Min(Math.Max(1, dataset.Session.Options.MaxDegreeOfParallelism), job.Inputs.Count);
        int admitted = (int)Math.Clamp(JobRoom(dataset) / perLane, 1, most);

        ParallelOptions lanes = new ParallelOptions
        {
            MaxDegreeOfParallelism = admitted,
            CancellationToken = cancellationToken,
        };
        await Parallel.ForAsync(0, job.Inputs.Count, lanes, async (input, token) =>
        {
            List<ValueCount> counted = await CountAsync(dataset, schema, job, input, variants, memory, piece, token).ConfigureAwait(false);
            lock (gate)
            {
                counts[input] = counted;
                for (; next < counts.Length && counts[next] is { } ready; counts[next++] = null)
                {
                    foreach (ValueCount count in ready)
                    {
                        Add(count);
                    }
                }
            }
        }).ConfigureAwait(false);

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
    /// one that holds several, the key's columns too, the input merged alone so that its keys tell its values apart.
    /// </summary>
    private static async ValueTask<List<ValueCount>> CountAsync(
        VortexDataset dataset, DatasetSchema schema, CompactionJob job, int input, int[] variants, JobMemory memory, KeyRanges? piece,
        CancellationToken cancellationToken)
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
                VariantDirectory.DirectoryBody?[] directories = piece is null && schema.Holds(held.Entry)
                    ? await DirectoriesAsync(lease, held.Entry, variants, cancellationToken).ConfigureAwait(false)
                    : new VariantDirectory.DirectoryBody?[variants.Length];
                try
                {
                    await foreach (RecordBatch batch in RowsOfAsync(lease, held.Entry, schema, cancellationToken, variants, piece?.RowsOf(input, 0), account: memory.InputsAccount)
                        .WithCancellation(cancellationToken).ConfigureAwait(false))
                    {
                        for (int v = 0; v < variants.Length; v++)
                        {
                            if (directories[v] is null)
                            {
                                paths[v].Add(batch, v);
                            }
                            else
                            {
                                paths[v].AddRows(batch, v);
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
        // its bytes, `byteBound`, bound what the store sizes the parts of one it streams from.
        internal ObjectStream(VortexDataset dataset, DatasetSchema schema, long target, long firstRow, bool inKeyOrder, long rowBound, int level, long byteBound)
        {
            _dataset = dataset;
            _schema = schema;
            _inKeyOrder = inKeyOrder;
            _target = target;
            _firstRow = firstRow;
            _rowBound = rowBound;
            _level = level;
            _byteBound = byteBound;
        }

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

        /// <summary>Whether the object being written has reached the destination's size.</summary>
        internal bool WantsRoll => _output is not null && _output.Sink.Position + _output.Writer.HeldBytes >= _target;

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
        /// Writes rows <paramref name="from"/> to <paramref name="to"/> of the merge's run, which the object takes next in the
        /// key's order: the first starts the next partition value's shredding when they start an object of another value,
        /// and the object's sparse index and blocks take them.
        /// </summary>
        internal async ValueTask WriteAsync(KeyOrderedMerge merge, int from, int to, CancellationToken cancellationToken)
        {
            Starts(merge.KeyAt(from));
            ObjectOutput output = Open();
            if (Keys is { } keys)
            {
                (_sorted ??= new SortedRows(keys, Prefix, skip: Skip)).Take(merge, from, to, output.Writer.BlockRows);
            }

            _firstKey ??= merge.KeyAt(from).ToArray();
            _lastKeyLength = SortedRows.Keep(ref _lastKey, merge.KeyAt(to - 1));
            RecordBatch run = merge.Current;
            await WriteAsync(from == 0 && to == run.RowCount ? run : run.Window(from, to - from), cancellationToken).ConfigureAwait(false);
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
                variantChunks: shredding?.VariantChunks, byteBound: _byteBound, countsPaths: shredding is not null, postings: Postings, budget: Budget);
            _output = output;
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

        internal ValueTask RollIfFullAsync(CancellationToken cancellationToken) =>
            WantsRoll ? RollAsync(cancellationToken) : ValueTask.CompletedTask;

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
            _output = null;
            _firstKey = null;
            _lastKeyLength = -1;
            WrittenObject? written;
            try
            {
                await using (output.Writer.ConfigureAwait(false))
                {
                    _sorted?.Finish(output.Writer);
                    await output.Writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
                    ScratchBytes += output.Writer.ScratchBytes;
                    BatchesAcross += output.Writer.BatchesIngestedAcross;
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
