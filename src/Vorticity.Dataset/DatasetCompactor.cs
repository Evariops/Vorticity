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
    /// under, and weighs its synopsis tables against before the end, which it gives up none of without them.
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

    /// <summary>From its last row to its object put: the last slice, the indexes' merges, the tables, the footer.</summary>
    internal TimeSpan Finish { get; init; }

    /// <summary>The commit, the wait for the commits of the periods before it included.</summary>
    internal TimeSpan Commit { get; init; }
}

/// <summary>Runs one compaction: a k-way merge of the inputs on the clustering key when there is
/// one, a concatenation otherwise.</summary>
internal static class DatasetCompactor
{
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
    /// day's seal 105 GB at once. Each period's rows are read once, in the order of their instants, and written in sorted
    /// slices with its synopsis tables and its indexes: its slices cut as the rows come, at the pace of its first rows, and
    /// the order inside them settled by its first slice from the columns that may be synopsis columns. A schema with a
    /// variant column reads them twice, once to plan its object, its slices, the order inside them and what its variant
    /// columns shred, as a seal's first pass does, then to write it.
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
                calendar.Unit,
                SynopsisOrder.Candidates(schema.DType, key, dataset.Options.Identifiers),
                dataset.Options.Periods?.SliceTargetRows ?? TimeSlicer.DefaultTargetRows,
                period.ExpectedRows,
                TimeSlicer.SecondsBetween(period.Period, next, calendar.Unit)),
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

        // A job of a dataset of periods writes its objects in sorted slices: a first pass over its rows
        // cuts each object's slices, picks the order inside them and the paths its variant columns
        // shred, the second sorts and writes them. The instant must be a top-level column, which
        // slices are declared on. Appends write neither slices nor shredded variants.
        IReadOnlyDictionary<(int Level, long Period), ObjectPlan>? plans = null;
        if (merge && job.Cut is not null && !key!.InstantPath.Contains('.'))
        {
            using Activity? planning = DatasetTelemetry.StartCompactionPass("plan");
            plans = await PlanSlicesAsync(dataset, schema, job, key, cancellationToken).ConfigureAwait(false);
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
                    dataset, schema, job.TargetBytes, job.FirstRow, inKeyOrder: merge && plans is null, rowBound: job.Rows, job.ToLevel,
                    ObjectBound.Of(job.Bytes, doublings, dataset.Options.BoundOutPerIn))
                {
                    Plans = plans,
                    InstantPath = plans is null ? null : key!.InstantPath,
                    Partition = key?.SharedPartition(job.Inputs.Select(static input => input.Entry.Summaries)),
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

        // A seal weighs the period it wrote, once every partition sealed it, and asks in the same commit for
        // the length that load calls for, from past the newest instant: the periods already written keep
        // their bounds.
        List<DatasetOperation> operations = [replacement];
        if (job.Cut is { Slices: true, Newest: { } newest, Peers: { } peers } sealing && outputs.Written.Count == 1
            && Reweigh(dataset, sealing, outputs.Written[0], peers, newest) is { } change)
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
    /// <paramref name="schema"/>'s columns: its own scan, each batch reshaped when it was written
    /// under another schema.
    /// </summary>
    private static IAsyncEnumerable<RecordBatch> RowsOfAsync(
        ObjectLease lease, ObjectEntry entry, DatasetSchema schema, IReadOnlyList<string>? paths, CancellationToken cancellationToken, bool wide = false)
    {
        VortexFile file = lease.File;
        if (schema.ColumnsOf(file.DType, entry.Key) is not { } columns)
        {
            return LiveRowsAsync(file, file.ScanBuilder, entry, paths, wide, cancellationToken);
        }

        // The clustering key keeps its columns under every schema, so a key-ordered read of an
        // earlier object names them as the dataset does.
        ObjectReadPlan read = columns.Read(null, FieldMask.All);
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
    /// late and one in ten thousand up to an hour, a window of 2^18 entries reads them in 6.8 s instead of 13.6 s
    /// (2.8 s without late rows); 2^20 reads the objects of sorted slices half again slower, its windows past their
    /// slices.
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

                if (outputs.WantsRoll && !outputs.HoldsKey(merge.FirstKey))
                {
                    // Never split a key across two objects: two outputs sharing a boundary value
                    // would overlap, and a level wants key-disjoint objects.
                    await outputs.RollAsync(cancellationToken).ConfigureAwait(false);
                }

                outputs.Note(merge.LastKey);
                rows += run.RowCount;
                await outputs.WriteAsync(run, cancellationToken).ConfigureAwait(false);
            }
        }

        if (pastHorizon > 0)
        {
            DatasetTelemetry.PastHorizon(pastHorizon);
        }

        return rows;
    }

    /// <summary>
    /// The k-way merge of a job's inputs on the clustering key. Offered by leaf key, an exact lower bound,
    /// so an input opens only once it could hold the next row; the job's own order breaks ties among
    /// inputs holding the same key.
    /// </summary>
    private static KeyOrderedMerge InputsMerged(VortexDataset dataset, DatasetSchema schema, CompactionJob job, ClusteringKey key, CancellationToken cancellationToken)
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
            (lease, entry) => RowsOfAsync(lease, entry, schema, paths, cancellationToken, wide: job.Cut is not null),
            opened: null,
            cancellationToken);
    }

    /// <summary>
    /// The first pass of a job of a dataset of periods: its rows merged once, each object it writes,
    /// by level and period, counting its rows per second, the distinct values of the columns that may
    /// be synopsis columns, and the paths of its variant columns. Each object's slices are then cut from
    /// its seconds, its order picked from its counts, and what each variant column shreds chosen from
    /// its paths (<see cref="ShreddingChoice"/>), before a row is written.
    /// </summary>
    /// <remarks>
    /// The pass reads the inputs whole a second time: the instant and the candidate columns alone would
    /// do, read through a projection, which a later change takes.
    /// </remarks>
    private static async ValueTask<IReadOnlyDictionary<(int Level, long Period), ObjectPlan>> PlanSlicesAsync(
        VortexDataset dataset, DatasetSchema schema, CompactionJob job, ClusteringKey key, CancellationToken cancellationToken)
    {
        PeriodCut cut = job.Cut!;
        PeriodPlanner planner = new PeriodPlanner(dataset, schema, key, cut.Unit);
        List<Cut> cuts = [];
        KeyOrderedMerge merge = InputsMerged(dataset, schema, job, key, cancellationToken);
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
    /// told apart by its level and its period: the object's rows per second, the distinct values of the columns that may
    /// be synopsis columns, and the paths of its variant columns; then each object's plan, from what its rows counted,
    /// before a row is written. A seal's first pass feeds it the rows its inputs merge into, a load of periods the rows
    /// it is given.
    /// </summary>
    private sealed class PeriodPlanner
    {
        private readonly VortexDataset _dataset;
        private readonly DatasetSchema _schema;
        private readonly string _instant;
        private readonly TimeUnit _unit;
        private readonly long _target;
        private readonly int[] _candidates;
        private readonly int[] _variants;
        private readonly int _textBound;
        private readonly Dictionary<(int Level, long Period), (SecondCounts Seconds, CardinalitySketch[] Sketches, PathCensus[] Paths)> _tallies = [];

        internal PeriodPlanner(VortexDataset dataset, DatasetSchema schema, ClusteringKey key, TimeUnit unit)
        {
            _dataset = dataset;
            _schema = schema;
            _instant = key.InstantPath;
            _unit = unit;
            _target = dataset.Options.Periods?.SliceTargetRows ?? TimeSlicer.DefaultTargetRows;
            _candidates = SynopsisOrder.Candidates(schema.DType, key, dataset.Options.Identifiers);
            _variants = VariantColumns(schema.DType);
            _textBound = dataset.Options.Write.StringBoundBytes;
        }

        /// <summary>Counts the rows of <paramref name="part"/>, every one of which goes to the object of <paramref name="level"/> and <paramref name="period"/>.</summary>
        internal void Add(int level, long period, RecordBatch part)
        {
            if (!_tallies.TryGetValue((level, period), out var tally))
            {
                tally = (new SecondCounts(_unit), [.. _candidates.Select(static _ => new CardinalitySketch())], [.. _variants.Select(_ => new PathCensus(_textBound, _target))]);
                _tallies[(level, period)] = tally;
            }

            ReadOnlySpan<long> instants = ColumnPath.Instants(part, _instant);
            tally.Seconds.Add(instants);
            SynopsisOrder.Count(part, _candidates, tally.Sketches);
            for (int v = 0; v < _variants.Length; v++)
            {
                tally.Paths[v].Add(part, _variants[v], instants);
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

        /// <summary>Each object's plan, by level and period: its slices and the order inside them, what each variant column shreds, and each one's path directory.</summary>
        internal IReadOnlyDictionary<(int Level, long Period), ObjectPlan> Plans()
        {
            Dictionary<(int Level, long Period), ObjectPlan> plans = [];
            foreach (((int level, long period), (SecondCounts seconds, CardinalitySketch[] sketches, PathCensus[] paths)) in _tallies)
            {
                (int, long)[] counted = new (int, long)[_candidates.Length];
                for (int i = 0; i < _candidates.Length; i++)
                {
                    counted[i] = (_candidates[i], sketches[i].Estimate());
                }

                // Each variant column shreds what its rows in the object pay for, in chunks of its own, and
                // lists every path and class they hold, shredded or not, with the slices that hold it.
                SliceTable table = new SliceTable(_unit, TimeSlicer.Cut(seconds.Seconds, _unit, _target), SynopsisOrder.Choose(counted, seconds.Rows));
                ImmutableDictionary<string, VariantShredding> shredded = ImmutableDictionary<string, VariantShredding>.Empty;
                ImmutableDictionary<string, int> chunks = ImmutableDictionary<string, int>.Empty;
                ImmutableDictionary<string, VariantDirectory> directories = ImmutableDictionary<string, VariantDirectory>.Empty;
                for (int v = 0; v < _variants.Length; v++)
                {
                    string name = _schema.DType.GetFieldName(_variants[v]);
                    VariantShredding? chosen = ShreddingChoice.Choose(paths[v], required: IdentifierKeys(_dataset, _schema.DType, _variants[v]));
                    if (chosen is not null)
                    {
                        shredded = shredded.SetItem(name, chosen);
                        chunks = chunks.SetItem(name, ShreddingChoice.ChunkTargetBytes(paths[v]));
                    }

                    directories = directories.SetItem(name, VariantDirectory.From(paths[v], chosen, table.Slices));
                }

                plans[(level, period)] = new ObjectPlan(
                    new SliceTable(table.Unit, table.Slices, table.Order, SynopsisPaths(_schema.DType, _variants, paths, shredded)),
                    shredded,
                    chunks,
                    directories);
            }

            return plans;
        }
    }

    /// <summary>
    /// The paths an object's synopsis may count. First each key its variant columns shred as one class, in
    /// the order of the columns and of their keys: the first slice places those whose values repeat in the
    /// object's order, and the synopsis sums the numeric ones that do not. Then, per column, the top-level
    /// keys it leaves in the residual that a table could answer for less than parsing their rows, in their
    /// class most of their values hold, those held most first, as many as read into columns of their own
    /// take no more memory than the column's values: the synopsis counts their values, or sums them.
    /// </summary>
    private static List<SynopsisPath> SynopsisPaths(
        Vorticity.Types.DType schema, int[] variants, PathCensus[] censuses, ImmutableDictionary<string, VariantShredding> shredded)
    {
        List<SynopsisPath> paths = [];
        for (int field = 0; field < schema.FieldCount; field++)
        {
            if (shredded.TryGetValue(schema.GetFieldName(field), out VariantShredding? plan) && plan.Kind == VariantShreddingKind.Object)
            {
                int counted = Array.IndexOf(variants, field);
                foreach (ShreddedKey key in plan.Keys)
                {
                    if (key.Field.Kind == VariantShreddingKind.Primitive)
                    {
                        paths.Add(new SynopsisPath(
                            field, key.Name, key.Field.Class, Floats: key.Field.Class == VariantClass.Double && HoldsFloats(counted < 0 ? null : censuses[counted], key.Name)));
                    }
                }
            }
        }

        for (int v = 0; v < variants.Length; v++)
        {
            PathCensus census = censuses[v];
            shredded.TryGetValue(schema.GetFieldName(variants[v]), out VariantShredding? plan);
            List<(string Key, VariantClass Class, long Held)> residual = [];
            foreach ((string key, PathCensus.PathCount count) in census.Root.Keys)
            {
                if (plan is not null && plan.Kind == VariantShreddingKind.Object && plan.IndexOf(System.Text.Encoding.UTF8.GetBytes(key)) >= 0)
                {
                    continue;
                }

                VariantClass best = VariantClass.Integer;
                foreach (VariantClass @class in (ReadOnlySpan<VariantClass>)[VariantClass.Double, VariantClass.String, VariantClass.Boolean])
                {
                    best = count.Of(@class) > count.Of(best) ? @class : best;
                }

                // A question over the key parses at least its occurrences' bytes and groups its rows; the
                // smallest table costs its bytes. Past that, a table may answer it for less.
                long held = count.Of(best);
                SynopsisPrices prices = SynopsisPrices.Default;
                if (held * ((ShreddingChoice.ResidualBytesOf(best) * prices.ByteSeconds) + prices.RowSeconds) > SmallestTableBytes * prices.ByteSeconds)
                {
                    residual.Add((key, best, held));
                }
            }

            // A column of a path takes up to 16 bytes a row, its value or its view: no more paths than the
            // variant column takes in memory a row, its value's bytes and the views of its metadata and value.
            long room = census.Rows == 0 ? 0 : ((census.ValueBytes / census.Rows) + 32) / 16;
            foreach ((string key, VariantClass @class, long _) in residual.OrderByDescending(r => r.Held).ThenBy(r => r.Key, StringComparer.Ordinal).Take((int)Math.Min(room, int.MaxValue)))
            {
                paths.Add(new SynopsisPath(variants[v], key, @class, Residual: true, Floats: @class == VariantClass.Double && HoldsFloats(census, key)));
            }
        }

        return paths;
    }

    /// <summary>
    /// Whether the object holds 32-bit floats at the top-level key <paramref name="key"/>: values <c>AsDouble</c>
    /// reads that a path of doubles leaves out. Without a census, it may.
    /// </summary>
    private static bool HoldsFloats(PathCensus? census, string key) =>
        census is null
        || (census.Root.Keys.TryGetValue(key, out PathCensus.PathCount? count) && count.Read(PathClass.Double) > count.Of(VariantClass.Double));

    /// <summary>
    /// The bytes of the smallest synopsis table: one row, three measures, as the write bench measured it
    /// (`vortex-queries --micro slicebytes`, 1 452 bytes). A table has no fewer, so a path whose question
    /// costs less than reading them gains nothing from one.
    /// </summary>
    private const long SmallestTableBytes = 1_452;

    /// <summary>
    /// The keys of the variant column <paramref name="field"/> the dataset declares as identifiers or as shredded paths,
    /// each with the class it is read in, which every object shreds wherever a row holds one; null for none. A key
    /// declared both ways keeps its identifier's class, the one its index holds.
    /// </summary>
    private static Dictionary<string, VariantClass>? IdentifierKeys(VortexDataset dataset, Vorticity.Types.DType schema, int field)
    {
        Dictionary<string, VariantClass>? keys = null;
        foreach (string declared in dataset.Options.ShreddedPaths ?? [])
        {
            if (VortexDataset.ShreddedPath(declared, schema) is { } path && path.Field == field)
            {
                (keys ??= new Dictionary<string, VariantClass>(StringComparer.Ordinal))[path.Key] = path.Class;
            }
        }

        foreach (string identifier in dataset.Options.Identifiers ?? [])
        {
            if (VortexDataset.IdentifierPath(identifier, schema) is { } path && path.Field == field)
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
    /// What a job writes an object of a level and period with: its slices and the order inside them,
    /// what each variant column shreds, with its chunk target, and each variant column's path directory.
    /// </summary>
    internal sealed record ObjectPlan(
        SliceTable Slices,
        ImmutableDictionary<string, VariantShredding> Shredded,
        ImmutableDictionary<string, int> VariantChunks,
        ImmutableDictionary<string, VariantDirectory> Directories);

    /// <summary>
    /// What a load writes an object of one pass with, its slices cut as its rows come: the unit of its instant, the columns
    /// that may be its synopsis columns, the rows a slice aims at, the rows the object is expected to hold, 0 when unknown,
    /// and the seconds its period spans.
    /// </summary>
    internal sealed record StreamPlan(TimeUnit Unit, int[] Candidates, long Target, long ExpectedRows, double PeriodSeconds);

    /// <summary>
    /// The change of period length the load of the period a seal wrote calls for, or null: the bytes and
    /// rows over its span of data time of the period's largest object, the one written or the largest
    /// another partition sealed of it (<paramref name="peers"/>), every constraint bounding an object and
    /// each partition sealing its own; the columns, the object cap, the erasure delay. A row's bytes of data
    /// bound its widest column's, which keeps the chunk constraint on the safe side; the object's indexes and
    /// synopsis tables belong to no column, and would make a small period's rows look wide, so the object
    /// written counts its data alone, and another its whole bytes, which errs on the same side.
    /// </summary>
    private static DatasetOperation.ChangePeriodLength? Reweigh(
        VortexDataset dataset, PeriodCut cut, WrittenObject written, (long Bytes, long Rows) peers, long newest)
    {
        ObjectEntry period = written.Entry;
        if (dataset.Key is not { } key || !period.Summaries.TryGet(key.InstantPath, out ColumnSummary instant)
            || !instant.HasMin || instant.Min.Kind != Vorticity.Expressions.FilterLiteralKind.Signed || period.Rows == 0
            || newest == long.MaxValue)
        {
            return null;
        }

        (long bytes, long rows, long data) = peers.Bytes > period.Bytes && peers.Rows > 0
            ? (peers.Bytes, peers.Rows, peers.Bytes)
            : (period.Bytes, period.Rows, written.DataBytes > 0 ? written.DataBytes : period.Bytes);
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

    /// <summary>
    /// Cuts a run, sorted on its instant, into the parts that go to one level and one period, into
    /// <paramref name="cuts"/>.
    /// </summary>
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
        /// Each object's plan, by level and period, when the objects are written in sorted slices: its
        /// slices and what its variant columns shred; null for rows written as they come.
        /// </summary>
        internal IReadOnlyDictionary<(int Level, long Period), ObjectPlan>? Plans { get; init; }

        /// <summary>The instant column, top-level, the slices are declared on; null without plans.</summary>
        internal string? InstantPath { get; init; }

        /// <summary>The partition every input holds, which every output then holds; null without one.</summary>
        internal Vorticity.Expressions.FilterLiteral[]? Partition { get; init; }

        /// <summary>Where the stages of its sorted slices take their time is added; null for no count.</summary>
        internal WriteTimings? Timings { get; init; }

        /// <summary>
        /// The lane of the step that hands the rows over, which its sorted slices give back while it waits for the one before to
        /// be written, each of them written on a lane of the session's own; null for a write on no lane.
        /// </summary>
        internal LaneHold? Hold { get; init; }

        /// <summary>
        /// What its objects are cut and sorted by when they are written in one pass, their slices cut as their rows come;
        /// null for objects written as their plans say, or as their rows come.
        /// </summary>
        internal StreamPlan? Streamed { get; init; }

        // The slices of the object being written, sorted as they fill; null without plans. Whether the first slice's order
        // still has to name the object's synopsis columns to its writer.
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
        internal bool WantsRoll => _draft is not null && _draft.Sink.Position >= _target;

        /// <summary>Whether the last row written carries this key, which must not be split.</summary>
        internal bool HoldsKey(ReadOnlySpan<byte> key) =>
            _lastKeyLength >= 0 && key.SequenceEqual(_lastKey.AsSpan(0, _lastKeyLength));

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
                    _draft = _dataset.StartObjectUnder(
                        _schema, _inKeyOrder, _rowBound, sliced: plan is not null, shredded: plan?.Shredded, variantChunks: plan?.VariantChunks,
                        directories: plan?.Directories, byteBound: _byteBound);
                    _draft.Partition = Partition;
                    if (plan is not null)
                    {
                        SliceTable table = plan.Slices;
                        int instant = _schema.DType.IndexOfField(InstantPath!);
                        _sorter = new SortedSlices(table, instant, _schema.DType, Shapes(plan.Shredded))
                        {
                            Timings = Timings,
                            Lanes = _dataset.Session.WriteLanes,
                            Hold = Hold,
                        };

                        // The synopsis's tables, counted from each slice as it is sorted, beside its write on a lane of their
                        // own, their chunks held by the writer.
                        _synopsis = new SynopsisTables(
                            _schema.DType, table.Unit, SynopsisTables.Measures(_schema.DType, instant), _draft.Writer, table.Paths, table.Slices,
                            _dataset.Options.SynopsisChunking)
                        {
                            Groupings = _dataset.Options.SynopsisGroupings,
                        };
                        _slices = new SliceWriter(_draft.Writer, _synopsis, _dataset.Session.WriteLanes, Timings);
                        _declare = true;
                    }
                }
            }

            _rows += batch.RowCount;
            if (_sorter is { } sorter)
            {
                // Rows come in time order; each slice they complete is sorted and written whole.
                await sorter.AppendAsync(batch, WriteSortedAsync, cancellationToken).ConfigureAwait(false);
                return;
            }

            await _draft.Writer.WriteBatchAsync(batch, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Starts the object of one pass whose first rows are <paramref name="first"/>: its slices cut as the rows come at the
        /// pace of the rows its order is judged from, which its first slice holds and settles the order on from the columns
        /// that may be synopsis columns. Its first slice is laid out for the rows a unit holds at the pace of
        /// <paramref name="first"/>, those it is judged from at least.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_draft))]
        private void Start(StreamPlan streamed, RecordBatch first)
        {
            int instant = _schema.DType.IndexOfField(InstantPath!);
            ReadOnlySpan<long> instants = ColumnPath.Instants(first, InstantPath!);
            double pace = instants.Length / Math.Max(TimeSlicer.SecondsBetween(instants[0], instants[^1], streamed.Unit), 1);
            double rows = Math.Max(TimeSlicer.RowsAt(pace, TimeSlicer.WidthAt(pace, streamed.Target)), SortedSlices.JudgedRows);
            _stream = new SliceStream(streamed.Unit, streamed.Target, least: SortedSlices.JudgedRows);
            _streamed = streamed;
            _draft = _dataset.StartObjectUnder(_schema, _inKeyOrder, streamed.ExpectedRows, sliced: true, byteBound: _byteBound);
            _draft.Partition = Partition;
            _sorter = new SortedSlices(_stream, streamed.Candidates, instant, _schema.DType, rows, streamed.Target)
            {
                Timings = Timings,
                Lanes = _dataset.Session.WriteLanes,
                Hold = Hold,
            };

            _synopsis = new SynopsisTables(
                _schema.DType, streamed.Unit, SynopsisTables.Measures(_schema.DType, instant), _draft.Writer, chunking: _dataset.Options.SynopsisChunking)
            {
                Groupings = _dataset.Options.SynopsisGroupings,
            };
            _slices = new SliceWriter(_draft.Writer, _synopsis, _dataset.Session.WriteLanes, Timings);
            _declare = true;
        }

        /// <summary>
        /// Tells the writer and the synopsis of an object of one pass what its first slice judged, before it is written: the
        /// rows its period holds at the pace of the rows judged, unless it is expected to hold some, which size its chunks
        /// and its hashed postings; and the extent the synopsis weighs its tables by, which gives up none before the end
        /// without the rows expected, and spans hours when its period does.
        /// </summary>
        private void Judged(SliceStream stream, StreamPlan streamed)
        {
            _draft!.Writer.DeclareRowBound(streamed.ExpectedRows > 0 ? streamed.ExpectedRows : (long)(stream.Pace * streamed.PeriodSeconds));
            _synopsis!.Extend(stream.Extent(streamed.ExpectedRows, streamed.PeriodSeconds > 3_600));
        }

        // The stream an object of one pass cuts its slices with, and what it was started with; null for another.
        private SliceStream? _stream;
        private StreamPlan? _streamed;

        /// <summary>The object's synopsis columns, by name: the top-level columns its slices are sorted by, shapes and paths aside.</summary>
        private List<string> SynopsisColumns(IReadOnlyList<int> order)
        {
            List<string> columns = [];
            foreach (int entry in order)
            {
                if (entry >= 0 && !SynopsisPath.Names(entry))
                {
                    columns.Add(_schema.DType.GetFieldName(entry));
                }
            }

            return columns;
        }

        /// <summary>A shape coder for each variant column the object shreds as an object, by its field; null for none.</summary>
        private Dictionary<int, VariantShapes>? Shapes(ImmutableDictionary<string, VariantShredding> shredded)
        {
            Dictionary<int, VariantShapes>? shapes = null;
            foreach (KeyValuePair<string, VariantShredding> column in shredded)
            {
                int field = _schema.DType.IndexOfField(column.Key);
                if (field >= 0 && column.Value.Kind == VariantShreddingKind.Object)
                {
                    (shapes ??= [])[field] = new VariantShapes(column.Value);
                }
            }

            return shapes;
        }

        /// <summary>
        /// A slice sorted, counted into the synopsis and written, one slice at a time, on the sorter's thread for writes: the
        /// synopsis beside the write when a lane is free (<see cref="SliceWriter"/>).
        /// </summary>
        private ValueTask WriteSortedAsync(SortedSlice sorted, CancellationToken cancellationToken)
        {
            if (_declare)
            {
                // The first slice settled the order: its top-level columns are the synopsis columns, whose chunks the writer
                // places together after the rest, told before its first row, as what a stream's first slice judged.
                _declare = false;
                if (_stream is { } stream)
                {
                    Judged(stream, _streamed!);
                }

                _draft!.Writer.DeclareSynopsisColumns(SynopsisColumns(_sorter!.Order));
            }

            return _slices!.WriteAsync(sorted, sorted.Start, _sorter!.Order, _sorter.CountedPaths, cancellationToken);
        }

        // What counts and writes the object's slices, its synopsis beside its writes.
        private SliceWriter? _slices;

        // The tables of the object's synopsis as its slices are sorted.
        private SynopsisTables? _synopsis;

        internal ValueTask RollIfFullAsync(CancellationToken cancellationToken) =>
            WantsRoll ? RollAsync(cancellationToken) : ValueTask.CompletedTask;

        /// <summary>Seals the object being written and starts the next one.</summary>
        internal async ValueTask RollAsync(CancellationToken cancellationToken)
        {
            if (_draft is not { } draft)
            {
                return;
            }

            // The slice being written goes out first, and the stream's last: their writes read the draft, the sorter and the
            // synopsis.
            if (_sorter is { } writing)
            {
                long draining = Stopwatch.GetTimestamp();
                await writing.EndAsync(cancellationToken).ConfigureAwait(false);
                await writing.DrainAsync().ConfigureAwait(false);
                Timings?.AddDrain(Stopwatch.GetTimestamp() - draining);
            }

            // The object is this method's from here: whatever fails gives it up, its upload included when it streams, since
            // nothing else holds the draft any more.
            _draft = null;
            _lastKeyLength = -1;
            SynopsisTables? synopsis = _synopsis;
            _synopsis = null;
            _slices = null;
            _stream = null;
            WrittenObject? written;
            try
            {
                await using (draft.Writer.ConfigureAwait(false))
                {
                    try
                    {
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

                        long finishing = Stopwatch.GetTimestamp();
                        if (synopsis is not null)
                        {
                            draft.Writer.DeclareSynopsisTables(await synopsis.FinishAsync(cancellationToken).ConfigureAwait(false));
                        }

                        long closing = Stopwatch.GetTimestamp();
                        Timings?.AddTables(closing - finishing);
                        await draft.Writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
                        Timings?.AddClose(Stopwatch.GetTimestamp() - closing);
                    }
                    finally
                    {
                        synopsis?.Dispose();
                    }
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

            // A slice being written reads the draft and the synopsis: its write ends, whatever it ends with, before they go,
            // waited for on no lane, which the slice's write may need.
            if (_sorter is { } sorter)
            {
                _sorter = null;
                await sorter.DisposeAsync().ConfigureAwait(false);
            }

            _draft = null;
            _synopsis?.Dispose();
            _synopsis = null;
            _slices = null;
            _stream = null;
            await draft.Writer.DisposeAsync().ConfigureAwait(false);
            draft.Sink.Discard();
        }
    }
}
