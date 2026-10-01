using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Layouts;
using Vorticity.Scanning;
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

    /// <summary>The objects it wrote.</summary>
    public long ObjectsOut { get; init; }

    /// <summary>The rows it rewrote.</summary>
    public long Rows { get; init; }

    /// <summary>The bytes it read, the denominator of write amplification.</summary>
    public long BytesIn { get; init; }

    /// <summary>The bytes it wrote.</summary>
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
    /// <exception cref="InvalidOperationException">The outputs do not hold the inputs' rows.</exception>
    public static async ValueTask<CompactionResult> RunAsync(
        VortexDataset dataset, CompactionJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(job);
        if (job.Inputs.Count == 0)
        {
            throw new ArgumentException("A compaction reads at least one object.", nameof(job));
        }

        if (job.Trigger == CompactionTrigger.Fragments)
        {
            return await BundleAsync(dataset, job, cancellationToken).ConfigureAwait(false);
        }

        bool merge = job.Style == CompactionStyle.Leveled;
        ClusteringKey? key = dataset.Key;
        if (merge)
        {
            Check(key, job);
        }

        // Every input is read as this schema and every output written in it: a compaction is also
        // how objects of an earlier schema come to hold the current one.
        DatasetSchema schema = dataset.Snapshot.Schema;
        ObjectStream outputs = new ObjectStream(dataset, schema, job.TargetBytes, job.FirstRow, inKeyOrder: merge);
        long rows;
        try
        {
            rows = merge
                ? await MergeAsync(dataset, schema, job, key!, outputs, cancellationToken).ConfigureAwait(false)
                : await ConcatenateAsync(dataset, schema, job, outputs, cancellationToken).ConfigureAwait(false);
            await outputs.FinishAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await outputs.AbandonAsync().ConfigureAwait(false);
            throw;
        }

        if (rows != job.Rows)
        {
            throw new InvalidOperationException(
                $"The compaction read {job.Rows} row(s) from {job.Inputs.Count} object(s) and wrote " +
                $"{rows}: a rewrite that loses rows is the one failure it must not have.");
        }

        List<(int Level, ReadOnlyMemory<byte> Key)> consumed = [];
        foreach (CompactionInput input in job.Inputs)
        {
            consumed.Add((input.Level, input.Key));
        }

        List<(int Level, ReadOnlyMemory<byte> Key, ObjectEntry Entry)> produced = [];
        foreach (WrittenObject written in outputs.Written)
        {
            produced.Add((job.ToLevel, written.Key, written.Entry));
        }

        // What each input was read as: a delete that marked rows in one meanwhile leaves it under its
        // key, and outputs worked out from the rows before the delete would bring them back.
        List<ObjectEntry> read = new List<ObjectEntry>(job.Inputs.Count);
        foreach (CompactionInput input in job.Inputs)
        {
            read.Add(input.Entry);
        }

        DatasetOperation.ReplaceObjects replacement =
            new DatasetOperation.ReplaceObjects(consumed, produced) { Expected = read };
        CommitResult commit = await dataset
            .CommitAsync([replacement], cancellationToken).ConfigureAwait(false);

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
            Outcome = commit.Outcomes.Count == 1 ? commit.Outcomes[0] : OperationOutcome.Applied,
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
        // key's run holds no tuple with a null, so a merge that went on would drop those rows.
        if (!key.IsComposite)
        {
            return;
        }

        foreach (CompactionInput input in job.Inputs)
        {
            foreach (string path in key.Paths)
            {
                if (input.Entry.Summaries.TryGet(path, out ColumnSummary column)
                    && column.HasNullCount
                    && column.NullCount > 0)
                {
                    throw new VortexUnsupportedException(
                        input.Entry.Key,
                        ComponentKind.Feature,
                        $"'{path}' holds {column.NullCount} null(s) in this object, and a composite key's " +
                        "run holds no tuple with a null: the merge would drop those rows. Declare " +
                        "the composite clustering key on non-nullable columns.");
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

        CommitResult commit = await dataset.CommitAsync(operations, cancellationToken).ConfigureAwait(false);

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
        ObjectRead read = columns.Read(null, FieldMask.All);
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
        ScanBuilder? nulls = paths.Count == 1 && LiveRows.MayBeNull(file.DType, paths[0])
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
        // Offered by leaf key, an exact lower bound, so an input opens only once it could hold the
        // next row; the job's own order breaks ties among inputs holding the same key.
        List<(ReadOnlyMemory<byte> Key, MergeObject Object)> inputs = new(job.Inputs.Count);
        for (int i = 0; i < job.Inputs.Count; i++)
        {
            CompactionInput input = job.Inputs[i];
            inputs.Add((input.Key, new MergeObject(input.Entry, VortexDataset.OrderOf(input.Key), i)));
        }

        inputs.Sort(static (left, right) => left.Key.Span.SequenceCompareTo(right.Key.Span));

        IReadOnlyList<string> paths = key.Paths;
        KeyOrderedMerge merge = new KeyOrderedMerge(
            dataset.Snapshot,
            inputs.Select(static held => held.Object).ToAsyncEnumerable(),
            key.Paths,
            descending: false,
            rankedTies: false,
            (lease, entry) => RowsOfAsync(lease, entry, schema, paths, cancellationToken),
            opened: null,
            cancellationToken);
        long rows = 0;
        await using (merge.ConfigureAwait(false))
        {
            while (await merge.MoveNextAsync().ConfigureAwait(false))
            {
                if (outputs.WantsRoll && !outputs.HoldsKey(merge.FirstKey))
                {
                    // Never split a key across two objects: two outputs sharing a boundary value
                    // would overlap, and a level wants key-disjoint objects.
                    await outputs.RollAsync(cancellationToken).ConfigureAwait(false);
                }

                outputs.Note(merge.LastKey);
                RecordBatch run = merge.Current;
                rows += run.RowCount;
                await outputs.WriteAsync(run, cancellationToken).ConfigureAwait(false);
            }
        }

        return rows;
    }

    /// <summary>The objects a compaction writes, rolled at the destination level's size.</summary>
    private sealed class ObjectStream
    {
        private readonly VortexDataset _dataset;
        private readonly DatasetSchema _schema;
        private readonly bool _inKeyOrder;
        private readonly long _target;
        private readonly List<WrittenObject> _written = [];
        private byte[] _lastKey = [];
        private int _lastKeyLength = -1;
        private ObjectDraft? _draft;
        private long _rows;
        private long _firstRow;

        internal ObjectStream(VortexDataset dataset, DatasetSchema schema, long target, long firstRow, bool inKeyOrder)
        {
            _dataset = dataset;
            _schema = schema;
            _inKeyOrder = inKeyOrder;
            _target = target;
            _firstRow = firstRow;
        }

        /// <summary>The objects it has sealed.</summary>
        internal IReadOnlyList<WrittenObject> Written => _written;

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
            _draft ??= _dataset.StartObjectUnder(_schema, _inKeyOrder);
            await _draft.Writer.WriteBatchAsync(batch, cancellationToken).ConfigureAwait(false);
            _rows += batch.RowCount;
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

            _draft = null;
            _lastKeyLength = -1;
            await using (draft.Writer.ConfigureAwait(false))
            {
                await draft.Writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
            }

            WrittenObject? written = await _dataset
                .SealAsync(draft, _rows, _firstRow, cancellationToken).ConfigureAwait(false);
            if (written is { } output)
            {
                _written.Add(output);
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
