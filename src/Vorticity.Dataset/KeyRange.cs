// A range on the clustering key, read off a filter - docs/13-dataset.md §6.6's fourth row: "rank and
// count on the clustering key: the row counts of the objects wholly inside the range, the exact cover
// in the two boundary objects per level".
//
// WHAT "WHOLLY INSIDE" RESTS ON is the entry's summary, and a summary bound is inexact in the one
// direction that keeps this sound: a minimum at or below the true one, a maximum at or above it (a
// truncated string's maximum is rounded up, step 19). So summary bounds inside the range put every
// key inside it. Two more conditions, each a way the row count would lie: the key holds no null,
// which no comparison selects; and the key's type has summary bounds that are bounds in the filter's
// order -- not a float, whose zone minimum and maximum exclude NaN (08 §2).
//
// ONLY A CONJUNCTION OF COMPARISONS ON THE KEY. Anything else in the filter -- another column, an
// OR, a NOT -- selects rows the summaries of the key cannot count, and the object is opened.
using Vorticity.Expressions;
using Vorticity.Keys;
using Vorticity.Scan;
using Vorticity.Types;

namespace Vorticity.Dataset;

/// <summary>A range on the dataset's clustering key, and whether an object lies wholly inside it.</summary>
internal sealed class KeyRange
{
    private readonly string _path;
    private FilterLiteral _low;
    private bool _hasLow;
    private bool _lowInclusive;
    private FilterLiteral _high;
    private bool _hasHigh;
    private bool _highInclusive;

    private KeyRange(string path) => _path = path;

    /// <summary>The range <paramref name="filter"/> states on the clustering key, or null when it states something else.</summary>
    /// <param name="dataset">The dataset, whose clustering key the range is on.</param>
    /// <param name="filter">The scan's filter; null is every row.</param>
    /// <returns>The range, or null when the entries cannot count the filter.</returns>
    internal static KeyRange? Of(VortexDataset dataset, VortexExpr? filter)
    {
        if (dataset.Key is not { IsComposite: false } key)
        {
            return filter is null ? new KeyRange(string.Empty) : null;
        }

        KeyRange range = new KeyRange(key.Paths[0]);
        if (filter is null)
        {
            return range;
        }

        DType dtype = ClusteringKey.Resolve(dataset.Schema, key.Paths[0]);
        bool bounded = dtype.Kind switch
        {
            DTypeKind.Primitive => !dtype.PType.IsFloat(),
            DTypeKind.Bool or DTypeKind.Utf8 or DTypeKind.Binary => true,
            _ => false,
        };
        return bounded && range.Take(filter) ? range : null;
    }

    /// <summary>Whether every row of an object with these summaries lies inside the range.</summary>
    /// <param name="summaries">The object's summaries, from its leaf entry.</param>
    internal bool Holds(ObjectSummaries summaries)
    {
        if (!_hasLow && !_hasHigh)
        {
            return true;
        }

        if (!summaries.TryGet(_path, out ColumnSummary column)
            || !column.HasMin || !column.HasMax || !column.HasNullCount || column.NullCount != 0)
        {
            return false;
        }

        return (!_hasLow || Above(column.Min, _low, _lowInclusive))
            && (!_hasHigh || Above(_high, column.Max, _highInclusive));
    }

    /// <summary>Whether <paramref name="value"/> is above <paramref name="bound"/>, or at it when inclusive.</summary>
    private static bool Above(FilterLiteral value, FilterLiteral bound, bool inclusive)
    {
        if (value.Kind != bound.Kind || value.Kind == FilterLiteralKind.Null)
        {
            return false;
        }

        int order = KeyCursor.Compare(value, bound);
        return order > 0 || (inclusive && order == 0);
    }

    /// <summary>Narrows the range by one conjunct; false when it is not a comparison on the key.</summary>
    private bool Take(VortexExpr conjunct)
    {
        switch (conjunct)
        {
            case LogicalExpr { IsAnd: true } and:
                return Take(and.Left) && Take(and.Right);

            case ComparisonExpr comparison when comparison.Field.Path == _path
                && comparison.Value.Kind != FilterLiteralKind.Null:
                FilterLiteral value = comparison.Value;
                switch (comparison.Op)
                {
                    case ComparisonOp.Equal:
                        Low(value, inclusive: true);
                        High(value, inclusive: true);
                        return true;
                    case ComparisonOp.Greater:
                        Low(value, inclusive: false);
                        return true;
                    case ComparisonOp.GreaterOrEqual:
                        Low(value, inclusive: true);
                        return true;
                    case ComparisonOp.Less:
                        High(value, inclusive: false);
                        return true;
                    case ComparisonOp.LessOrEqual:
                        High(value, inclusive: true);
                        return true;
                    default:
                        return false;
                }

            default:
                return false;
        }
    }

    /// <summary>Raises the lower bound to <paramref name="value"/> when that is tighter.</summary>
    private void Low(FilterLiteral value, bool inclusive)
    {
        int order = _hasLow && value.Kind == _low.Kind ? KeyCursor.Compare(value, _low) : 1;
        if (!_hasLow || order > 0 || (order == 0 && !inclusive))
        {
            _low = value;
            _lowInclusive = inclusive;
            _hasLow = true;
        }
    }

    /// <summary>Lowers the upper bound to <paramref name="value"/> when that is tighter.</summary>
    private void High(FilterLiteral value, bool inclusive)
    {
        int order = _hasHigh && value.Kind == _high.Kind ? KeyCursor.Compare(value, _high) : -1;
        if (!_hasHigh || order < 0 || (order == 0 && !inclusive))
        {
            _high = value;
            _highInclusive = inclusive;
            _hasHigh = true;
        }
    }
}
