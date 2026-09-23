using System;
using System.Runtime.CompilerServices;
using Vorticity.Expressions;

namespace Vorticity.Compute;

/// <summary>
/// The zones of a numeric column laid out as columns: the minima and the maxima as the 64-bit
/// patterns of their literals, the null and NaN counts, and one bit per zone for each flag and for
/// what the bounds leave open. A comparison is answered for sixty-four zones at a time, a compare
/// and a shift a zone and a few word operations per sixty-four, where the zone-by-zone question
/// walks the filter and orders two literals for every zone; and the zones take about a third of
/// the bytes of as many <see cref="ZoneBounds"/>, which are made again one at a time when a
/// question needs them.
/// </summary>
/// <remarks>
/// It answers exactly what <see cref="ZonePruner"/> asks zone by zone: a zone whose nulls fill it
/// holds no match, an absent bound rules nothing out, a bound that does not order against the
/// constant -- a NaN -- rules nothing out, and under a negated ordering a zone that may hold a NaN
/// may match. Only a column whose every bound is of one numeric kind has a table, and only a
/// constant of that kind, or an integer against a float column, is answered from it; anything else
/// is asked zone by zone.
/// </remarks>
internal sealed class ZoneTable
{
    /// <summary>The answer for a column that has no table: its zones are asked one by one.</summary>
    internal static readonly ZoneTable None = new ZoneTable(FilterLiteralKind.Null, 0);

    private readonly ulong[] _min;
    private readonly ulong[] _max;
    private readonly int[] _nulls;
    private int[]? _nans;
    private readonly ulong[] _hasMin;
    private readonly ulong[] _hasMax;
    private readonly ulong[] _exact;
    private readonly ulong[] _hasNulls;
    private readonly ulong[] _hasNaNs;
    private readonly ulong[] _empty;
    private readonly ulong[] _mayHoldNaN;

    /// <summary>A table of <paramref name="zones"/> zones with nothing recorded, for <see cref="TrySet"/> to fill.</summary>
    /// <param name="kind">The kind every bound is of: signed, unsigned or float.</param>
    /// <param name="zones">How many zones.</param>
    internal ZoneTable(FilterLiteralKind kind, int zones)
    {
        Kind = kind;
        ZoneCount = zones;
        int words = (zones + 63) >> 6;
        _min = new ulong[zones];
        _max = new ulong[zones];
        _nulls = new int[zones];
        _hasMin = new ulong[words];
        _hasMax = new ulong[words];
        _exact = new ulong[words];
        _hasNulls = new ulong[words];
        _hasNaNs = new ulong[words];
        _empty = new ulong[words];
        _mayHoldNaN = new ulong[words];
    }

    /// <summary>The kind every bound is of: signed, unsigned or float.</summary>
    internal FilterLiteralKind Kind { get; }

    /// <summary>How many zones the table describes.</summary>
    internal int ZoneCount { get; }

    /// <summary>
    /// The table of <paramref name="column"/>, or <see cref="None"/> when its bounds are not all of
    /// one numeric kind, or are unscaled decimals.
    /// </summary>
    /// <param name="column">The column's zones.</param>
    internal static ZoneTable Build(ZoneColumn column)
    {
        int zones = column.ZoneCount;
        if (column.IsDecimal || zones == 0)
        {
            return None;
        }

        FilterLiteralKind kind = FilterLiteralKind.Null;
        for (int zone = 0; zone < zones && kind == FilterLiteralKind.Null; zone++)
        {
            ZoneBounds bounds = column.Bounds(zone);
            kind = bounds.HasMin ? bounds.Min.Kind : bounds.HasMax ? bounds.Max.Kind : FilterLiteralKind.Null;
        }

        if (kind is not (FilterLiteralKind.Signed or FilterLiteralKind.Unsigned or FilterLiteralKind.Float))
        {
            return None;
        }

        ZoneTable table = new ZoneTable(kind, zones);
        for (int zone = 0; zone < zones; zone++)
        {
            if (!table.TrySet(zone, column.Bounds(zone), column.RowsInZone(zone)))
            {
                return None;
            }
        }

        return table;
    }

