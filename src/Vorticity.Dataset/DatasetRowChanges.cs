using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Scanning;

namespace Vorticity.Dataset;

/// <summary>What a delete or an update did.</summary>
public sealed record RowChangeResult
{
    /// <summary>The version the commit created; the one the handle held when no row matched and nothing was committed.</summary>
    public ulong Version { get; init; }

    /// <summary>
    /// <see cref="OperationOutcome.Applied"/>, or <see cref="OperationOutcome.AlreadyThere"/> when no
    /// row matched, in which case no version was created.
    /// </summary>
    public OperationOutcome Outcome { get; init; }

    /// <summary>The rows deleted, or updated.</summary>
    public long Rows { get; init; }

    /// <summary>The objects taken out of the version: those rewritten without the rows, and those every row of which went.</summary>
    public long ObjectsIn { get; init; }

    /// <summary>The objects put in: the rewritten ones and, for an update, the ones holding the changed rows.</summary>
    public long ObjectsOut { get; init; }

    /// <summary>The bytes of the objects taken out, the denominator of the write amplification.</summary>
    public long BytesIn { get; init; }

    /// <summary>The bytes written.</summary>
    public long BytesOut { get; init; }

    /// <summary>
    /// How many times the change was worked out: once, and once more for each concurrent commit that
    /// took one of its objects first; none when its filter could match no row.
    /// </summary>
    public int Attempts { get; init; }
}

/// <summary>
/// Deletes or updates rows by rewriting the objects that hold them. Copy on write: the read path
/// pays nothing, every statistic stays exact and every object stays a plain Vortex file, and the
/// price is the rewrite of each object a row is taken from.
/// </summary>
/// <remarks>
/// <para>
/// An object whose summaries refute the filter is not opened, and one whose own count finds no row
/// is not rewritten; one every row of which goes is removed without a read. The others are read
/// once, in row order. The rows the filter is not true for -- false, or unknown under a null, since
/// a delete takes only what the filter selects -- are written into an object that takes the old
/// one's place in its level: a subset of its keys, so a level above 0 stays key-disjoint, and
/// without a clustering key the old one's position, so the order holds.
/// </para>
/// <para>
/// An update reads the rows it selects as records, changes them and writes them into new objects
/// of level 0, as an append would, since a changed row may carry another key: without a clustering
/// key they follow every other row. One commit replaces everything, so a reader sees all of the
/// change or none of it; when a concurrent commit has taken one of the objects first, the change is
/// worked out again on the version that won, and what the lost attempt wrote is left for vacuum.
/// </para>
/// </remarks>
internal static class DatasetRowChanges
{
    /// <summary>
    /// Takes the rows <paramref name="filter"/> is true for out of the dataset, every row when it is
    /// null, and hands them to the sink <paramref name="start"/> makes, when there is one; commits,
    /// and moves the handle to the version created.
    /// </summary>
    /// <param name="dataset">The dataset.</param>
    /// <param name="filter">The rows to take, checked against the schema; null for all of them.</param>
    /// <param name="start">Makes, for one attempt, the sink the taken rows go to, given the position of a row that follows every other; null for a delete.</param>
    /// <param name="cancellationToken">Cancels the reads, the writes and the commit.</param>
    /// <exception cref="ObjectStoreException">Concurrent commits took an object first on every attempt.</exception>
    internal static async ValueTask<RowChangeResult> RunAsync(
        VortexDataset dataset, VortexExpr? filter, Func<long, ChangedRows>? start, CancellationToken cancellationToken)
    {
        int attempts = Math.Max(dataset.Options.MaxAttempts, 1);
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            DatasetSnapshot version = dataset.Snapshot;
            ChangedRows? changed = start?.Invoke(await dataset.EndAsync(cancellationToken).ConfigureAwait(false));
            List<(int Level, ReadOnlyMemory<byte> Key)> inputs = [];
            List<(int Level, ReadOnlyMemory<byte> Key, ObjectEntry Entry)> outputs = [];
            long rows = 0;
            long bytesIn = 0;
            long bytesOut = 0;
            try
            {
                SummaryPruner? pruner = filter is null ? null : new SummaryPruner(filter);
                await foreach (PositionedObject held in version
                    .WalkAsync(pruner, 0, long.MaxValue, null, cancellationToken).ConfigureAwait(false))
                {
                    (long taken, WrittenObject? rewritten) = await RewriteAsync(dataset, version, held, filter, changed, cancellationToken)
                        .ConfigureAwait(false);
                    if (taken == 0)
                    {
                        continue;
                    }

                    rows += taken;
                    bytesIn += held.Entry.Bytes;
                    inputs.Add((held.Level, held.TreeKey.ToArray()));
                    if (rewritten is { } kept)
                    {
                        outputs.Add((held.Level, kept.Key, kept.Entry));
                        bytesOut += kept.Entry.Bytes;
                    }
                }

                if (changed is not null)
                {
                    foreach (WrittenObject written in await changed.FinishAsync(cancellationToken).ConfigureAwait(false))
                    {
                        outputs.Add((0, written.Key, written.Entry));
                        bytesOut += written.Entry.Bytes;
                    }
                }
            }
            catch
            {
                if (changed is not null)
                {
                    await changed.AbandonAsync().ConfigureAwait(false);
                }

                throw;
            }

            if (inputs.Count == 0)
            {
                return new RowChangeResult { Version = version.Version, Outcome = OperationOutcome.AlreadyThere, Attempts = attempt };
            }

            CommitResult commit = await dataset
                .CommitAsync([new DatasetOperation.ReplaceObjects(inputs, outputs)], cancellationToken).ConfigureAwait(false);
            if (commit.Outcomes[0] == OperationOutcome.Applied)
            {
                return new RowChangeResult
                {
                    Version = commit.Version,
                    Outcome = OperationOutcome.Applied,
                    Rows = rows,
                    ObjectsIn = inputs.Count,
                    ObjectsOut = outputs.Count,
                    BytesIn = bytesIn,
                    BytesOut = bytesOut,
                    Attempts = attempt,
                };
            }
        }

