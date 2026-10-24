using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Writing;

namespace Vorticity.Dataset;

/// <summary>
/// An object's sorted slices counted into its synopsis and written, one at a time: a slice's synopsis beside its write, on
/// a lane of <paramref name="lanes"/> of its own when one is free and once the writer says nothing its tables' chunks touch
/// the write does, before the write otherwise. Its tables are weighed against the data written before the slice whichever
/// runs first, so the object's bytes are the same either way.
/// </summary>
/// <remarks>
/// A slice's count took 0.37 s and its write 0.29 s for the design's minute of three million rows, one after the other on
/// the thread that writes it; side by side, the slice takes the longer of the two, and an hour's merge of the synopsis's
/// runs holds the write no more than the count does. The count reads the slice through records of an arena of its own
/// over the same buffers: the write cuts its chunks as nodes of the slice's arena meanwhile.
/// </remarks>
/// <param name="writer">The object's writer.</param>
/// <param name="synopsis">The object's synopsis; null for a write alone.</param>
/// <param name="lanes">The session's lanes the counts take one of; null to count each slice before its write.</param>
/// <param name="timings">Where the counts and the writes add their time; null for no count.</param>
internal sealed class SliceWriter(VortexFileWriter writer, SynopsisTables? synopsis, LaneBudget? lanes, WriteTimings? timings)
{
    // The records the synopsis reads a slice through while it is written, reset after each.
    private CanonicalArena? _view;

    /// <summary>
    /// Counts <paramref name="sorted"/>, the slice starting at <paramref name="start"/> in the order <paramref name="order"/>,
    /// with the paths <paramref name="counted"/> the synopsis counts the values of, and writes it.
    /// </summary>
    internal async ValueTask WriteAsync(SortedSlice sorted, long start, IReadOnlyList<int> order, IReadOnlyList<int> counted, CancellationToken cancellationToken)
    {
        if (synopsis is null)
        {
            long writing = Stopwatch.GetTimestamp();
            await writer.WriteBatchAsync(sorted.Batch, cancellationToken).ConfigureAwait(false);
            timings?.AddWrite(Stopwatch.GetTimestamp() - writing);
            return;
        }

        bool beside = writer.HoldSynopsisBudget();
        try
        {
            long entered = -1;
            if (!beside || lanes is null || !lanes.TryEnter(out entered))
            {
                long begin = Stopwatch.GetTimestamp();
                await synopsis.AddSliceAsync(sorted.Batch, start, order, sorted.Codes, sorted.PathColumns, counted, sorted.Packed, cancellationToken).ConfigureAwait(false);
                long counting = Stopwatch.GetTimestamp();
                await writer.WriteBatchAsync(sorted.Batch, cancellationToken).ConfigureAwait(false);
                timings?.AddSynopsis(counting - begin);
                timings?.AddWrite(Stopwatch.GetTimestamp() - counting);
                return;
            }

            await BesideAsync(sorted, start, order, counted, entered, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writer.ReleaseSynopsisBudget();
        }
    }

    /// <summary>The slice counted on lane <paramref name="entered"/>, which it gives back, while this thread writes it.</summary>
    private async ValueTask BesideAsync(SortedSlice sorted, long start, IReadOnlyList<int> order, IReadOnlyList<int> counted, long entered, CancellationToken cancellationToken)
    {
        CanonicalArena view = _view ??= new CanonicalArena();
        CanonicalArena arena = sorted.Batch.Arena;
        int root = view.ReferenceFrom(arena, sorted.Batch.RootIndex);
        int[] paths = new int[sorted.PathColumns.Count];
        for (int p = 0; p < paths.Length; p++)
        {
            paths[p] = sorted.PathColumns[p] < 0 ? -1 : view.ReferenceFrom(arena, sorted.PathColumns[p]);
        }

        timings?.AddBeside();
        SynopsisTables tables = synopsis!;
        LaneBudget budget = lanes!;
        Task count = Task.Run(
            async () =>
            {
                try
                {
                    long begin = Stopwatch.GetTimestamp();
                    using RecordBatch rows = new RecordBatch(view, root, 0);
                    await tables.AddSliceAsync(rows, start, order, sorted.Codes, paths, counted, sorted.Packed, cancellationToken).ConfigureAwait(false);
                    timings?.AddSynopsis(Stopwatch.GetTimestamp() - begin);
                }
                finally
                {
                    budget.Exit(entered);
                }
            },
            CancellationToken.None);

        try
        {
            long writing = Stopwatch.GetTimestamp();
            await writer.WriteBatchAsync(sorted.Batch, cancellationToken).ConfigureAwait(false);
            long written = Stopwatch.GetTimestamp();
            timings?.AddWrite(written - writing);
            await count.ConfigureAwait(false);
            timings?.AddLag(Stopwatch.GetTimestamp() - written);
        }
        catch
        {
            // The count reads the slice's buffers, which go once its write ends: it ends first, what it threw dropped
            // when the write failed, the write's failure being the slice's.
            await count.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            throw;
        }
        finally
        {
            view.Reset();
        }
    }
}
