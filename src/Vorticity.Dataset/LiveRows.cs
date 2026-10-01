using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Keys;
using Vorticity.Scanning;
using Vorticity.Types;

namespace Vorticity.Dataset;

/// <summary>
/// An object's rows without the ones its deletion vector names. An object's own scan knows nothing
/// of them: it delivers every row its filter keeps, at the object's own positions, and these streams
/// take the deleted ones out and number the rest as the live rows they are.
/// </summary>
internal static class LiveRows
{
    /// <summary>
    /// The batches of an object's own scan, run without compaction so that each batch holds its
    /// rows at their positions, with the deleted rows taken out. Each batch that comes out starts at
    /// its first live row's place among the live ones; with <paramref name="compact"/> it holds only
    /// the rows the scan's filter kept, and otherwise it keeps the filter's selection over the live rows.
    /// </summary>
    /// <remarks>
    /// A batch with no deleted row in it goes out as it came, under its new start. The others are
    /// gathered in their own arena, by a mask where most rows stay, as a filter's survivors are.
    /// </remarks>
    internal static async IAsyncEnumerable<RecordBatch> WithoutDeletedAsync(
        IAsyncEnumerable<RecordBatch> source,
        DeletionVector deletions,
        bool compact,
        long baseRow,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        int[] indices = [];
        ulong[] mask = [];
        RecordBatch? view = null;
        try
        {
            await foreach (RecordBatch batch in source.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                int rows = batch.RowCount;
                long start = batch.StartRow;
                long logical = baseRow + deletions.Logical(start);
                ReadOnlySpan<ulong> selection = batch.SelectionWords;
                bool deletes = deletions.DeletedIn(start, start + rows) > 0;
                if (!deletes && (!compact || selection.IsEmpty))
                {
                    view?.Dispose();
                    view = batch.Reshaped(batch.RootIndex, logical, view);
                    yield return view;
                    continue;
                }

                int words = (rows + 63) >> 6;
                if (mask.Length < words)
                {
                    Return(mask);
                    mask = ArrayPool<ulong>.Shared.Rent(words);
                }

                if (indices.Length < rows)
                {
                    Return(indices);
                    indices = ArrayPool<int>.Shared.Rent(rows);
                }

                // The rows kept: the live ones, and of those the selected ones when compacting.
                Span<ulong> kept = mask.AsSpan(0, words);
                Live(deletions, start, rows, kept);
                if (compact && !selection.IsEmpty)
                {
                    for (int word = 0; word < words; word++)
                    {
                        kept[word] &= selection[word];
                    }
                }

                int count = Indices(kept, rows, indices);
                if (count == 0)
                {
                    continue;
                }

                CanonicalArena arena = batch.Arena;
                int root = count == rows
                    ? batch.RootIndex
                    : CanonicalFilter.Apply(arena, batch.RootIndex, indices.AsSpan(0, count), count * 2 > rows ? kept : default);

                // What the filter selected among the live rows, when it is not compacted away.
                VortexBuffer carried = default;
                int selected = count;
                if (!compact && !selection.IsEmpty)
                {
                    carried = arena.AllocateUninitialized(Math.Max((count + 63) >> 6, 1) * sizeof(ulong), 64, out Span<byte> raw);
                    Span<ulong> bits = MemoryMarshal.Cast<byte, ulong>(raw);
                    selected = Gathered(selection, indices.AsSpan(0, count), bits);
                    if (selected == 0)
                    {
                        continue;
                    }
                }

                view?.Dispose();
                view = batch.Reshaped(root, logical, view);
                view.Select(carried, selected);
                yield return view;
            }
        }
        finally
        {
            view?.Dispose();
            Return(indices);
            Return(mask);
        }
    }

    /// <summary>
    /// The batches of an object's own scan, run without compaction, with the deleted rows
    /// deselected where they lie rather than taken out: for a consumer that reads no row's place, an
    /// aggregation, to which a batch is the rows its selection names. Nothing is gathered, so every
    /// column stays as its decoder left it, encoded or not; a batch holding a deleted row only gains
    /// selection words, and one left with no selected row is skipped. Each starts at its first row's
    /// place among the live ones, from <paramref name="baseRow"/>; the rows after a deleted one are
    /// not numbered from there.
    /// </summary>
    internal static async IAsyncEnumerable<RecordBatch> DeselectedAsync(
        IAsyncEnumerable<RecordBatch> source,
        DeletionVector deletions,
        long baseRow,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        RecordBatch? view = null;
        try
        {
            await foreach (RecordBatch batch in source.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                int rows = batch.RowCount;
                long start = batch.StartRow;
                long logical = baseRow + deletions.Logical(start);
                view?.Dispose();
                view = batch.Reshaped(batch.RootIndex, logical, view);
                if (rows == 0 || deletions.DeletedIn(start, start + rows) == 0)
                {
                    yield return view;
                    continue;
                }

                int words = (rows + 63) >> 6;
                VortexBuffer buffer = batch.Arena.AllocateUninitialized(words * sizeof(ulong), 64, out Span<byte> raw);
                Span<ulong> kept = MemoryMarshal.Cast<byte, ulong>(raw);
                Live(deletions, start, rows, kept);
                ReadOnlySpan<ulong> selection = batch.SelectionWords;
                int selected = 0;
                for (int word = 0; word < words; word++)
                {
                    if (!selection.IsEmpty)
                    {
                        kept[word] &= selection[word];
                    }

                    selected += BitOperations.PopCount(kept[word]);
                }

                if (selected == 0)
                {
                    continue;
                }

                view.Select(buffer, selected);
                yield return view;
            }
        }
        finally
        {
            view?.Dispose();
        }
    }

