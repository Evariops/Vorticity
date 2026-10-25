using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Indexes;
using Vorticity.Layouts;
using Vorticity.Scanning;
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
    /// What the commit made of it: applied, or abandoned because another compaction took an input
    /// first, in which case its outputs are left for vacuum.
    /// </summary>
    public OperationOutcome Outcome { get; init; }
}

/// <summary>
/// A period a load writes as its sealed object (<see cref="DatasetCompactor.LoadPeriodsAsync"/>): <paramref name="Period"/>, its
/// start in the instant's units as the dataset's calendar floors its rows, and <paramref name="Parts"/>, which read its rows in
/// the order of their instants, a part's after the rows of the part before. A load reads them once, one after the other, its
/// slices cut as they come; a schema with a variant column reads them twice, one after the other each time, first to plan the
/// object and what each variant column shreds, then to write it, and each part reads the same rows every time.
/// </summary>
internal sealed record PeriodRows(long Period, IReadOnlyList<Func<CancellationToken, IAsyncEnumerable<RecordBatch>>> Parts)
{
    /// <summary>
    /// The rows the period holds, when the caller knows them, 0 otherwise: what a load in one pass announces its object
    /// under.
    /// </summary>
    internal long ExpectedRows { get; init; }
}

/// <summary>A period a load wrote: its start, its rows, its object's bytes, and the version its commit made; no rows, no object.</summary>
internal readonly record struct LoadedPeriod(long Period, long Rows, long Bytes, ulong Version)
{
    /// <summary>The first pass of a schema with a variant column: its rows read and counted, and its object planned; none for one pass.</summary>
    internal TimeSpan Plan { get; init; }

    /// <summary>The pass that writes it, until its last row is handed to the object.</summary>
    internal TimeSpan Write { get; init; }

    /// <summary>From its last row to its object put: the last slice, the indexes' merges, the footer.</summary>
    internal TimeSpan Finish { get; init; }

    /// <summary>The commit, the wait for the commits of the periods before it included.</summary>
    internal TimeSpan Commit { get; init; }
}

/// <summary>Runs one compaction: a k-way merge of the inputs on the clustering key when there is
/// one, a concatenation otherwise.</summary>
internal static class DatasetCompactor
{
    /// <summary>
    /// The least rows the slices of a job of periods may aim at for its objects to be written in one pass: from it on, the
    /// first slice of a stream, which holds the rows it judges its pace from, is the first unit a count of every row cuts at
    /// any steady pace, a unit of the width a count chooses holding more than a thirtieth of twice the target, the widths
    /// being sixty times apart at most. Slices of fewer rows, which a stream would hold together in its first slice until it
    /// judged its pace, two passes cut.
    /// </summary>
    private const long OnePassTargetRows = 32L * SortedSlices.JudgedRows;