        throw new ObjectStoreException(
            $"The change lost {attempts} times to commits that rewrote the objects it read; a coordinator " +
            "that serialises the dataset's writers is the answer to contention, not more attempts.");
    }

    /// <summary>
    /// Takes an object's matching rows: none when its count finds none, the whole object without a
    /// read when every row matches and no sink wants them, and otherwise one pass over its rows that
    /// writes the others into a new object and hands the matching ones to the sink.
    /// </summary>
    /// <returns>How many rows it took, and the object that replaces it; null when no row is left.</returns>
    private static async ValueTask<(long Taken, WrittenObject? Rewritten)> RewriteAsync(
        VortexDataset dataset,
        DatasetSnapshot version,
        PositionedObject held,
        VortexExpr? filter,
        ChangedRows? changed,
        CancellationToken cancellationToken)
    {
        ObjectLease lease = await version.RentAsync(held.Entry, cancellationToken).ConfigureAwait(false);
        await using (lease.ConfigureAwait(false))
        {
            VortexFile file = lease.File;
            long matched = filter is null
                ? held.Entry.Rows
                : await file.ScanBuilder().Where(filter).CountAsync(cancellationToken).ConfigureAwait(false);
            if (matched == 0 || (matched == held.Entry.Rows && changed is null))
            {
                return (matched, null);
            }

            ObjectDraft? draft = matched < held.Entry.Rows ? dataset.StartObject() : null;
            try
            {
                long kept = await SplitAsync(file, filter, draft?.Writer, changed, cancellationToken).ConfigureAwait(false);
                if (draft is null)
                {
                    return (matched, null);
                }

                if (kept != held.Entry.Rows - matched)
                {
                    throw new InvalidOperationException(
                        $"'{held.Entry.Key}' kept {kept} of its {held.Entry.Rows} rows where its count took {matched}: " +
                        "a rewrite that loses or doubles rows is the one failure it must not have.");
                }

                ObjectDraft sealing = draft;
                draft = null;
                await using (sealing.Writer.ConfigureAwait(false))
                {
                    await sealing.Writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
                }

                // Without a clustering key the object keeps the old one's position, so that it is
                // read where the old one was.
                long position = dataset.Key is null ? VortexDataset.PositionAt(held.TreeKey.Span) : held.FirstRow;
                return (matched, await dataset.SealAsync(sealing, kept, position, cancellationToken).ConfigureAwait(false));
            }
            catch
            {
                if (draft is not null)
                {
                    await draft.DisposeAsync().ConfigureAwait(false);
                }

                throw;
            }
        }
    }

    /// <summary>
    /// One pass over an object's rows: the rows the filter is not true for go to
    /// <paramref name="writer"/> and the others to <paramref name="changed"/>, each batch selected
    /// rather than copied.
    /// </summary>
    /// <returns>The rows written to <paramref name="writer"/>.</returns>
    private static async ValueTask<long> SplitAsync(
        VortexFile file, VortexExpr? filter, VortexFileWriter? writer, ChangedRows? changed, CancellationToken cancellationToken)
    {
        FilterEvaluator? evaluator = filter is null ? null : new FilterEvaluator(filter);
        byte[] states = [];
        long kept = 0;
        try
        {
            await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync()
                .WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                int rows = batch.RowCount;
                if (rows == 0)
                {
                    continue;
                }

                if (evaluator is null)
                {
                    await changed!.TakeAsync(batch, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (states.Length < rows)
                {
                    if (states.Length > 0)
                    {
                        ArrayPool<byte>.Shared.Return(states);
                    }

                    states = ArrayPool<byte>.Shared.Rent(rows);
                }

                evaluator.Evaluate(batch.Arena, batch.RootIndex, rows, states.AsSpan(0, rows));
                (VortexBuffer keptWords, int keeping) = Words(batch.Arena, states.AsSpan(0, rows), matching: false);
                if (keeping > 0 && writer is not null)
                {
                    if (keeping < rows)
                    {
                        batch.Select(keptWords, keeping);
                    }

                    await writer.WriteAsync(batch, cancellationToken).ConfigureAwait(false);
                    kept += keeping;
                }

                if (keeping < rows && changed is not null)
                {
                    (VortexBuffer matchedWords, int matching) = Words(batch.Arena, states.AsSpan(0, rows), matching: true);
                    batch.Select(matchedWords, matching);
                    await changed.TakeAsync(batch, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (states.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(states);
            }
        }

        return kept;
    }

    /// <summary>
    /// The rows whose state is <see cref="Trilean.True"/>, or with <paramref name="matching"/> false
    /// the others, as selection words in the batch's arena, and how many there are.
    /// </summary>
    private static (VortexBuffer Words, int Count) Words(Arrays.CanonicalArena arena, ReadOnlySpan<byte> states, bool matching)
    {
        int words = Math.Max((states.Length + 63) >> 6, 1);
        VortexBuffer buffer = arena.AllocateUninitialized(words * sizeof(ulong), 64, out Span<byte> raw);
        Span<ulong> bits = MemoryMarshal.Cast<byte, ulong>(raw);
        bits[0] = 0;
        int count = Trilean.ToWords(states, Trilean.True, equal: matching, bits);
        return (buffer, count);
    }
}

/// <summary>Where the rows a change takes go, for an update: written again, changed, into new objects.</summary>
internal abstract class ChangedRows
{
    /// <summary>Takes the selected rows of <paramref name="batch"/>, every row when it selects none.</summary>
    internal abstract ValueTask TakeAsync(RecordBatch batch, CancellationToken cancellationToken);

    /// <summary>Seals what is left and returns every object written, each bound for level 0.</summary>
    internal abstract ValueTask<IReadOnlyList<WrittenObject>> FinishAsync(CancellationToken cancellationToken);

    /// <summary>Drops the object being written; what was sealed already is left for vacuum.</summary>
    internal abstract ValueTask AbandonAsync();
}

/// <summary>
/// The rows an update selects, read out of each batch as records, changed, and written into new
/// objects rolled at level 1's target size. A changed row may carry another key, which is why they
/// go to level 0, as appended rows do.
/// </summary>
/// <typeparam name="TRecord">The record the change is written against; its members cover every column.</typeparam>
internal sealed class ChangedRecords<TRecord> : ChangedRows
    where TRecord : IVortexRecord<TRecord>
{
    private readonly VortexDataset _dataset;
    private readonly Func<TRecord, TRecord> _update;
    private readonly RecordBinding _binding;
    private readonly long _target;
    private readonly List<WrittenObject> _written = [];
    private TRecord[] _rows = [];
    private ObjectDraft? _draft;
    private long _draftRows;
    private long _firstRow;

    /// <param name="dataset">The dataset the objects are written for.</param>
    /// <param name="update">The change, applied to every row once.</param>
    /// <param name="binding">The record bound to the dataset's schema.</param>
    /// <param name="firstRow">Where the first object's rows go when the dataset has no clustering key: after every other row.</param>
    internal ChangedRecords(VortexDataset dataset, Func<TRecord, TRecord> update, RecordBinding binding, long firstRow)
    {
        _dataset = dataset;
        _update = update;
        _binding = binding;
        _firstRow = firstRow;
        long levelOne = dataset.Compaction.LevelTargetBytes > 0
            ? dataset.Compaction.LevelTargetBytes
            : CompactionOptions.DefaultTargetBytesAtLevelOne;
        _target = Math.Min(levelOne, Math.Max(dataset.Options.MaxObjectBytes / 2, 1));
    }

    internal override async ValueTask TakeAsync(RecordBatch batch, CancellationToken cancellationToken)
    {
        int count = Fill(batch);
        for (int i = 0; i < count; i++)
        {
            _rows[i] = _update(_rows[i]);
        }

        _draft ??= _dataset.StartObject();
        await _draft.Writer.WriteAsync<TRecord>(_rows.AsSpan(0, count), cancellationToken).ConfigureAwait(false);
        _draftRows += count;
        if (_draft.Sink.Position >= _target)
        {
            await RollAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal override async ValueTask<IReadOnlyList<WrittenObject>> FinishAsync(CancellationToken cancellationToken)
    {
        await RollAsync(cancellationToken).ConfigureAwait(false);
        Release();
        return _written;
    }

    internal override async ValueTask AbandonAsync()
    {
        Release();
        if (_draft is { } draft)
        {
            _draft = null;
            await draft.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Seals the object being written, if there is one.</summary>
    private async ValueTask RollAsync(CancellationToken cancellationToken)
    {
        if (_draft is not { } draft)
        {
            return;
        }

        _draft = null;
        try
        {
            await using (draft.Writer.ConfigureAwait(false))
            {
                await draft.Writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            await draft.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        if (await _dataset.SealAsync(draft, _draftRows, _firstRow, cancellationToken).ConfigureAwait(false) is { } written)
        {
            _written.Add(written);
        }

        _firstRow += _draftRows;
        _draftRows = 0;
    }

    /// <summary>The selected rows of a batch as records, in order, at the front of the buffer.</summary>
    private int Fill(RecordBatch batch)
    {
        int count = batch.RowCount;
        if (_rows.Length < count)
        {
            Release();
            _rows = ArrayPool<TRecord>.Shared.Rent(count);
        }

        Columns<TRecord> columns = new Columns<TRecord>(
            batch, batch.Arena, batch.RootIndex, _binding, batch.StartRow, batch.SelectionWords, batch.SelectedRows, projected: false);
        TRecord.ReadRows(columns, _rows.AsSpan(0, count));
        if (batch.SelectionWords.IsEmpty)
        {
            return count;
        }

        int kept = 0;
        foreach (int row in columns.Selection)
        {
            _rows[kept++] = _rows[row];
        }

        return kept;
    }

    private void Release()
    {
        if (_rows.Length > 0)
        {
            ArrayPool<TRecord>.Shared.Return(_rows, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<TRecord>());
            _rows = [];
        }
    }
}
