// The exact cover of a predicate - docs/12-index-reads.md §5.1 and §5.2's first tier, and the row
// selection of docs/10-indexes.md §6.6.
//
// A PREDICATE ON ONE KEYED COLUMN IS A SET OF SLICES OF ITS ENTRIES. An equality is the slice
// between the key's two bounds, a comparison the slice from one end to a bound, `IN` a union of
// points, `StartsWith(p)` the slice from `p` to its successor, and AND and OR the intersection and
// union of those. The slices are counted in rank space -- the number of entries below a bound --
// which both exact sources answer in `O(r log n)` without reading a data segment, so a count is a
// sum of lengths and a membership is a non-empty sum.
//
// THE IEEE MEANING MEETS THE TOTAL ORDER BY CONSTRUCTION OF THE BOUNDS (§4.4). The scan's `x = 0.0`
// matches both zeros, so its slice runs from `-0.0` to `+0.0`; `x > v` never matches a NaN, so its
// slice ends at `+inf`; a NaN literal matches nothing. On a sorted column, whose order is IEEE and
// holds no NaN, the same bounds give the same slices. `!=` on a float is left to the decode: IEEE
// makes a NaN unequal to itself, and the slice algebra has no room for that.
//
// ANYTHING ELSE DECLINES, and the scan takes its other tiers: another column, a literal of another
// domain, `NOT`, `Contains`, `LIKE`, a null check. A declined cover costs the source's open and
// nothing more.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Expressions;
using Vorticity.File;

namespace Vorticity.Keys;

/// <summary>The slices of a keyed column a predicate selects, exactly.</summary>
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
    /// The cover of <paramref name="filter"/>, or null when no exact source serves the whole of it.
    /// </summary>
    /// <param name="file">The open file.</param>
    /// <param name="filter">The scan's predicate.</param>
    /// <param name="indexes">Whether the index directory may serve.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    internal static async ValueTask<ExactCover?> TryCreateAsync(
        VortexFile file, VortexExpr filter, bool indexes, CancellationToken cancellationToken)
    {
        string? path = null;
        if (!OneColumn(filter, ref path) || path is null)
        {
            return null;
        }

        (KeySource? source, KeySourceKind kind) =
            await KeyCursorBuilder.OpenSourceAsync(file, path, indexes, cancellationToken).ConfigureAwait(false);
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
            // A run that does not decode costs the cover and never the scan: an index is a hint
            // (docs/10-indexes.md §4.1), and the scan's other tiers still answer.
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
    /// The slices of <paramref name="source"/> a key-ordered scan walks (docs/12-index-reads.md §6):
    /// every entry, narrowed by each top-level <c>AND</c> conjunct that tests
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
    /// The smallest or largest key the predicate selects -- §5.3's third resolution, a seek inside
    /// the range -- <see cref="FilterLiteral.Null"/> when it selects nothing; not answered when a
    /// run the seek reads does not decode.
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
        Array.Sort(rows);
        return rows;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _source.DisposeAsync();

    /// <summary>Whether every leaf tests the same column, and which.</summary>
    private static bool OneColumn(VortexExpr expr, ref string? path)
    {
        string leaf;
        switch (expr)
        {
            case LogicalExpr logical:
                return OneColumn(logical.Left, ref path) && OneColumn(logical.Right, ref path);
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
                foreach (FilterLiteral value in @in.Values)
                {
                    List<(long, long)>? point =
                        await ComparisonAsync(ComparisonOp.Equal, value, source, cancellationToken).ConfigureAwait(false);
                    if (point is null)
                    {
                        return null;
                    }

                    points = Union(points, point);
                }

                return points;

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

    private static List<(long, long)> Union(List<(long, long)> left, List<(long, long)> right)
    {
        List<(long, long)> all = [.. left, .. right];
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
