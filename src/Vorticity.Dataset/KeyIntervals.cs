using System;
using System.Collections.Generic;
using Vorticity.Aggregating;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.RowEncoding;
using Vorticity.Types;

namespace Vorticity.Dataset;

/// <summary>
/// The intervals of an object's sparse index keys that a filter's conjuncts on the indexed components bound: equalities and
/// sets on the leading components, then a range on the next one, each through the component's function, as ClickHouse's
/// condition on a continuous range of the key. Ascending and disjoint, each row-encoded once a question; an object's index
/// finds the granules of each by a bisection (<see cref="Ranges"/>), where the exclusion search judges boxes.
/// </summary>
/// <remarks>
/// <para>
/// Granule <c>i</c> holds keys from its key <c>kᵢ</c> to the next one's, both included, the last to the object's last key.
/// For an interval from <c>L</c> to <c>H</c> it may hold a row when <c>kᵢ₊₁</c> reaches <c>L</c> and <c>kᵢ</c> does not pass
/// <c>H</c>, each compared on the bytes the bound holds: the box the exclusion search judges for that granule meets the
/// filter on the same terms. A strict bound compares one way further. A bound through a function is the image of the
/// source's, the function non-decreasing, and holds the source's values of the image: it keeps at most the granule at its
/// edge more than the box would.
/// </para>
/// <para>
/// The intervals are exact when they say everything a box could judge: each conjunct that reads the indexed components'
/// columns and the partition's alone is an equality, a set, a null check or a comparison of one column, and every
/// component that column feeds lies among those the intervals bound. Otherwise the exclusion search runs within them.
/// </para>
/// </remarks>
internal sealed class KeyIntervals
{
    /// <summary>The most intervals the sets of the leading components make, their values multiplied; past it, fewer components bound them.</summary>
    internal const int MostIntervals = 256;

    /// <summary>The most intervals a set of tuples makes, each its own: a search costs some twenty comparisons each.</summary>
    internal const int MostTuples = 65_536;

    /// <summary>The intervals of a filter that bounds no leading component: the exclusion search alone.</summary>
    internal static readonly KeyIntervals Unbounded = new KeyIntervals([], [], lowStrict: false, highStrict: false, exact: false, bounded: false);

    // The bounds end to end, an interval's low then its high; where each ends, two an interval.
    private readonly byte[] _bytes;
    private readonly int[] _ends;

    private KeyIntervals(byte[] bytes, int[] ends, bool lowStrict, bool highStrict, bool exact, bool bounded)
    {
        _bytes = bytes;
        _ends = ends;
        LowStrict = lowStrict;
        HighStrict = highStrict;
        Exact = exact;
        Bounded = bounded;
    }

    /// <summary>Whether the filter bounds the leading component: else there are no intervals, and the exclusion search judges every box.</summary>
    internal bool Bounded { get; }

    /// <summary>Whether the intervals say everything a box of the indexed components could judge of the filter, so that no box is judged.</summary>
    internal bool Exact { get; }

    /// <summary>The intervals, none when no row may keep the conjuncts.</summary>
    internal int Count => _ends.Length / 2;

    /// <summary>Whether the low bounds exclude their own keys.</summary>
    internal bool LowStrict { get; }

    /// <summary>Whether the high bounds exclude their own keys.</summary>
    internal bool HighStrict { get; }

    /// <summary>The low bound of interval <paramref name="interval"/>, a key or its leading values; empty for none.</summary>
    internal ReadOnlySpan<byte> Low(int interval)
    {
        int start = interval == 0 ? 0 : _ends[(2 * interval) - 1];
        return _bytes.AsSpan(start, _ends[2 * interval] - start);
    }

    /// <summary>The high bound of interval <paramref name="interval"/>, a key or its leading values; empty for none.</summary>
    internal ReadOnlySpan<byte> High(int interval)
    {
        int start = _ends[2 * interval];
        return _bytes.AsSpan(start, _ends[(2 * interval) + 1] - start);
    }

