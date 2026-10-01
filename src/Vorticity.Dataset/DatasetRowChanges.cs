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
using Vorticity.Layouts;
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

    /// <summary>
    /// The objects the rows were taken from without a rewrite: their entries name the rows deleted,
    /// and their files, unchanged, are read without them until a compaction rewrites them.
    /// </summary>
    public long ObjectsMarked { get; init; }

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
/// Deletes or updates rows by marking them in the objects that hold them, or by rewriting those
/// objects. A mark costs the rows' positions, a few bytes in the object's entry, whatever the
/// object's size; a rewrite costs the object, and is taken where it is cheap or where the marks
/// would weigh: a small object, or one whose deleted rows would pass an eighth of its rows.
/// </summary>
/// <remarks>
/// <para>
/// An object whose summaries refute the filter is not opened, and one whose own count finds no row
/// is not touched; one every live row of which goes is removed without a read. The rows the filter
/// is not true for -- false, or unknown under a null, since a delete takes only what the filter
/// selects -- stay: in the marked object, whose entry takes its place under the same key, or in the
/// rewritten one, which takes the old one's place in its level. Either way a level above 0 stays
/// key-disjoint, and without a clustering key the order holds.
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
    /// <param name="start">Makes, for one attempt, the sink the taken rows go to, given the schema they are read as and the position of a row that follows every other; null for a delete.</param>
    /// <param name="cancellationToken">Cancels the reads, the writes and the commit.</param>
    /// <exception cref="ObjectStoreException">Concurrent commits took an object first on every attempt.</exception>
    internal static async ValueTask<RowChangeResult> RunAsync(
        VortexDataset dataset, VortexExpr? filter, Func<DatasetSchema, long, ChangedRows>? start, CancellationToken cancellationToken)
    {
        int attempts = Math.Max(dataset.Options.MaxAttempts, 1);
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            DatasetSnapshot version = dataset.Snapshot;
            ChangedRows? changed = start?.Invoke(version.Schema, await dataset.EndAsync(cancellationToken).ConfigureAwait(false));
            List<(int Level, ReadOnlyMemory<byte> Key)> inputs = [];
            List<ObjectEntry> expected = [];
            List<(int Level, ReadOnlyMemory<byte> Key, ObjectEntry Entry)> outputs = [];
            long rows = 0;
            long bytesIn = 0;
            long bytesOut = 0;
            long marked = 0;
            try
            {
                SummaryPruner? pruner = filter is null ? null : new SummaryPruner(filter);
                await foreach (PositionedObject held in version
                    .WalkAsync(pruner, 0, long.MaxValue, null, cancellationToken).ConfigureAwait(false))
                {
                    Change change = await RewriteAsync(dataset, version, held, filter, changed, cancellationToken)
                        .ConfigureAwait(false);
                    if (change.Taken == 0)
                    {
                        continue;
                    }

                    rows += change.Taken;
                    ReadOnlyMemory<byte> key = held.TreeKey.ToArray();
                    inputs.Add((held.Level, key));
                    expected.Add(held.Entry);
                    if (change.Marked is { } entry)
                    {
                        // The same object under the same key, its entry naming the rows gone.
                        outputs.Add((held.Level, key, entry));
                        marked++;
                        continue;
                    }

                    bytesIn += held.Entry.Bytes;
                    if (change.Rewritten is { } kept)
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
                .CommitAsync([new DatasetOperation.ReplaceObjects(inputs, outputs) { Expected = expected }], cancellationToken)
                .ConfigureAwait(false);
            if (commit.Outcomes[0] == OperationOutcome.Applied)
            {
                if (outputs.Count > marked)
                {
                    await dataset.CompactInlineAsync(cancellationToken).ConfigureAwait(false);
                }

                return new RowChangeResult
                {
                    Version = commit.Version,
                    Outcome = OperationOutcome.Applied,
                    Rows = rows,
                    ObjectsIn = inputs.Count - marked,
                    ObjectsOut = outputs.Count - marked,
                    ObjectsMarked = marked,
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
    /// Takes an object's matching rows: none when its count finds none; the whole object without a
    /// read when every live row matches and no sink wants them; the rows marked in its entry when
    /// marks are what the object can take; and otherwise one pass over its live rows that writes the
    /// others into a new object. The matching rows go to the sink either way.
    /// </summary>
    private static async ValueTask<Change> RewriteAsync(
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
            ObjectColumns? columns = version.Schema.ColumnsOf(file.DType, held.Entry.Key);
            long matched = await CountAsync(file, held, columns, filter, cancellationToken).ConfigureAwait(false);
            if (matched == 0 || (matched == held.Entry.Rows && changed is null))
            {
                return new Change(matched, null, null);
            }

            if (matched < held.Entry.Rows && MayMark(dataset.Options, held.Entry, matched))
            {
                // Marked at once when the rows fit the vector's bound even as a run each; otherwise
                // their positions are found first, since rows a range takes lie in a few runs, and
                // only then handed to the sink, which a rewrite would hand them to instead.
                bool fits = FitsAtWorst(dataset.Options, held.Entry, matched);
                DeletionVector deletions = await MarkAsync(file, held, columns, filter, fits ? changed : null, cancellationToken).ConfigureAwait(false);
                if (fits || deletions.EncodedBytes <= dataset.Options.MarkedVectorBytes)
                {
                    if (!fits && changed is not null)
                    {
                        deletions = await MarkAsync(file, held, columns, filter, changed, cancellationToken).ConfigureAwait(false);
                    }

                    if (deletions.Count - held.Entry.Deletions.Count != matched)
                    {
                        throw new InvalidOperationException(
                            $"'{held.Entry.Key}' marked {deletions.Count - held.Entry.Deletions.Count} rows where its count took {matched}: " +
                            "a delete that loses or doubles rows is the one failure it must not have.");
                    }

                    return new Change(matched, null, held.Entry.WithDeletions(deletions));
                }
            }

            // Written in the version's schema, which an object of an earlier one is read as. The rows
            // kept are the object's own in its own order, and an object above level 0 is a
            // compaction's, whose rows come in key order.
            ObjectDraft? draft = matched < held.Entry.Rows ? dataset.StartObjectUnder(version.Schema, inKeyOrder: held.Level > 0) : null;
            try
            {
                long kept = await SplitAsync(file, held.Entry, columns, filter, draft?.Writer, changed, cancellationToken).ConfigureAwait(false);
                if (draft is null)
                {
                    return new Change(matched, null, null);
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
                return new Change(matched, await dataset.SealAsync(sealing, kept, position, cancellationToken).ConfigureAwait(false), null);
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
    /// Whether an object may take <paramref name="matched"/> more deleted rows as marks rather than a
    /// rewrite: it is large enough for a rewrite to cost more than the marks' reads, and its deleted
    /// rows stay under a share of its rows, which a read skips over and the store keeps. Its vector
    /// has a bound too, which <see cref="FitsAtWorst"/> or the vector itself settles.
    /// </summary>
    private static bool MayMark(DatasetOptions options, ObjectEntry entry, long matched) =>
        entry.Bytes >= options.MarkedObjectBytes
        && (entry.DeletedRows + matched) * Math.Max(options.MarkedShare, 1) <= entry.PhysicalRows;

    /// <summary>
    /// Whether an object's marks stay under the bound that keeps its entry a small part of a tree page
    /// even when each of the <paramref name="matched"/> rows is a run of its own: settled before a
    /// position is read.
    /// </summary>
    private static bool FitsAtWorst(DatasetOptions options, ObjectEntry entry, long matched)
    {
        int varint = TreePage.VarintBytes((ulong)entry.PhysicalRows);
        long held = entry.VectorBytes;
        return held + (matched * 2 * varint) <= options.MarkedVectorBytes;
    }

    /// <summary>
    /// The object's deletion vector with its matching live rows added, found by the object's own
    /// scan under the filter and read at their positions; the matching rows go to the sink too.
    /// </summary>
    private static async ValueTask<DeletionVector> MarkAsync(
        VortexFile file, PositionedObject held, ObjectColumns? columns, VortexExpr? filter, ChangedRows? changed, CancellationToken cancellationToken)
    {
        DeletionVector deletions = held.Entry.Deletions;
        List<long> rows = [];
        ulong[] live = [];
        try
        {
            await foreach (RecordBatch batch in MatchingAsync(file, columns, filter, whole: changed is not null, cancellationToken)
                .WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                int count = batch.RowCount;
                if (count == 0)
                {
                    continue;
                }

                int words = (count + 63) >> 6;
                if (live.Length < words)
                {
                    if (live.Length > 0)
                    {
                        ArrayPool<ulong>.Shared.Return(live);
                    }

                    live = ArrayPool<ulong>.Shared.Rent(words);
                }

                // The rows the filter kept that no earlier delete took: their bits, and their places.
                Span<ulong> taken = live.AsSpan(0, words);
                ReadOnlySpan<ulong> selection = batch.SelectionWords;
                long start = batch.StartRow;
                int matching = 0;
                taken.Clear();
                for (int row = 0; row < count; row++)
                {
                    if ((selection.IsEmpty || (selection[row >> 6] & (1UL << (row & 63))) != 0) && !deletions.Contains(start + row))
                    {
                        taken[row >> 6] |= 1UL << (row & 63);
                        rows.Add(start + row);
                        matching++;
                    }
                }

                if (matching > 0 && changed is not null)
                {
                    VortexBuffer buffer = batch.Arena.AllocateUninitialized(Math.Max(words, 1) * sizeof(ulong), 64, out Span<byte> raw);
                    taken.CopyTo(MemoryMarshal.Cast<byte, ulong>(raw));
                    batch.Select(buffer, matching);
                    await changed.TakeAsync(batch, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (live.Length > 0)
            {
                ArrayPool<ulong>.Shared.Return(live);
            }
        }

        return deletions.With(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(rows));
    }

    /// <summary>
    /// An object's rows under the filter, at their positions: its own scan without compaction, each
    /// batch selecting what the filter keeps, reshaped when it was written under another schema.
    /// With <paramref name="whole"/> every column is read, for a sink that writes the rows again;
    /// otherwise only what the filter needs.
    /// </summary>
    private static IAsyncEnumerable<RecordBatch> MatchingAsync(
        VortexFile file, ObjectColumns? columns, VortexExpr? filter, bool whole, CancellationToken cancellationToken)
    {
        if (columns is null)
        {
            ScanBuilder scan = file.ScanBuilder().WithCompaction(false).WithEncodings(false, false);
            if (filter is not null)
            {
                scan.Where(filter);
            }

            if (!whole && file.DType.Kind == Types.DTypeKind.Struct && file.DType.FieldCount > 0)
            {
                scan.ProjectFields([0]);
            }

            return scan.ExecuteAsync();
        }

        ObjectRead read = columns.Read(filter, whole ? FieldMask.All : FieldMask.Empty);
        ScanBuilder adapted = file.ScanBuilder().ProjectMask(read.Shape.Source).WithEncodings(false, false).WithCompaction(false);
        if (read.Pushed is { } pushed)
        {
            adapted.Where(pushed);
        }

        return read.Fate == FilterFate.None
            ? System.Linq.AsyncEnumerable.Empty<RecordBatch>()
            : ObjectColumns.ReshapeAsync(adapted.ExecuteAsync(), read.Shape, read.Evaluated, 0, compact: false, cancellationToken);
    }

    /// <summary>What a change did to one object: the rows it took, and the object rewritten without them or the entry marking them.</summary>
    private readonly record struct Change(long Taken, WrittenObject? Rewritten, ObjectEntry? Marked);

    /// <summary>
    /// One pass over an object's rows: the rows the filter is not true for go to
    /// <paramref name="writer"/> and the others to <paramref name="changed"/>, each batch selected
    /// rather than copied.
    /// </summary>
    /// <returns>The rows written to <paramref name="writer"/>.</returns>
    private static async ValueTask<long> SplitAsync(
        VortexFile file,
        ObjectEntry entry,
        ObjectColumns? columns,
        VortexExpr? filter,
        VortexFileWriter? writer,
        ChangedRows? changed,
        CancellationToken cancellationToken)
    {
        FilterEvaluator? evaluator = filter is null ? null : new FilterEvaluator(filter);
        byte[] states = [];
        long kept = 0;
        try
        {
            await foreach (RecordBatch batch in RowsOfAsync(file, entry, columns, cancellationToken)
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
    /// How many of an object's rows the filter takes, every one when there is none: the object's
    /// own count, answered from its statistics when they settle it, or for an object of an earlier
    /// schema the count of what the filter comes to over its columns.
    /// </summary>
    private static async ValueTask<long> CountAsync(
        VortexFile file, PositionedObject held, ObjectColumns? columns, VortexExpr? filter, CancellationToken cancellationToken)
    {
        if (filter is null)
        {
            return held.Entry.Rows;
        }

        VortexExpr counted = filter;
        if (columns is not null)
        {
            ObjectRead read = columns.Read(filter, FieldMask.Empty);
            switch (read.Fate)
            {
                case FilterFate.None:
                    return 0;
                case FilterFate.Every:
                    return held.Entry.Rows;
                case FilterFate.Pushed:
                    counted = read.Pushed!;
                    break;
                default:
                    long rows = 0;
                    ScanBuilder scan = file.ScanBuilder().ProjectMask(read.Shape.Source).WithEncodings(false, false);
                    IAsyncEnumerable<RecordBatch> source = held.Entry.HasDeletions
                        ? LiveRows.WithoutDeletedAsync(scan.WithCompaction(false).ExecuteAsync(), held.Entry.Deletions, compact: true, 0, cancellationToken)
                        : scan.ExecuteAsync();
                    await foreach (RecordBatch batch in ObjectColumns.ReshapeAsync(source, read.Shape, read.Evaluated, 0, compact: false, cancellationToken)
                        .ConfigureAwait(false))
                    {
                        rows += batch.SelectedRows;
                    }

                    return rows;
            }
        }

        // The file's own count, cheapest proof first, the rows already deleted left out.
        ScanBuilder counting = file.ScanBuilder().Where(counted);
        return held.Entry.HasDeletions
            ? await counting.CountExcludingAsync(held.Entry.Deletions, cancellationToken).ConfigureAwait(false)
            : await counting.CountAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>An object's live rows, whole and in order, as the version's columns.</summary>
    private static IAsyncEnumerable<RecordBatch> RowsOfAsync(VortexFile file, ObjectEntry entry, ObjectColumns? columns, CancellationToken cancellationToken)
    {
        if (columns is null)
        {
            return entry.HasDeletions
                ? LiveRows.WithoutDeletedAsync(
                    file.ScanBuilder().WithCompaction(false).WithEncodings(false, false).ExecuteAsync(), entry.Deletions, compact: true, 0, cancellationToken)
                : file.ScanBuilder().ExecuteAsync();
        }

        ObjectRead read = columns.Read(null, FieldMask.All);
        ScanBuilder scan = file.ScanBuilder().ProjectMask(read.Shape.Source).WithEncodings(false, false);
        IAsyncEnumerable<RecordBatch> rows = entry.HasDeletions
            ? LiveRows.WithoutDeletedAsync(scan.WithCompaction(false).ExecuteAsync(), entry.Deletions, compact: true, 0, cancellationToken)
            : scan.ExecuteAsync();
        return ObjectColumns.ReshapeAsync(rows, read.Shape, null, 0, compact: true, cancellationToken);
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
    private readonly DatasetSchema _schema;
    private readonly Func<TRecord, TRecord> _update;
    private readonly RecordBinding _binding;
    private readonly long _target;
    private readonly List<WrittenObject> _written = [];
    private TRecord[] _rows = [];
    private ObjectDraft? _draft;
    private long _draftRows;
    private long _firstRow;

    /// <param name="dataset">The dataset the objects are written for.</param>
    /// <param name="schema">The schema the rows are read as and written in.</param>
    /// <param name="update">The change, applied to every row once.</param>
    /// <param name="firstRow">Where the first object's rows go when the dataset has no clustering key: after every other row.</param>
    internal ChangedRecords(VortexDataset dataset, DatasetSchema schema, Func<TRecord, TRecord> update, long firstRow)
    {
        _dataset = dataset;
        _schema = schema;
        _update = update;
        _binding = RecordBinding.For<TRecord>(schema.Columns, dataset.Session.Options.Extensions);
        _firstRow = firstRow;
        // Rolled at what compaction writes at most: the changed rows land in level 0, where a
        // large change in many small objects would put the level over its ceiling at once.
        _target = Math.Min(CompactionOptions.DefaultMaxObjectBytes, Math.Max(dataset.Options.MaxObjectBytes / 2, 1));
    }

    internal override async ValueTask TakeAsync(RecordBatch batch, CancellationToken cancellationToken)
    {
        int count = Fill(batch);
        for (int i = 0; i < count; i++)
        {
            _rows[i] = _update(_rows[i]);
        }

        _draft ??= _dataset.StartObjectUnder(_schema);
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
