using Vorticity.Expressions;
using Vorticity.Keys;
using Vorticity.Scanning;
using Vorticity.Types;

namespace Vorticity.Dataset;

/// <summary>
/// A range on the dataset's clustering key, and whether an object lies wholly inside it. A summary
/// bound is inexact outwards -- a minimum at or below the true one, a maximum at or above it -- so
/// summary bounds inside the range put every key inside it, provided the key holds no null and its
/// type has summary bounds that are bounds in the filter's order.
/// </summary>
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

    /// <summary>
    /// The range <paramref name="filter"/> states on the clustering key -- a null filter is every
    /// row -- or null when the entries cannot count it.
    /// </summary>
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

    private static bool Above(FilterLiteral value, FilterLiteral bound, bool inclusive)
    {
        if (value.Kind != bound.Kind || value.Kind == FilterLiteralKind.Null)
        {
            return false;
        }

        int order = KeyCursor.Compare(value, bound);
        return order > 0 || (inclusive && order == 0);
    }

    /// <summary>
    /// Narrows the range by one conjunct; false for anything but a conjunction of comparisons on
    /// the key, since the key's summaries cannot count what it selects.
    /// </summary>
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