    /// <summary>
    /// Adds to <paramref name="into"/>, ascending, the ranges <c>[first, end)</c> of <paramref name="keys"/>' granules that may
    /// hold a key of an interval, every page in hand; the keys compared added to <paramref name="compared"/>. Each interval's
    /// search starts where the one before found its first granule.
    /// </summary>
    internal void Ranges(GranuleKeys keys, List<(int First, int End)> into, ref int compared)
    {
        int granules = keys.Granules;
        int from = 0;
        for (int interval = 0; interval < Count; interval++)
        {
            ReadOnlySpan<byte> low = Low(interval);
            ReadOnlySpan<byte> high = High(interval);
            int start = 0;
            if (!low.IsEmpty)
            {
                int reaching = keys.First(low, from, LowStrict, ref compared);
                from = reaching;
                if (reaching == granules)
                {
                    // Only the last granule may still hold such a key, up to the object's last.
                    compared++;
                    if (!GranuleKeys.Reaches(keys.LastKey, low, LowStrict))
                    {
                        break;
                    }
                }

                start = Math.Max(0, reaching - 1);
            }

            int end = high.IsEmpty ? granules : keys.First(high, start, past: !HighStrict, ref compared);
            if (end > start)
            {
                into.Add((start, end));
            }
        }
    }

    /// <summary>
    /// The intervals <paramref name="judged"/>, a filter as the key reads it, bounds over the components of
    /// <paramref name="indexed"/>, an object's sparse index keeps of each granule's key, as the tree
    /// <paramref name="tree"/> encodes them past its first <paramref name="skip"/> values; <paramref name="partition"/> the
    /// partition's name, null for none. <see cref="Unbounded"/> when the filter bounds no leading component.
    /// </summary>
    internal static KeyIntervals Of(VortexExpr judged, LayoutColumn?[] indexed, string?[] names, string? partition, SortKey tree, int skip)
    {
        if (indexed.Length == 0)
        {
            return Unbounded;
        }

        List<VortexExpr> conjuncts = [];
        GroupPredicates.Conjuncts(judged, conjuncts);
        Bounds[] bounds = new Bounds[indexed.Length];
        List<string> read = [];
        bool residual = false;
        HashSet<string> usedColumns = new HashSet<string>(StringComparer.Ordinal);
        List<TupleInExpr> tupleSets = [];
        foreach (VortexExpr conjunct in conjuncts)
        {
            // Tuples bound the leading components together, past what their columns' sets do; no box judges them.
            if (conjunct is TupleInExpr tuples)
            {
                tupleSets.Add(tuples);
                continue;
            }

            read.Clear();
            conjunct.CollectFields(read);
            bool reads = false;
            bool boxed = read.Count > 0;
            foreach (string path in read)
            {
                bool component = Array.IndexOf(names, path) >= 0;
                reads |= component;
                boxed &= component || string.Equals(path, partition, StringComparison.Ordinal);
            }

            if (!reads)
            {
                continue;
            }

            // A conjunct of the indexed columns and the partition's that the intervals cannot hold is judged by the boxes.
            if (Shape(conjunct) is not { } shape)
            {
                residual |= boxed;
                continue;
            }

            usedColumns.Add(shape.Column);
            for (int position = 0; position < indexed.Length; position++)
            {
                if (string.Equals(names[position], shape.Column, StringComparison.Ordinal))
                {
                    bounds[position] ??= new Bounds();
                    if (!bounds[position].Take(shape, indexed[position]!, tree.TypeAt(skip + position)))
                    {
                        residual = true;
                    }
                }
            }
        }

        // The leading components the intervals bound: those of values, while their product stays within the most, then one of a range.
        int points = 0;
        long product = 1;
        bool ranged = false;
        while (points < indexed.Length && bounds[points] is { Failed: false } at)
        {
            if (at.IsEmpty)
            {
                return new KeyIntervals([], [], false, false, exact: true, bounded: true);
            }

            if (at.Points is not { } values)
            {
                ranged = true;
                break;
            }

            if (product * values.Count > MostIntervals)
            {
                break;
            }

            product *= values.Count;
            points++;
        }

        // A set of tuples of the leading components' columns bounds them together: its tuples are the points, where it
        // bounds more components than the product of the columns' sets does.
        List<byte[]>? tupled = null;
        foreach (TupleInExpr tuples in tupleSets)
        {
            int covered = Covered(tuples, names);
            if (covered > points && TuplePrefixes(tuples, covered, indexed, names, tree, skip, bounds) is { } prefixesOfTuples)
            {
                (tupled, points) = (prefixesOfTuples, covered);
            }
        }

        if (tupled is not null)
        {
            if (tupled.Count == 0 || (points < indexed.Length && bounds[points] is { Failed: false, IsEmpty: true }))
            {
                return new KeyIntervals([], [], false, false, exact: true, bounded: true);
            }

            ranged = points < indexed.Length && bounds[points] is { Failed: false, Points: null } next && (next.Low is not null || next.High is not null);
        }

        int used = points + (ranged ? 1 : 0);
        if (used == 0)
        {
            return Unbounded;
        }

        // Exact when every column a conjunct was taken for feeds the bounded components alone.
        bool exact = !residual;
        for (int position = used; position < indexed.Length && exact; position++)
        {
            exact = names[position] is not { } name || !usedColumns.Contains(name);
        }

        // The intervals: the values' tuples in order, the encodings ordering as the values do, each with the range after it.
        List<byte[]> prefixes = tupled ?? [[]];
        for (int position = 0; position < points && tupled is null; position++)
        {
            List<byte[]> longer = new List<byte[]>(prefixes.Count * bounds[position].Points!.Count);
            foreach (byte[] prefix in prefixes)
            {
                foreach (byte[] value in bounds[position].Points!)
                {
                    longer.Add([.. prefix, .. value]);
                }
            }

            prefixes = longer;
        }

        Bounds? range = ranged ? bounds[points] : null;
        List<byte> bytes = [];
        int[] ends = new int[2 * prefixes.Count];
        for (int interval = 0; interval < prefixes.Count; interval++)
        {
            bytes.AddRange(prefixes[interval]);
            if (range?.Low is { } low)
            {
                bytes.AddRange(low);
            }

            ends[2 * interval] = bytes.Count;
            bytes.AddRange(prefixes[interval]);
            if (range?.High is { } high)
            {
                bytes.AddRange(high);
            }

            ends[(2 * interval) + 1] = bytes.Count;
        }

        return new KeyIntervals(
            [.. bytes], ends, range is { Low: not null, LowStrict: true }, range is { High: not null, HighStrict: true }, exact, bounded: true);
    }

