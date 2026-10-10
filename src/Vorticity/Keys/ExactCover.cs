using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Expressions;
using Vorticity.File;

namespace Vorticity.Keys;

/// <summary>The slices of a keyed column a predicate selects, exactly.</summary>
/// <remarks>
/// A predicate on one keyed column is a set of slices of its entries: an equality is the slice
/// between the key's two bounds, a comparison the slice from one end to a bound, <c>IN</c> a union
/// of points, <c>StartsWith</c> the slice from the prefix to its successor, and conjunction and
/// disjunction the intersection and union of those. The slices live in rank space — the number of
/// entries below a bound — which an exact source answers without reading a data segment, so a count
/// is a sum of lengths and a membership a non-empty sum.
/// <para>
/// The bounds are built so that the scan's IEEE meaning and the keys' total order agree: <c>x =
/// 0.0</c> runs from <c>-0.0</c> to <c>+0.0</c>, <c>x &gt; v</c> stops at <c>+inf</c> because no
/// comparison matches a NaN, and a NaN literal matches nothing. On a sorted column, whose order is
/// IEEE and holds no NaN, the same bounds give the same slices. <c>!=</c> on a float is left to the
/// decode, since IEEE makes a NaN unequal to itself and the slice algebra has no room for that.
/// </para>
/// <para>
/// Anything else declines and the scan falls back: another column, a literal of another domain,
/// negation, <c>Contains</c>, <c>Like</c>, a null check. A declined cover costs the source's open
/// and nothing more.
/// </para>
/// </remarks>
internal sealed class ExactCover : IAsyncDisposable
{
    private readonly KeySource _source;

    private ExactCover(KeySource source, KeySourceKind kind, string path, List<(long Low, long High)> slices)
    {
        _source = source;
        Kind = kind;
        Path = path;
        Slices = slices;
        long count = 0;
        foreach ((long low, long high) in slices)
        {
            count += high - low;
        }

        Count = count;
    }

    /// <summary>The source that answered.</summary>
    internal KeySourceKind Kind { get; }

    /// <summary>The column every leaf of the predicate tests.</summary>
    internal string Path { get; }

    /// <summary>The selected entries, as disjoint rank ranges in key order.</summary>
    internal List<(long Low, long High)> Slices { get; }

    /// <summary>How many rows the predicate selects.</summary>
    internal long Count { get; }

    /// <summary>
    /// Whether <paramref name="file"/> may hold an exact source for <paramref name="filter"/>, told
    /// without a read: the one column every leaf tests is sorted by the file's statistics, or the
    /// file's indexes may serve it. Without, <see cref="TryCreateAsync"/> declines, and whatever a
    /// scan would weigh before asking it is weighed for nothing.
    /// </summary>
    /// <param name="file">The open file.</param>
    /// <param name="filter">The scan's predicate.</param>
    /// <param name="indexes">Whether the index directory may serve.</param>
    internal static bool MayExist(VortexFile file, VortexExpr filter, bool indexes)
    {
        string? path = null;
        return OneColumn(filter, ref path) && path is not null
            && (KeyCursorBuilder.StatedSorted(file, path) || (indexes && file.HasIndexDirectory));
    }

    /// <summary>
    /// The cover of <paramref name="filter"/>, or null when no exact source serves the whole of it.
    /// </summary>
    /// <param name="file">The open file.</param>
    /// <param name="filter">The scan's predicate.</param>
    /// <param name="indexes">Whether the index directory may serve.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <param name="zones">The zone maps a pruning pass already read for the filter, or null to read the column's.</param>
    /// <param name="metrics">The scan's sink, to which a sorted column adds what it reads; null when nobody asks.</param>
    internal static async ValueTask<ExactCover?> TryCreateAsync(
        VortexFile file, VortexExpr filter, bool indexes, CancellationToken cancellationToken,
        Compute.ZonePruner? zones = null, Scanning.ScanCounters? metrics = null)
    {
        string? path = null;
        if (!OneColumn(filter, ref path) || path is null)
        {
            return null;
        }

        (KeySource? source, KeySourceKind kind) = await KeyCursorBuilder
            .OpenSourceAsync(file, path, indexes, cancellationToken, zones?.Column(path), metrics)
            .ConfigureAwait(false);
        if (source is null)
        {
            return null;
        }

        List<(long, long)>? slices;
        try
        {
            slices = await SlicesAsync(filter, source, cancellationToken).ConfigureAwait(false);
        }
        catch (VortexFormatException)
        {
            // A run that does not decode costs the cover and never the scan: an index is a hint,
            // and the scan's other tiers still answer.
            slices = null;
        }

        if (slices is null)
        {
            await source.DisposeAsync().ConfigureAwait(false);
            return null;
        }

        return new ExactCover(source, kind, path, slices);
    }