    /// <summary>Records zone <paramref name="zone"/>, of <paramref name="rows"/> rows.</summary>
    /// <param name="zone">The zone.</param>
    /// <param name="bounds">What its map says of it.</param>
    /// <param name="rows">How many rows it covers.</param>
    /// <returns>Whether it was recorded: not when a bound is of another kind than the table's.</returns>
    internal bool TrySet(int zone, in ZoneBounds bounds, long rows)
    {
        if ((bounds.HasMin && bounds.Min.Kind != Kind) || (bounds.HasMax && bounds.Max.Kind != Kind))
        {
            return false;
        }

        int word = zone >> 6;
        ulong bit = 1UL << (zone & 63);
        if (bounds.HasMin)
        {
            _min[zone] = bounds.Min.UnsignedValue;
            _hasMin[word] |= bit;
        }

        if (bounds.HasMax)
        {
            _max[zone] = bounds.Max.UnsignedValue;
            _hasMax[word] |= bit;
        }

        if (bounds.IsExact)
        {
            _exact[word] |= bit;
        }

        if (bounds.HasNullCount)
        {
            _nulls[zone] = (int)bounds.NullCount;
            _hasNulls[word] |= bit;
        }

        if (bounds.HasNanCount)
        {
            (_nans ??= new int[ZoneCount])[zone] = (int)bounds.NanCount;
            _hasNaNs[word] |= bit;
        }

        bool empty = bounds.HasNullCount && bounds.NullCount >= rows;
        if (empty)
        {
            _empty[word] |= bit;
        }

        // A zone with no bound says nothing of its kind, so it may hold a NaN; one with bounds
        // may only when they are floats, unless it counted its NaNs.
        bool nan = bounds.HasNanCount
            ? bounds.NanCount > 0
            : Kind == FilterLiteralKind.Float || !(bounds.HasMin || bounds.HasMax);
        if (nan && !empty)
        {
            _mayHoldNaN[word] |= bit;
        }

        return true;
    }

    /// <summary>Zone <paramref name="zone"/> as the zone-by-zone questions read it.</summary>
    /// <param name="zone">A zone index.</param>
    internal ZoneBounds Bounds(int zone)
    {
        if ((uint)zone >= (uint)ZoneCount)
        {
            return ZoneBounds.Unknown;
        }

        int word = zone >> 6;
        ulong bit = 1UL << (zone & 63);
        bool hasMin = (_hasMin[word] & bit) != 0;
        bool hasMax = (_hasMax[word] & bit) != 0;
        return ZoneBounds.Create(
            hasMin ? Literal(_min[zone]) : default,
            hasMin,
            hasMax ? Literal(_max[zone]) : default,
            hasMax,
            (_exact[word] & bit) != 0,
            _nulls[zone],
            (_hasNulls[word] & bit) != 0,
            _nans is { } nans ? nans[zone] : 0,
            (_hasNaNs[word] & bit) != 0);
    }

    /// <summary>A bound's literal, back from its pattern.</summary>
    private FilterLiteral Literal(ulong bits) => Kind switch
    {
        FilterLiteralKind.Signed => FilterLiteral.From(unchecked((long)bits)),
        FilterLiteralKind.Unsigned => FilterLiteral.From(bits),
        _ => FilterLiteral.From(BitConverter.UInt64BitsToDouble(bits)),
    };

    /// <summary>
    /// Writes into <paramref name="words"/> one bit per zone, set where <c>x op value</c> may hold on
    /// some row of it, when the table can answer for this constant.
    /// </summary>
    /// <param name="op">The comparison, any negation already pushed into it.</param>
    /// <param name="value">The constant.</param>
    /// <param name="nanMatches">Whether a NaN row satisfies the pushed-down comparison.</param>
    /// <param name="words">One bit per zone; a bit past the table's zones is set, as a zone with no bounds would be.</param>
    /// <returns>Whether it answered; when not, the zones are to be asked one by one.</returns>
    internal bool TryMayMatch(ComparisonOp op, FilterLiteral value, bool nanMatches, Span<ulong> words)
    {
        switch (Kind)
        {
            case FilterLiteralKind.Signed when value.Kind == FilterLiteralKind.Signed:
                Signed(op, value.SignedValue, words);
                break;
            case FilterLiteralKind.Unsigned when value.Kind == FilterLiteralKind.Unsigned:
                Unsigned(op, value.UnsignedValue, words);
                break;
            case FilterLiteralKind.Float when value.Kind == FilterLiteralKind.Float && !double.IsNaN(value.FloatValue):
                Float(op, value.FloatValue, words);
                break;

            // An integer constant against a float column is widened, as the kernels widen it for
            // every row.
            case FilterLiteralKind.Float when value.Kind == FilterLiteralKind.Signed:
                Float(op, value.SignedValue, words);
                break;
            case FilterLiteralKind.Float when value.Kind == FilterLiteralKind.Unsigned:
                Float(op, value.UnsignedValue, words);
                break;
            default:
                return false;
        }

        for (int i = 0; i < words.Length; i++)
        {
            ulong may = words[i] & ~Word(_empty, i);
            if (nanMatches)
            {
                may |= Word(_mayHoldNaN, i);
            }

            words[i] = may | Beyond(i);
        }

        return true;
    }