    /// <summary>The leading components whose columns are all among the columns of <paramref name="tuples"/>.</summary>
    private static int Covered(TupleInExpr tuples, string?[] names)
    {
        int covered = 0;
        while (covered < names.Length && names[covered] is { } name && Array.Exists(tuples.Fields, field => field is ColumnFieldExpr && string.Equals(field.Path, name, StringComparison.Ordinal)))
        {
            covered++;
        }

        return covered;
    }

    /// <summary>
    /// Each tuple of <paramref name="tuples"/> as the key of its first <paramref name="covered"/> components, row-encoded and
    /// each through its component's function, ascending and distinct; a tuple the other conjuncts' bounds refute leaves.
    /// Null when a value does not encode as its component, or past <see cref="MostTuples"/>: the columns' sets bound then.
    /// </summary>
    private static List<byte[]>? TuplePrefixes(
        TupleInExpr tuples, int covered, LayoutColumn?[] indexed, string?[] names, SortKey tree, int skip, Bounds?[] bounds)
    {
        int[] columns = new int[covered];
        for (int position = 0; position < covered; position++)
        {
            columns[position] = Array.FindIndex(tuples.Fields, field => string.Equals(field.Path, names[position], StringComparison.Ordinal));
        }

        List<byte[]> prefixes = new List<byte[]>(tuples.Count);
        List<byte> prefix = [];
        for (int t = 0; t < tuples.Count; t++)
        {
            ReadOnlySpan<FilterLiteral> values = tuples.Tuple(t);
            prefix.Clear();
            bool kept = true;
            for (int position = 0; position < covered && kept; position++)
            {
                if (Bounds.Encode(values[columns[position]], indexed[position]!, tree.TypeAt(skip + position)) is not { } encoded)
                {
                    return null;
                }

                kept = bounds[position] is not { Failed: false } bound || bound.Holds(encoded);
                prefix.AddRange(encoded);
            }

            if (kept)
            {
                prefixes.Add([.. prefix]);
            }
        }

        prefixes.Sort(static (left, right) => left.AsSpan().SequenceCompareTo(right));
        int distinct = 0;
        for (int i = 0; i < prefixes.Count; i++)
        {
            if (distinct == 0 || !prefixes[i].AsSpan().SequenceEqual(prefixes[distinct - 1]))
            {
                prefixes[distinct++] = prefixes[i];
            }
        }

        prefixes.RemoveRange(distinct, prefixes.Count - distinct);
        return prefixes.Count <= MostTuples ? prefixes : null;
    }

