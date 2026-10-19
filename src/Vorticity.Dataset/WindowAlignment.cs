using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.Scanning;
using Vorticity.Types;

namespace Vorticity.Dataset;

/// <summary>
/// A window on a dataset's instant grown to the bounds an aggregation's tables answer at
/// (<see cref="ScanOptions.AlignWindow"/>). Each end that falls inside an object moves out, in the object
/// of most rows it cuts, to the nearest bound whose side of the window that object's tables answer with no row:
/// its slice's when a table of slices holds the question's keys and measures, its hour's when only an hour's
/// does, the object's own otherwise. An end that cuts no object, or that no bound frees of rows, stays. Any other
/// object a moved end cuts, a few late or fresh rows whose slices fall elsewhere, reads its rows there, so the
/// answer is the exact answer of the window applied.
/// </summary>
/// <remarks>
/// The bounds come from the objects' entries alone, their summaries and their tables of contents, so the window
/// costs no read, and every operation of a scan over one version applies the same one.
/// </remarks>
internal static class WindowAlignment
{
    /// <summary>
    /// The filter with its window on <paramref name="instant"/> grown for <paramref name="request"/>, and that
    /// window's first and last instants; null when the filter bounds the instant other than by comparisons with
    /// instants, or not at all.
    /// </summary>
    internal static async ValueTask<(VortexExpr Filter, long Low, long High)?> OfAsync(
        DatasetSnapshot version, string instant, VortexExpr filter, StateRequest request, IReadOnlyList<string>? identifiers, CancellationToken cancellationToken)
    {
        long low = long.MinValue;
        long high = long.MaxValue;
        List<VortexExpr> others = [];
        foreach (VortexExpr conjunct in SynopsisAnswer.Conjuncts(filter))
        {
            HashSet<string> paths = new HashSet<string>(StringComparer.Ordinal);
            conjunct.CollectFields(paths);
            if (!paths.Contains(instant))
            {
                others.Add(conjunct);
                continue;
            }

            if (paths.Count != 1 || !SynopsisAnswer.Bound(conjunct, instant, ref low, ref high))
            {
                return null;
            }
        }

        if ((low == long.MinValue && high == long.MaxValue) || low > high)
        {
            return null;
        }

        // The objects each end cuts, the one of most rows first.
        (long Rows, SynopsisContents Contents, DType Schema, (long Least, long Greatest) Held)? starts = null;
        (long Rows, SynopsisContents Contents, DType Schema, (long Least, long Greatest) Held)? ends = null;
        await foreach (PositionedObject held in version.WalkAsync(null, 0, long.MaxValue, null, cancellationToken).ConfigureAwait(false))
        {
            ObjectEntry entry = held.Entry;
            if (!entry.Summaries.TryGet(instant, out ColumnSummary summary)
                || summary is not { HasMin: true, HasMax: true, Min.Kind: FilterLiteralKind.Signed, Max.Kind: FilterLiteralKind.Signed }
                || SynopsisContents.From(entry.Synopsis.Span) is not { } contents
                || SchemaOf(contents) is not { } schema)
            {
                continue;
            }

            long least = summary.Min.SignedValue;
            long greatest = summary.Max.SignedValue;
            if (low != long.MinValue && least < low && low <= greatest && entry.Rows > (starts?.Rows ?? -1))
            {
                starts = (entry.Rows, contents, schema, (least, greatest));
            }

            if (high != long.MaxValue && least <= high && high < greatest && entry.Rows > (ends?.Rows ?? -1))
            {
                ends = (entry.Rows, contents, schema, (least, greatest));
            }
        }

        // A bound moves no further than the instants the object holds: its tables answer a bucket by the rows it holds,
        // so a slice wider than the object, as a quiet hour's may be, still ends where the object's rows do, and the
        // window applied stays the data's.
        FieldExpr column = Expr.Field(instant);
        long start = low;
        if (starts is { } cut)
        {
            foreach (long bound in Bounds(cut.Contents.Slices, low, upper: false))
            {
                long at = Math.Max(bound, cut.Held.Least);
                if (Answers(cut.Contents, cut.Schema, cut.Held, instant, request, identifiers, others, Expr.Ge(column, Expr.Literal(FilterLiteral.From(at)))))
                {
                    start = at;
                    break;
                }
            }
        }

        long end = high;
        if (ends is { } last)
        {
            foreach (long bound in Bounds(last.Contents.Slices, high, upper: true))
            {
                long at = Math.Min(bound, last.Held.Greatest);
                if (Answers(last.Contents, last.Schema, last.Held, instant, request, identifiers, others, Expr.Le(column, Expr.Literal(FilterLiteral.From(at)))))
                {
                    end = at;
                    break;
                }
            }
        }

        VortexExpr? aligned = And(others);
        if (start != long.MinValue)
        {
            ComparisonExpr from = Expr.Ge(column, Expr.Literal(FilterLiteral.From(start)));
            aligned = aligned is null ? from : Expr.And(aligned, from);
        }

        if (end != long.MaxValue)
        {
            ComparisonExpr to = Expr.Le(column, Expr.Literal(FilterLiteral.From(end)));
            aligned = aligned is null ? to : Expr.And(aligned, to);
        }

        return (aligned!, start, end);
    }