    /// <summary>Reads the job's inputs and replaces them by the objects it writes; the dataset moves
    /// to the version the commit creates.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The job has no input.</exception>
    /// <exception cref="VortexUnsupportedException">
    /// The clustering key is composite and one of its columns holds nulls in an input: its run holds
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
    /// Writes each period of <paramref name="periods"/> as its sealed object, straight from its rows, as a seal writes it,
    /// up to <paramref name="degree"/> periods at once, and commits each into the periods level once it is written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What a load of finished periods takes in place of their appends: no object of level 0, no fresh merge, and no seal
    /// that reads again what they wrote, where an incremental build wrote each row three times over on a core or two, a
    /// day's seal 105 GB at once. Each period's rows are read once, in the order of their instants, and written in time
    /// slices with its indexes, cut as the rows come, at the pace of its first rows. A schema with a variant column reads
    /// them twice, once to plan its object, its slices and what its variant columns shred, as a seal's first pass does,
    /// then to write it.
    /// </para>
    /// <para>
    /// The periods are the dataset's own, each a start the calendar floors its rows to: their objects hold no row of
    /// another, and a commit adds each beside those already there, as the outputs of a replacement that reads nothing. The
    /// dataset keeps the length its header names; a load weighs no period.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The dataset is not one of periods, or holds rows of a period the load writes.</exception>
    /// <exception cref="ArgumentException">A period is named twice, does not start a period of the dataset's calendar, or reads rows of another, or out of the order of their instants.</exception>
    /// <exception cref="DatasetIntegrityException">A period of a schema with a variant column read other rows to write its object than to plan it.</exception>
    internal static async Task<IReadOnlyList<LoadedPeriod>> LoadPeriodsAsync(
        VortexDataset dataset, IReadOnlyList<PeriodRows> periods, int degree, WriteTimings? timings = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(periods);
        if (dataset.Options.Periods is null || dataset.Key is not { } key || key.InstantPath.Contains('.'))
        {
            throw new InvalidOperationException("A load of periods writes into a dataset of periods, its instant a top-level column.");
        }

        DatasetSchema schema = dataset.Snapshot.Schema;
        TimeUnit unit = PeriodOptions.InstantOf(key);
        PeriodCalendar calendar = PeriodCalendar.Of(dataset.Snapshot.Header, unit);
        HashSet<long> starts = [];
        foreach (PeriodRows period in periods)
        {
            if (calendar.Floor(period.Period) != period.Period || !starts.Add(period.Period))
            {
                throw new ArgumentException($"The period at {period.Period} is named twice, or starts no period of the dataset's calendar.", nameof(periods));
            }
        }

        // Its objects lie beside the others: a period the dataset holds a row of would overlap them.
        for (int level = 0; level < dataset.Levels.Count; level++)
        {
            await foreach (TreeEntry entry in dataset.Levels[level].EnumerateAsync(dataset.Pages, cancellationToken).ConfigureAwait(false))
            {
                if (!ObjectEntry.FromBytes(entry.Value.Span).Summaries.TryGet(key.InstantPath, out ColumnSummary instants)
                    || !instants.HasMin || !instants.HasMax
                    || starts.Any(start => instants.Min.SignedValue < calendar.Next(start) && instants.Max.SignedValue >= start))
                {
                    throw new InvalidOperationException($"The dataset holds rows of a period the load writes, in an object of level {level}.");
                }
            }
        }

        LoadedPeriod[] loaded = new LoadedPeriod[periods.Count];
        using SemaphoreSlim commits = new SemaphoreSlim(1, 1);
        await Parallel.ForEachAsync(
            Enumerable.Range(0, periods.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, degree), CancellationToken = cancellationToken },
            async (index, token) => loaded[index] = await LoadPeriodAsync(dataset, schema, key, calendar, periods[index], commits, timings, token).ConfigureAwait(false))
            .ConfigureAwait(false);
        return loaded;
    }

    /// <summary>One period of a load: planned, written, and committed.</summary>
    private static async ValueTask<LoadedPeriod> LoadPeriodAsync(
        VortexDataset dataset, DatasetSchema schema, ClusteringKey key, PeriodCalendar calendar, PeriodRows period, SemaphoreSlim commits,
        WriteTimings? timings, CancellationToken cancellationToken)
    {
        const int Level = PeriodLevels.Periods;
        long begin = Stopwatch.GetTimestamp();
        long next = calendar.Next(period.Period);

        // A schema with a variant column is planned by a first pass, whose census chooses what each shreds before a row is
        // written; another is written in one pass, its slices cut as its rows come.
        PeriodPlanner? planner = null;
        long rows = 0;
        if (VariantColumns(schema.DType).Length > 0)
        {
            planner = await PlanAsync(dataset, schema, key, calendar.Unit, period, cancellationToken).ConfigureAwait(false);
            rows = planner.RowsOf(Level, period.Period);
            if (rows == 0)
            {
                return new LoadedPeriod(period.Period, 0, 0, 0);
            }

            // The counts hold the rows in the order of their instants; the first and the last say whether they are the period's.
            (long first, long last) = planner.SecondsOf(Level, period.Period);
            Within(period.Period, next, first, last, calendar);
        }

        long planned = Stopwatch.GetTimestamp();
        long fed = planned;

        // The step that hands the rows over computes on a lane of the session's, which it gives back while it waits for a
        // slice to be written, each slice written on a lane of its own.
        LaneHold hold = new LaneHold(dataset.Session.WriteLanes);
        ObjectStream outputs = new ObjectStream(
            dataset, schema, target: long.MaxValue, firstRow: 0, inKeyOrder: false, rowBound: rows, Level, byteBound: -1)
        {
            Plans = planner?.Plans(),
            Streamed = planner is not null ? null : new StreamPlan(
                calendar, dataset.Options.Periods?.SliceTargetRows ?? TimeSlicer.DefaultTargetRows, period.ExpectedRows),
            InstantPath = key.InstantPath,
            Timings = timings,
            Hold = hold,
        };
        long written = 0;
        try
        {
            await hold.EnterAsync(cancellationToken).ConfigureAwait(false);
            await outputs.RouteAsync(Level, period.Period, cancellationToken).ConfigureAwait(false);
            foreach (Func<CancellationToken, IAsyncEnumerable<RecordBatch>> part in period.Parts)
            {
                // The time between two batches handed over is the caller's: what its rows took to come.
                long asked = Stopwatch.GetTimestamp();
                await foreach (RecordBatch batch in part(cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    long came = Stopwatch.GetTimestamp();
                    timings?.AddFeed(came - asked);
                    if (planner is null && batch.RowCount > 0)
                    {
                        // Rows come in the order of their instants, which their slices check: the first and the last of a
                        // batch say whether its rows are the period's.
                        ReadOnlySpan<long> instants = ColumnPath.Instants(batch, key.InstantPath);
                        Within(period.Period, next, instants[0], instants[^1], calendar);
                    }

                    await outputs.WriteAsync(batch, cancellationToken).ConfigureAwait(false);
                    written += batch.RowCount;
                    asked = Stopwatch.GetTimestamp();
                }
            }

            fed = Stopwatch.GetTimestamp();
            await outputs.FinishAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await outputs.AbandonAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            hold.Exit();
        }

        if (planner is not null && written != rows)
        {
            throw new DatasetIntegrityException(
                $"The period at {period.Period} read {rows} row(s) to plan its object and {written} to write it: a load reads the same rows twice.");
        }

        rows = written;
        if (rows == 0)
        {
            return new LoadedPeriod(period.Period, 0, 0, 0);
        }

        List<(int Level, ReadOnlyMemory<byte> Key, ObjectEntry Entry)> produced = [];
        for (int i = 0; i < outputs.Written.Count; i++)
        {
            produced.Add((outputs.Levels[i], outputs.Written[i].Key, outputs.Written[i].Entry));
        }

        long finished = Stopwatch.GetTimestamp();
        CommitResult commit;
        await commits.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            commit = await dataset.CommitAsync(
                [new DatasetOperation.ReplaceObjects([], produced)], CommitKind.Compaction, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            commits.Release();
        }

        return new LoadedPeriod(period.Period, rows, outputs.Bytes, commit.Version)
        {
            Plan = Stopwatch.GetElapsedTime(begin, planned),
            Write = Stopwatch.GetElapsedTime(planned, fed),
            Finish = Stopwatch.GetElapsedTime(fed, finished),
            Commit = Stopwatch.GetElapsedTime(finished),
        };
    }

    /// <summary>Throws unless the rows from <paramref name="first"/> to <paramref name="last"/> are those of the period from <paramref name="period"/> to <paramref name="next"/>.</summary>
    private static void Within(long period, long next, long first, long last, PeriodCalendar calendar)
    {
        if (first < period || last >= next)
        {
            throw new ArgumentException(
                $"The period at {period} reads rows of the periods at {calendar.Floor(first)} to {calendar.Floor(last)}: a load writes each period's rows alone.", nameof(period));
        }
    }

    /// <summary>
    /// The first pass of a load's period, whose schema has a variant column: its parts counted one after the other, in
    /// their order, on a lane of the session's, since a variant column's census follows its rows in their order.
    /// </summary>
    private static async ValueTask<PeriodPlanner> PlanAsync(
        VortexDataset dataset, DatasetSchema schema, ClusteringKey key, TimeUnit unit, PeriodRows period, CancellationToken cancellationToken)
    {
        const int Level = PeriodLevels.Periods;
        LaneBudget lanes = dataset.Session.WriteLanes;
        PeriodPlanner planner = new PeriodPlanner(dataset, schema, key, unit);
        long entered = await lanes.EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (Func<CancellationToken, IAsyncEnumerable<RecordBatch>> part in period.Parts)
            {
                await foreach (RecordBatch batch in part(cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    planner.Add(Level, period.Period, batch);
                }
            }
        }
        finally
        {
            lanes.Exit(entered);
        }

        return planner;
    }

    /// <summary>The job's rows read, merged or concatenated, written, and its replacement committed.</summary>
    private static async ValueTask<CompactionResult> RewriteAsync(
        VortexDataset dataset, CompactionJob job, CancellationToken cancellationToken)
    {
        if (job.Moves && await MoveAsync(dataset, job, cancellationToken).ConfigureAwait(false) is { } moved)
        {
            return moved;
        }

        bool merge = job.Style is CompactionStyle.Leveled or CompactionStyle.Periods;
        ClusteringKey? key = dataset.Key;
        if (merge)
        {
            Check(key, job);
        }

        // Every input is read as this schema and every output written in it: a compaction is also
        // how objects of an earlier schema come to hold the current one.
        DatasetSchema schema = dataset.Snapshot.Schema;

        // A job of a dataset of periods writes its objects in time slices. Of a schema with a variant
        // column, or slices of fewer rows than a stream cuts as a count does, a first pass over its rows
        // cuts each object's slices and picks the paths its variant columns shred, the second writes
        // them; of another, each object is written in one pass, as a load writes it, its slices cut as
        // its rows come at the pace of its first slice's. The instant must be a top-level column, which
        // slices are declared on. Appends write neither slices nor shredded variants.
        IReadOnlyDictionary<(int Level, long Period), ObjectPlan>? plans = null;
        StreamPlan? streamed = null;
        long target = dataset.Options.Periods?.SliceTargetRows ?? TimeSlicer.DefaultTargetRows;
        if (merge && job.Cut is { } cut && !key!.InstantPath.Contains('.'))
        {
            if (VariantColumns(schema.DType).Length > 0 || target < OnePassTargetRows)
            {
                using Activity? planning = DatasetTelemetry.StartCompactionPass("plan");
                plans = await PlanSlicesAsync(dataset, schema, job, key, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // A seal's one object holds every row the job reads; the objects of another share them out.
                streamed = new StreamPlan(cut.Calendar, target, cut.Slices ? job.Rows : 0);
            }
        }

        // A job of a dataset with a key whose objects aim at a mebibyte or more writes them in slices bounded by their
        // keys, which their tables of contents name: what a lookup of an identifier and a filter on the key's first
        // column read without an open of the object's footer. Its first column must be a top-level one, as for time.
        string? keySliced = merge && job.Cut is null && job.TargetBytes >= dataset.Options.ContentsObjectBytes
            && !key!.InstantPath.Contains('.') ? key.InstantPath : null;

        // Such a job over a schema with a variant column counts, in a first pass, the paths of each clustering value's
        // rows, and its objects of a value shred what those rows pay for; each object then lists its paths and the slices
        // that hold them, counted as its rows are written.
        IReadOnlyList<ValueShredding>? shreddings = null;
        if (keySliced is not null && plans is null && streamed is null && VariantColumns(schema.DType).Length > 0)
        {
            using Activity? planning = DatasetTelemetry.StartCompactionPass("plan");
            shreddings = await PlanValuesAsync(dataset, schema, job, key!, cancellationToken).ConfigureAwait(false);
        }

        // Each object it streams is announced to the store as at most what the job reads, times what a job
        // writes a byte at worst; one that runs past it starts the job again under a bound twice as large.
        ObjectStream outputs;
        long rows;
        Activity? writing = DatasetTelemetry.StartCompactionPass("write");
        try
        {
            for (int doublings = 0; ; doublings++)
            {
                outputs = new ObjectStream(
                    dataset, schema, job.TargetBytes, job.FirstRow, inKeyOrder: merge && plans is null && streamed is null, rowBound: job.Rows,
                    job.ToLevel, ObjectBound.Of(job.Bytes, doublings, dataset.Options.BoundOutPerIn))
                {
                    Plans = plans,
                    Streamed = streamed,
                    InstantPath = plans is null && streamed is null ? null : key!.InstantPath,
                    KeySliced = keySliced,
                    SliceTarget = dataset.Options.SliceTargetRows,
                    Shreddings = shreddings,
                    Values = schema.Cut,
                };
                try
                {
                    rows = merge
                        ? await MergeAsync(dataset, schema, job, key!, outputs, cancellationToken).ConfigureAwait(false)
                        : await ConcatenateAsync(dataset, schema, job, outputs, cancellationToken).ConfigureAwait(false);
                    await outputs.FinishAsync(cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (ObjectPastBoundException) when (doublings < ObjectBound.MostDoublings)
                {
                    await outputs.AbandonAsync().ConfigureAwait(false);
                }
                catch
                {
                    await outputs.AbandonAsync().ConfigureAwait(false);
                    throw;
                }
            }
        }
        finally
        {
            writing?.Dispose();
        }

        if (rows != job.Rows)
        {
            throw new DatasetIntegrityException(
                $"The compaction read {job.Rows} row(s) from {job.Inputs.Count} object(s) and wrote " +
                $"{rows}: a rewrite that loses rows is the one failure it must not have.");
        }

        List<(int Level, ReadOnlyMemory<byte> Key)> consumed = [];
        foreach (CompactionInput input in job.Inputs)
        {
            consumed.Add((input.Level, input.Key));
        }

        List<(int Level, ReadOnlyMemory<byte> Key, ObjectEntry Entry)> produced = [];
        for (int i = 0; i < outputs.Written.Count; i++)
        {
            WrittenObject written = outputs.Written[i];
            produced.Add((outputs.Levels[i], written.Key, written.Entry));
        }

        // What each input was read as: a delete that marked rows in one meanwhile leaves it under its
        // key, and outputs worked out from the rows before the delete would bring them back.
        List<ObjectEntry> read = new List<ObjectEntry>(job.Inputs.Count);
        foreach (CompactionInput input in job.Inputs)
        {
            read.Add(input.Entry);
        }

        DatasetOperation.ReplaceObjects replacement = new DatasetOperation.ReplaceObjects(consumed, produced)
        {
            Expected = read,
            Pointer = job.Stop.IsEmpty ? null : (job.FromLevel, job.Stop),
            Range = job.Range,
        };

        // A seal weighs the period it wrote, and asks in the same commit for the length that load calls for,
        // from past the newest instant: the periods already written keep their bounds.
        List<DatasetOperation> operations = [replacement];
        if (job.Cut is { Slices: true, Newest: { } newest } sealing && outputs.Written.Count == 1
            && Reweigh(dataset, sealing, outputs.Written[0], newest) is { } change)
        {
            operations.Add(change);
        }

        CommitResult commit = await dataset
            .CommitAsync(operations, CommitKind.Compaction, cancellationToken).ConfigureAwait(false);

        return new CompactionResult
        {
            Version = commit.Version,
            FromLevel = job.FromLevel,
            ToLevel = job.ToLevel,
            Trigger = job.Trigger,
            Style = job.Style,
            ObjectsIn = job.Inputs.Count,
            ObjectsOut = outputs.Written.Count,
            Rows = rows,
            BytesIn = job.Bytes,
            BytesOut = outputs.Bytes,
            // The replacement's, first among the operations; a change of period length rides beside it.
            Outcome = commit.Outcomes.Count > 0 ? commit.Outcomes[0] : OperationOutcome.Applied,
        };
    }

    /// <summary>
    /// A trivial move: the one input's entry taken out of its level and put, byte for byte, into the
    /// level above, where nothing overlaps it, so that no row is read or written. Null when the object
    /// holds an earlier schema, which only a rewrite brings to the current one; its tail, which the
    /// open reads to say so, is the move's one request.
    /// </summary>
    /// <remarks>
    /// The commit checks what a merge's does: the entry is still the one planned, and nothing came into
    /// the part of the level above that its bounds cover. An index fragment attached to the object in
    /// between goes up with it, since the object is not rewritten to fold it.
    /// </remarks>
    private static async ValueTask<CompactionResult?> MoveAsync(VortexDataset dataset, CompactionJob job, CancellationToken cancellationToken)
    {
        CompactionInput input = job.Inputs[0];
        ObjectLease lease = await dataset.RentAsync(input.Entry, cancellationToken).ConfigureAwait(false);
        await using (lease.ConfigureAwait(false))
        {
            if (dataset.Snapshot.Schema.ColumnsOf(lease.File.DType, input.Entry.Key) is not null)
            {
                return null;
            }
        }

        DatasetOperation.ReplaceObjects move = new DatasetOperation.ReplaceObjects(
            [(input.Level, input.Key)], [(job.ToLevel, input.Key, input.Entry)])
        {
            Expected = [input.Entry],
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
            ObjectsIn = 1,
            ObjectsOut = 1,
            ObjectsMoved = 1,
            Outcome = commit.Outcomes[0],
        };
    }

    private static void Check(ClusteringKey? key, CompactionJob job)
    {
        if (key is null)
        {
            throw new VortexUnsupportedException(
                "clustering key",
                ComponentKind.Feature,
                "A leveled compaction merges on the clustering key, and this dataset declares none. " +
                "Compact it tiered, which concatenates.");
        }

        // A single column's null keys are read, last, where the encoding puts them; a composite
        // key's run holds no tuple with a null, so a merge that went on would drop those rows. A new
        // dataset declares no such key; one declared before that was refused is merged only where
        // the summaries say no null is there, which a summary loosened by marks no longer says.
        if (!key.IsComposite || !key.MayHoldNull)
        {
            return;
        }

        foreach (CompactionInput input in job.Inputs)
        {
            foreach (string path in key.Paths)
            {
                if (!input.Entry.Summaries.TryGet(path, out ColumnSummary column) || !column.HasNullCount || column.NullCount > 0)
                {
                    throw new VortexUnsupportedException(
                        input.Entry.Key,
                        ComponentKind.Feature,
                        $"'{path}' may hold a null in this object, and a composite key's run holds no tuple with a null: " +
                        "the merge would drop those rows. Declare the composite clustering key on non-nullable columns.");
                }
            }
        }
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
    /// An input's rows, in its own order or in key order along <paramref name="paths"/>, as
    /// <paramref name="schema"/>'s columns, or those of them <paramref name="fields"/> names: its own scan, each batch
    /// reshaped when it was written under another schema.
    /// </summary>
    private static IAsyncEnumerable<RecordBatch> RowsOfAsync(
        ObjectLease lease, ObjectEntry entry, DatasetSchema schema, IReadOnlyList<string>? paths, CancellationToken cancellationToken, bool wide = false,
        int[]? fields = null)
    {
        VortexFile file = lease.File;
        FieldMask projection = fields is null ? FieldMask.All : Layouts.FieldMasks.Of(fields);
        if (schema.ColumnsOf(file.DType, entry.Key) is not { } columns)
        {
            return LiveRowsAsync(file, fields is null ? file.ScanBuilder : () => file.ScanBuilder().ProjectMask(projection), entry, paths, wide, cancellationToken);
        }

        // The clustering key keeps its columns under every schema, so a key-ordered read of an
        // earlier object names them as the dataset does.
        ObjectReadPlan read = columns.Read(null, projection);
        return ObjectColumns.ReshapeAsync(
            LiveRowsAsync(file, () => file.ScanBuilder().ProjectMask(read.Shape.Source).WithEncodings(false, false), entry, paths, wide, cancellationToken),
            read.Shape,
            null,
            0,
            compact: true,
            cancellationToken);
    }

    /// <summary>
    /// The entries a key-ordered read of an input walks at once: an append holds its rows mostly in time order, some of
    /// them a few seconds late, which a window of the file's zones reads ahead of itself split after split, once for
    /// each window that holds a few of them. On 3 × 10⁷ requests at the design's rate, two in a hundred a few seconds
    /// late and one in ten thousand up to an hour, a window of 2^18 entries reads them in 6.8 s, one of the zones'
    /// 8 192 in 13.6 s (2.8 s without late rows); 2^20 read the objects of slices then sorted by their synopsis columns
    /// half again slower, its windows past their slices (<c>vortex-queries --micro lifecycle</c>).
    /// </summary>
    private const int KeyWindowRows = 1 << 18;

    /// <summary>
    /// The key-ordered scan of an input, in windows of <see cref="KeyWindowRows"/> when <paramref name="wide"/>: a job of a
    /// dataset of periods, whose objects end at their periods rather than at a size, which batches of a window's rows
    /// would overshoot.
    /// </summary>
    private static ScanBuilder Ordered(ScanBuilder scan, IReadOnlyList<string> paths, bool wide) =>
        wide ? scan.InKeyOrder(paths).WithKeyWindow(KeyWindowRows) : scan.InKeyOrder(paths);

    /// <summary>
    /// An input's rows through the scans <paramref name="scan"/> makes, in key order along
    /// <paramref name="paths"/> or in its own, without the rows it deleted: a compaction is also where
    /// a deletion vector ends, its outputs holding only the live rows.
    /// </summary>
    private static IAsyncEnumerable<RecordBatch> LiveRowsAsync(
        VortexFile file, Func<ScanBuilder> scan, ObjectEntry entry, IReadOnlyList<string>? paths, bool wide, CancellationToken cancellationToken)
    {
        if (!entry.HasDeletions)
        {
            return (paths is null ? scan() : Ordered(scan(), paths, wide)).ExecuteAsync();
        }

        if (paths is null)
        {
            return LiveRows.WithoutDeletedAsync(
                scan().WithCompaction(false).WithEncodings(false, false).ExecuteAsync(), entry.Deletions, compact: true, 0, cancellationToken);
        }

        ScanBuilder ordered = Ordered(scan(), paths, wide);
        ScanBuilder? nulls = paths.Count == 1 && ColumnPath.MayBeNull(file.DType, paths[0])
            ? scan().Where(Vorticity.Expressions.Expr.IsNull(Vorticity.Expressions.Expr.Field(paths[0]))).WithEncodings(false, false)
            : null;
        return LiveRows.OrderedAsync(ordered, nulls, entry.Deletions, descending: false, cancellationToken);
    }

    /// <summary>Each input's rows, in its own order, one after another.</summary>
    private static async ValueTask<long> ConcatenateAsync(
        VortexDataset dataset, DatasetSchema schema, CompactionJob job, ObjectStream outputs, CancellationToken cancellationToken)
    {
        long rows = 0;
        foreach (CompactionInput input in job.Inputs)
        {
            ObjectLease lease = await dataset.RentAsync(input.Entry, cancellationToken).ConfigureAwait(false);
            await using (lease.ConfigureAwait(false))
            {
                await foreach (RecordBatch batch in RowsOfAsync(lease, input.Entry, schema, null, cancellationToken)
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

    /// <summary>The k-way merge, one run of rows at a time.</summary>
    private static async ValueTask<long> MergeAsync(
        VortexDataset dataset,
        DatasetSchema schema,
        CompactionJob job,
        ClusteringKey key,
        ObjectStream outputs,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> paths = key.Paths;
        KeyOrderedMerge merge = InputsMerged(dataset, schema, job, key, cancellationToken);
        ValueCut? values = schema.Cut;
        long rows = 0;
        long pastHorizon = 0;
        List<Cut> cuts = [];
        await using (merge.ConfigureAwait(false))
        {
            while (await merge.MoveNextAsync().ConfigureAwait(false))
            {
                RecordBatch run = merge.Current;
                if (job.Cut is { } cut)
                {
                    // An object per period, and in level 0's exit, per level: a run whose rows cross a
                    // period's end, or the last fresh instant, is written in parts. One key has one
                    // instant, so no part splits a key.
                    CutRun(run, key.InstantPath, cut, job.ToLevel, cuts);
                    int at = 0;
                    foreach ((int end, int level, long period) in cuts)
                    {
                        // A row of a period already closed, later than the horizon: kept beside its period, and counted.
                        pastHorizon += level == PeriodLevels.Late && cut.PastHorizon(period) ? end - at : 0;
                        await outputs.RouteAsync(level, period, cancellationToken).ConfigureAwait(false);
                        await outputs.WriteAsync(at == 0 && end == run.RowCount ? run : run.Window(at, end - at), cancellationToken)
                            .ConfigureAwait(false);
                        at = end;
                    }

                    rows += run.RowCount;
                    continue;
                }

                // An object holds the rows of one clustering value: a run of several is written in parts, one a value,
                // and a part of another value than the object's seals it. A key has one value, so no part splits a key.
                for (int at = 0, end; at < run.RowCount; at = end)
                {
                    end = values is null ? run.RowCount : values.EndOfValue(merge, at, run.RowCount);

                    // A run that goes on with the key the object or its slice ends on, past its size: the rows of that key
                    // stay with it, and the next part starts where the key changes. An input runs through the rows of a key
                    // it shares with others, into the next key, so that a key repeated across the inputs changes inside
                    // runs: without the split, a key of one text column rolled no object, 18 000 rows for 16 KiB aimed at.
                    if ((outputs.WantsRoll || outputs.WantsSlice) && outputs.HoldsKey(merge.KeyAt(at)))
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
                    else if (outputs.WantsSlice && !outputs.HoldsKey(merge.KeyAt(at)))
                    {
                        // Nor across two slices, whose bounds then hold each key once.
                        await outputs.CutSliceAsync(cancellationToken).ConfigureAwait(false);
                    }

                    outputs.Starts(merge.KeyAt(at));
                    outputs.Note(merge.KeyAt(end - 1));
                    await outputs.WriteAsync(at == 0 && end == run.RowCount ? run : run.Window(at, end - at), cancellationToken).ConfigureAwait(false);
                }

                rows += run.RowCount;
            }
        }

        if (pastHorizon > 0)
        {
            DatasetTelemetry.PastHorizon(pastHorizon);
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
    /// The k-way merge of a job's inputs on the clustering key. Offered by leaf key, an exact lower bound,
    /// so an input opens only once it could hold the next row; the job's own order breaks ties among
    /// inputs holding the same key.
    /// </summary>
    /// <remarks>
    /// With <paramref name="fields"/>, each input reads those fields of <paramref name="schema"/> alone, the clustering
    /// key's among them, and the rows merged hold them alone.
    /// </remarks>
    private static KeyOrderedMerge InputsMerged(
        VortexDataset dataset, DatasetSchema schema, CompactionJob job, ClusteringKey key, CancellationToken cancellationToken, int[]? fields = null)
    {
        List<(ReadOnlyMemory<byte> Key, MergeObject Object)> inputs = new(job.Inputs.Count);
        for (int i = 0; i < job.Inputs.Count; i++)
        {
            CompactionInput input = job.Inputs[i];
            inputs.Add((input.Key, new MergeObject(input.Entry, VortexDataset.OrderOf(input.Key), i)));
        }

        inputs.Sort(static (left, right) => left.Key.Span.SequenceCompareTo(right.Key.Span));
        IReadOnlyList<string> paths = key.Paths;
        return new KeyOrderedMerge(
            dataset.Snapshot,
            inputs.Select(static held => held.Object).ToAsyncEnumerable(),
            key.Paths,
            descending: false,
            rankedTies: false,
            (lease, entry) => RowsOfAsync(lease, entry, schema, paths, cancellationToken, wide: job.Cut is not null, fields),
            opened: null,
            cancellationToken);
    }

    /// <summary>
    /// The first pass of a job of a dataset of periods: its rows merged once, each object it writes,
    /// by level and period, counting its rows per second and the paths of its variant columns. Each
    /// object's slices are then cut from its seconds, and what each variant column shreds chosen from
    /// its paths (<see cref="ShreddingChoice"/>), before a row is written.
    /// </summary>
    /// <remarks>
    /// The pass reads the columns it counts alone, the clustering key and the variant columns, through a projection of
    /// each input, where it read every column and decoded them all a second time.
    /// </remarks>
    private static async ValueTask<IReadOnlyDictionary<(int Level, long Period), ObjectPlan>> PlanSlicesAsync(
        VortexDataset dataset, DatasetSchema schema, CompactionJob job, ClusteringKey key, CancellationToken cancellationToken)
    {
        PeriodCut cut = job.Cut!;
        int[]? counted = PeriodPlanner.Counted(schema, key);
        PeriodPlanner planner = new PeriodPlanner(dataset, schema, key, cut.Unit, counted);
        List<Cut> cuts = [];
        KeyOrderedMerge merge = InputsMerged(dataset, schema, job, key, cancellationToken, counted);
        await using (merge.ConfigureAwait(false))
        {
            while (await merge.MoveNextAsync().ConfigureAwait(false))
            {
                RecordBatch run = merge.Current;
                CutRun(run, key.InstantPath, cut, job.ToLevel, cuts);
                int at = 0;
                foreach ((int end, int level, long period) in cuts)
                {
                    planner.Add(level, period, at == 0 && end == run.RowCount ? run : run.Window(at, end - at));
                    at = end;
                }
            }
        }

        return planner.Plans();
    }

    /// <summary>
    /// The first pass of a write of a dataset of periods, over rows in the order of their instants, each object it writes
    /// told apart by its level and its period: the object's rows per second and the paths of its variant columns; then each
    /// object's plan, from what its rows counted, before a row is written. A seal's first pass feeds it the rows its inputs
    /// merge into, a load of periods the rows it is given.
    /// </summary>
    private sealed class PeriodPlanner
    {
        private readonly DatasetSchema _schema;
        private readonly string _instant;
        private readonly TimeUnit _unit;
        private readonly long _target;
        private readonly int[] _variants;

        // The variant columns' places in the rows the planner is given: the schema's own, or their places among the fields
        // a projection of the rows keeps.
        private readonly int[] _variantsRead;
        private readonly int _textBound;
        private readonly Dictionary<(int Level, long Period), (SecondCounts Seconds, PathCensus[] Paths)> _tallies = [];

        /// <summary>
        /// A planner of the objects of <paramref name="schema"/> a job or a load writes, given rows that hold the fields
        /// <paramref name="fields"/> names alone, ascending, a projection's (<see cref="Counted"/>), or every field.
        /// </summary>
        internal PeriodPlanner(VortexDataset dataset, DatasetSchema schema, ClusteringKey key, TimeUnit unit, int[]? fields = null)
        {
            _schema = schema;
            _instant = key.InstantPath;
            _unit = unit;
            _target = dataset.Options.Periods?.SliceTargetRows ?? TimeSlicer.DefaultTargetRows;
            _variants = VariantColumns(schema.DType);
            _textBound = dataset.Options.Write.StringBoundBytes;
            _variantsRead = fields is null ? _variants : [.. _variants.Select(field => Array.BinarySearch(fields, field))];
        }

        /// <summary>
        /// The fields of <paramref name="schema"/> the planner counts, ascending: the clustering key's and the variant columns,
        /// what a seal's first pass reads of its inputs; null, every field, for a key on a nested column.
        /// </summary>
        internal static int[]? Counted(DatasetSchema schema, ClusteringKey key)
        {
            SortedSet<int> fields = [.. VariantColumns(schema.DType)];
            foreach (string path in key.Paths)
            {
                int field = schema.DType.IndexOfField(path);
                if (field < 0)
                {
                    return null;
                }

                fields.Add(field);
            }

            return [.. fields];
        }

        /// <summary>Counts the rows of <paramref name="part"/>, every one of which goes to the object of <paramref name="level"/> and <paramref name="period"/>.</summary>
        internal void Add(int level, long period, RecordBatch part)
        {
            if (!_tallies.TryGetValue((level, period), out var tally))
            {
                tally = (new SecondCounts(_unit), [.. _variants.Select(_ => new PathCensus(_textBound, _target))]);
                _tallies[(level, period)] = tally;
            }

            ReadOnlySpan<long> instants = ColumnPath.Instants(part, _instant);
            tally.Seconds.Add(instants);
            for (int v = 0; v < _variants.Length; v++)
            {
                tally.Paths[v].Add(part, _variantsRead[v], instants);
            }
        }

        /// <summary>The rows counted for the object of <paramref name="level"/> and <paramref name="period"/>.</summary>
        internal long RowsOf(int level, long period) => _tallies.TryGetValue((level, period), out var tally) ? tally.Seconds.Rows : 0;

        /// <summary>The first and the last second the object of <paramref name="level"/> and <paramref name="period"/> holds a row in; it holds one.</summary>
        internal (long First, long Last) SecondsOf(int level, long period)
        {
            ReadOnlySpan<SecondCount> seconds = _tallies[(level, period)].Seconds.Seconds;
            return (seconds[0].Start, seconds[^1].Start);
        }

        /// <summary>Each object's plan, by level and period: its slices, what each variant column shreds, and each one's path directory.</summary>
        internal IReadOnlyDictionary<(int Level, long Period), ObjectPlan> Plans()
        {
            Dictionary<(int Level, long Period), ObjectPlan> plans = [];
            foreach (((int level, long period), (SecondCounts seconds, PathCensus[] paths)) in _tallies)
            {
                // Each variant column shreds what its rows in the object pay for, in chunks of its own, and
                // lists every path and class they hold, shredded or not, with the slices that hold it.
                SliceTable table = new SliceTable(_unit, TimeSlicer.Cut(seconds.Seconds, _unit, _target));
                ImmutableDictionary<string, VariantShredding> shredded = ImmutableDictionary<string, VariantShredding>.Empty;
                ImmutableDictionary<string, int> chunks = ImmutableDictionary<string, int>.Empty;
                ImmutableDictionary<string, VariantDirectory> directories = ImmutableDictionary<string, VariantDirectory>.Empty;
                for (int v = 0; v < _variants.Length; v++)
                {
                    string name = _schema.DType.GetFieldName(_variants[v]);
                    VariantShredding? chosen = ShreddingChoice.Choose(paths[v], required: IdentifierKeys(_schema, _variants[v]));
                    if (chosen is not null)
                    {
                        shredded = shredded.SetItem(name, chosen);
                        chunks = chunks.SetItem(name, ShreddingChoice.ChunkTargetBytes(paths[v]));
                    }

                    directories = directories.SetItem(name, VariantDirectory.From(paths[v], chosen, table.Slices));
                }

                plans[(level, period)] = new ObjectPlan(table, shredded, chunks, directories);
            }

            return plans;
        }
    }

    /// <summary>
    /// The first pass of a job whose objects carry slices bounded by keys, over a schema with a variant column: its rows
    /// merged once, through a projection of the key and the variant columns, the paths of each clustering value's rows
    /// counted, in the order of the values, and what each variant column shreds in the objects of each chosen from them
    /// (<see cref="ShreddingChoice"/>), the keys the dataset declares shredded wherever a row holds one. Without a
    /// clustering value, the job's rows are counted together.
    /// </summary>
    private static async ValueTask<IReadOnlyList<ValueShredding>> PlanValuesAsync(
        VortexDataset dataset, DatasetSchema schema, CompactionJob job, ClusteringKey key, CancellationToken cancellationToken)
    {
        int[] variants = VariantColumns(schema.DType);
        int[]? counted = PeriodPlanner.Counted(schema, key);
        int[] read = counted is null ? variants : [.. variants.Select(field => Array.BinarySearch(counted, field))];
        int textBound = dataset.Options.Write.StringBoundBytes;
        ValueCut? values = schema.Cut;
        List<PathCensus[]> censuses = [];
        byte[]? last = null;
        KeyOrderedMerge merge = InputsMerged(dataset, schema, job, key, cancellationToken, counted);
        await using (merge.ConfigureAwait(false))
        {
            while (await merge.MoveNextAsync().ConfigureAwait(false))
            {
                RecordBatch run = merge.Current;
                for (int at = 0, end; at < run.RowCount; at = end)
                {
                    // A value starts the job's rows, or follows the last counted where the key's value changes.
                    if (censuses.Count == 0 || (values is not null && !values.Same(last, merge.KeyAt(at))))
                    {
                        censuses.Add([.. variants.Select(_ => new PathCensus(textBound))]);
                    }

                    end = values is null ? run.RowCount : values.EndOfValue(merge, at, run.RowCount);
                    RecordBatch part = at == 0 && end == run.RowCount ? run : run.Window(at, end - at);
                    for (int v = 0; v < variants.Length; v++)
                    {
                        censuses[^1][v].Add(part, read[v]);
                    }

                    last = merge.KeyAt(end - 1).ToArray();
                }
            }
        }

        List<ValueShredding> shreddings = new List<ValueShredding>(censuses.Count);
        foreach (PathCensus[] paths in censuses)
        {
            ImmutableDictionary<string, VariantShredding> shredded = ImmutableDictionary<string, VariantShredding>.Empty;
            ImmutableDictionary<string, int> chunks = ImmutableDictionary<string, int>.Empty;
            for (int v = 0; v < variants.Length; v++)
            {
                if (ShreddingChoice.Choose(paths[v], required: IdentifierKeys(schema, variants[v])) is { } chosen)
                {
                    string name = schema.DType.GetFieldName(variants[v]);
                    shredded = shredded.SetItem(name, chosen);
                    chunks = chunks.SetItem(name, ShreddingChoice.ChunkTargetBytes(paths[v]));
                }
            }

            shreddings.Add(new ValueShredding(shredded, chunks));
        }

        return shreddings;
    }

    /// <summary>
    /// What the objects of one clustering value of a job shred (<see cref="PlanValuesAsync"/>): each variant column's
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

    /// <summary>
    /// What a job writes an object of a level and period with: its slices, what each variant column
    /// shreds, with its chunk target, and each variant column's path directory.
    /// </summary>
    internal sealed record ObjectPlan(
        SliceTable Slices,
        ImmutableDictionary<string, VariantShredding> Shredded,
        ImmutableDictionary<string, int> VariantChunks,
        ImmutableDictionary<string, VariantDirectory> Directories);

    /// <summary>
    /// What a load or a job writes an object of one pass with, its slices cut as its rows come: the calendar of its periods,
    /// the rows a slice aims at, and the rows the object is expected to hold, 0 when unknown.
    /// </summary>
    internal sealed record StreamPlan(PeriodCalendar Calendar, long Target, long ExpectedRows)
    {
        /// <summary>The unit of the instant.</summary>
        internal TimeUnit Unit => Calendar.Unit;

        /// <summary>The seconds the object from <paramref name="period"/> spans: its period's.</summary>
        internal double SecondsOf(long period) => TimeSlicer.SecondsBetween(period, Calendar.Next(period), Calendar.Unit);
    }

    /// <summary>
    /// The change of period length the load of the period a seal wrote calls for, or null: the bytes and
    /// rows over its span of data time of the object written; the columns, the object cap, the erasure
    /// delay. A row's bytes of data bound its widest column's, which keeps the chunk constraint on the safe
    /// side; the object's indexes belong to no column, and would make a small period's rows look wide, so
    /// the object counts its data alone.
    /// </summary>
    private static DatasetOperation.ChangePeriodLength? Reweigh(
        VortexDataset dataset, PeriodCut cut, WrittenObject written, long newest)
    {
        ObjectEntry period = written.Entry;
        if (dataset.Key is not { } key || !period.Summaries.TryGet(key.InstantPath, out ColumnSummary instant)
            || !instant.HasMin || instant.Min.Kind != Vorticity.Expressions.FilterLiteralKind.Signed || period.Rows == 0
            || newest == long.MaxValue)
        {
            return null;
        }

        (long bytes, long rows, long data) = (period.Bytes, period.Rows, written.DataBytes > 0 ? written.DataBytes : period.Bytes);
        long start = cut.Calendar.Floor(instant.Min.SignedValue);
        double seconds = (cut.Calendar.Next(start) - start) / (double)TimeSlicer.Units(TimeSpan.FromSeconds(1), cut.Unit);
        PeriodLoad load = new PeriodLoad(
            bytes / seconds,
            rows / seconds,
            LeafColumns(dataset.Snapshot.Schema.DType),
            (double)data / rows,
            dataset.Options.MaxObjectBytes,
            dataset.Snapshot.Header.ErasureDelay);
        SliceWidth length = PeriodLengths.Choose(load);
        DatasetTelemetry.PeriodWeighed(load.BytesPerSecond, length);
        return cut.Calendar.ChangeTo(length, newest) is { } change
            ? new DatasetOperation.ChangePeriodLength(change, cut.Calendar.Changes.Count)
            : null;
    }

    /// <summary>The leaf columns of a schema, which a footer holds a node per chunk of.</summary>
    private static int LeafColumns(Vorticity.Types.DType dtype)
    {
        if (dtype.Kind != Vorticity.Types.DTypeKind.Struct)
        {
            return 1;
        }

        int leaves = 0;
        for (int field = 0; field < dtype.FieldCount; field++)
        {
            leaves += LeafColumns(dtype.GetField(field));
        }

        return Math.Max(leaves, 1);
    }

    /// <summary>Where a part of a run ends, and the level and period its rows go to.</summary>
    private readonly record struct Cut(int End, int Level, long Period);

    /// <summary>Cuts a run, sorted on its instant, into the parts that go to one level and one period, into <paramref name="cuts"/>.</summary>
    private static void CutRun(RecordBatch run, string instant, PeriodCut cut, int to, List<Cut> cuts)
    {
        cuts.Clear();
        ReadOnlySpan<long> instants = ColumnPath.Instants(run, instant);
        int at = 0;
        while (at < instants.Length)
        {
            long first = instants[at];
            int level = cut.LevelOf(first, to);

            long period = cut.Calendar.Floor(first);
            long stop = cut.Calendar.Next(period);
            if (level == PeriodLevels.Late && cut.Exit && cut.FreshAfter is { } after && after < long.MaxValue)
            {
                stop = Math.Min(stop, after + 1);
            }

            int end = at + LowerBound(instants[at..], stop);
            if (end == at)
            {
                throw new InvalidOperationException($"A row of instant {first} lies past the end, at {stop}, of the span its job writes.");
            }

            cuts.Add(new Cut(end, level, period));
            at = end;
        }
    }

    /// <summary>The first index of the ascending <paramref name="values"/> at or past <paramref name="value"/>.</summary>
    private static int LowerBound(ReadOnlySpan<long> values, long value)
    {
        int low = 0;
        int high = values.Length;
        while (low < high)
        {
            int middle = (int)((uint)(low + high) >> 1);
            if (values[middle] < value)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
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
        private ObjectDraft? _draft;
        private long _rows;
        private long _firstRow;
        private int _level;
        private long _period;

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

        /// <summary>
        /// Each object's plan, by level and period, when the objects are written in time slices: its
        /// slices and what its variant columns shred; null for rows written as they come.
        /// </summary>
        internal IReadOnlyDictionary<(int Level, long Period), ObjectPlan>? Plans { get; init; }

        /// <summary>The instant column, top-level, the slices are declared on; null without plans.</summary>
        internal string? InstantPath { get; init; }

        /// <summary>Where the stages of its slices take their time is added; null for no count.</summary>
        internal WriteTimings? Timings { get; init; }

        /// <summary>
        /// The lane of the step that hands the rows over, which its slices give back while it waits for the one before to be
        /// written, each of them written on a lane of the session's own; null for a write on no lane.
        /// </summary>
        internal LaneHold? Hold { get; init; }

        /// <summary>
        /// What its objects are cut by when they are written in one pass, their slices cut as their rows come;
        /// null for objects written as their plans say, or as their rows come.
        /// </summary>
        internal StreamPlan? Streamed { get; init; }

        // The slices of the object being written, written as they fill; null without plans. Whether the first slice of an
        // object of one pass still has to tell its writer what it judged.
        private SortedSlices? _sorter;
        private bool _declare;

        /// <summary>
        /// Sends what is written next to <paramref name="level"/> and <paramref name="period"/>, sealing
        /// the object being written when it holds another's: an object holds one level's rows of one
        /// period.
        /// </summary>
        internal async ValueTask RouteAsync(int level, long period, CancellationToken cancellationToken)
        {
            if (_draft is not null && (level != _level || period != _period))
            {
                await RollAsync(cancellationToken).ConfigureAwait(false);
            }

            _level = level;
            _period = period;
        }

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

        /// <summary>Whether the object being written has reached the destination's size.</summary>
        internal bool WantsRoll => _draft is not null && _draft.Sink.Position + _draft.Writer.GatheredBytes >= _target;

        /// <summary>Whether the last row written carries this key, which must not be split.</summary>
        internal bool HoldsKey(ReadOnlySpan<byte> key) =>
            _lastKeyLength >= 0 && key.SequenceEqual(_lastKey.AsSpan(0, _lastKeyLength));

        /// <summary>Whether the object being written holds rows of another clustering value than this key's.</summary>
        internal bool HoldsOtherValue(ValueCut values, ReadOnlySpan<byte> key) =>
            _lastKeyLength >= 0 && !values.Same(_lastKey.AsSpan(0, _lastKeyLength), key);

        /// <summary>Records the last key of a run, so the next roll does not split it.</summary>
        internal void Note(ReadOnlySpan<byte> key)
        {
            if (_lastKey.Length < key.Length)
            {
                _lastKey = new byte[key.Length];
            }

            key.CopyTo(_lastKey);
            _lastKeyLength = key.Length;
        }

        internal async ValueTask WriteAsync(RecordBatch batch, CancellationToken cancellationToken)
        {
            if (_draft is null)
            {
                if (Streamed is { } streamed)
                {
                    // An object of one pass starts with its first row, whose pace cuts its slices.
                    if (batch.RowCount == 0)
                    {
                        return;
                    }

                    Start(streamed, batch);
                }
                else
                {
                    // An object of a dataset of periods is written as its plan says: its slices, and what its
                    // variant columns shred.
                    ObjectPlan? plan = Plans?[(_level, _period)];
                    ValueShredding? shredding = Shreddings is { } shreddings ? shreddings[Math.Clamp(_value, 0, shreddings.Count - 1)] : null;
                    _draft = _dataset.StartObjectUnder(
                        _schema, _inKeyOrder, _rowBound, sliced: plan is not null || KeySliced is not null, shredded: plan?.Shredded ?? shredding?.Shredded,
                        variantChunks: plan?.VariantChunks ?? shredding?.VariantChunks, directories: plan?.Directories, byteBound: _byteBound);
                    _census = shredding is null ? null : [.. VariantColumns(_schema.DType).Select(_ => new PathCensus(_dataset.Options.Write.StringBoundBytes, SliceTarget))];
                    if (plan is not null)
                    {
                        _sorter = new SortedSlices(plan.Slices, _schema.DType.IndexOfField(InstantPath!), _schema.DType)
                        {
                            Timings = Timings,
                            Lanes = _dataset.Session.WriteLanes,
                            Hold = Hold,
                        };
                    }
                }
            }

            _rows += batch.RowCount;
            if (_sorter is { } sorter)
            {
                // Rows come in time order; each slice they complete is written whole.
                await sorter.AppendAsync(batch, WriteSliceAsync, cancellationToken).ConfigureAwait(false);
                return;
            }

            _sliceFilled += batch.RowCount;
            if (_census is { } census)
            {
                // Each row counted at its place in the object, which the slices' rows tell apart.
                Count(census, batch, _rows - batch.RowCount);
            }

            await _draft.Writer.WriteBatchAsync(batch, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Counts the paths of each variant column of <paramref name="batch"/>, its first row at <paramref name="first"/> of the object.</summary>
        private void Count(PathCensus[] census, RecordBatch batch, long first)
        {
            if (_places.Length < batch.RowCount)
            {
                _places = new long[Math.Max(batch.RowCount, _places.Length * 2)];
            }

            Span<long> places = _places.AsSpan(0, batch.RowCount);
            for (int row = 0; row < places.Length; row++)
            {
                places[row] = first + row;
            }

            int[] variants = VariantColumns(_schema.DType);
            for (int v = 0; v < variants.Length; v++)
            {
                census[v].Add(batch, variants[v], places);
            }
        }

        /// <summary>
        /// Declares to <paramref name="writer"/> each variant column's path directory, of what <paramref name="census"/>
        /// counted, each path naming the slices of <paramref name="slices"/> that hold it, told apart by their rows.
        /// </summary>
        private void DeclareDirectories(VortexFileWriter writer, PathCensus[] census, SliceTable? slices, ImmutableDictionary<string, VariantShredding> shredded)
        {
            List<TimeSlice>? places = null;
            if (slices is not null)
            {
                places = new List<TimeSlice>(slices.Slices.Count);
                foreach (TimeSlice slice in slices.Slices)
                {
                    places.Add(new TimeSlice(slice.FirstRow, slice.FirstRow + slice.Rows, slice.FirstRow, slice.Rows));
                }
            }

            int[] variants = VariantColumns(_schema.DType);
            for (int v = 0; v < variants.Length; v++)
            {
                string name = _schema.DType.GetFieldName(variants[v]);
                writer.DeclareDirectory(name, VariantDirectory.From(census[v], shredded.GetValueOrDefault(name), places));
            }
        }

        /// <summary>
        /// The key's first column, top-level, the objects' slices bounded by their keys are declared on; null for objects
        /// written without them. Their rows come in key order.
        /// </summary>
        internal string? KeySliced { get; init; }

        /// <summary>The rows a slice bounded by keys aims at, past which the next key starts another.</summary>
        internal long SliceTarget { get; init; }

        // The slices of the object being written, bounded by keys: each closed slice's rows, each slice's first key, and the
        // rows of the one being written.
        private readonly List<long> _sliceRows = [];
        private readonly List<byte[]> _sliceLows = [];
        private long _sliceFilled;

        /// <summary>Whether the slice being written, bounded by keys, holds its target, so that the next key starts another.</summary>
        internal bool WantsSlice => KeySliced is not null && _sliceFilled >= SliceTarget;

        /// <summary>
        /// What the objects of each clustering value shred, in the order of the values, which the rows come in; null for
        /// objects that shred nothing, and list no path.
        /// </summary>
        internal IReadOnlyList<ValueShredding>? Shreddings { get; init; }

        /// <summary>The clustering value the objects are cut by, which tells <see cref="Shreddings"/> apart; null for one.</summary>
        internal ValueCut? Values { get; init; }

        // The place among the shreddings of the value being written and its first key; the paths of the object being
        // written, a census a variant column, counted at each row's place in the object.
        private int _value = -1;
        private byte[]? _valueKey;
        private PathCensus[]? _census;
        private long[] _places = [];

        /// <summary>
        /// Records the first key of the rows written next, which starts a slice bounded by keys when they do, and the next
        /// clustering value's shredding when they start an object of another value.
        /// </summary>
        internal void Starts(ReadOnlySpan<byte> key)
        {
            if (KeySliced is not null && _sliceFilled == 0)
            {
                _sliceLows.Add(key.ToArray());
            }

            if (Shreddings is not null && _draft is null && (_value < 0 || (Values is { } values && !values.Same(_valueKey, key))))
            {
                _value++;
                _valueKey = key.ToArray();
            }
        }

        /// <summary>Ends the slice being written: its chunks end with it, every column's together, and the next rows start the next.</summary>
        internal async ValueTask CutSliceAsync(CancellationToken cancellationToken)
        {
            if (_draft is not { } draft || _sliceFilled == 0)
            {
                return;
            }

            await draft.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            _sliceRows.Add(_sliceFilled);
            _sliceFilled = 0;
        }

        /// <summary>
        /// The slices of the object being written, bounded by keys, its last one closed at its last key; null when it holds
        /// none. The lists start over for the next object.
        /// </summary>
        private SliceTable? KeySlices()
        {
            SliceTable? table = null;
            if (KeySliced is not null && _sliceLows.Count > 0 && _lastKeyLength >= 0)
            {
                if (_sliceFilled > 0)
                {
                    _sliceRows.Add(_sliceFilled);
                }

                table = SliceTable.ByKeys(_sliceRows, _sliceLows, _lastKey.AsSpan(0, _lastKeyLength).ToArray());
            }

            _sliceRows.Clear();
            _sliceLows.Clear();
            _sliceFilled = 0;
            return table;
        }

        /// <summary>
        /// Starts the object of one pass whose first rows are <paramref name="first"/>: its slices cut as the rows come at the
        /// pace of the rows its first slice holds. Its first slice is laid out for the rows a unit holds at the pace of
        /// <paramref name="first"/>, those its pace is judged from at least.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_draft))]
        private void Start(StreamPlan streamed, RecordBatch first)
        {
            ReadOnlySpan<long> instants = ColumnPath.Instants(first, InstantPath!);
            double pace = instants.Length / Math.Max(TimeSlicer.SecondsBetween(instants[0], instants[^1], streamed.Unit), 1);
            double rows = Math.Max(TimeSlicer.RowsAt(pace, TimeSlicer.WidthAt(pace, streamed.Target)), SortedSlices.JudgedRows);
            _stream = new SliceStream(streamed.Unit, streamed.Target, least: SortedSlices.JudgedRows);
            _streamed = streamed;
            _draft = _dataset.StartObjectUnder(
                _schema, _inKeyOrder, streamed.ExpectedRows > 0 ? streamed.ExpectedRows : _rowBound, sliced: true, byteBound: _byteBound);
            _sorter = new SortedSlices(_stream, _schema.DType.IndexOfField(InstantPath!), _schema.DType, rows, streamed.Target)
            {
                Timings = Timings,
                Lanes = _dataset.Session.WriteLanes,
                Hold = Hold,
            };
            _declare = true;
        }

        /// <summary>
        /// Tells the writer of an object of one pass what its first slice judged, before it is written: the rows its period
        /// holds at the pace of the rows judged, unless it is expected to hold some, at most the rows a job reads, which size
        /// its chunks and its hashed postings.
        /// </summary>
        private void Judged(SliceStream stream, StreamPlan streamed)
        {
            long rows = streamed.ExpectedRows > 0 ? streamed.ExpectedRows : (long)(stream.Pace * streamed.SecondsOf(_period));
            _draft!.Writer.DeclareRowBound(_rowBound > 0 ? Math.Min(rows, _rowBound) : rows);
        }

        // The stream an object of one pass cuts its slices with, and what it was started with; null for another.
        private SliceStream? _stream;
        private StreamPlan? _streamed;

        /// <summary>A slice written, one slice at a time, on the thread of the slices' writes.</summary>
        private async ValueTask WriteSliceAsync(RecordBatch slice, CancellationToken cancellationToken)
        {
            if (_declare)
            {
                // The first slice of a stream judged its pace, which the writer is told before its first row.
                _declare = false;
                Judged(_stream!, _streamed!);
            }

            // The slice's chunks end with it, every column's together: a lane's range of the object starts at a slice.
            long writing = Stopwatch.GetTimestamp();
            await _draft!.Writer.WriteBatchAsync(slice, cancellationToken).ConfigureAwait(false);
            await _draft.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            Timings?.AddWrite(Stopwatch.GetTimestamp() - writing);
        }

        internal ValueTask RollIfFullAsync(CancellationToken cancellationToken) =>
            WantsRoll ? RollAsync(cancellationToken) : ValueTask.CompletedTask;

        /// <summary>Seals the object being written and starts the next one.</summary>
        internal async ValueTask RollAsync(CancellationToken cancellationToken)
        {
            if (_draft is not { } draft)
            {
                return;
            }

            // The slice being written goes out first, and the stream's last: their writes read the draft and the sorter.
            if (_sorter is { } writing)
            {
                long draining = Stopwatch.GetTimestamp();
                await writing.EndAsync(cancellationToken).ConfigureAwait(false);
                await writing.DrainAsync().ConfigureAwait(false);
                Timings?.AddDrain(Stopwatch.GetTimestamp() - draining);
            }

            // The object is this method's from here: whatever fails gives it up, its upload included when it streams, since
            // nothing else holds the draft any more. Its slices bounded by keys close at its last key.
            SliceTable? keySlices = KeySlices();
            _draft = null;
            _lastKeyLength = -1;
            _stream = null;
            WrittenObject? written;
            try
            {
                await using (draft.Writer.ConfigureAwait(false))
                {
                    if (keySlices is not null)
                    {
                        draft.Writer.DeclareSlices(KeySliced!, keySlices);
                    }

                    if (_census is { } census)
                    {
                        _census = null;
                        DeclareDirectories(
                            draft.Writer, census, keySlices, Shreddings![Math.Clamp(_value, 0, Shreddings.Count - 1)].Shredded);
                    }

                    if (_sorter is { } sorter)
                    {
                        // A first pass counted these rows: every slice is written, or the passes disagree.
                        _sorter = null;
                        bool complete = sorter.Complete;
                        SliceTable table = sorter.Table;
                        await sorter.DisposeAsync().ConfigureAwait(false);
                        if (!complete)
                        {
                            throw new DatasetIntegrityException(
                                $"The object of level {_level} wrote {_rows} rows where its slices hold {table.Rows}: the two passes of the job read different rows.");
                        }

                        draft.Writer.DeclareSlices(InstantPath!, table);
                    }

                    long closing = Stopwatch.GetTimestamp();
                    await draft.Writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
                    Timings?.AddClose(Stopwatch.GetTimestamp() - closing);
                }

                written = await _dataset.SealAsync(draft, _rows, _firstRow, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                draft.Sink.Discard();
                throw;
            }

            if (written is { } output)
            {
                _written.Add(output);
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
            if (_draft is not { } draft)
            {
                return;
            }

            // A slice being written reads the draft: its write ends, whatever it ends with, before it goes, waited for on no
            // lane, which the slice's write may need.
            if (_sorter is { } sorter)
            {
                _sorter = null;
                await sorter.DisposeAsync().ConfigureAwait(false);
            }

            _draft = null;
            _stream = null;
            await draft.Writer.DisposeAsync().ConfigureAwait(false);
            draft.Sink.Discard();
        }
    }
}
