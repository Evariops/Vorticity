// The compaction itself - docs/13-dataset.md §5.3: "a k-way merge of the inputs on the clustering
// key when there is one (the row encoding compares by memcmp, 06), a concatenation otherwise, and
// the writer builds the embedded indexes of the outputs once".
//
// THE MERGE EMITS RUNS, NOT ROWS. At every step one input holds the smallest key; the rows it may
// emit before another input takes over are the ones at or below the smallest key the others are
// holding. That is a contiguous window of its current batch, so the merge writes windows of
// batches rather than rows: k comparisons per window instead of per row, and the writer keeps
// receiving batches of a useful size. `RecordBatch.Window` is the seam this needs and the reason
// it was made public -- 13 §4.2 already named it as one of the two things the core did not expose.
//
// THE COMPARATOR IS THE ROW ENCODING, for one reason: it is the order the tree, the runs and the
// seeks already use (06's memcmp order), so a merge cannot disagree with the sources it merges. A
// per-dtype comparison written here would be a second order, and the first time the two differed
// the dataset would hold an object whose own run says it is sorted and whose rows are not.
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
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Columns;
using Vorticity.RowEncoding;
using Vorticity.Scan;
using Vorticity.Types;
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

        bool merge = job.Style == CompactionStyle.Leveled;
        ClusteringKey? key = dataset.Key;
        if (merge)
        {
            Check(key, job);
        }

        List<ObjectLease> leases = [];
        ObjectStream outputs = new ObjectStream(dataset, job.TargetBytes, job.FirstRow);
        long rows;
        try
        {
            foreach (CompactionInput input in job.Inputs)
            {
                leases.Add(await dataset.RentAsync(input.Entry.Key, cancellationToken).ConfigureAwait(false));
            }

            rows = merge
                ? await MergeAsync(leases, key!, outputs, cancellationToken).ConfigureAwait(false)
                : await ConcatenateAsync(leases, outputs, cancellationToken).ConfigureAwait(false);
            await outputs.FinishAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await outputs.AbandonAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            foreach (ObjectLease lease in leases)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
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

    /// <summary>§5.3's concatenation: each input's rows, in its own order, one after another.</summary>
    private static async ValueTask<long> ConcatenateAsync(
        List<ObjectLease> leases, ObjectStream outputs, CancellationToken cancellationToken)
    {
        long rows = 0;
        foreach (ObjectLease lease in leases)
        {
            await foreach (RecordBatch batch in lease.File.Scan()
                .ExecuteAsync().WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                await outputs.RollIfFullAsync(cancellationToken).ConfigureAwait(false);
                await outputs.WriteAsync(batch, cancellationToken).ConfigureAwait(false);
                rows += batch.RowCount;
            }
        }

        return rows;
    }

    /// <summary>§5.3's k-way merge, one run of rows at a time.</summary>
    private static async ValueTask<long> MergeAsync(
        List<ObjectLease> leases,
        ClusteringKey key,
        ObjectStream outputs,
        CancellationToken cancellationToken)
    {
        MergeInput?[] inputs = new MergeInput?[leases.Count];
        long rows = 0;
        try
        {
            for (int i = 0; i < inputs.Length; i++)
            {
                MergeInput input = new MergeInput(leases[i], key);
                inputs[i] = input;
                await input.StartAsync(cancellationToken).ConfigureAwait(false);
            }

            while (true)
            {
                int smallest = -1;
                for (int i = 0; i < inputs.Length; i++)
                {
                    if (inputs[i]!.IsLive
                        && (smallest < 0 || inputs[i]!.Key.SequenceCompareTo(inputs[smallest]!.Key) < 0))
                    {
                        smallest = i;
                    }
                }

                if (smallest < 0)
                {
                    return rows;
                }

                // The rows this input may emit before another one takes over: those at or below the
                // smallest key the others hold. Ties go to this input, which keeps the output
                // non-decreasing whichever way they fall.
                int limit = -1;
                for (int i = 0; i < inputs.Length; i++)
                {
                    if (i != smallest
                        && inputs[i]!.IsLive
                        && (limit < 0 || inputs[i]!.Key.SequenceCompareTo(inputs[limit]!.Key) < 0))
                    {
                        limit = i;
                    }
                }

                MergeInput chosen = inputs[smallest]!;
                if (outputs.WantsRoll && !outputs.HoldsKey(chosen.Key))
                {
                    // Never split a key across two objects: two outputs sharing a boundary value
                    // would have overlapping ranges, and §5.2 asks a level for disjoint ones.
                    await outputs.RollAsync(cancellationToken).ConfigureAwait(false);
                }

                int count = limit < 0 ? chosen.Remaining : chosen.RunUpTo(inputs[limit]!.Key);
                outputs.Note(chosen.KeyAt(chosen.Row + count - 1));
                rows += count;
                await chosen.EmitAsync(count, outputs, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (MergeInput? input in inputs)
            {
                if (input is not null)
                {
                    await input.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>One input of the merge: its batches in key order, and its keys row-encoded.</summary>
    private sealed class MergeInput : IAsyncDisposable
    {
        private readonly ObjectLease _lease;
        private readonly ClusteringKey _key;
        private readonly RowSortField[] _fields;
        private IAsyncEnumerator<RecordBatch>? _batches;
        private RecordBatch? _batch;
        private RowKeys? _keys;
        private int _row;

        internal MergeInput(ObjectLease lease, ClusteringKey key)
        {
            _lease = lease;
            _key = key;
            _fields = new RowSortField[key.Paths.Count];
            Array.Fill(_fields, RowSortField.Ascending);
        }

        /// <summary>Whether it still holds a row.</summary>
        internal bool IsLive => _batch is not null;

        /// <summary>Its current row within its current batch.</summary>
        internal int Row => _row;

        /// <summary>The rows left in its current batch.</summary>
        internal int Remaining => _batch is null ? 0 : _batch.RowCount - _row;

        /// <summary>Its current key, row-encoded (06).</summary>
        internal ReadOnlySpan<byte> Key => _keys!.Row(_row);

        /// <summary>The encoded key of one row of its current batch.</summary>
        /// <param name="row">The row, within the batch.</param>
        /// <returns>Its key.</returns>
        internal ReadOnlySpan<byte> KeyAt(int row) => _keys!.Row(row);

        internal ValueTask StartAsync(CancellationToken cancellationToken)
        {
            _batches = _lease.File.Scan()
                .InKeyOrder(_key.Paths[0])
                .ExecuteAsync()
                .GetAsyncEnumerator(cancellationToken);
            return NextAsync();
        }

        /// <summary>How many rows from here on are at or below <paramref name="limit"/>.</summary>
        /// <param name="limit">The smallest key another input is holding.</param>
        /// <returns>At least one, since this input holds the smallest key of all.</returns>
        internal int RunUpTo(ReadOnlySpan<byte> limit)
        {
            int end = _row;
            while (end < _batch!.RowCount && _keys!.Row(end).SequenceCompareTo(limit) <= 0)
            {
                end++;
            }

            return end - _row;
        }

        /// <summary>Writes <paramref name="count"/> rows to the outputs and steps over them.</summary>
        /// <param name="count">The run's length.</param>
        /// <param name="outputs">Where the rows go.</param>
        /// <param name="cancellationToken">Cancels the write.</param>
        internal async ValueTask EmitAsync(
            int count, ObjectStream outputs, CancellationToken cancellationToken)
        {
            RecordBatch batch = _batch!;
            if (_row == 0 && count == batch.RowCount)
            {
                // The whole batch is the run: no window, no gather, the batch goes straight through.
                await outputs.WriteAsync(batch, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                RecordBatch window = batch.Window(_row, count);
                try
                {
                    await outputs.WriteAsync(window, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    window.Dispose();
                }
            }

            _row += count;
            if (_row >= batch.RowCount)
            {
                await NextAsync().ConfigureAwait(false);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _keys?.Dispose();
            _keys = null;
            _batch = null;
            if (_batches is not null)
            {
                await _batches.DisposeAsync().ConfigureAwait(false);
                _batches = null;
            }
        }

        /// <summary>Takes the next batch and row-encodes its keys.</summary>
        private async ValueTask NextAsync()
        {
            _keys?.Dispose();
            _keys = null;
            _batch = null;
            _row = 0;
            while (await _batches!.MoveNextAsync().ConfigureAwait(false))
            {
                RecordBatch batch = _batches.Current;
                if (batch.RowCount == 0)
                {
                    continue;
                }

                _batch = batch;
                _keys = RowEncoder.Encode(batch.Arena, Columns(batch, _key.Paths), _fields);
                return;
            }
        }

        /// <summary>The canonical node of each key column of a batch.</summary>
        private static int[] Columns(RecordBatch batch, IReadOnlyList<string> paths)
        {
            int[] columns = new int[paths.Count];
            for (int i = 0; i < paths.Count; i++)
            {
                DType at = batch.Schema;
                int node = batch.RootIndex;
                foreach (string segment in paths[i].Split('.'))
                {
                    int field = at.IndexOfField(segment);
                    if (field < 0)
                    {
                        throw new ArgumentException(
                            $"'{paths[i]}' names no column of the object's schema.", nameof(paths));
                    }

                    node = batch.Arena.GetNode(node).GetFieldIndex(field);
                    at = at.GetField(field);
                }

                columns[i] = node;
            }

            return columns;
        }
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