    /// <summary>
    /// The bounds an end at <paramref name="instant"/> may move to in an object of <paramref name="table"/>, nearest
    /// first: the start of its slice, of its hour among the object's slices and of the object, or, for an
    /// <paramref name="upper"/> end, the last instant of each. None when the instant lies between slices.
    /// </summary>
    private static IEnumerable<long> Bounds(SliceTable table, long instant, bool upper)
    {
        IReadOnlyList<TimeSlice> slices = table.Slices;
        int at = SliceOf(slices, instant);
        if (at < 0)
        {
            yield break;
        }

        long hour = TimeSlicer.Floor(slices[at].Start, table.Unit, SliceWidth.Hour);
        int first = at;
        while (first > 0 && TimeSlicer.Floor(slices[first - 1].Start, table.Unit, SliceWidth.Hour) == hour)
        {
            first--;
        }

        int last = at;
        while (last + 1 < slices.Count && TimeSlicer.Floor(slices[last + 1].Start, table.Unit, SliceWidth.Hour) == hour)
        {
            last++;
        }

        long[] bounds = upper
            ? [slices[at].End - 1, slices[last].End - 1, slices[^1].End - 1]
            : [slices[at].Start, slices[first].Start, slices[0].Start];
        long previous = upper ? long.MinValue : long.MaxValue;
        foreach (long bound in bounds)
        {
            if (bound != previous)
            {
                yield return bound;
                previous = bound;
            }
        }
    }

    /// <summary>
    /// Whether the object answers <paramref name="request"/> from its tables, with no row at the window's end
    /// that <paramref name="side"/> sets, the other end left open.
    /// </summary>
    private static bool Answers(
        SynopsisContents contents, DType schema, (long, long) held, string instant, StateRequest request, IReadOnlyList<string>? identifiers,
        List<VortexExpr> others, VortexExpr side)
    {
        VortexExpr filter = others.Count == 0 ? side : Expr.And(And(others)!, side);
        return SynopsisAnswer.Of(contents, schema, instant, request, filter, held, identifiers) is { Edges.Count: 0, Parts.Count: > 0 };
    }

    /// <summary>The object's schema from its table of contents, or null when it does not decode.</summary>
    private static DType? SchemaOf(SynopsisContents contents)
    {
        try
        {
            return contents.SchemaIn(new DTypeArena());
        }
        catch (VortexFormatException)
        {
            return null;
        }
    }

    /// <summary>The conjunction of <paramref name="parts"/>, null for none.</summary>
    private static VortexExpr? And(List<VortexExpr> parts)
    {
        VortexExpr? all = parts.Count == 0 ? null : parts[0];
        for (int i = 1; i < parts.Count; i++)
        {
            all = Expr.And(all!, parts[i]);
        }

        return all;
    }

    /// <summary>The slice <paramref name="instant"/> lies in, or -1 when it lies between two or outside them.</summary>
    private static int SliceOf(IReadOnlyList<TimeSlice> slices, long instant)
    {
        int low = 0;
        int high = slices.Count - 1;
        while (low <= high)
        {
            int middle = (low + high) >>> 1;
            TimeSlice slice = slices[middle];
            if (instant < slice.Start)
            {
                high = middle - 1;
            }
            else if (instant >= slice.End)
            {
                low = middle + 1;
            }
            else
            {
                return middle;
            }
        }

        return -1;
    }
}
