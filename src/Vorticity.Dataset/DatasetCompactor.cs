using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Indexes;
using Vorticity.Layouts;
using Vorticity.RowEncoding;
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

        // A seal weighs the period it wrote, and asks in the same commit for the length that load
        // calls for, from past the newest instant: the periods already written keep their bounds.
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
    /// <paramref name="schema"/>'s columns: its own scan, each batch reshaped when it was written
    /// under another schema.
    /// </summary>
    private static IAsyncEnumerable<RecordBatch> RowsOfAsync(
        ObjectLease lease, ObjectEntry entry, DatasetSchema schema, IReadOnlyList<string>? paths, CancellationToken cancellationToken)
    {
        VortexFile file = lease.File;
        if (schema.ColumnsOf(file.DType, entry.Key) is not { } columns)
        {
            return LiveRowsAsync(file, file.ScanBuilder, entry, paths, cancellationToken);
        }

        // The clustering key keeps its columns under every schema, so a key-ordered read of an
        // earlier object names them as the dataset does.
        ObjectReadPlan read = columns.Read(null, FieldMask.All);
        return ObjectColumns.ReshapeAsync(
            LiveRowsAsync(file, () => file.ScanBuilder().ProjectMask(read.Shape.Source).WithEncodings(false, false), entry, paths, cancellationToken),
            read.Shape,
            null,
            0,
            compact: true,
            cancellationToken);
    }

    /// <summary>
    /// An input's rows through the scans <paramref name="scan"/> makes, in key order along
    /// <paramref name="paths"/> or in its own, without the rows it deleted: a compaction is also where
    /// a deletion vector ends, its outputs holding only the live rows.
    /// </summary>
    private static IAsyncEnumerable<RecordBatch> LiveRowsAsync(
        VortexFile file, Func<ScanBuilder> scan, ObjectEntry entry, IReadOnlyList<string>? paths, CancellationToken cancellationToken)
    {
        if (!entry.HasDeletions)
        {
            return (paths is null ? scan() : scan().InKeyOrder(paths)).ExecuteAsync();
        }

        if (paths is null)
        {
            return LiveRows.WithoutDeletedAsync(
                scan().WithCompaction(false).WithEncodings(false, false).ExecuteAsync(), entry.Deletions, compact: true, 0, cancellationToken);
        }

        ScanBuilder ordered = scan().InKeyOrder(paths);
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
            (lease, entry) => RowsOfAsync(lease, entry, schema, paths, cancellationToken),
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
        string instant = key.InstantPath;
        long target = dataset.Options.Periods?.SliceTargetRows ?? TimeSlicer.DefaultTargetRows;
        int[] candidates = SynopsisOrder.Candidates(schema.DType, key, dataset.Options.Identifiers);
        int[] variants = VariantColumns(schema.DType);
        int textBound = dataset.Options.Write.StringBoundBytes;
        Dictionary<(int, long), (SecondCounts Seconds, CardinalitySketch[] Sketches, PathCensus[] Paths)> tallies = [];
        List<Cut> cuts = [];
        KeyOrderedMerge merge = InputsMerged(dataset, schema, job, key, cancellationToken);
        await using (merge.ConfigureAwait(false))
        {
            while (await merge.MoveNextAsync().ConfigureAwait(false))
            {
                RecordBatch run = merge.Current;
                CutRun(run, instant, cut, job.ToLevel, cuts);
                int at = 0;
                foreach ((int end, int level, long period) in cuts)
                {
                    if (!tallies.TryGetValue((level, period), out var tally))
                    {
                        tally = (new SecondCounts(cut.Unit), [.. candidates.Select(static _ => new CardinalitySketch())], [.. variants.Select(_ => new PathCensus(textBound, target))]);
                        tallies[(level, period)] = tally;
                    }

                    RecordBatch part = at == 0 && end == run.RowCount ? run : run.Window(at, end - at);
                    ReadOnlySpan<long> instants = Instants(part, instant);
                    tally.Seconds.Add(instants);
                    SynopsisOrder.Count(part, candidates, tally.Sketches);
                    for (int v = 0; v < variants.Length; v++)
                    {
                        tally.Paths[v].Add(part, variants[v], instants);
                    }

                    at = end;
                }
            }
        }

        Dictionary<(int Level, long Period), ObjectPlan> plans = [];
        foreach (((int level, long period), (SecondCounts seconds, CardinalitySketch[] sketches, PathCensus[] paths)) in tallies)
        {
            (int, long)[] counted = new (int, long)[candidates.Length];
            for (int i = 0; i < candidates.Length; i++)
            {
                counted[i] = (candidates[i], sketches[i].Estimate());
            }

            // Each variant column shreds what its rows in the object pay for, in chunks of its own, and
            // lists every path and class they hold, shredded or not, with the slices that hold it.
            SliceTable table = new SliceTable(cut.Unit, TimeSlicer.Cut(seconds.Seconds, cut.Unit, target), SynopsisOrder.Choose(counted, seconds.Rows));
            ImmutableDictionary<string, VariantShredding> shredded = ImmutableDictionary<string, VariantShredding>.Empty;
            ImmutableDictionary<string, int> chunks = ImmutableDictionary<string, int>.Empty;
            ImmutableDictionary<string, VariantDirectory> directories = ImmutableDictionary<string, VariantDirectory>.Empty;
            for (int v = 0; v < variants.Length; v++)
            {
                string name = schema.DType.GetFieldName(variants[v]);
                VariantShredding? chosen = ShreddingChoice.Choose(paths[v], required: IdentifierKeys(dataset, schema.DType, variants[v]));
                if (chosen is not null)
                {
                    shredded = shredded.SetItem(name, chosen);
                    chunks = chunks.SetItem(name, ShreddingChoice.ChunkTargetBytes(paths[v]));
                }

                directories = directories.SetItem(name, VariantDirectory.From(paths[v], chosen, table.Slices));
            }

            plans[(level, period)] = new ObjectPlan(
                new SliceTable(table.Unit, table.Slices, table.Order, SynopsisPaths(schema.DType, variants, paths, shredded)),
                shredded,
                chunks,
                directories);
        }

        return plans;
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
    /// The keys of the variant column <paramref name="field"/> the dataset declares as identifiers, each
    /// with the class it is read in, which every object shreds wherever a row holds one; null for none.
    /// </summary>
    private static Dictionary<string, VariantClass>? IdentifierKeys(VortexDataset dataset, Vorticity.Types.DType schema, int field)
    {
        Dictionary<string, VariantClass>? keys = null;
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
    /// The change of period length the load of the period a seal wrote calls for, or null: its bytes
    /// and rows over its span of data time, its columns, the object cap, the erasure delay. A row's
    /// bytes of data bound its widest column's, which keeps the chunk constraint on the safe side; the
    /// object's indexes and synopsis tables belong to no column, and would make a small period's rows
    /// look wide.
    /// </summary>
    private static DatasetOperation.ChangePeriodLength? Reweigh(VortexDataset dataset, PeriodCut cut, WrittenObject written, long newest)
    {
        ObjectEntry period = written.Entry;
        if (dataset.Key is not { } key || !period.Summaries.TryGet(key.InstantPath, out ColumnSummary instant)
            || !instant.HasMin || instant.Min.Kind != Vorticity.Expressions.FilterLiteralKind.Signed || period.Rows == 0)
        {
            return null;
        }

        long start = cut.Calendar.Floor(instant.Min.SignedValue);
        double seconds = (cut.Calendar.Next(start) - start) / (double)TimeSlicer.Units(TimeSpan.FromSeconds(1), cut.Unit);
        PeriodLoad load = new PeriodLoad(
            period.Bytes / seconds,
            period.Rows / seconds,
            LeafColumns(dataset.Snapshot.Schema.DType),
            (double)(written.DataBytes > 0 ? written.DataBytes : period.Bytes) / period.Rows,
            dataset.Options.MaxObjectBytes,
            dataset.Snapshot.Header.ErasureDelay,
            Retention: null);
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
        ReadOnlySpan<long> instants = Instants(run, instant);
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

    /// <summary>
    /// The instants of a run, its key's leading column as stored, read where they lie: a window's
    /// values may start anywhere in a buffer, which a read of longs does not mind.
    /// </summary>
    private static ReadOnlySpan<long> Instants(RecordBatch run, string path)
    {
        CanonicalArena arena = run.Arena;
        int node = RowEncoder.Storage(arena, ColumnPath.NodeOf(run, path));
        if (arena.RecordRef(node).Kind != CanonicalKind.Primitive)
        {
            node = EncodedForms.Canonical(arena, node);
        }

        return MemoryMarshal.Cast<byte, long>(arena.RecordRef(node).BufferA.Span)[..run.RowCount];
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

        // The slices of the object being written, sorted as they fill; null without plans.
        private SortedSlices? _sorter;
        private SliceTable? _table;

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
                // An object of a dataset of periods is written as its plan says: its slices, and what its
                // variant columns shred.
                ObjectPlan? plan = Plans?[(_level, _period)];
                _draft = _dataset.StartObjectUnder(
                    _schema, _inKeyOrder, _rowBound, sliced: plan is not null, shredded: plan?.Shredded, variantChunks: plan?.VariantChunks,
                    directories: plan?.Directories, synopsis: plan is null ? null : SynopsisColumns(plan.Slices), byteBound: _byteBound);
                _draft.Partition = Partition;
                if (plan is not null)
                {
                    _table = plan.Slices;
                    int instant = _schema.DType.IndexOfField(InstantPath!);
                    _sorter = new SortedSlices(_table, instant, _schema.DType, Shapes(plan.Shredded));

                    // The synopsis's tables, counted from each slice as it is sorted, their chunks held by the writer.
                    _synopsis = new SynopsisTables(
                        _schema.DType, _table.Unit, SynopsisTables.Measures(_schema.DType, instant, _table.Order), _draft.Writer, _table.Paths, _table.Slices);
                    _slice = 0;
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

        /// <summary>The object's synopsis columns, by name: the top-level columns its slices are sorted by, shapes and paths aside.</summary>
        private List<string> SynopsisColumns(SliceTable table)
        {
            List<string> columns = [];
            foreach (int entry in table.Order)
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

        private async ValueTask WriteSortedAsync(RecordBatch sorted, CancellationToken cancellationToken)
        {
            if (_synopsis is { } synopsis)
            {
                await synopsis.AddSliceAsync(
                    sorted, _table!.Slices[_slice].Start, _sorter!.Order, _sorter.SortedCodes, _sorter.PathColumns, _sorter.CountedPaths, cancellationToken).ConfigureAwait(false);
            }

            _slice++;
            await _draft!.Writer.WriteBatchAsync(sorted, cancellationToken).ConfigureAwait(false);
        }

        // The tables of the object's synopsis as its slices are sorted, and the next slice's index.
        private SynopsisTables? _synopsis;
        private int _slice;

        internal ValueTask RollIfFullAsync(CancellationToken cancellationToken) =>
            WantsRoll ? RollAsync(cancellationToken) : ValueTask.CompletedTask;

        /// <summary>Seals the object being written and starts the next one.</summary>
        internal async ValueTask RollAsync(CancellationToken cancellationToken)
        {
            if (_draft is not { } draft)
            {
                return;
            }

            _draft = null;
            _lastKeyLength = -1;
            await using (draft.Writer.ConfigureAwait(false))
            {
                if (_sorter is { } sorter)
                {
                    // The first pass counted these rows: every slice is written, or the passes disagree.
                    _sorter = null;
                    bool complete = sorter.Complete;
                    _table = sorter.Table;
                    sorter.Dispose();
                    if (!complete)
                    {
                        throw new DatasetIntegrityException(
                            $"The object of level {_level} wrote {_rows} rows where its slices hold {_table!.Rows}: the two passes of the job read different rows.");
                    }

                    draft.Writer.DeclareSlices(InstantPath!, _table!);
                }

                SynopsisTables? synopsis = _synopsis;
                _synopsis = null;
                try
                {
                    if (synopsis is not null)
                    {
                        draft.Writer.DeclareSynopsisTables(await synopsis.FinishAsync(cancellationToken).ConfigureAwait(false));
                    }

                    await draft.Writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    synopsis?.Dispose();
                }
            }

            WrittenObject? written = await _dataset
                .SealAsync(draft, _rows, _firstRow, cancellationToken).ConfigureAwait(false);
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

            _draft = null;
            _sorter?.Dispose();
            _sorter = null;
            _synopsis?.Dispose();
            _synopsis = null;
            await draft.Writer.DisposeAsync().ConfigureAwait(false);
            draft.Sink.Discard();
        }
    }
}