    /// <summary>
    /// The batches of an object's key-ordered scan, which leaves the deleted rows out by itself,
    /// each numbered from its first row's place among the live ones rather than among the object's.
    /// </summary>
    internal static async IAsyncEnumerable<RecordBatch> RenumberedAsync(
        IAsyncEnumerable<RecordBatch> source,
        DeletionVector deletions,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        RecordBatch? view = null;
        try
        {
            await foreach (RecordBatch batch in source.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                view?.Dispose();
                view = batch.Reshaped(batch.RootIndex, deletions.Logical(batch.StartRow), view);
                yield return view;
            }
        }
        finally
        {
            view?.Dispose();
        }
    }

    /// <summary>
    /// Each batch's rows in the reverse of their order: with the splits read last one first, the
    /// rows of a range in the reverse of file order, as a key-ordered scan downward delivers the rows
    /// its key source does not hold.
    /// </summary>
    internal static async IAsyncEnumerable<RecordBatch> ReversedAsync(
        IAsyncEnumerable<RecordBatch> source,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        int[] order = [];
        RecordBatch? view = null;
        try
        {
            await foreach (RecordBatch batch in source.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                int rows = batch.RowCount;
                if (order.Length < rows)
                {
                    Return(order);
                    order = ArrayPool<int>.Shared.Rent(rows);
                }

                for (int row = 0; row < rows; row++)
                {
                    order[row] = rows - 1 - row;
                }

                int root = CanonicalFilter.Apply(batch.Arena, batch.RootIndex, order.AsSpan(0, rows));
                view?.Dispose();
                view = batch.Reshaped(root, batch.StartRow, view);
                view.Select(default, rows);
                yield return view;
            }
        }
        finally
        {
            view?.Dispose();
            Return(order);
        }
    }

