using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Indexes;
using Vorticity.Layouts;
using Vorticity.RowEncoding;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Types.Variants;

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

        bool merge = job.Style is CompactionStyle.Leveled;
        ClusteringKey? key = dataset.Key;
        if (merge)
        {
            Check(key, job);
        }

        // Every input is read as this schema and every output written in it: a compaction is also
        // how objects of an earlier schema come to hold the current one.
        DatasetSchema schema = dataset.Snapshot.Schema;

        // A job of a dataset with a key whose objects aim at a mebibyte or more writes them in slices bounded by their
        // keys, which their tables of contents name: what a lookup of an identifier and a filter on the key's first
        // column read without an open of the object's footer. Its first column must be a top-level one. Appends write
        // neither slices nor shredded variants.
        string? keySliced = merge && job.TargetBytes >= dataset.Options.ContentsObjectBytes
            && !key!.InstantPath.Contains('.') ? key.InstantPath : null;

        // Such a job over a schema with a variant column counts, in a first pass, the paths of each clustering value's
        // rows, and its objects of a value shred what those rows pay for; each object then lists its paths and the slices
        // that hold them, counted as its rows are written.
        IReadOnlyList<ValueShredding>? shreddings = null;
        if (keySliced is not null && VariantColumns(schema.DType).Length > 0)
        {
            using Activity? planning = DatasetTelemetry.StartCompactionPass("plan");
            shreddings = await PlanValuesAsync(dataset, schema, job, key!, cancellationToken).ConfigureAwait(false);
        }

        // A leveled exit of level 0 keeps the rows that came late apart, in an object of level 0, while they weigh little
        // against the objects of its destination they fall among (LateRows): it reads its sources alone, and those
        // objects stand. Past that weight, it runs again as the merge with them it was planned as.
        LateRows? late = merge ? LateRows.Of(job, key!) : null;
        CompactionJob ran = late is null ? job : LateRows.Sources(job);

        // Each object it streams is announced to the store as at most what the job reads, times what a job
        // writes a byte at worst; one that runs past it starts the job again under a bound twice as large.
        ObjectStream outputs;
        ObjectStream? lateOutputs = null;
        long rows;
        Provenance? provenance = null;
        Activity? writing = DatasetTelemetry.StartCompactionPass("write");
        try
        {
            for (int doublings = 0; ; doublings++)
            {
                lateOutputs = late is null ? null : new ObjectStream(
                    dataset, schema, ran.Bytes, ran.FirstRow, inKeyOrder: true, rowBound: ran.Rows,
                    0, ObjectBound.Of(ran.Bytes, doublings, dataset.Options.BoundOutPerIn));
                outputs = new ObjectStream(
                    dataset, schema, ran.TargetBytes, ran.FirstRow, inKeyOrder: merge, rowBound: ran.Rows,
                    ran.ToLevel, ObjectBound.Of(ran.Bytes, doublings, dataset.Options.BoundOutPerIn))
                {
                    KeySliced = keySliced,
                    Blocked = keySliced is null ? null : key!.LeadingType,
                    BlockValues = keySliced is null ? null : schema.BlockCut,
                    SliceTarget = dataset.Options.SliceTargetRows,
                    Shreddings = shreddings,
                    Values = schema.Cut,
                };
                try
                {
                    provenance = new Provenance();
                    rows = merge
                        ? await MergeAsync(dataset, schema, ran, key!, outputs, provenance, late, lateOutputs, cancellationToken).ConfigureAwait(false)
                        : await ConcatenateAsync(dataset, schema, ran, outputs, provenance, cancellationToken).ConfigureAwait(false);
                    await outputs.FinishAsync(cancellationToken).ConfigureAwait(false);
                    if (lateOutputs is not null)
                    {
                        await lateOutputs.FinishAsync(cancellationToken).ConfigureAwait(false);
                    }

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
        finally
        {
            writing?.Dispose();
        }

        if (rows != ran.Rows)
        {
            throw new DatasetIntegrityException(
                $"The compaction read {ran.Rows} row(s) from {ran.Inputs.Count} object(s) and wrote " +
                $"{rows}: a rewrite that loses rows is the one failure it must not have.");
        }

        List<(int Level, ReadOnlyMemory<byte> Key)> consumed = [];
        foreach (CompactionInput input in ran.Inputs)
        {
            consumed.Add((input.Level, input.Key));
        }

        // The late rows' objects go back to level 0, the others to the destination, past every object there they met.
        List<(int Level, ReadOnlyMemory<byte> Key, ObjectEntry Entry)> produced = [];
        List<CompactionInput> filled = [];
        for (int i = 0; i < (lateOutputs?.Written.Count ?? 0); i++)
        {
            produced.Add((lateOutputs!.Levels[i], lateOutputs.Written[i].Key, lateOutputs.Written[i].Entry));
        }

        for (int i = 0; i < outputs.Written.Count; i++)
        {
            WrittenObject written = outputs.Written[i];
            produced.Add((outputs.Levels[i], written.Key, written.Entry));
            filled.Add(new CompactionInput(outputs.Levels[i], written.Key, written.Entry));
        }

        // What each input was read as: a delete that marked rows in one meanwhile leaves it under its
        // key, and outputs worked out from the rows before the delete would bring them back.
        List<ObjectEntry> read = new List<ObjectEntry>(ran.Inputs.Count);
        foreach (CompactionInput input in ran.Inputs)
        {
            read.Add(input.Entry);
        }

        DatasetOperation.ReplaceObjects replacement = new DatasetOperation.ReplaceObjects(consumed, produced)
        {
            Expected = read,
            Pointer = job.Stop.IsEmpty ? null : (job.FromLevel, job.Stop),

            // Apart, the outputs take over the part of the destination they fill alone, which no object it holds may meet.
            Range = late is null ? job.Range : filled.Count == 0 ? null : LevelRange.Of(job.ToLevel, filled, key!),
        };

        CommitResult commit = await dataset
            .CommitAsync([replacement], CommitKind.Compaction, cancellationToken).ConfigureAwait(false);

        // An object written from inputs none of which a delete marked, each of which sent it every row, holds their rows,
        // all and no other: the partials the questions learned of them, summed, are its own (LearnedPartials).
        OperationOutcome outcome = commit.Outcomes.Count > 0 ? commit.Outcomes[0] : OperationOutcome.Applied;
        if (outcome == OperationOutcome.Applied && !ran.Inputs.Any(static input => input.Entry.HasDeletions))
        {
            for (int o = 0; o < outputs.Written.Count; o++)
            {
                if (provenance!.SoleInputsOf(o, ran.Inputs) is { Length: > 0 and <= LearnedPartials.MaxInputs } inputs)
                {
                    dataset.Partials.Inherit(outputs.Written[o].Entry.Uid, inputs, dataset.Session.Options.SynopsisCache);
                }
            }
        }

        return new CompactionResult
        {
            Version = commit.Version,
            FromLevel = job.FromLevel,
            ToLevel = job.ToLevel,
            Trigger = job.Trigger,
            Style = job.Style,
            ObjectsIn = ran.Inputs.Count,
            ObjectsOut = outputs.Written.Count + (lateOutputs?.Written.Count ?? 0),
            Rows = rows,
            BytesIn = ran.Bytes,
            BytesOut = outputs.Bytes + (lateOutputs?.Bytes ?? 0),
            Outcome = commit.Outcomes.Count > 0 ? commit.Outcomes[0] : OperationOutcome.Applied,
        };
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
        ObjectLease lease, ObjectEntry entry, DatasetSchema schema, IReadOnlyList<string>? paths, CancellationToken cancellationToken, int[]? fields = null)
    {
        VortexFile file = lease.File;
        FieldMask projection = fields is null ? FieldMask.All : Layouts.FieldMasks.Of(fields);
        if (schema.ColumnsOf(file.DType, entry.Key) is not { } columns)
        {
            return LiveRowsAsync(file, fields is null ? file.ScanBuilder : () => file.ScanBuilder().ProjectMask(projection), entry, paths, cancellationToken);
        }

        // The clustering key keeps its columns under every schema, so a key-ordered read of an
        // earlier object names them as the dataset does.
        ObjectReadPlan read = columns.Read(null, projection);
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

    /// <summary>
    /// Which outputs each input's rows went to, as a job writes them, an input by its key: an output whose every input sent it
    /// all its rows holds theirs, all and no other, and inherits the partials learned of them.
    /// </summary>
    private sealed class Provenance
    {
        private readonly Dictionary<string, HashSet<int>> _outputs = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);

        /// <summary>Notes that rows of input <paramref name="input"/> go to output <paramref name="output"/>, by its place among the outputs.</summary>
        internal void Wrote(string input, int output)
        {
            if (!_outputs.TryGetValue(input, out HashSet<int>? outputs))
            {
                _outputs[input] = outputs = [];
            }

            outputs.Add(output);
        }

        /// <summary>The identities of the inputs whose rows output <paramref name="output"/> holds, when each sent it every row; null when one sent rows elsewhere too.</summary>
        internal UInt128[]? SoleInputsOf(int output, IReadOnlyList<CompactionInput> inputs)
        {
            List<UInt128> sole = [];
            foreach (CompactionInput input in inputs)
            {
                if (_outputs.TryGetValue(input.Entry.Key, out HashSet<int>? outputs) && outputs.Contains(output))
                {
                    if (outputs.Count > 1)
                    {
                        return null;
                    }

                    sole.Add(input.Entry.Uid);
                }
            }

            return [.. sole];
        }
    }

    /// <summary>Each input's rows, in its own order, one after another.</summary>
    private static async ValueTask<long> ConcatenateAsync(
        VortexDataset dataset, DatasetSchema schema, CompactionJob job, ObjectStream outputs, Provenance provenance, CancellationToken cancellationToken)
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
                    provenance.Wrote(input.Entry.Key, outputs.Written.Count);
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
        Provenance provenance,
        LateRows? late,
        ObjectStream? lateOutputs,
        CancellationToken cancellationToken)
    {
        KeyOrderedMerge merge = InputsMerged(dataset, schema, job, key, cancellationToken);
        ValueCut? values = schema.Cut;
        long rows = 0;
        await using (merge.ConfigureAwait(false))
        {
            while (await merge.MoveNextAsync().ConfigureAwait(false))
            {
                RecordBatch run = merge.Current;

                // The rows that came late lead the merge, keys ascending: those of the run go to level 0, kept apart.
                // An object a clustering value, so that each lies within the rows of its value, which a merge of neighbours
                // there takes in with them.
                int start = late?.End(merge, 0, run.RowCount) ?? 0;
                if (start > 0)
                {
                    late!.Add(start);
                    provenance.Wrote(merge.CurrentObject, LateRows.Output);
                    for (int at = 0, end; at < start; at = end)
                    {
                        end = values is null ? start : values.EndOfValue(merge, at, start);
                        if (values is not null && lateOutputs!.HoldsOtherValue(values, merge.KeyAt(at)))
                        {
                            await lateOutputs.RollAsync(cancellationToken).ConfigureAwait(false);
                            late.Opens(lateOutputs.Written.Count + 1);
                        }

                        lateOutputs!.Note(merge.KeyAt(end - 1));
                        await lateOutputs.WriteAsync(at == 0 && end == run.RowCount ? run : run.Window(at, end - at), cancellationToken).ConfigureAwait(false);
                    }
                }

                // An object holds the rows of one clustering value: a run of several is written in parts, one a value,
                // and a part of another value than the object's seals it. A key has one value, so no part splits a key.
                for (int at = start, end; at < run.RowCount; at = end)
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
                    outputs.Block(merge, at, end);
                    outputs.Note(merge.KeyAt(end - 1));
                    provenance.Wrote(merge.CurrentObject, outputs.Written.Count);
                    await outputs.WriteAsync(at == 0 && end == run.RowCount ? run : run.Window(at, end - at), cancellationToken).ConfigureAwait(false);
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
            (lease, entry) => RowsOfAsync(lease, entry, schema, paths, cancellationToken, fields),
            opened: null,
            cancellationToken);
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
        int[]? counted = Counted(schema, key);
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
    /// The fields of <paramref name="schema"/> a first pass counts, ascending: the clustering key's and the variant columns,
    /// what it reads of its inputs; null, every field, for a key on a nested column.
    /// </summary>
    private static int[]? Counted(DatasetSchema schema, ClusteringKey key)
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
                // An object in slices bounded by keys shreds what its clustering value's rows pay for, and counts its
                // paths as its rows come.
                ValueShredding? shredding = Shreddings is { } shreddings ? shreddings[Math.Clamp(_value, 0, shreddings.Count - 1)] : null;
                _draft = _dataset.StartObjectUnder(
                    _schema, _inKeyOrder, _rowBound, sliced: KeySliced is not null, shredded: shredding?.Shredded, variantChunks: shredding?.VariantChunks,
                    byteBound: _byteBound, blocked: Blocked is not null);
                _census = shredding is null ? null : [.. VariantColumns(_schema.DType).Select(_ => new PathCensus(_dataset.Options.Write.StringBoundBytes, SliceTarget))];
            }

            // The blocks the rows before these and these themselves closed, declared before their rows are written: the
            // statistics of each column over them cut where they end.
            for (; _declaredBlocks < _blockRows.Count; _declaredBlocks++)
            {
                _declaredEnd += _blockRows[_declaredBlocks];
                _draft.Writer.EndBlock(_declaredEnd);
            }

            _rows += batch.RowCount;
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

        /// <summary>
        /// The type of the key's first column, whose runs of one value cut the blocks of an object in slices bounded by keys
        /// (<see cref="BlockTable"/>); null for objects written without them.
        /// </summary>
        internal DType? Blocked { get; init; }

        /// <summary>
        /// Where a block also ends, by the declared block value of the key's second column
        /// (<see cref="ClusteringOptions.BlockValue"/>); null for none.
        /// </summary>
        internal ValueCut? BlockValues { get; init; }

        // The blocks of the object being written: each closed block's rows and first and last keys, then the rows of the one
        // being written, its first key and the key of its last row.
        private readonly List<long> _blockRows = [];
        private readonly List<byte[]> _blockLows = [];
        private readonly List<byte[]> _blockHighs = [];
        private long _blockFilled;
        private byte[] _blockLow = [];
        private byte[] _blockHigh = [];
        private int _blockHighLength;

        // The blocks of the object being written already declared to its writer, and the rows they end at.
        private int _declaredBlocks;
        private long _declaredEnd;

        /// <summary>
        /// Notes rows <paramref name="from"/> to <paramref name="to"/> of the merge's run, which the object takes next in the
        /// key's order: a block ends at the first change of the key's first column, or of the declared block value of its
        /// second (<see cref="BlockValues"/>), once it holds <see cref="BlockTable.LeastRows"/> rows, and at
        /// <see cref="BlockTable.MostRows"/> rows. The keys of a run follow one another, and so the pairs of values, so a
        /// bisection finds each change.
        /// </summary>
        internal void Block(KeyOrderedMerge merge, int from, int to)
        {
            if (Blocked is not { } column)
            {
                return;
            }

            for (int at = from; at < to;)
            {
                if (_blockFilled == 0)
                {
                    _blockLow = merge.KeyAt(at).ToArray();
                }

                // The rows up to the least a block holds join it whatever their values; past them, those up to the first
                // change of the first column, up to the most a block holds.
                int limit = (int)Math.Min(to, at + (BlockTable.MostRows - _blockFilled));
                int open = (int)Math.Clamp(BlockTable.LeastRows - _blockFilled, 0, limit - at);
                int end = limit;
                int checkFrom = at + open;
                if (checkFrom < limit)
                {
                    ReadOnlySpan<byte> before = checkFrom > at ? merge.KeyAt(checkFrom - 1) : _blockHigh.AsSpan(0, _blockHighLength);
                    end = FirstChange(merge, column, BlockValues, before, checkFrom, limit);
                }

                if (end > at)
                {
                    ReadOnlySpan<byte> last = merge.KeyAt(end - 1);
                    if (_blockHigh.Length < last.Length)
                    {
                        _blockHigh = new byte[Math.Max(last.Length, _blockHigh.Length * 2)];
                    }

                    last.CopyTo(_blockHigh);
                    _blockHighLength = last.Length;
                    _blockFilled += end - at;
                    at = end;
                }

                if (end < limit || _blockFilled == BlockTable.MostRows)
                {
                    CloseBlock();
                }
            }
        }

        /// <summary>
        /// The first row from <paramref name="from"/> to <paramref name="to"/> of the merge's run whose key's first column,
        /// or the block value of its second under <paramref name="values"/>, is not <paramref name="before"/>'s, or
        /// <paramref name="to"/>.
        /// </summary>
        private static int FirstChange(KeyOrderedMerge merge, DType column, ValueCut? values, ReadOnlySpan<byte> before, int from, int to)
        {
            if (SameRun(before, merge.KeyAt(to - 1), column, values))
            {
                return to;
            }

            if (!SameRun(before, merge.KeyAt(from), column, values))
            {
                return from;
            }

            // Row `low` holds the first column's value of `before`, row `high` another.
            int low = from;
            int high = to - 1;
            while (high - low > 1)
            {
                int middle = low + ((high - low) >> 1);
                if (SameRun(before, merge.KeyAt(middle), column, values))
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

        /// <summary>Whether two row-encoded keys hold one value of their first column, and one block value of their second under <paramref name="values"/>.</summary>
        private static bool SameRun(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, DType column, ValueCut? values) =>
            SameFirst(left, right, column) && (values is null || values.Same(left, right));

        /// <summary>Whether two row-encoded keys hold one value of their first column: a value's bytes end where they end.</summary>
        private static bool SameFirst(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, DType column)
        {
            int length = RowEncoder.FirstValueLength(left, column);
            return right.Length >= length && left[..length].SequenceEqual(right[..length]);
        }

        /// <summary>Closes the block being written at its last row.</summary>
        private void CloseBlock()
        {
            if (_blockFilled == 0)
            {
                return;
            }

            _blockRows.Add(_blockFilled);
            _blockLows.Add(_blockLow);
            _blockHighs.Add(_blockHigh.AsSpan(0, _blockHighLength).ToArray());
            _blockFilled = 0;
        }

        /// <summary>
        /// The blocks of the object being written, its last one closed; null when it was written without them, or when they
        /// do not cover its rows. The lists start over for the next object.
        /// </summary>
        private BlockTable? Blocks()
        {
            CloseBlock();
            long covered = 0;
            foreach (long rows in _blockRows)
            {
                covered += rows;
            }

            BlockTable? table = null;
            if (Blocked is { } column && _blockRows.Count > 0 && covered == _rows)
            {
                // The rows of the blocks whose first and last keys hold one value of the first column and one block value: the
                // keys follow one another, so every row between holds them too.
                long pure = 0;
                for (int b = 0; b < _blockRows.Count; b++)
                {
                    pure += SameRun(_blockLows[b], _blockHighs[b], column, BlockValues) ? _blockRows[b] : 0;
                }

                table = BlockTable.Of(_blockRows, _blockLows, _blockHighs, pure);
            }

            _declaredBlocks = 0;
            _declaredEnd = 0;
            _blockRows.Clear();
            _blockLows.Clear();
            _blockHighs.Clear();
            return table;
        }

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

        internal ValueTask RollIfFullAsync(CancellationToken cancellationToken) =>
            WantsRoll ? RollAsync(cancellationToken) : ValueTask.CompletedTask;

        /// <summary>Seals the object being written and starts the next one.</summary>
        internal async ValueTask RollAsync(CancellationToken cancellationToken)
        {
            if (_draft is not { } draft)
            {
                return;
            }

            // The object is this method's from here: whatever fails gives it up, its upload included when it streams, since
            // nothing else holds the draft any more. Its slices bounded by keys close at its last key.
            SliceTable? keySlices = KeySlices();
            BlockTable? blocks = Blocks();
            _draft = null;
            _lastKeyLength = -1;
            WrittenObject? written;
            try
            {
                await using (draft.Writer.ConfigureAwait(false))
                {
                    if (keySlices is not null)
                    {
                        draft.Writer.DeclareSlices(KeySliced!, keySlices);
                        if (blocks is not null)
                        {
                            draft.Writer.DeclareBlocks(KeySliced!, blocks);
                        }
                    }

                    if (_census is { } census)
                    {
                        _census = null;
                        DeclareDirectories(
                            draft.Writer, census, keySlices, Shreddings![Math.Clamp(_value, 0, Shreddings.Count - 1)].Shredded);
                    }

                    await draft.Writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
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

            _draft = null;
            await draft.Writer.DisposeAsync().ConfigureAwait(false);
            draft.Sink.Discard();
        }
    }
}