    private void Signed(ComparisonOp op, long value, Span<ulong> words)
    {
        switch (op)
        {
            case ComparisonOp.Greater:
                Upper(new SignedAbove(value), words);
                break;
            case ComparisonOp.GreaterOrEqual:
                Upper(new SignedAtLeast(value), words);
                break;
            case ComparisonOp.Less:
                Lower(new SignedBelow(value), words);
                break;
            case ComparisonOp.LessOrEqual:
                Lower(new SignedAtMost(value), words);
                break;
            case ComparisonOp.Equal:
                Inside(new SignedAbove(value), new SignedBelow(value), words);
                break;
            default:
                // Not equal rules out only a zone every value of which is the constant, which the
                // zone-by-zone question does not claim either.
                words.Fill(ulong.MaxValue);
                break;
        }
    }

    private void Unsigned(ComparisonOp op, ulong value, Span<ulong> words)
    {
        switch (op)
        {
            case ComparisonOp.Greater:
                Upper(new UnsignedAbove(value), words);
                break;
            case ComparisonOp.GreaterOrEqual:
                Upper(new UnsignedAtLeast(value), words);
                break;
            case ComparisonOp.Less:
                Lower(new UnsignedBelow(value), words);
                break;
            case ComparisonOp.LessOrEqual:
                Lower(new UnsignedAtMost(value), words);
                break;
            case ComparisonOp.Equal:
                Inside(new UnsignedAbove(value), new UnsignedBelow(value), words);
                break;
            default:
                words.Fill(ulong.MaxValue);
                break;
        }
    }

    /// <summary>
    /// The float shapes: a bound that does not order against the constant, a NaN, leaves an
    /// ordering open, so each test is the negation of its complement, which a NaN fails; and it
    /// proves no bound outside an equality, so those two are the plain tests, which a NaN fails too.
    /// </summary>
    private void Float(ComparisonOp op, double value, Span<ulong> words)
    {
        switch (op)
        {
            case ComparisonOp.Greater:
                Upper(new FloatNotAtMost(value), words);
                break;
            case ComparisonOp.GreaterOrEqual:
                Upper(new FloatNotBelow(value), words);
                break;
            case ComparisonOp.Less:
                Lower(new FloatNotAtLeast(value), words);
                break;
            case ComparisonOp.LessOrEqual:
                Lower(new FloatNotAbove(value), words);
                break;
            case ComparisonOp.Equal:
                Inside(new FloatAbove(value), new FloatBelow(value), words);
                break;
            default:
                words.Fill(ulong.MaxValue);
                break;
        }
    }

    /// <summary>A zone may hold <c>x above v</c> where its maximum passes <paramref name="test"/> or it has none.</summary>
    private void Upper<TTest>(TTest test, Span<ulong> words)
        where TTest : struct, IBoundTest
    {
        Fill(_max, test, words);
        for (int i = 0; i < words.Length; i++)
        {
            words[i] |= ~Word(_hasMax, i);
        }
    }

    /// <summary>A zone may hold <c>x below v</c> where its minimum passes <paramref name="test"/> or it has none.</summary>
    private void Lower<TTest>(TTest test, Span<ulong> words)
        where TTest : struct, IBoundTest
    {
        Fill(_min, test, words);
        for (int i = 0; i < words.Length; i++)
        {
            words[i] |= ~Word(_hasMin, i);
        }
    }