    /// <summary>
    /// An object's rows in key order without the ones it deleted: its key-ordered scan, told to skip
    /// their entries as it walks, then the live rows whose key is null, which no key source holds and
    /// a key-ordered read delivers last in either direction, in file order upward and in its reverse
    /// downward. <paramref name="nulls"/> reads those rows, filtered and projected as the ordered
    /// scan is; null when none can be, a composite key's run holding no tuple with a null.
    /// </summary>
    internal static async IAsyncEnumerable<RecordBatch> OrderedAsync(
        ScanBuilder ordered,
        ScanBuilder? nulls,
        DeletionVector deletions,
        bool descending,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (RecordBatch batch in RenumberedAsync(ordered.ExecuteExcludingAsync(deletions), deletions, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return batch;
        }

        if (nulls is null)
        {
            yield break;
        }

        IAsyncEnumerable<RecordBatch> live = WithoutDeletedAsync(
            (descending ? nulls.InReverse() : nulls).WithCompaction(false).ExecuteAsync(), deletions, compact: true, 0, cancellationToken);
        await foreach (RecordBatch batch in (descending ? ReversedAsync(live, cancellationToken) : live).ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    /// <summary>
    /// The entries a key cursor over <paramref name="paths"/> leaves out of an object, in
    /// <c>(key, row)</c> order: the keys of its deleted rows, read by a take, a composite key's as
    /// the row encoding its run holds. A deleted row whose key is null is in no source, and is left
    /// out of these too.
    /// </summary>
    internal static async ValueTask<ExcludedKey[]> ExcludedKeysAsync(
        VortexFile file, DeletionVector deletions, IReadOnlyList<string> paths, ClusteringKey key, CancellationToken cancellationToken)
    {
        long[] rows = DeletedIn(deletions, 0, long.MaxValue);
        List<ExcludedKey> keys = new List<ExcludedKey>(rows.Length);
        if (rows.Length > 0)
        {
            FilterLiteral[] values = new FilterLiteral[paths.Count];
            int[] nodes = new int[paths.Count];
            int at = 0;
            ScanBuilder scan = file.ScanBuilder().Project([.. paths]).WithEncodings(false, false).Take(rows);
            await foreach (RecordBatch batch in scan.ExecuteAsync().WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                for (int column = 0; column < nodes.Length; column++)
                {
                    nodes[column] = NodeOf(batch, paths[column]);
                }

                for (int row = 0; row < batch.RowCount; row++, at++)
                {
                    bool keyed = true;
                    for (int column = 0; column < nodes.Length && keyed; column++)
                    {
                        keyed = TryRead(batch.Arena, nodes[column], row, out values[column]);
                    }

                    if (keyed)
                    {
                        FilterLiteral entry = values.Length == 1 ? values[0] : FilterLiteral.From(key.Encode(values));
                        keys.Add(new ExcludedKey(entry, rows[at]));
                    }
                }
            }
        }

        keys.Sort(static (left, right) =>
        {
            int order = KeyOrder.Total(left.Key, right.Key);
            return order != 0 ? order : left.Row.CompareTo(right.Row);
        });
        return [.. keys];
    }

    /// <summary>A row of a column as a key: false for a null, which is no entry.</summary>
    private static bool TryRead(CanonicalArena arena, int node, int row, out FilterLiteral value) =>
        arena.GetNode(ComparisonKernels.Unwrap(arena, node)).DType.Kind == DTypeKind.Decimal
            ? LiteralReader.TryReadDecimal(arena, node, row, out value)
            : LiteralReader.TryRead(arena, node, row, out value);

    /// <summary>The node of a <c>.</c>-separated path in a batch, by the names of its struct.</summary>
    private static int NodeOf(RecordBatch batch, string path)
    {
        DType at = batch.DType;
        int node = batch.RootIndex;
        foreach (string segment in path.Split('.'))
        {
            int field = at.IndexOfField(segment);
            node = batch.Arena.GetNode(node).GetFieldIndex(field);
            at = at.GetField(field);
        }

        return node;
    }

    /// <summary>Whether a path of a file may hold a null: a field on the way, or the column, is nullable.</summary>
    internal static bool MayBeNull(DType schema, string path)
    {
        DType current = schema;
        foreach (string segment in path.Split('.'))
        {
            int field = current.Kind == DTypeKind.Struct ? current.IndexOfField(segment) : -1;
            if (field < 0)
            {
                return true;
            }

            current = current.GetField(field);
            if (current.IsNullable)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The deleted rows of <c>[start, end)</c>, the object's own positions, ascending.</summary>
    internal static long[] DeletedIn(DeletionVector deletions, long start, long end)
    {
        List<long> rows = new List<long>((int)Math.Min(deletions.DeletedIn(start, end), int.MaxValue));
        deletions.Collect(start, end, rows);
        return [.. rows];
    }

    /// <summary>A bit per row of <c>[start, start + rows)</c>, set for the live ones.</summary>
    private static void Live(DeletionVector deletions, long start, int rows, Span<ulong> bits)
    {
        bits.Fill(ulong.MaxValue);
        int tail = rows & 63;
        if (tail != 0)
        {
            bits[^1] = (1UL << tail) - 1;
        }

        long end = start + rows;
        for (int run = deletions.FirstEndingAfter(start); run < deletions.Runs && deletions.StartOf(run) < end; run++)
        {
            int from = (int)(Math.Max(deletions.StartOf(run), start) - start);
            int to = (int)(Math.Min(deletions.EndOf(run), end) - start);
            for (int row = from; row < to; row++)
            {
                bits[row >> 6] &= ~(1UL << (row & 63));
            }
        }
    }

    /// <summary>The rows whose bit is set, ascending, into <paramref name="indices"/>; how many.</summary>
    private static int Indices(ReadOnlySpan<ulong> bits, int rows, Span<int> indices)
    {
        int count = 0;
        for (int word = 0; word < bits.Length; word++)
        {
            ulong value = bits[word];
            while (value != 0)
            {
                int row = (word << 6) + BitOperations.TrailingZeroCount(value);
                if (row >= rows)
                {
                    return count;
                }

                indices[count++] = row;
                value &= value - 1;
            }
        }

        return count;
    }

    /// <summary>The selection's bit of each gathered row, packed; how many are set.</summary>
    private static int Gathered(ReadOnlySpan<ulong> selection, ReadOnlySpan<int> gathered, Span<ulong> bits)
    {
        bits.Clear();
        int selected = 0;
        for (int i = 0; i < gathered.Length; i++)
        {
            int row = gathered[i];
            if ((selection[row >> 6] & (1UL << (row & 63))) != 0)
            {
                bits[i >> 6] |= 1UL << (i & 63);
                selected++;
            }
        }

        return selected;
    }

    private static void Return(int[] array)
    {
        if (array.Length > 0)
        {
            ArrayPool<int>.Shared.Return(array);
        }
    }

    private static void Return(ulong[] array)
    {
        if (array.Length > 0)
        {
            ArrayPool<ulong>.Shared.Return(array);
        }
    }
}
