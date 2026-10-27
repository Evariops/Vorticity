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
    /// object when there is no filter and, under a range on the clustering key, an object whose
    /// summary bounds lie wholly inside it and whose key holds no null; any other object is counted
    /// by its own file's count, from statistics, zone maps and indexes before any decode.
    /// </summary>
    public async ValueTask<long> CountAsync(CancellationToken cancellationToken = default)
    {
        long rows = 0;
        KeyRange? range = CountRange();
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
    public async ValueTask<bool> AnyAsync(CancellationToken cancellationToken = default)
    {
        KeyRange? range = CountRange();
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
    private async ValueTask<FilterLiteral> ExtremeAsync(
        string path, bool wantMin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        FieldMask projection = new FieldMaskBuilder().Include(ToolPaths.Resolve(_version.Schema.Columns, path)).Build();
        FilterLiteral best = FilterLiteral.Null;
        await foreach (PositionedObject held in WalkAsync(cancellationToken).ConfigureAwait(false))
        {
            if (Covered(held) == 0)
            {
                continue;
            }

            if (_summaries && Loses(held.Entry.Summaries, path, best, wantMin))
            {
                if (_metrics is { } skipped)
                {
                    skipped.ObjectsSkipped++;
                }

                continue;
            }

            if (_summaries && _filter is null && Whole(held) && Exact(held.Entry.Summaries, path, wantMin, out FilterLiteral bound))
            {
                if (Beats(bound, best, wantMin))
                {
                    best = bound;
                }

                RecordCounted();
                continue;
            }

            ObjectLease lease = await RentAsync(held, cancellationToken).ConfigureAwait(false);
            await using (lease.ConfigureAwait(false))
            {
                RecordOpen(lease);
                FilterLiteral candidate = await ObjectExtremeAsync(lease.File, held, ColumnsOf(lease, held.Entry), path, projection, wantMin, cancellationToken)
                    .ConfigureAwait(false);
                if (candidate.Kind != FilterLiteralKind.Null && Beats(candidate, best, wantMin))
                {
                    best = candidate;
                }
            }
        }

        return best;
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
    /// count with them left out, cheapest proof first, in one pass. A take of the dataset's rows
    /// names live rows only.
    /// </summary>
    private async ValueTask<long> LiveCountAsync(VortexFile file, PositionedObject held, VortexExpr filter, CancellationToken cancellationToken)
    {
        ScanBuilder scan = Of(file, held, filter, null, canonical: false);
        return _take is null && held.Entry.HasDeletions
            ? await scan.CountExcludingAsync(held.Entry.Deletions, cancellationToken).ConfigureAwait(false)
            : await scan.CountAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether <paramref name="filter"/> keeps a live row of an object, the first one found answering.</summary>
    private async ValueTask<bool> LiveAnyAsync(VortexFile file, PositionedObject held, VortexExpr filter, CancellationToken cancellationToken)
    {
        ScanBuilder scan = Of(file, held, filter, null, canonical: false);
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

        DType column = ClusteringKey.Resolve(file.DType, named);
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
