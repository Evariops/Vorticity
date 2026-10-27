using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Layouts;
using Vorticity.Scanning;

namespace Vorticity;

/// <summary>
/// A window of the rows in the order of a column, read late (spec G, 2): the places of its best rows first, from the filter
/// and the column alone, then the rows at those places, sorted back by the column. What a projection ordered and bounded
/// reads from a source that says so (<see cref="IRowsCapability.ReadsTopLate"/>), in place of a walk of a key source.
/// </summary>
internal static class TopRows
{
    /// <summary>The best <paramref name="keep"/> rows <paramref name="spec"/> keeps in the order it asks, in that order.</summary>
    internal static async IAsyncEnumerable<RecordBatch> BatchesAsync(
        ScanSource source, ScanSpec spec, long keep, ScanCounters metrics, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string path = spec.OrderPath!;
        ScanSpec unordered = spec with { OrderPath = null, Descending = false };
        long[] places = await source.Rows.TopPositionsAsync(unordered, path, spec.Descending, keep, metrics, cancellationToken).ConfigureAwait(false)
            ?? await PlacesAsync(source, unordered, path, spec.Descending, keep, metrics, cancellationToken).ConfigureAwait(false);
        if (places.Length == 0)
        {
            yield break;
        }

        // The rows at those places, the columns the projection reads and the order's: sorted back by the order, a tie in the
        // order of the places, which the take brings the rows in.
        FieldMask mask = new FieldMaskBuilder().Include(spec.Projection ?? FieldMask.All).Include(ToolPaths.Resolve(source.Schema, path)).Build();
        VortexSchema schema = mask.IsAll ? source.Schema : ToolPaths.Project(source.Schema, mask);
        ScanSpec take = unordered with
        {
            Filter = null,
            MatchesNothing = false,
            Rows = null,
            Take = places,
            Projection = mask,
            PositionsUnread = false,
            KeepEncodings = false,
            SinkDecodes = false,
            Analysis = null,
            Answering = null,
            Options = spec.Options with { Compact = true },
        };

        int[] delivered = new int[schema.Count];
        for (int c = 0; c < delivered.Length; c++)
        {
            delivered[c] = c;
        }

        using QueryMemory memory = new QueryMemory(source.Session.Options.MemoryBudget ?? QueryMemoryBudget.Process);
        SortKey key = new SortKey(new ColumnFieldExpr(path), TypeOf(source, path), spec.Descending);
        ExternalSort sort = new ExternalSort(source.Session, schema, [key], delivered, memory, GroupBatches.BatchRows);
        await using (sort.ConfigureAwait(false))
        {
            await foreach (RecordBatch batch in source.Rows.BatchesAsync(take, metrics).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                await sort.AddAsync(batch, cancellationToken).ConfigureAwait(false);
            }

            IAsyncEnumerator<RecordBatch> sorted = await sort.SortedAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                while (await sorted.MoveNextAsync().ConfigureAwait(false))
                {
                    yield return sorted.Current;
                }
            }
            finally
            {
                await sorted.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The places of the best <paramref name="keep"/> rows <paramref name="spec"/> keeps in the order of
    /// <paramref name="path"/>, ascending: one read of the filter and the column, its batches numbered.
    /// </summary>
    internal static async ValueTask<long[]> PlacesAsync(
        ScanSource source, ScanSpec spec, string path, bool descending, long keep, ScanCounters metrics, CancellationToken cancellationToken)
    {
        using QueryMemory memory = new QueryMemory(source.Session.Options.MemoryBudget ?? QueryMemoryBudget.Process);
        using TopKeep top = new TopKeep(source.Session, TypeOf(source, path), descending, Kept(keep), memory);
        await FeedAsync(source, spec, path, top, metrics, cancellationToken).ConfigureAwait(false);
        return top.Places(cancellationToken);
    }

    /// <summary>
    /// Gives <paramref name="top"/> the rows <paramref name="spec"/> keeps of <paramref name="source"/>: the column of the
    /// order read alone beside the filter, the batches whole with their selection, which number their rows.
    /// </summary>
    internal static async ValueTask FeedAsync(
        ScanSource source, ScanSpec spec, string path, TopKeep top, ScanCounters metrics, CancellationToken cancellationToken)
    {
        ScanSpec pass = spec with
        {
            Projection = new FieldMaskBuilder().Include(ToolPaths.Resolve(source.Schema, path)).Build(),
            Take = null,
            PositionsUnread = false,
            KeepEncodings = false,
            SinkDecodes = false,
            Answering = null,
            Options = spec.Options with { Compact = false },
        };

        FieldExpr column = new ColumnFieldExpr(path);
        await foreach (RecordBatch batch in source.Rows.BatchesAsync(pass, metrics).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            int rows = batch.RowCount;
            int node = FilterEvaluator.Resolve(batch.Arena, batch.RootIndex, column, rows);
            top.Add(batch.Arena, node, rows, batch.SelectionWords, batch.StartRow, cancellationToken);
        }
    }

    /// <summary>The type of the column at <paramref name="path"/>.</summary>
    internal static VortexType TypeOf(ScanSource source, string path) => ToolPaths.TypeOf(source.Schema, path.Split('.'), path);

    /// <summary>The rows a top keeps: the window's, within what an array holds.</summary>
    internal static int Kept(long keep) => (int)Math.Clamp(keep, 1, Array.MaxLength / 4);
}