    /// <summary>
    /// A zone may hold <c>x = v</c> unless its minimum is above the constant or its maximum below it:
    /// both tests in one walk, no scratch.
    /// </summary>
    private void Inside<TAbove, TBelow>(TAbove above, TBelow below, Span<ulong> words)
        where TAbove : struct, IBoundTest
        where TBelow : struct, IBoundTest
    {
        int zones = _min.Length;
        for (int w = 0; w < words.Length; w++)
        {
            int start = w << 6;
            int end = Math.Min(start + 64, zones);
            ulong minAbove = 0;
            ulong maxBelow = 0;
            for (int zone = start; zone < end; zone++)
            {
                minAbove |= (ulong)Unsafe.BitCast<bool, byte>(above.Holds(_min[zone])) << (zone - start);
                maxBelow |= (ulong)Unsafe.BitCast<bool, byte>(below.Holds(_max[zone])) << (zone - start);
            }

            words[w] = ~((minAbove & Word(_hasMin, w)) | (maxBelow & Word(_hasMax, w)));
        }
    }

    /// <summary>The bits of word <paramref name="index"/> past the table's zones, which no bound describes.</summary>
    private ulong Beyond(int index)
    {
        int first = ZoneCount - (index << 6);
        return first >= 64 ? 0 : first <= 0 ? ulong.MaxValue : ulong.MaxValue << first;
    }

    private static ulong Word(ulong[] words, int index) => (uint)index < (uint)words.Length ? words[index] : 0;

    /// <summary>
    /// One bit per zone for <paramref name="test"/>, sixty-four zones a word: a compare and a shift a
    /// zone, no branch, the test inlined per shape. Words past the zones are cleared.
    /// </summary>
    private static void Fill<TTest>(ulong[] bounds, TTest test, Span<ulong> words)
        where TTest : struct, IBoundTest
    {
        int zones = bounds.Length;
        for (int w = 0; w < words.Length; w++)
        {
            int start = w << 6;
            int end = Math.Min(start + 64, zones);
            ulong word = 0;
            for (int zone = start; zone < end; zone++)
            {
                word |= (ulong)Unsafe.BitCast<bool, byte>(test.Holds(bounds[zone])) << (zone - start);
            }

            words[w] = word;
        }
    }

    private interface IBoundTest
    {
        bool Holds(ulong bound);
    }

    private readonly struct SignedAbove(long value) : IBoundTest
    {
        public bool Holds(ulong bound) => unchecked((long)bound) > value;
    }

    private readonly struct SignedAtLeast(long value) : IBoundTest
    {
        public bool Holds(ulong bound) => unchecked((long)bound) >= value;
    }

    private readonly struct SignedBelow(long value) : IBoundTest
    {
        public bool Holds(ulong bound) => unchecked((long)bound) < value;
    }

    private readonly struct SignedAtMost(long value) : IBoundTest
    {
        public bool Holds(ulong bound) => unchecked((long)bound) <= value;
    }

    private readonly struct UnsignedAbove(ulong value) : IBoundTest
    {
        public bool Holds(ulong bound) => bound > value;
    }

    private readonly struct UnsignedAtLeast(ulong value) : IBoundTest
    {
        public bool Holds(ulong bound) => bound >= value;
    }

    private readonly struct UnsignedBelow(ulong value) : IBoundTest
    {
        public bool Holds(ulong bound) => bound < value;
    }

    private readonly struct UnsignedAtMost(ulong value) : IBoundTest
    {
        public bool Holds(ulong bound) => bound <= value;
    }

    private readonly struct FloatAbove(double value) : IBoundTest
    {
        public bool Holds(ulong bound) => BitConverter.UInt64BitsToDouble(bound) > value;
    }

    private readonly struct FloatBelow(double value) : IBoundTest
    {
        public bool Holds(ulong bound) => BitConverter.UInt64BitsToDouble(bound) < value;
    }

    private readonly struct FloatNotAtMost(double value) : IBoundTest
    {
        public bool Holds(ulong bound) => !(BitConverter.UInt64BitsToDouble(bound) <= value);
    }

    private readonly struct FloatNotBelow(double value) : IBoundTest
    {
        public bool Holds(ulong bound) => !(BitConverter.UInt64BitsToDouble(bound) < value);
    }

    private readonly struct FloatNotAtLeast(double value) : IBoundTest
    {
        public bool Holds(ulong bound) => !(BitConverter.UInt64BitsToDouble(bound) >= value);
    }

    private readonly struct FloatNotAbove(double value) : IBoundTest
    {
        public bool Holds(ulong bound) => !(BitConverter.UInt64BitsToDouble(bound) > value);
    }
}