    /// <summary>
    /// What one conjunct says of one column the intervals may hold: values, by an equality, a set or a null check, or a bound,
    /// by a comparison; null for any other conjunct.
    /// </summary>
    private static Shaped? Shape(VortexExpr conjunct)
    {
        switch (conjunct)
        {
            case ComparisonExpr { Field: ColumnFieldExpr field } comparison when comparison.Value.Kind != FilterLiteralKind.Null:
                return comparison.Op switch
                {
                    ComparisonOp.Equal => new Shaped(field.Path, [comparison.Value], null, default, Nulls: false),
                    ComparisonOp.Less or ComparisonOp.LessOrEqual or ComparisonOp.Greater or ComparisonOp.GreaterOrEqual =>
                        new Shaped(field.Path, null, comparison.Op, comparison.Value, Nulls: false),
                    _ => null,
                };
            case InExpr { Field: ColumnFieldExpr field } set:
                return new Shaped(field.Path, [.. set.Values], null, default, Nulls: false);
            case TypedInExpr { Field: ColumnFieldExpr field } typed when typed.Count <= MostIntervals:
            {
                FilterLiteral[] values = new FilterLiteral[typed.Count];
                for (int i = 0; i < values.Length; i++)
                {
                    values[i] = typed.Shown(i);
                }

                return new Shaped(field.Path, values, null, default, Nulls: false);
            }

            case NullCheckExpr { Field: ColumnFieldExpr field, IsNull: true }:
                return new Shaped(field.Path, [FilterLiteral.Null], null, default, Nulls: true);
            default:
                return null;
        }
    }

    /// <summary>A conjunct's column, and its values, its comparison, or its check of nulls.</summary>
    private readonly record struct Shaped(string Column, FilterLiteral[]? Values, ComparisonOp? Op, FilterLiteral Bound, bool Nulls);

    /// <summary>
    /// What the conjuncts on one component's column say of the component's values, each row-encoded as the component: the
    /// values they allow, ascending, or the range they bound.
    /// </summary>
    private sealed class Bounds
    {
        /// <summary>The values allowed, ascending and distinct; null when no conjunct names values.</summary>
        internal List<byte[]>? Points { get; private set; }

        internal byte[]? Low { get; private set; }

        internal bool LowStrict { get; private set; }

        internal byte[]? High { get; private set; }

        internal bool HighStrict { get; private set; }

        /// <summary>Whether a conjunct named a value the component's type does not take: the component bounds nothing.</summary>
        internal bool Failed { get; private set; }

        /// <summary>Whether no value keeps the conjuncts.</summary>
        internal bool IsEmpty => Points is { Count: 0 } || (Low is { } low && High is { } high && Order(low, high) is var order && (order > 0 || (order == 0 && (LowStrict || HighStrict))));

        /// <summary>Takes <paramref name="shape"/> for the component <paramref name="component"/> of type <paramref name="type"/>; false when its values do not encode.</summary>
        internal bool Take(Shaped shape, LayoutColumn component, DType type)
        {
            if (shape.Values is { } values)
            {
                List<byte[]> encoded = new List<byte[]>(values.Length);
                foreach (FilterLiteral value in values)
                {
                    // A null in a set keeps no row; a null check keeps the nulls.
                    if (value.Kind == FilterLiteralKind.Null && !shape.Nulls)
                    {
                        continue;
                    }

                    if (Encode(value, component, type) is not { } bytes)
                    {
                        Failed = true;
                        return false;
                    }

                    encoded.Add(bytes);
                }

                encoded.Sort(static (left, right) => Order(left, right));
                int distinct = 0;
                for (int i = 0; i < encoded.Count; i++)
                {
                    if (distinct == 0 || Order(encoded[i], encoded[distinct - 1]) != 0)
                    {
                        encoded[distinct++] = encoded[i];
                    }
                }

                encoded.RemoveRange(distinct, encoded.Count - distinct);
                Points = Points is null ? encoded : encoded.FindAll(value => Points.Exists(known => Order(known, value) == 0));
                Filter();
                return true;
            }

            // Through a function, a strict bound of an integer is the wide one next to it, whose image bounds the image's.
            FilterLiteral literal = shape.Bound;
            bool strict = shape.Op is ComparisonOp.Greater or ComparisonOp.Less;
            if (strict && component.Function is not null && ValueFunction.TryInteger(literal, out Int128 integer))
            {
                // Past either end of a 64-bit integer, its end: a wider bound, never a narrower one.
                Int128 next = integer + (shape.Op == ComparisonOp.Greater ? 1 : -1);
                literal = next > long.MaxValue
                    ? FilterLiteral.From((ulong)Int128.Min(next, ulong.MaxValue))
                    : FilterLiteral.From((long)Int128.Max(next, long.MinValue));
                strict = false;
            }

            if (Encode(literal, component, type) is not { } bound)
            {
                Failed = true;
                return false;
            }

            // Through a function, a strict bound of a float is a wide one of the image.
            strict &= component.Function is null;
            if (shape.Op is ComparisonOp.Greater or ComparisonOp.GreaterOrEqual)
            {
                int order = Low is null ? 1 : Order(bound, Low);
                if (order > 0 || (order == 0 && strict))
                {
                    (Low, LowStrict) = (bound, strict);
                }
            }
            else
            {
                int order = High is null ? -1 : Order(bound, High);
                if (order < 0 || (order == 0 && strict))
                {
                    (High, HighStrict) = (bound, strict);
                }
            }

            Filter();
            return true;
        }