    /// <summary>
    /// The slices of <paramref name="source"/> a key-ordered scan walks: every entry, narrowed by
    /// each top-level <c>AND</c> conjunct that tests
    /// <paramref name="path"/> alone. The other conjuncts are the filter's to evaluate on the rows;
    /// the slices only have to contain every row the filter keeps, and they contain exactly the
    /// ones the narrowing conjuncts keep.
    /// </summary>
    /// <param name="filter">The scan's predicate, or null.</param>
    /// <param name="source">The key source of <paramref name="path"/>.</param>
    /// <param name="path">The column the scan is ordered by.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    internal static async ValueTask<List<(long Low, long High)>> RangeAsync(
        VortexExpr? filter, KeySource source, string path, CancellationToken cancellationToken)
    {
        List<(long, long)> slices = Slice(0, source.EntryCount!.Value);
        if (filter is not null)
        {
            slices = await NarrowAsync(filter, source, path, slices, cancellationToken).ConfigureAwait(false);
        }

        return slices;
    }

    private static async ValueTask<List<(long, long)>> NarrowAsync(
        VortexExpr conjunct, KeySource source, string path, List<(long, long)> slices, CancellationToken cancellationToken)
    {
        if (conjunct is LogicalExpr { IsAnd: true } and)
        {
            slices = await NarrowAsync(and.Left, source, path, slices, cancellationToken).ConfigureAwait(false);
            return await NarrowAsync(and.Right, source, path, slices, cancellationToken).ConfigureAwait(false);
        }

        string? leaf = path;
        if (slices.Count == 0 || !OneColumn(conjunct, ref leaf))
        {
            return slices;
        }

        List<(long, long)>? own = await SlicesAsync(conjunct, source, cancellationToken).ConfigureAwait(false);
        return own is null ? slices : Intersect(slices, own);
    }

    /// <summary>
    /// The smallest or largest key the predicate selects, found by a seek inside the range, or
    /// <see cref="FilterLiteral.Null"/> when it selects nothing; not answered when a run the seek
    /// reads does not decode.
    /// </summary>
    /// <param name="wantMin">Whether the smallest is wanted.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    internal async ValueTask<(bool Answered, FilterLiteral Value)> ExtremeAsync(
        bool wantMin, CancellationToken cancellationToken)
    {
        if (Slices.Count == 0)
        {
            return (true, FilterLiteral.Null);
        }

        long rank = wantMin ? Slices[0].Low : Slices[^1].High - 1;
        try
        {
            return await _source.SeekRankAsync(rank, cancellationToken).ConfigureAwait(false)
                ? (true, _source.Key)
                : (false, FilterLiteral.Null);
        }
        catch (VortexFormatException)
        {
            return (false, FilterLiteral.Null);
        }
    }

    /// <summary>
    /// How many selected rows inside <paramref name="bounds"/> are not in <paramref name="excluded"/>,
    /// when the source's ranks are rows, as a sorted column's are: each slice is then a range of rows,
    /// and the count arithmetic on the rows left out. Null for any other source.
    /// </summary>
    internal long? KeptCount(Scanning.IRowExclusion excluded, RowRange bounds)
    {
        if (_source is not SortedColumnWalker walker)
        {
            return null;
        }

        long first = walker.FirstRow;
        long count = 0;
        foreach ((long low, long high) in Slices)
        {
            long start = Math.Max(first + low, bounds.Start);
            long end = Math.Min(first + high, bounds.End);
            if (start < end)
            {
                count += end - start - (excluded.ExcludedBefore(end) - excluded.ExcludedBefore(start));
            }
        }

        return count;
    }

    /// <summary>
    /// The selected rows, in file order, when there are at most <paramref name="cap"/> of them
    /// inside <paramref name="bounds"/>; null past the cap, or when a run the walk reads does not
    /// decode.
    /// </summary>
    /// <param name="bounds">The scan's row range.</param>
    /// <param name="cap">The most rows to gather: a batch's worth, the memory bound of a scan.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    internal async ValueTask<long[]?> RowsAsync(RowRange bounds, long cap, CancellationToken cancellationToken)
    {
        if (Count > cap)
        {
            return null;
        }

        try
        {
            return await WalkAsync(bounds, cancellationToken).ConfigureAwait(false);
        }
        catch (VortexFormatException)
        {
            return null;
        }
    }

    private async ValueTask<long[]> WalkAsync(RowRange bounds, CancellationToken cancellationToken)
    {
        long[] rows = new long[Count];
        int kept = 0;
        foreach ((long low, long high) in Slices)
        {
            bool valid = await _source.SeekRankAsync(low, cancellationToken).ConfigureAwait(false);
            for (long i = low; i < high && valid; i++)
            {
                long row = _source.Row;
                if (row >= bounds.Start && row < bounds.End)
                {
                    rows[kept++] = row;
                }

                if (i + 1 < high)
                {
                    valid = await _source.NextAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        Array.Resize(ref rows, kept);
        Compute.SpanSort.Sort(rows);
        return rows;
    }

    public ValueTask DisposeAsync() => _source.DisposeAsync();

    /// <summary>Whether every leaf tests the same column, and which.</summary>
    private static bool OneColumn(VortexExpr expr, ref string? path)
    {
        string leaf;
        switch (expr)
        {
            case LogicalExpr logical:
                return OneColumn(logical.Left, ref path) && OneColumn(logical.Right, ref path);

            // A function of the column is not the column its keys are.
            case ComparisonExpr { Field: Compute.FunctionFieldExpr }:
            case InExpr { Field: Compute.FunctionFieldExpr }:
                return false;
            case ComparisonExpr comparison:
                leaf = comparison.Field.Path;
                break;
            case InExpr @in:
                leaf = @in.Field.Path;
                break;
            case StringMatchExpr { Op: StringMatchOp.StartsWith } match:
                leaf = match.Field.Path;
                break;
            default:
                return false;
        }

        path ??= leaf;
        return string.Equals(path, leaf, StringComparison.Ordinal);
    }

    private static async ValueTask<List<(long, long)>?> SlicesAsync(
        VortexExpr expr, KeySource source, CancellationToken cancellationToken)
    {
        switch (expr)
        {
            case LogicalExpr logical:
                List<(long, long)>? left = await SlicesAsync(logical.Left, source, cancellationToken).ConfigureAwait(false);
                if (left is null)
                {
                    return null;
                }

                List<(long, long)>? right = await SlicesAsync(logical.Right, source, cancellationToken).ConfigureAwait(false);
                if (right is null)
                {
                    return null;
                }

                return logical.IsAnd ? Intersect(left, right) : Union(left, right);

            case ComparisonExpr comparison:
                return await ComparisonAsync(comparison.Op, comparison.Value, source, cancellationToken).ConfigureAwait(false);

            case InExpr @in:
                List<(long, long)> points = [];

                // The literals are looked up in key order, which turns the lookups into a merge
                // join: each one lands in the segment the one before it left decoded, so a long
                // list reads each segment once instead of once per literal.
                FilterLiteral[] ordered = [.. @in.Values];
                Array.Sort(ordered, (left, right) => left.Kind == right.Kind ? KeyOrder.Total(left, right) : 0);
                foreach (FilterLiteral value in ordered)
                {
                    List<(long, long)>? point =
                        await ComparisonAsync(ComparisonOp.Equal, value, source, cancellationToken).ConfigureAwait(false);
                    if (point is null)
                    {
                        return null;
                    }

                    // Gathered here and merged once at the end: merging inside the loop would sort
                    // every slice found so far on each literal, so the cost would grow with the
                    // square of the list's length.
                    points.AddRange(point);
                }

                return Merge(points);

            case StringMatchExpr { Op: StringMatchOp.StartsWith } match:
                if (source.KeyKind != FilterLiteralKind.Bytes || match.Pattern.Kind != FilterLiteralKind.Bytes)
                {
                    return null;
                }

                long low = await source.RankAsync(match.Pattern, cancellationToken).ConfigureAwait(false);
                byte[]? successor = Successor(match.Pattern.BytesValue);
                long high = successor is null
                    ? source.EntryCount!.Value
                    : await source.RankAsync(FilterLiteral.From(successor), cancellationToken).ConfigureAwait(false);
                return Slice(low, high);

            default:
                return null;
        }
    }

    private static async ValueTask<List<(long, long)>?> ComparisonAsync(
        ComparisonOp op, FilterLiteral value, KeySource source, CancellationToken cancellationToken)
    {
        if (value.Kind != source.KeyKind)
        {
            return null;
        }

        long entries = source.EntryCount!.Value;
        bool floats = value.Kind == FilterLiteralKind.Float;
        if (floats && (op == ComparisonOp.NotEqual))
        {
            return null;
        }

        if (floats && double.IsNaN(value.FloatValue))
        {
            // Every comparison with a NaN is false in IEEE.
            return [];
        }

        // The first and last key an IEEE equality with the literal matches, and the two ends of
        // the ordered, NaN-free part of a float column.
        FilterLiteral lowKey = value;
        FilterLiteral highKey = value;
        if (floats && value.FloatValue == 0)
        {
            lowKey = FilterLiteral.From(-0.0);
            highKey = FilterLiteral.From(0.0);
        }

        long low = await source.RankAsync(lowKey, cancellationToken).ConfigureAwait(false);
        long high = await source.UpperRankAsync(highKey, cancellationToken).ConfigureAwait(false);
        return op switch
        {
            ComparisonOp.Equal => Slice(low, high),
            ComparisonOp.NotEqual => Union(
                Slice(await StartAsync(source, floats, cancellationToken).ConfigureAwait(false), low),
                Slice(high, await EndAsync(source, floats, entries, cancellationToken).ConfigureAwait(false))),
            ComparisonOp.Less => Slice(await StartAsync(source, floats, cancellationToken).ConfigureAwait(false), low),
            ComparisonOp.LessOrEqual => Slice(await StartAsync(source, floats, cancellationToken).ConfigureAwait(false), high),
            ComparisonOp.Greater => Slice(high, await EndAsync(source, floats, entries, cancellationToken).ConfigureAwait(false)),
            _ => Slice(low, await EndAsync(source, floats, entries, cancellationToken).ConfigureAwait(false)),
        };
    }

    /// <summary>The first rank a range can match: past the negative NaNs of a float column.</summary>
    private static ValueTask<long> StartAsync(KeySource source, bool floats, CancellationToken cancellationToken) =>
        floats
            ? source.RankAsync(FilterLiteral.From(double.NegativeInfinity), cancellationToken)
            : new ValueTask<long>(0);

    /// <summary>One past the last rank a range can match: before the positive NaNs of a float column.</summary>
    private static ValueTask<long> EndAsync(KeySource source, bool floats, long entries, CancellationToken cancellationToken) =>
        floats
            ? source.UpperRankAsync(FilterLiteral.From(double.PositiveInfinity), cancellationToken)
            : new ValueTask<long>(entries);

    private static List<(long, long)> Slice(long low, long high) => low < high ? [(low, high)] : [];

    /// <summary>The smallest byte string above every string that starts with <paramref name="prefix"/>; null when none is.</summary>
    internal static byte[]? Successor(ReadOnlySpan<byte> prefix)
    {
        int length = prefix.Length;
        while (length > 0 && prefix[length - 1] == 0xFF)
        {
            length--;
        }

        if (length == 0)
        {
            return null;
        }

        byte[] successor = prefix[..length].ToArray();
        successor[^1]++;
        return successor;
    }

    private static List<(long, long)> Intersect(List<(long, long)> left, List<(long, long)> right)
    {
        List<(long, long)> result = [];
        int i = 0;
        int j = 0;
        while (i < left.Count && j < right.Count)
        {
            long low = Math.Max(left[i].Item1, right[j].Item1);
            long high = Math.Min(left[i].Item2, right[j].Item2);
            if (low < high)
            {
                result.Add((low, high));
            }

            if (left[i].Item2 < right[j].Item2)
            {
                i++;
            }
            else
            {
                j++;
            }
        }

        return result;
    }

    private static List<(long, long)> Union(List<(long, long)> left, List<(long, long)> right) =>
        Merge([.. left, .. right]);

    /// <summary>The slices of <paramref name="all"/>, in row order, with the overlaps folded in.</summary>
    /// <param name="all">The slices, in any order; the list is sorted in place.</param>
    private static List<(long, long)> Merge(List<(long, long)> all)
    {
        all.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        List<(long, long)> result = [];
        foreach ((long low, long high) in all)
        {
            if (result.Count > 0 && low <= result[^1].Item2)
            {
                result[^1] = (result[^1].Item1, Math.Max(result[^1].Item2, high));
            }
            else
            {
                result.Add((low, high));
            }
        }

        return result;
    }
}
