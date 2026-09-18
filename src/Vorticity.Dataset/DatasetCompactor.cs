// The compaction itself - docs/13-dataset.md §5.3: "a k-way merge of the inputs on the clustering
// key when there is one (the row encoding compares by memcmp, 06), a concatenation otherwise, and
// the writer builds the embedded indexes of the outputs once".
//
// THE MERGE IS `KeyOrderedMerge`, the one a key-ordered scan reads with (§6.6): runs of rows rather
// than rows, compared by the row encoding, an input opened only once it could hold the next row. The
// inputs are offered by their leaf keys, which on the clustering key are their exact minima (§4.1),
// so the key-disjoint objects of the destination level are read one after another rather than held
// open together. What stays here is what only a WRITER needs of a run: its first and last keys, so
// that a roll never splits a key across two outputs.
//
// READING AN INPUT IN KEY ORDER IS `InKeyOrder`, which is §5.3's own sentence: "the compaction
// reads each input in key order through that run, the permuted read InKeyOrder already performs".
// Level 0's objects have the mandatory run of §6.1; an object of a level above is stated sorted and
// `InKeyOrder` takes the cheaper source by itself.
//
// TWO REFUSALS, BOTH NAMED RATHER THAN WORKED AROUND. A composite clustering key has no permuted
// read yet -- `InKeyOrder` drives one column, and a composite key source exists only for cursors
// (12 §4.6) -- so a compaction of such a dataset is refused with what would fix it. And a key
// column holding nulls is refused because `InKeyOrder` delivers no row whose key is null (12 §6):
// a compaction that used it would silently drop those rows. The row count is checked against the
// inputs' at the end regardless, because a silent row loss is the one failure a rewrite must never
// have.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.Scan;
using Vorticity.Writing;

namespace Vorticity.Dataset;

