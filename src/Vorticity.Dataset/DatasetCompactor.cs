using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.Scanning;
using Vorticity.Writing;

namespace Vorticity.Dataset;

/// <summary>What one compaction did.</summary>
/// <param name="Version">The version it created, or the one it found when it was abandoned.</param>
/// <param name="FromLevel">The level it read.</param>
/// <param name="ToLevel">The level it wrote.</param>
/// <param name="Trigger">Why it ran.</param>
/// <param name="Style">Whether it merged or concatenated.</param>
/// <param name="ObjectsIn">The objects it read.</param>
/// <param name="ObjectsOut">The objects it wrote.</param>
/// <param name="Rows">The rows it rewrote.</param>
/// <param name="BytesIn">The bytes it read, the denominator of write amplification.</param>
/// <param name="BytesOut">The bytes it wrote.</param>
/// <param name="Outcome">What the commit made of it.</param>
internal sealed record CompactionResult(
    ulong Version,
    int FromLevel,
    int ToLevel,
    CompactionTrigger Trigger,
    CompactionStyle Style,
    long ObjectsIn,
    long ObjectsOut,
    long Rows,
    long BytesIn,
    long BytesOut,
    OperationOutcome Outcome);

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
            throw new ArgumentException("A compaction reads at least one object (13 §5.3).", nameof(job));
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

        ObjectStream outputs = new ObjectStream(dataset, job.TargetBytes, job.FirstRow);
        long rows;
        try
        {
            rows = merge
                ? await MergeAsync(dataset, job, key!, outputs, cancellationToken).ConfigureAwait(false)
                : await ConcatenateAsync(dataset, job, outputs, cancellationToken).ConfigureAwait(false);
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
                $"{rows}: a rewrite that loses rows is the one failure it must not have (13 §5.3).");
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

        DatasetOperation.ReplaceObjects replacement =
            new DatasetOperation.ReplaceObjects(consumed, produced);
        CommitResult commit = await dataset
            .CommitAsync([replacement], cancellationToken).ConfigureAwait(false);

        return new CompactionResult(
            commit.Version,
            job.FromLevel,
            job.ToLevel,
            job.Trigger,
            job.Style,
            job.Inputs.Count,
            outputs.Written.Count,
            rows,
            job.Bytes,
            outputs.Bytes,
            commit.Outcomes.Count == 1 ? commit.Outcomes[0] : OperationOutcome.Applied);
    }

    private static void Check(ClusteringKey? key, CompactionJob job)
    {
        if (key is null)
        {
            throw new VortexUnsupportedException(
                "clustering key",
                ComponentKind.Feature,
                "A leveled compaction merges on the clustering key, and this dataset declares none " +
                "(13 §4.1). Compact it tiered, which concatenates.");
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
                        "run holds no tuple with a null (12 §4.6): the merge would drop those rows. Declare " +
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

        return new CompactionResult(
            commit.Version,
            job.FromLevel,
            job.ToLevel,
            job.Trigger,
            job.Style,
            job.Inputs.Count,
            job.Inputs.Count,
            0,
            bytesIn,
            bytesOut,
            outcome);
    }

    /// <summary>Each input's rows, in its own order, one after another.</summary>
    private static async ValueTask<long> ConcatenateAsync(
        VortexDataset dataset, CompactionJob job, ObjectStream outputs, CancellationToken cancellationToken)
    {
        long rows = 0;
        foreach (CompactionInput input in job.Inputs)
        {
            ObjectLease lease = await dataset.RentAsync(input.Entry, cancellationToken).ConfigureAwait(false);
            await using (lease.ConfigureAwait(false))
            {
                await foreach (RecordBatch batch in lease.File.ScanBuilder()
                    .ExecuteAsync().WithCancellation(cancellationToken).ConfigureAwait(false))
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
            file => file.ScanBuilder().InKeyOrder(paths),
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
        private readonly long _target;
        private readonly List<WrittenObject> _written = [];
        private byte[] _lastKey = [];
        private int _lastKeyLength = -1;
        private ObjectDraft? _draft;
        private long _rows;
        private long _firstRow;

        internal ObjectStream(VortexDataset dataset, long target, long firstRow)
        {
            _dataset = dataset;
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
            _draft ??= _dataset.StartObject();
            await _draft.Writer.WriteAsync(batch, cancellationToken).ConfigureAwait(false);
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
