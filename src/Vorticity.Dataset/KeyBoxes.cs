using System;
using System.Threading;
using Vorticity.Indexes;
using Vorticity.RowEncoding;

namespace Vorticity.Dataset;

/// <summary>
/// The boxes of an object's sparse index judged on the bytes of their keys (spec A, 7.3): what a filter's conjuncts on the
/// indexed components allow of each, row-encoded once a question as <see cref="KeyIntervals"/> encodes its bounds, and
/// coded once a question as the indexes it meets code their keys. A box between two keys holds, on the components the two
/// share, those values, and on the next one the values between theirs: it may hold a row the filter keeps exactly when each
/// shared value is one the conjuncts allow, and the next component's span meets what they allow. What the summaries' pruner
/// says of the box decoded, value by value, when every conjunct on the components' columns is an equality, a set or a
/// comparison of one column that feeds one component as it is (<see cref="KeyIntervals.Boxes"/>).
/// </summary>
/// <remarks>
/// A null value of a component leaves it unjudged, as the decoded box leaves a null bound open; the order puts nulls after
/// every value, so that a span up to a null holds every value from its low one on. A conjunct on the partition's column alone
/// is the object's to judge, constant over its granules, as the intervals' exactness has it.
/// </remarks>
internal sealed class KeyBoxes
{
    // What the conjuncts allow of each indexed component, row-encoded, null for a component they leave free; the encoded
    // width of each; the bytes of a null of each, null for a component that holds none; the bounds with their texts unpadded,
    // made at the first index that codes its keys so.
    private readonly Allowed?[] _rows;
    private readonly int[] _widths;
    private readonly byte[]?[] _nulls;
    private Allowed?[]? _unpadded;

    /// <param name="allowed">What the conjuncts allow of each indexed component, row-encoded; null for a free one.</param>
    /// <param name="widths">The encoded width of each.</param>
    /// <param name="nulls">The bytes of a null of each, row-encoded; null for one that holds none.</param>
    internal KeyBoxes(Allowed?[] allowed, int[] widths, byte[]?[] nulls)
    {
        _rows = allowed;
        _widths = widths;
        _nulls = nulls;
    }

    /// <summary>
    /// The values a component may hold: <see cref="Points"/>, ascending and distinct, within the range when there is one, or
    /// the range from <see cref="Low"/> to <see cref="High"/>, each side open when null.
    /// </summary>
    internal sealed record Allowed(byte[][]? Points, byte[]? Low, bool LowStrict, byte[]? High, bool HighStrict)
    {
        /// <summary>Whether <paramref name="value"/> is one of them.</summary>
        internal bool Holds(ReadOnlySpan<byte> value)
        {
            if (Points is { } points)
            {
                int at = First(points, value);
                return at < points.Length && points[at].AsSpan().SequenceEqual(value);
            }

            return Above(value) && Below(value);
        }

        /// <summary>Whether one of them lies from <paramref name="low"/> to <paramref name="high"/>, both included.</summary>
        internal bool Meets(ReadOnlySpan<byte> low, ReadOnlySpan<byte> high)
        {
            if (Points is { } points)
            {
                int at = First(points, low);
                return at < points.Length && points[at].AsSpan().SequenceCompareTo(high) <= 0;
            }

            return Below(low) && Above(high);
        }

        /// <summary>Whether <paramref name="value"/> does not lie below the range.</summary>
        private bool Above(ReadOnlySpan<byte> value) =>
            Low is not { } low || (value.SequenceCompareTo(low) is var order && (order > 0 || (order == 0 && !LowStrict)));

        /// <summary>Whether <paramref name="value"/> does not lie past the range.</summary>
        private bool Below(ReadOnlySpan<byte> value) =>
            High is not { } high || (value.SequenceCompareTo(high) is var order && (order < 0 || (order == 0 && !HighStrict)));

        /// <summary>The first of <paramref name="points"/> at or past <paramref name="value"/>: a bisection.</summary>
        private static int First(byte[][] points, ReadOnlySpan<byte> value)
        {
            int low = 0;
            int high = points.Length;
            while (low < high)
            {
                int middle = (low + high) >>> 1;
                if (points[middle].AsSpan().SequenceCompareTo(value) < 0)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            return low;
        }

        /// <summary>These values with their texts unpadded, a value of the encoded width <paramref name="width"/>.</summary>
        internal Allowed Unpadded(int width)
        {
            if (width != RowEncoder.VariableWidth)
            {
                return this;
            }

            byte[][]? narrowed = null;
            if (Points is { } points)
            {
                narrowed = new byte[points.Length][];
                for (int i = 0; i < points.Length; i++)
                {
                    narrowed[i] = UnpaddedKeys.Narrow(points[i], [width]);
                }
            }

            return new Allowed(
                narrowed, Low is { } low ? UnpaddedKeys.Narrow(low, [width]) : null, LowStrict, High is { } high ? UnpaddedKeys.Narrow(high, [width]) : null, HighStrict);
        }
    }

    /// <summary>
    /// Whether rows between <paramref name="low"/> and <paramref name="high"/>, keys of the indexed components coded as
    /// <paramref name="coding"/> says, both included, may hold a row the filter keeps.
    /// </summary>
    internal bool MayHold(ReadOnlySpan<byte> low, ReadOnlySpan<byte> high, KeyCoding coding)
    {
        Allowed?[] components = In(coding);
        int lowAt = 0;
        int highAt = 0;
        for (int c = 0; c < components.Length && lowAt < low.Length && highAt < high.Length; c++)
        {
            int width = _widths[c];
            int lowLength = coding == KeyCoding.Rows ? RowEncoder.ValueLength(low[lowAt..], width) : UnpaddedKeys.ValueLength(low[lowAt..], width);
            int highLength = coding == KeyCoding.Rows ? RowEncoder.ValueLength(high[highAt..], width) : UnpaddedKeys.ValueLength(high[highAt..], width);
            if (lowLength <= 0 || highLength <= 0 || lowAt + lowLength > low.Length || highAt + highLength > high.Length)
            {
                break;
            }

            ReadOnlySpan<byte> lowValue = low.Slice(lowAt, lowLength);
            ReadOnlySpan<byte> highValue = high.Slice(highAt, highLength);
            bool pinned = lowValue.SequenceEqual(highValue);
            if (components[c] is { } allowed && !(_nulls[c] is { } none && lowValue.SequenceEqual(none))
                && !(pinned ? allowed.Holds(lowValue) : allowed.Meets(lowValue, highValue)))
            {
                return false;
            }

            // Past the first component the two keys part on, the next ones are free.
            if (!pinned)
            {
                break;
            }

            lowAt += lowLength;
            highAt += highLength;
        }

        return true;
    }

    /// <summary>What the conjuncts allow of each component as <paramref name="coding"/> codes keys.</summary>
    private Allowed?[] In(KeyCoding coding)
    {
        if (coding == KeyCoding.Rows)
        {
            return _rows;
        }

        if (Volatile.Read(ref _unpadded) is { } known)
        {
            return known;
        }

        Allowed?[] made = new Allowed?[_rows.Length];
        for (int c = 0; c < made.Length; c++)
        {
            made[c] = _rows[c]?.Unpadded(_widths[c]);
        }

        return Interlocked.CompareExchange(ref _unpadded, made, null) ?? made;
    }
}