/// <summary>What one compaction did (§5.3).</summary>
/// <param name="Version">The version it created, or the one it found when it was abandoned.</param>
/// <param name="FromLevel">The level it read.</param>
/// <param name="ToLevel">The level it wrote.</param>
/// <param name="Trigger">Why it ran.</param>
/// <param name="Style">Whether it merged or concatenated.</param>
/// <param name="ObjectsIn">The objects it read.</param>
/// <param name="ObjectsOut">The objects it wrote.</param>
/// <param name="Rows">The rows it rewrote.</param>
/// <param name="BytesIn">The bytes it read — §5.4's write amplification, measured.</param>
/// <param name="BytesOut">The bytes it wrote.</param>
/// <param name="Outcome">What the commit made of it (§8.2).</param>
public sealed record CompactionResult(
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

/// <summary>Runs one compaction (§5.3).</summary>
public static class DatasetCompactor
{
    /// <summary>Reads the job's inputs and replaces them by the objects it writes.</summary>
    /// <param name="dataset">The dataset, which moves to the version the commit creates.</param>
    /// <param name="job">What to compact, as <see cref="CompactionPolicy"/> planned it.</param>
    /// <param name="cancellationToken">Cancels the reads, the writes and the commit.</param>
    /// <returns>What it did.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The job has no input.</exception>
    /// <exception cref="VortexUnsupportedException">
    /// The clustering key is composite, or an input's key column holds nulls: neither has a
    /// permuted read that would keep every row (12 §6).
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

    /// <summary>The two things a merge needs of the dataset before it reads a byte.</summary>
    private static void Check(ClusteringKey? key, CompactionJob job)
    {
        if (key is null)
        {
            throw new VortexUnsupportedException(
                "clustering key",
                "compaction",
                "A leveled compaction merges on the clustering key, and this dataset declares none " +
                "(13 §4.1). Compact it tiered, which concatenates.");
        }

        if (key.IsComposite)
        {
            throw new VortexUnsupportedException(
                "clustering key",
                "compaction",
                "A composite clustering key has no permuted read: `InKeyOrder` drives one column, " +
                "and the composite key source serves cursors only (12 §4.6). A merge that ordered " +
                "on the first column alone would write objects whose ranges overlap below it, " +
                "which is the invariant of 13 §5.2.");
        }

        string path = key.Paths[0];
        foreach (CompactionInput input in job.Inputs)
        {
            if (input.Entry.Summaries.TryGet(path, out ColumnSummary column)
                && column.HasNullCount
                && column.NullCount > 0)
            {
                throw new VortexUnsupportedException(
                    input.Entry.Key,
                    "compaction",
                    $"'{path}' holds {column.NullCount} null key(s) in this object, and a key-ordered " +
                    "read delivers no row whose key is null (12 §6): the merge would drop them. " +
                    "Declare the clustering key on a non-nullable column.");
            }
        }
    }

    /// <summary>
    /// §6.4's fragment compaction: each object's fragments bundled into one, index bytes only, and
    /// swapped for it in one commit.
    /// </summary>
    /// <remarks>
    /// THE SWAP IS BY CONTENT, which is what makes it safe under a rebase (§8.2). An indexer that
    /// attached a fragment meanwhile keeps it: only the fragments read here are dropped. A second
    /// compaction of the same fragments finds them gone and the same bundle there. A compaction of
    /// the object's DATA meanwhile dropped every fragment with it, and the bundle is dropped too.
    /// </remarks>
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

        // What became of the bundles, the one operation per object that writes something.
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

    /// <summary>§5.3's concatenation: each input's rows, in its own order, one after another.</summary>
    private static async ValueTask<long> ConcatenateAsync(
        VortexDataset dataset, CompactionJob job, ObjectStream outputs, CancellationToken cancellationToken)
    {
        long rows = 0;
        foreach (CompactionInput input in job.Inputs)
        {
            ObjectLease lease = await dataset.RentAsync(input.Entry, cancellationToken).ConfigureAwait(false);
            await using (lease.ConfigureAwait(false))
            {
                await foreach (RecordBatch batch in lease.File.Scan()
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

    /// <summary>§5.3's k-way merge, one run of rows at a time.</summary>
    private static async ValueTask<long> MergeAsync(
        VortexDataset dataset,
        CompactionJob job,
        ClusteringKey key,
        ObjectStream outputs,
        CancellationToken cancellationToken)
    {
        // Offered by leaf key, which is the encoded minimum and then the uid — the order of an exact
        // lower bound, so an input is opened only once it could hold the next row — and ranked by
        // the job's own order, which is who takes a tie among inputs holding the same key.
        List<(ReadOnlyMemory<byte> Key, MergeObject Object)> inputs = new(job.Inputs.Count);
        for (int i = 0; i < job.Inputs.Count; i++)
        {
            CompactionInput input = job.Inputs[i];
            inputs.Add((input.Key, new MergeObject(input.Entry, VortexDataset.OrderOf(input.Key), i)));
        }

        inputs.Sort(static (left, right) => left.Key.Span.SequenceCompareTo(right.Key.Span));

        string path = key.Paths[0];
        KeyOrderedMerge merge = new KeyOrderedMerge(
            dataset,
            inputs.Select(static held => held.Object).ToAsyncEnumerable(),
            key.Paths,
            descending: false,
            rankedTies: false,
            file => file.Scan().InKeyOrder(path),
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
                    // would have overlapping ranges, and §5.2 asks a level for disjoint ones.
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

    /// <summary>The objects a compaction writes, rolled at the destination level's size (§5.3).</summary>
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

        /// <summary>Their bytes.</summary>
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
        /// <param name="key">The next row's encoded key.</param>
        /// <returns>Whether they are the same key.</returns>
        internal bool HoldsKey(ReadOnlySpan<byte> key) =>
            _lastKeyLength >= 0 && key.SequenceEqual(_lastKey.AsSpan(0, _lastKeyLength));

        /// <summary>Records the last key of a run, so the next roll does not split it.</summary>
        /// <param name="key">The run's last encoded key.</param>
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

        /// <summary>Seals the current object when it has reached its size; for a concatenation.</summary>
        /// <param name="cancellationToken">Cancels the put.</param>
        internal ValueTask RollIfFullAsync(CancellationToken cancellationToken) =>
            WantsRoll ? RollAsync(cancellationToken) : ValueTask.CompletedTask;

        /// <summary>Seals the object being written and starts the next one.</summary>
        /// <param name="cancellationToken">Cancels the put.</param>
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
        /// <param name="cancellationToken">Cancels the put.</param>
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