        /// <summary>Whether <paramref name="value"/>, encoded as the component, is one the conjuncts allow.</summary>
        internal bool Holds(byte[] value) =>
            (Points is not { } points || points.Exists(known => Order(known, value) == 0))
            && !(Low is { } low && Order(value, low) is var above && (above < 0 || (above == 0 && LowStrict)))
            && !(High is { } high && Order(value, high) is var below && (below > 0 || (below == 0 && HighStrict)));

        /// <summary>The values allowed kept to those the range holds, the range then said by them.</summary>
        private void Filter()
        {
            if (Points is not { } points || (Low is null && High is null))
            {
                return;
            }

            points.RemoveAll(value =>
                (Low is { } low && Order(value, low) is var above && (above < 0 || (above == 0 && LowStrict)))
                || (High is { } high && Order(value, high) is var below && (below > 0 || (below == 0 && HighStrict))));
        }

        /// <summary>
        /// <paramref name="value"/> row-encoded as the component's, through its function; null for a value its type does not
        /// take, a float's, whose order its encoding does not keep for zero's sign, or one past its storage.
        /// </summary>
        internal static byte[]? Encode(FilterLiteral value, LayoutColumn component, DType type)
        {
            if (value.Kind != FilterLiteralKind.Null && component.Function is { } function)
            {
                if (!function.TryMap(value, out FilterLiteral mapped))
                {
                    return null;
                }

                value = mapped;
            }

            DType storage = type.Kind == DTypeKind.Extension ? type.StorageType : type;
            if (value.Kind == FilterLiteralKind.Null)
            {
                if (!type.IsNullable || storage.Kind is not (DTypeKind.Bool or DTypeKind.Primitive or DTypeKind.Utf8 or DTypeKind.Binary))
                {
                    return null;
                }
            }
            else
            {
                bool taken = storage.Kind switch
                {
                    DTypeKind.Bool => value.Kind == FilterLiteralKind.Bool,
                    DTypeKind.Utf8 or DTypeKind.Binary => value.Kind == FilterLiteralKind.Bytes,
                    DTypeKind.Primitive when !storage.PType.IsFloat() => ValueFunction.TryInteger(value, out Int128 integer)
                        && ValueFunction.Integers(storage.PType) is var (min, max) && integer >= min && integer <= max,
                    _ => false,
                };
                if (!taken)
                {
                    return null;
                }

                if (storage.Kind == DTypeKind.Primitive && ValueFunction.TryInteger(value, out Int128 within))
                {
                    value = storage.PType.IsSignedInteger() ? FilterLiteral.From((long)within) : FilterLiteral.From((ulong)within);
                }
            }

            RowSortField field = KeyOrderedMerge.Field(descending: false);
            byte[] bytes = new byte[RowEncoder.ValueLength(value, type)];
            RowEncoder.WriteValue(value, type, field, bytes);
            return bytes;
        }

        private static int Order(byte[] left, byte[] right) => left.AsSpan().SequenceCompareTo(right);
    }
}
