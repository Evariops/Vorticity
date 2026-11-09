using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Keys;
using Vorticity.Layouts;
using Vorticity.RowEncoding;
using Vorticity.Scanning;
using Vorticity.Types;

namespace Vorticity.Dataset;

internal sealed partial class DatasetScanBuilder
{
    /// <summary>
    /// The rows the scan selects, without materialising them, cheapest proof first: an object whose
    /// covered rows are all kept is counted from its entry without being opened, which is every
    /// object when there is no filter and, under a range on the sort key, an object whose
    /// summary bounds lie wholly inside it and whose key holds no null; any other object is counted
    /// by its own file's count, from statistics, zone maps and indexes before any decode.
    /// </summary>
    /// <remarks>
    /// Under a degree of parallelism above one, as many objects as the degree are counted at once (<see cref="FanOutAsync"/>):
    /// a sum does not depend on the order its parts come in. Objects counted one after the other left every lane but one idle.
    /// </remarks>
    public async ValueTask<long> CountAsync(CancellationToken cancellationToken = default)
    {
        long rows = 0;
        KeyRange? range = CountRange();
        if (ScanDegree > 1)
        {
            await FanOutAsync(
                held =>
                {
                    long covered = Covered(held);
                    if (covered == 0)
                    {
                        return Visit.Pass;
                    }

                    if (!Settled(held, range))
                    {
                        return Visit.Open;
                    }

                    Interlocked.Add(ref rows, covered);
                    RecordCounted();
                    return Visit.Pass;
                },
                async (held, stop) =>
                {
                    ObjectLease lease = await RentAsync(held, stop).ConfigureAwait(false);
                    await using (lease.ConfigureAwait(false))
                    {
                        RecordOpen(lease);
                        long counted = await CountOfAsync(lease.File, held, PartOf(ColumnsOf(lease, held.Entry), FieldMask.Empty), stopAtOne: false, stop)
                            .ConfigureAwait(false);
                        Interlocked.Add(ref rows, counted);
                    }

                    return false;
                },
                null,
                cancellationToken).ConfigureAwait(false);
            return rows;
        }

        await foreach (PositionedObject held in WalkAsync(cancellationToken, granules: true).ConfigureAwait(false))
        {
            long covered = Covered(held);
            if (covered == 0)
            {
                continue;
            }

            if (Settled(held, range))
            {
                rows += covered;
                RecordCounted();
                continue;
            }

            ObjectLease lease = await RentAsync(held, cancellationToken).ConfigureAwait(false);
            await using (lease.ConfigureAwait(false))
            {
                RecordOpen(lease);
                rows += await CountOfAsync(lease.File, held, PartOf(ColumnsOf(lease, held.Entry), FieldMask.Empty), stopAtOne: false, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return rows;
    }

    /// <summary>
    /// Whether the scan would return at least one row; the first object that holds one answers for
    /// the dataset, and one whose entry settles it answers without being opened.
    /// </summary>
    /// <remarks>
    /// Under a degree of parallelism above one, as many objects as the degree are asked at once, and the first that holds a
    /// row stops the others (<see cref="FanOutAsync"/>).
    /// </remarks>
    public async ValueTask<bool> AnyAsync(CancellationToken cancellationToken = default)
    {
        KeyRange? range = CountRange();
        if (ScanDegree > 1)
        {
            return await FanOutAsync(
                held =>
                {
                    if (Covered(held) == 0)
                    {
                        return Visit.Pass;
                    }

                    if (!Settled(held, range))
                    {
                        return Visit.Open;
                    }

                    RecordCounted();
                    return Visit.Stop;
                },
                async (held, stop) =>
                {
                    ObjectLease lease = await RentAsync(held, stop).ConfigureAwait(false);
                    await using (lease.ConfigureAwait(false))
                    {
                        RecordOpen(lease);
                        return await CountOfAsync(lease.File, held, PartOf(ColumnsOf(lease, held.Entry), FieldMask.Empty), stopAtOne: true, stop)
                            .ConfigureAwait(false) > 0;
                    }
                },
                null,
                cancellationToken).ConfigureAwait(false);
        }

        await foreach (PositionedObject held in WalkAsync(cancellationToken, granules: true).ConfigureAwait(false))
        {
            if (Covered(held) == 0)
            {
                continue;
            }

            if (Settled(held, range))
            {
                RecordCounted();
                return true;
            }

            ObjectLease lease = await RentAsync(held, cancellationToken).ConfigureAwait(false);
            await using (lease.ConfigureAwait(false))
            {
                RecordOpen(lease);
                if (await CountOfAsync(lease.File, held, PartOf(ColumnsOf(lease, held.Entry), FieldMask.Empty), stopAtOne: true, cancellationToken)
                    .ConfigureAwait(false) > 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The smallest non-null value of a column among the rows the scan would return, or
    /// <see cref="FilterLiteral.Null"/> when there is none.
    /// </summary>
    public ValueTask<FilterLiteral> MinAsync(string path, CancellationToken cancellationToken = default) =>
        ExtremeAsync(path, wantMin: true, cancellationToken);

    /// <summary>
    /// The largest non-null value of a column among the rows the scan would return, or
    /// <see cref="FilterLiteral.Null"/> when there is none.
    /// </summary>
    public ValueTask<FilterLiteral> MaxAsync(string path, CancellationToken cancellationToken = default) =>
        ExtremeAsync(path, wantMin: false, cancellationToken);

    /// <summary>
    /// The extreme over the objects, each one skipped when its own summary cannot beat the best so
    /// far, and taken from its summary without being opened when that summary is exact and covers
    /// every row the scan keeps in it. A summary's min is at or below the object's true minimum, so
    /// a summary min that already loses cannot hide a winner behind it.
    /// </summary>
    /// <remarks>
    /// The objects are visited best summary first: the smallest lower bound first for a minimum, the largest upper bound
    /// first for a maximum, those whose summary says nothing of the column last. The first object opened is then the one
    /// likeliest to hold the answer, and the value it gives refutes the others by their summaries alone, where in key order
    /// every object of a column rising with the key beat the one before it and was opened. Under a degree of parallelism
    /// above one, as many objects as the degree are opened at once in that order, each asked again whether it loses as its
    /// turn comes (<see cref="FanOutAsync"/>).
    /// </remarks>
    private async ValueTask<FilterLiteral> ExtremeAsync(
        string path, bool wantMin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        FieldMask projection = new FieldMaskBuilder().Include(ToolPaths.Resolve(_version.Schema.Columns, path)).Build();
        List<PositionedObject> objects = [];
        await foreach (PositionedObject held in WalkAsync(cancellationToken).ConfigureAwait(false))
        {
            if (Covered(held) > 0)
            {
                objects.Add(held);
            }
        }

        if (_summaries)
        {
            BestFirst(objects, path, wantMin);
        }

        Lock gate = new Lock();
        FilterLiteral best = FilterLiteral.Null;

        // Whether the object is answered without being opened: its summary loses to the best so far, or gives its extreme.
        bool Settles(PositionedObject held)
        {
            lock (gate)
            {
                if (_summaries && Loses(held.Entry.Summaries, path, best, wantMin))
                {
                    RecordSkipped();
                    return true;
                }

                if (_summaries && _filter is null && Whole(held) && Exact(held.Entry.Summaries, path, wantMin, out FilterLiteral bound))
                {
                    if (Beats(bound, best, wantMin))
                    {
                        best = bound;
                    }

                    RecordCounted();
                    return true;
                }

                return false;
            }
        }

        async ValueTask<bool> OpenAsync(PositionedObject held, CancellationToken stop)
        {
            // Asked again as its turn comes: an object opened beside it may have set a bound it loses to.
            if (Settles(held))
            {
                return false;
            }

            ObjectLease lease = await RentAsync(held, stop).ConfigureAwait(false);
            await using (lease.ConfigureAwait(false))
            {
                RecordOpen(lease);
                FilterLiteral candidate = await ObjectExtremeAsync(lease.File, held, ColumnsOf(lease, held.Entry), path, projection, wantMin, stop)
                    .ConfigureAwait(false);
                lock (gate)
                {
                    if (candidate.Kind != FilterLiteralKind.Null && Beats(candidate, best, wantMin))
                    {
                        best = candidate;
                    }
                }
            }

            return false;
        }

        if (ScanDegree > 1 && objects.Count > 2)
        {
            // The best summary's object first, alone: the bound it sets refutes most of the others before a lane opens them,
            // where the first lanes all opened theirs before any bound was known.
            await OpenAsync(objects[0], cancellationToken).ConfigureAwait(false);
            await FanOutAsync(held => Settles(held) ? Visit.Pass : Visit.Open, OpenAsync, objects.GetRange(1, objects.Count - 1), cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            foreach (PositionedObject held in objects)
            {
                await OpenAsync(held, cancellationToken).ConfigureAwait(false);
            }
        }

        return best;
    }

    /// <summary>
    /// Orders <paramref name="objects"/> best summary first for the extreme of <paramref name="path"/>: by their summary's
    /// lower bound rising for a minimum, its upper bound falling for a maximum; bounds of one comparison kind side by side,
    /// and the objects whose summary bounds nothing last, each group in the walk's order.
    /// </summary>
    private static void BestFirst(List<PositionedObject> objects, string path, bool wantMin)
    {
        if (objects.Count < 2)
        {
            return;
        }

        (FilterLiteral Bound, int Order)[] keys = new (FilterLiteral, int)[objects.Count];
        for (int i = 0; i < keys.Length; i++)
        {
            FilterLiteral bound = objects[i].Entry.Summaries.TryGet(path, out ColumnSummary column) && (wantMin ? column.HasMin : column.HasMax)
                ? (wantMin ? column.Min : column.Max)
                : FilterLiteral.Null;
            keys[i] = (bound, i);
        }

        PositionedObject[] held = [.. objects];
        Array.Sort(keys, (left, right) =>
        {
            bool leftBounds = left.Bound.Kind != FilterLiteralKind.Null;
            bool rightBounds = right.Bound.Kind != FilterLiteralKind.Null;
            if (leftBounds != rightBounds)
            {
                return leftBounds ? -1 : 1;
            }

            if (leftBounds && left.Bound.Kind != right.Bound.Kind)
            {
                return left.Bound.Kind.CompareTo(right.Bound.Kind);
            }

            int order = leftBounds ? KeyCursor.Compare(left.Bound, right.Bound) : 0;
            return order != 0 ? (wantMin ? order : -order) : left.Order.CompareTo(right.Order);
        });

        for (int i = 0; i < keys.Length; i++)
        {
            objects[i] = held[keys[i].Order];
        }
    }

    /// <summary>What a fanned-out object visit does with an object the walk keeps.</summary>
    private enum Visit
    {
        /// <summary>Nothing more: the object is answered, or holds no row the scan keeps.</summary>
        Pass,

        /// <summary>The object is answered by a lane.</summary>
        Open,

        /// <summary>The question is answered: the walk stops, and the objects in flight are stopped.</summary>
        Stop,
    }

    /// <summary>
    /// Has <paramref name="answer"/> answer each object <paramref name="visit"/> sends to a lane, as many at once as the
    /// scan's degree of parallelism, in the order of <paramref name="objects"/>, or of the walk when it is null: for an
    /// answer whose parts may come in any order. Once <paramref name="visit"/> says <see cref="Visit.Stop"/>, or an answer
    /// returns true, the walk stops and the objects in flight are cancelled. Whether one did. The first failure of an
    /// object is thrown once every lane has stopped, unless an answer stopped the question first.
    /// </summary>
    private async ValueTask<bool> FanOutAsync(
        Func<PositionedObject, Visit> visit,
        Func<PositionedObject, CancellationToken, ValueTask<bool>> answer,
        IReadOnlyList<PositionedObject>? objects,
        CancellationToken cancellationToken)
    {
        using SemaphoreSlim slots = new SemaphoreSlim(ScanDegree);
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        FanOutState state = new FanOutState();
        List<Task> running = [];
        Exception? walkFailure = null;
        try
        {
            IAsyncEnumerable<PositionedObject> walk = objects is null ? WalkAsync(stop.Token, granules: true) : ListedAsync(objects);
            await foreach (PositionedObject held in walk.ConfigureAwait(false))
            {
                Visit next = visit(held);
                if (next == Visit.Stop)
                {
                    state.Stop();
                    break;
                }

                if (next == Visit.Open && !state.Ended)
                {
                    await slots.WaitAsync(stop.Token).ConfigureAwait(false);
                    running.Add(Task.Run(() => LaneAsync(held), CancellationToken.None));
                }

                if (state.Ended)
                {
                    break;
                }
            }
        }
        catch (Exception failure)
        {
            walkFailure = failure;
        }

        if (state.Ended || walkFailure is not null)
        {
            await stop.CancelAsync().ConfigureAwait(false);
        }

        // No lane throws: each keeps its failure in the state.
        await Task.WhenAll(running).ConfigureAwait(false);
        if (state.Stopped)
        {
            return true;
        }

        if (state.Failure is { } failed)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failed);
        }

        if (walkFailure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(walkFailure);
        }

        return false;

        async Task LaneAsync(PositionedObject held)
        {
            try
            {
                if (await answer(held, stop.Token).ConfigureAwait(false))
                {
                    state.Stop();
                    await stop.CancelAsync().ConfigureAwait(false);
                }
            }
            catch (Exception failure)
            {
                // A lane cancelled because the question was answered fails for nothing; any other failure is the question's.
                if (state.Fail(failure))
                {
                    await stop.CancelAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                slots.Release();
            }
        }

        static async IAsyncEnumerable<PositionedObject> ListedAsync(IReadOnlyList<PositionedObject> listed)
        {
            foreach (PositionedObject held in listed)
            {
                yield return held;
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    /// <summary>How a fan-out ended: stopped by an answer, or failed by a lane; at most one of the two, the first.</summary>
    private sealed class FanOutState
    {
        private int _ended;

        /// <summary>Whether an answer stopped the question.</summary>
        internal bool Stopped => Volatile.Read(ref _ended) == 1;

        /// <summary>Whether the fan-out stopped or failed.</summary>
        internal bool Ended => Volatile.Read(ref _ended) != 0;

        /// <summary>The first failure of a lane, when the question failed.</summary>
        internal Exception? Failure { get; private set; }

        /// <summary>Records that the question is answered, unless a failure came first.</summary>
        internal void Stop() => Interlocked.CompareExchange(ref _ended, 1, 0);

        /// <summary>Records a lane's failure; false when the fan-out had already ended, the failure then being for nothing.</summary>
        internal bool Fail(Exception failure)
        {
            if (Interlocked.CompareExchange(ref _ended, 2, 0) != 0)
            {
                return false;
            }

            Failure = failure;
            return true;
        }
    }

    /// <summary>Whether an object's summary proves it cannot hold a better value than the best.</summary>
    private static bool Loses(ObjectSummaries summaries, string path, FilterLiteral best, bool wantMin)
    {
        if (best.Kind == FilterLiteralKind.Null || !summaries.TryGet(path, out ColumnSummary column))
        {
            return false;
        }

        if (wantMin)
        {
            return column.HasMin && Comparable(column.Min, best) && KeyCursor.Compare(column.Min, best) >= 0;
        }

        return column.HasMax && Comparable(column.Max, best) && KeyCursor.Compare(column.Max, best) <= 0;
    }

    /// <summary>The true extreme an object's summary records, when it records one.</summary>
    private static bool Exact(ObjectSummaries summaries, string path, bool wantMin, out FilterLiteral bound)
    {
        bound = FilterLiteral.Null;
        if (!summaries.TryGet(path, out ColumnSummary column) || !column.IsExact)
        {
            return false;
        }

        bound = wantMin ? column.Min : column.Max;
        return (wantMin ? column.HasMin : column.HasMax) && bound.Kind != FilterLiteralKind.Null;
    }

    private static bool Beats(FilterLiteral candidate, FilterLiteral best, bool wantMin) =>
        best.Kind == FilterLiteralKind.Null
        || !Comparable(candidate, best)
        || (wantMin ? KeyCursor.Compare(candidate, best) < 0 : KeyCursor.Compare(candidate, best) > 0);

    /// <summary>Whether two literals are of one comparison kind, so that an order holds between them.</summary>
    private static bool Comparable(FilterLiteral left, FilterLiteral right) =>
        left.Kind == right.Kind && left.Kind != FilterLiteralKind.Null;

    /// <summary>
    /// The rows of an object its part of the read keeps; with <paramref name="stopAtOne"/>, 1 at the
    /// first. None when the filter keeps none, every row the scan covers when it keeps all, the
    /// object's own count when the filter goes to its scan, which leaves its marked rows out in the
    /// same pass, and the rows selected on its reshaped batches otherwise.
    /// </summary>
    private async ValueTask<long> CountOfAsync(
        VortexFile file, PositionedObject held, ObjectPart part, bool stopAtOne, CancellationToken cancellationToken)
    {
        switch (part.Fate)
        {
            case FilterFate.None:
                return 0;
            case FilterFate.Every:
                return stopAtOne ? Math.Min(Covered(held), 1) : Covered(held);
            case FilterFate.Pushed:
                if (part.Mapped is null)
                {
                    // The filter's columns read by the granules of the part counted, and of its live blocks, as a read of its
                    // rows takes them.
                    await GeometryAsync(file, held, null, cancellationToken, judged: true).ConfigureAwait(false);
                }

                return stopAtOne
                    ? await LiveAnyAsync(file, held, part.Pushed!, cancellationToken).ConfigureAwait(false) ? 1 : 0
                    : await LiveCountAsync(file, held, part.Pushed!, cancellationToken).ConfigureAwait(false);
            default:
                long rows = 0;
                await foreach (RecordBatch batch in BatchesOfAsync(file, held, part, 0, compact: false, cancellationToken)!.ConfigureAwait(false))
                {
                    rows += batch.SelectedRows;
                    if (stopAtOne && rows > 0)
                    {
                        break;
                    }
                }

                return rows;
        }
    }

    /// <summary>
    /// The extreme of a column among the rows an object keeps: none when it lacks the column; the
    /// object's own extreme, under the column's name there, when the filter goes to its scan and no
    /// row is marked; its live extreme when rows are; and the extreme of its reshaped batches when
    /// the filter is evaluated on them.
    /// </summary>
    private async ValueTask<FilterLiteral> ObjectExtremeAsync(
        VortexFile file, PositionedObject held, ObjectColumns? columns, string path, FieldMask projection, bool wantMin, CancellationToken cancellationToken)
    {
        string? named = columns is null ? path : columns.SourcePath(path);
        ObjectPart part = PartOf(columns, projection);
        if (named is null || part.Fate == FilterFate.None)
        {
            return FilterLiteral.Null;
        }

        if (part.Fate == FilterFate.Reshaped)
        {
            return await ExtremeOfAsync(BatchesOfAsync(file, held, part, 0, compact: true, cancellationToken)!, path, wantMin).ConfigureAwait(false);
        }

        if (held.Entry.HasDeletions)
        {
            return await LiveExtremeAsync(file, held, part, named, path, wantMin, cancellationToken).ConfigureAwait(false);
        }

        ScanBuilder scan = Of(file, held, part.Pushed, null, canonical: false);
        return wantMin
            ? await scan.MinAsync(named, cancellationToken).ConfigureAwait(false)
            : await scan.MaxAsync(named, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The rows of an object <paramref name="filter"/> keeps, the deleted ones not counted: its own
    /// count with them left out, cheapest proof first, in one pass, from the granules that hold a live
    /// block where the geometry the count read says so. A take of the dataset's rows names live rows only.
    /// </summary>
    private async ValueTask<long> LiveCountAsync(VortexFile file, PositionedObject held, VortexExpr filter, CancellationToken cancellationToken)
    {
        ScanBuilder scan = Of(file, held, filter, null, canonical: false).WithGranules(GeometryOf(file));
        return _take is null && held.Entry.HasDeletions
            ? await scan.CountExcludingAsync(held.Entry.Deletions, cancellationToken).ConfigureAwait(false)
            : await scan.CountAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether <paramref name="filter"/> keeps a live row of an object, the first one found answering.</summary>
    private async ValueTask<bool> LiveAnyAsync(VortexFile file, PositionedObject held, VortexExpr filter, CancellationToken cancellationToken)
    {
        ScanBuilder scan = Of(file, held, filter, null, canonical: false).WithGranules(GeometryOf(file));
        return _take is null && held.Entry.HasDeletions
            ? await scan.AnyExcludingAsync(held.Entry.Deletions, cancellationToken).ConfigureAwait(false)
            : await scan.AnyAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The extreme of a column among an object's live rows, under the filter its part of the read
    /// gives its scan, the column <paramref name="named"/> as the object names it. A column the
    /// object's statistics say is sorted and holds no null nor NaN has it at its first or its last
    /// live row, when every row is kept: the object's own extreme while that row is the column's end,
    /// and that row's value, one row read, once a delete took the end. Otherwise the object's own
    /// extreme is the answer when a live row holds it, which a count of the rows equal to it settles;
    /// when every row holding it is gone, or the column is a float whose equality a count cannot
    /// settle, its live rows are read for it, as the version's columns, under <paramref name="path"/>.
    /// </summary>
    private async ValueTask<FilterLiteral> LiveExtremeAsync(
        VortexFile file, PositionedObject held, ObjectPart part, string named, string path, bool wantMin, CancellationToken cancellationToken)
    {
        if (part.Fate == FilterFate.Every && Whole(held) && SortedWithoutGaps(file, named))
        {
            ObjectEntry entry = held.Entry;
            long end = wantMin ? 0 : entry.PhysicalRows - 1;
            long live = entry.Deletions.Physical(wantMin ? 0 : entry.Rows - 1);
            ScanBuilder ends = Configure(file.ScanBuilder(), null, null, canonical: false);
            if (live != end)
            {
                ends.Take([live]);
            }

            return wantMin
                ? await ends.MinAsync(named, cancellationToken).ConfigureAwait(false)
                : await ends.MaxAsync(named, cancellationToken).ConfigureAwait(false);
        }

        DType column = DatasetKey.Resolve(file.DType, named);
        if (column.Kind is DTypeKind.Utf8 or DTypeKind.Binary or DTypeKind.Bool or DTypeKind.Decimal
            || (column.Kind == DTypeKind.Primitive && !column.PType.IsFloat()))
        {
            ScanBuilder scan = Of(file, held, part.Pushed, null, canonical: false);
            FilterLiteral found = wantMin
                ? await scan.MinAsync(named, cancellationToken).ConfigureAwait(false)
                : await scan.MaxAsync(named, cancellationToken).ConfigureAwait(false);
            if (found.Kind == FilterLiteralKind.Null)
            {
                return found;
            }

            VortexExpr equal = Expr.Eq(Expr.Field(named), Expr.Literal(found));
            if (await LiveCountAsync(file, held, part.Pushed is null ? equal : Expr.And(part.Pushed, equal), cancellationToken).ConfigureAwait(false) > 0)
            {
                return found;
            }
        }

        return await ExtremeOfAsync(BatchesOfAsync(file, held, part, 0, compact: true, cancellationToken)!, path, wantMin).ConfigureAwait(false);
    }

    /// <summary>Whether a file's statistics say a top-level column is sorted, and holds no null and no NaN.</summary>
    private static bool SortedWithoutGaps(VortexFile file, string path)
    {
        if (!KeyCursorBuilder.StatedSorted(file, path))
        {
            return false;
        }

        int field = file.DType.IndexOfField(path);
        FieldStatistics statistics = file.FileStatistics.GetField(field);
        bool floats = file.DType.GetField(field) is { Kind: DTypeKind.Primitive } column && column.PType.IsFloat();
        return statistics.TryGetStoredNullCount(out ulong nulls) && nulls == 0
            && (!floats || (statistics.TryGetNanCount(out ulong nans) && nans == 0));
    }

    /// <summary>The extreme of a column over a stream of batches that hold it.</summary>
    private static async ValueTask<FilterLiteral> ExtremeOfAsync(IAsyncEnumerable<RecordBatch> batches, string path, bool wantMin)
    {
        FilterLiteral best = FilterLiteral.Null;
        await foreach (RecordBatch batch in batches.ConfigureAwait(false))
        {
            int node = ColumnPath.NodeOf(batch, path);
            if (!Extremes.TryFind(batch.Arena, node, default, listed: false, wantMin, out int row))
            {
                continue;
            }

            bool numeric = batch.Arena.GetNode(ComparisonKernels.Unwrap(batch.Arena, node)).DType.Kind == DTypeKind.Decimal;
            bool read = numeric
                ? LiteralReader.TryReadDecimal(batch.Arena, node, row, out FilterLiteral value)
                : LiteralReader.TryRead(batch.Arena, node, row, out value);
            if (read && Beats(value, best, wantMin))
            {
                best = value;
            }
        }

        return best;
    }
}
