using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Expressions;

namespace Vorticity.Dataset;

/// <summary>
/// The rows of an append to a dataset with partition columns, split as they come into an object per value of
/// them: every object then holds one partition, so that no job merges two and what dies together is written
/// together.
/// </summary>
/// <remarks>
/// A batch whose rows hold one value goes whole to its object, after one evaluation of the value over the batch.
/// Otherwise each value met is selected in turn among the rows left, an evaluation each, and the rows it selects
/// go to its object through a view of the batch, which stays as the caller gave it.
/// </remarks>
internal sealed class PartitionedAppend : IAsyncDisposable
{
    private readonly VortexDataset _dataset;
    private readonly ClusteringKey _key;
    private readonly int _bound;
    private readonly List<(FilterLiteral[] Values, ObjectDraft Draft)> _drafts = [];

    internal PartitionedAppend(VortexDataset dataset, ClusteringKey key, int bound)
    {
        _dataset = dataset;
        _key = key;
        _bound = bound;
    }

    /// <summary>The objects being written, one per partition value met, in the order they were met.</summary>
    internal IEnumerable<ObjectDraft> Drafts
    {
        get
        {
            foreach ((_, ObjectDraft draft) in _drafts)
            {
                yield return draft;
            }
        }
    }

    /// <summary>Writes the rows of <paramref name="batch"/> its selection keeps, each to the object of its partition value.</summary>
    /// <exception cref="ArgumentException">The rows hold more partition values than the dataset allows an append.</exception>
    internal async ValueTask WriteAsync(RecordBatch batch, CancellationToken cancellationToken)
    {
        int rows = batch.RowCount;
        if (rows == 0 || batch.SelectedRows == 0)
        {
            return;
        }

        int words = (rows + 63) >> 6;
        ulong[] left = ArrayPool<ulong>.Shared.Rent(words);
        byte[] states = ArrayPool<byte>.Shared.Rent(rows);
        try
        {
            ReadOnlySpan<ulong> selected = batch.SelectionWords;
            for (int w = 0; w < words; w++)
            {
                left[w] = selected.IsEmpty ? ulong.MaxValue : selected[w];
            }

            if ((rows & 63) != 0)
            {
                left[words - 1] &= (1UL << (rows & 63)) - 1;
            }

            int[] nodes = new int[_key.PartitionColumns];
            for (int i = 0; i < nodes.Length; i++)
            {
                nodes[i] = ColumnPath.NodeOf(batch, _key.PartitionPaths[i]);
            }

            int remaining = batch.SelectedRows;
            while (remaining > 0)
            {
                // The value of the first row left, and the rows left that hold it.
                int first = First(left.AsSpan(0, words));
                FilterLiteral[] values = new FilterLiteral[nodes.Length];
                VortexExpr? filter = null;
                for (int i = 0; i < nodes.Length; i++)
                {
                    // A dataset refuses partition columns that may hold a null or a NaN, the values no equality routes.
                    if (!LiteralReader.TryRead(batch.Arena, nodes[i], first, out values[i]))
                    {
                        throw new InvalidOperationException(
                            $"Row {batch.StartRow + first} holds no value of the partition column '{_key.PartitionPaths[i]}' an equality can route.");
                    }

                    ComparisonExpr equal = Expr.Eq(Expr.Field(_key.PartitionPaths[i]), Expr.Literal(values[i]));
                    filter = filter is null ? equal : Expr.And(filter, equal);
                }

                FilterEvaluator.Evaluate(filter!, batch.Arena, batch.RootIndex, rows, states.AsSpan(0, rows));
                VortexBuffer selection = batch.Arena.Allocate(words * sizeof(ulong), sizeof(ulong), out Span<byte> bytes);
                bytes.Clear();
                Span<ulong> taken = MemoryMarshal.Cast<byte, ulong>(bytes);
                int count = 0;
                for (int row = 0; row < rows; row++)
                {
                    ulong bit = 1UL << (row & 63);
                    if ((left[row >> 6] & bit) != 0 && states[row] == Trilean.True)
                    {
                        taken[row >> 6] |= bit;
                        left[row >> 6] &= ~bit;
                        count++;
                    }
                }

                // Every value of a partition column equals itself, so the first row is always among those taken; one that did
                // not would route nothing, and route each of its rows alone at a pass over the batch each.
                if (count == 0)
                {
                    throw new InvalidOperationException(
                        $"Row {batch.StartRow + first}'s partition value equals no row, its own included: no object can hold it.");
                }

                remaining -= count;
                ObjectDraft draft = DraftOf(values);
                if (count == rows)
                {
                    await draft.Writer.WriteBatchAsync(batch, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                using RecordBatch view = batch.Rebased(batch.StartRow);
                view.Select(selection, count);
                await draft.Writer.WriteAsync(view, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(states);
            ArrayPool<ulong>.Shared.Return(left);
        }
    }

    /// <summary>The object of a partition value, started the first time the value is met.</summary>
    private ObjectDraft DraftOf(FilterLiteral[] values)
    {
        foreach ((FilterLiteral[] held, ObjectDraft draft) in _drafts)
        {
            if (held.AsSpan().SequenceEqual(values))
            {
                return draft;
            }
        }

        if (_drafts.Count == _bound)
        {
            throw new ArgumentException(
                $"An append holds more than {_bound} values of the partition columns ({string.Join(", ", _key.PartitionPaths.ToArray())}), " +
                "each an object of its own: partition columns have few values (DatasetOptions.MaxPartitionValues).");
        }

        ObjectDraft started = _dataset.StartObject();
        started.Partition = values;
        _drafts.Add((values, started));
        return started;
    }

    private static int First(ReadOnlySpan<ulong> words)
    {
        for (int w = 0; w < words.Length; w++)
        {
            if (words[w] != 0)
            {
                return (w << 6) + BitOperations.TrailingZeroCount(words[w]);
            }
        }

        return -1;
    }

    /// <summary>Gives back every object not handed on, as an abandoned append does.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach ((_, ObjectDraft draft) in _drafts)
        {
            await draft.DisposeAsync().ConfigureAwait(false);
        }
    }
}
