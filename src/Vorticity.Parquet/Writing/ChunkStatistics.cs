using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Numerics;
using System.Text;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Compute;
using Vorticity.Parquet.Geospatial;
using Vorticity.Parquet.Metadata;
using Vorticity.Writing;

namespace Vorticity.Parquet.Writing;

/// <summary>A page's statistics, as the page index holds them: its bounds empty when it holds no value.</summary>
internal readonly record struct PageStatistics(bool NullPage, long Nulls, byte[] Min, byte[] Max);

/// <summary>A closed chunk's statistics: its bounds as PLAIN bytes without a length, its counts, and its pages'.</summary>
internal sealed record WrittenStatistics(
    bool HasBounds,
    byte[] Min,
    byte[] Max,
    bool MinExact,
    bool MaxExact,
    long Nulls,
    bool CountsNans,
    long Nans,
    PageStatistics[] Pages,
    BoundaryOrder Order,
    GeospatialStatistics? Geospatial = null)
{
    /// <summary>Whether the chunk's pages are bounded, so that a column index is written for it.</summary>
    internal bool Indexed => HasBounds || Pages.Length > 0 && Array.TrueForAll(Pages, page => page.NullPage);
}

/// <summary>
/// A column chunk's statistics, gathered a page at a time from the page's PLAIN values before any
/// other encoding: each page's bounds and counts, which the page index holds, merged into the
/// chunk's, which its metadata holds.
/// </summary>
/// <remarks>
/// <para>
/// A bound is compared in its domain's order: integers as their sign says, floats in IEEE 754's
/// total order from their non-NaN values alone, which the standard recommends and whose NaN count
/// is then written, booleans false first, byte arrays as unsigned bytes, a fixed-length decimal as
/// signed big-endian.
/// </para>
/// <para>
/// A byte array's bound past <see cref="BoundBytes"/> bytes is cut, and marked not exact: a lower
/// bound to its prefix, an upper bound to its prefix with its last unit raised, so that both still
/// bound. A text's are cut at a code point, its upper bound's last code point raised past the
/// surrogates, so that both stay UTF-8; a bound that cannot be raised is written whole.
/// </para>
/// </remarks>
internal sealed class ChunkStatistics(WriteColumn column)
{
    /// <summary>The longest byte array bound written whole.</summary>
    internal const int BoundBytes = 64;

    private readonly StatisticsDomain _domain = column.Domain;
    private readonly LogicalTypeKind _geospatial = column.Geospatial;
    private readonly WkbBounds? _box = column.Geospatial == LogicalTypeKind.None ? null : new WkbBounds();
    private readonly int _width = column.ValueWidth;
    private readonly List<PageStatistics> _pages = [];
    private byte[] _min = [];
    private byte[] _max = [];
    private bool _minExact;
    private bool _maxExact;
    private bool _bounded;
    private long _nulls;
    private long _nans;

    private bool CountsNans => _domain is StatisticsDomain.Float32 or StatisticsDomain.Float64 or StatisticsDomain.Float16;

    /// <summary>Adds a closed page: <paramref name="values"/> PLAIN values in <paramref name="plain"/>, and its entries without one.</summary>

    internal void AddPage(ReadOnlySpan<byte> plain, int values, int nulls)
    {
        _nulls += nulls;
        _box?.AddPlain(plain, values);
        if (_domain == StatisticsDomain.None)
        {
            return;
        }

        if (values == 0)
        {
            _pages.Add(new PageStatistics(true, nulls, [], []));
            return;
        }

        Span<byte> lowScratch = stackalloc byte[8];
        Span<byte> highScratch = stackalloc byte[8];
        Extremes(plain, values, lowScratch, highScratch, out ReadOnlySpan<byte> low, out ReadOnlySpan<byte> high, out long nans);
        _nans += nans;
        byte[] min = Lower(low, out bool minExact);
        byte[] max = Upper(high, out bool maxExact);
        _pages.Add(new PageStatistics(false, nulls, min, max));

        // Ties go to the exact bound: an inexact one equal to it bounds no value past it.
        if (!_bounded || Compare(min, _min) < 0 || (Compare(min, _min) == 0 && minExact))
        {
            _min = min;
            _minExact = minExact;
        }

        if (!_bounded || Compare(max, _max) > 0 || (Compare(max, _max) == 0 && maxExact))
        {
            _max = max;
            _maxExact = maxExact;
        }

        _bounded = true;
    }

    /// <summary>The chunk's statistics, closed; the gathering starts over for the next chunk.</summary>
    internal WrittenStatistics Close()
    {
        PageStatistics[] pages = [.. _pages];
        WrittenStatistics written = new(
            _bounded, _min, _max, _minExact, _maxExact, _nulls, CountsNans, _nans, pages, OrderOf(pages),
            _box?.Close(_geospatial == LogicalTypeKind.Geography));
        _pages.Clear();
        _min = [];
        _max = [];
        _bounded = false;
        _minExact = false;
        _maxExact = false;
        _nulls = 0;
        _nans = 0;
        return written;
    }

    /// <summary>Whether the pages' bounds climb, fall, or neither, the pages without a value left out.</summary>
    private BoundaryOrder OrderOf(PageStatistics[] pages)
    {
        bool ascending = true;
        bool descending = true;
        PageStatistics? previous = null;
        foreach (PageStatistics page in pages)
        {
            if (page.NullPage)
            {
                continue;
            }

            if (previous is { } before)
            {
                int min = Compare(before.Min, page.Min);
                int max = Compare(before.Max, page.Max);
                ascending &= min <= 0 && max <= 0;
                descending &= min >= 0 && max >= 0;
            }

            previous = page;
        }

        return ascending ? BoundaryOrder.Ascending : descending ? BoundaryOrder.Descending : BoundaryOrder.Unordered;
    }

    /// <summary>The page's least and greatest values, PLAIN, and its NaNs.</summary>
    private void Extremes(ReadOnlySpan<byte> plain, int count, Span<byte> lowScratch, Span<byte> highScratch, out ReadOnlySpan<byte> low, out ReadOnlySpan<byte> high, out long nans)
    {
        nans = 0;
        switch (_domain)
        {
            case StatisticsDomain.Signed32:
                BlockStatsPass.Bounds(MemoryMarshal.Cast<byte, int>(plain[..(count * 4)]), out int min32, out int max32);
                BinaryPrimitives.WriteInt32LittleEndian(lowScratch, min32);
                BinaryPrimitives.WriteInt32LittleEndian(highScratch, max32);
                low = lowScratch[..4];
                high = highScratch[..4];
                return;
            case StatisticsDomain.Unsigned32:
                BlockStatsPass.Bounds(MemoryMarshal.Cast<byte, uint>(plain[..(count * 4)]), out uint minU32, out uint maxU32);
                BinaryPrimitives.WriteUInt32LittleEndian(lowScratch, minU32);
                BinaryPrimitives.WriteUInt32LittleEndian(highScratch, maxU32);
                low = lowScratch[..4];
                high = highScratch[..4];
                return;
            case StatisticsDomain.Signed64:
                BlockStatsPass.Bounds(MemoryMarshal.Cast<byte, long>(plain[..(count * 8)]), out long min64, out long max64);
                BinaryPrimitives.WriteInt64LittleEndian(lowScratch, min64);
                BinaryPrimitives.WriteInt64LittleEndian(highScratch, max64);
                low = lowScratch[..8];
                high = highScratch[..8];
                return;
            case StatisticsDomain.Unsigned64:
                BlockStatsPass.Bounds(MemoryMarshal.Cast<byte, ulong>(plain[..(count * 8)]), out ulong minU64, out ulong maxU64);
                BinaryPrimitives.WriteUInt64LittleEndian(lowScratch, minU64);
                BinaryPrimitives.WriteUInt64LittleEndian(highScratch, maxU64);
                low = lowScratch[..8];
                high = highScratch[..8];
                return;
            case StatisticsDomain.Float32:
                nans = FloatExtremes(MemoryMarshal.Cast<byte, int>(plain[..(count * 4)]), out int lowBits32, out int highBits32);
                BinaryPrimitives.WriteInt32LittleEndian(lowScratch, lowBits32);
                BinaryPrimitives.WriteInt32LittleEndian(highScratch, highBits32);
                low = lowScratch[..4];
                high = highScratch[..4];
                return;
            case StatisticsDomain.Float64:
                nans = DoubleExtremes(MemoryMarshal.Cast<byte, long>(plain[..(count * 8)]), out long lowBits64, out long highBits64);
                BinaryPrimitives.WriteInt64LittleEndian(lowScratch, lowBits64);
                BinaryPrimitives.WriteInt64LittleEndian(highScratch, highBits64);
                low = lowScratch[..8];
                high = highScratch[..8];
                return;
            case StatisticsDomain.Float16:
                nans = HalfExtremes(MemoryMarshal.Cast<byte, short>(plain[..(count * 2)]), out short lowBits16, out short highBits16);
                BinaryPrimitives.WriteInt16LittleEndian(lowScratch, lowBits16);
                BinaryPrimitives.WriteInt16LittleEndian(highScratch, highBits16);
                low = lowScratch[..2];
                high = highScratch[..2];
                return;
            case StatisticsDomain.Boolean:
                int ones = BitmapKernels.CountSet(plain, 0, count);
                lowScratch[0] = (byte)(ones == count ? 1 : 0);
                highScratch[0] = (byte)(ones > 0 ? 1 : 0);
                low = lowScratch[..1];
                high = highScratch[..1];
                return;
            case StatisticsDomain.Binary:
            case StatisticsDomain.Utf8:
                PrefixedExtremes(plain, count, out low, out high);
                return;
            default:
                FixedExtremes(plain, count, out low, out high);
                return;
        }
    }

    /// <summary>
    /// The least and greatest of length-prefixed byte arrays, as unsigned bytes: each value's first
    /// eight bytes, big-endian, an integer whose order is theirs, so that a value is compared whole
    /// only where its prefix ties a bound's.
    /// </summary>
    private static void PrefixedExtremes(ReadOnlySpan<byte> plain, int count, out ReadOnlySpan<byte> low, out ReadOnlySpan<byte> high)
    {
        int at = 0;
        low = default;
        high = default;
        ulong lowKey = 0;
        ulong highKey = 0;
        for (int i = 0; i < count; i++)
        {
            int length = BinaryPrimitives.ReadInt32LittleEndian(plain[at..]);
            ReadOnlySpan<byte> value = plain.Slice(at + sizeof(int), length);
            at += sizeof(int) + length;
            ulong key = Prefix(value);
            if (i == 0)
            {
                low = value;
                high = value;
                lowKey = key;
                highKey = key;
                continue;
            }

            if (key < lowKey || (key == lowKey && value.SequenceCompareTo(low) < 0))
            {
                low = value;
                lowKey = key;
            }

            if (key > highKey || (key == highKey && value.SequenceCompareTo(high) > 0))
            {
                high = value;
                highKey = key;
            }
        }
    }

    /// <summary>A byte array's first eight bytes as a big-endian integer, zeros past its end.</summary>
    private static ulong Prefix(ReadOnlySpan<byte> value)
    {
        if (value.Length >= sizeof(ulong))
        {
            return BinaryPrimitives.ReadUInt64BigEndian(value);
        }

        ulong key = 0;
        for (int b = 0; b < value.Length; b++)
        {
            key |= (ulong)value[b] << (56 - (8 * b));
        }

        return key;
    }

    /// <summary>The least and greatest of values of the column's fixed width, in the domain's order.</summary>
    private void FixedExtremes(ReadOnlySpan<byte> plain, int count, out ReadOnlySpan<byte> low, out ReadOnlySpan<byte> high)
    {
        low = plain[.._width];
        high = low;
        for (int i = 1; i < count; i++)
        {
            ReadOnlySpan<byte> value = plain.Slice(i * _width, _width);
            if (Compare(value, low) < 0)
            {
                low = value;
            }

            if (Compare(value, high) > 0)
            {
                high = value;
            }
        }
    }

    /// <summary>
    /// The least and greatest non-NaN floats in IEEE 754's total order, by their bits; the NaNs.
    /// When every value is NaN, the least and greatest NaN, as the standard asks.
    /// </summary>
    private static long FloatExtremes(ReadOnlySpan<int> bits, out int low, out int high)
    {
        long nans = 0;
        int lowKey = int.MaxValue;
        int highKey = int.MinValue;
        int i = 0;
        if (Vector256.IsHardwareAccelerated && bits.Length >= Vector256<int>.Count)
        {
            // The keys without a branch: a negative value's magnitude flipped, a NaN's replaced by
            // the bound neither end keeps, and counted.
            ref int input = ref MemoryMarshal.GetReference(bits);
            Vector256<int> magnitude = Vector256.Create(0x7FFF_FFFF);
            Vector256<int> infinity = Vector256.Create(0x7F80_0000);
            Vector256<int> top = Vector256.Create(int.MaxValue);
            Vector256<int> bottom = Vector256.Create(int.MinValue);
            Vector256<int> lows = top;
            Vector256<int> highs = bottom;
            for (; i <= bits.Length - Vector256<int>.Count; i += Vector256<int>.Count)
            {
                Vector256<int> value = Vector256.LoadUnsafe(ref input, (nuint)i);
                Vector256<int> nan = Vector256.GreaterThan(value & magnitude, infinity);
                Vector256<int> key = value ^ (Vector256.LessThan(value, Vector256<int>.Zero) & magnitude);
                nans += BitOperations.PopCount(nan.ExtractMostSignificantBits());
                lows = Vector256.Min(lows, Vector256.ConditionalSelect(nan, top, key));
                highs = Vector256.Max(highs, Vector256.ConditionalSelect(nan, bottom, key));
            }

            for (int lane = 0; lane < Vector256<int>.Count; lane++)
            {
                lowKey = Math.Min(lowKey, lows[lane]);
                highKey = Math.Max(highKey, highs[lane]);
            }
        }

        foreach (int value in bits[i..])
        {
            if ((value & 0x7FFF_FFFF) > 0x7F80_0000)
            {
                nans++;
                continue;
            }

            int key = Key(value);
            lowKey = Math.Min(lowKey, key);
            highKey = Math.Max(highKey, key);
        }

        if (nans == bits.Length)
        {
            foreach (int value in bits)
            {
                lowKey = Math.Min(lowKey, Key(value));
                highKey = Math.Max(highKey, Key(value));
            }
        }

        low = Key(lowKey);
        high = Key(highKey);
        return nans;
    }

    /// <summary><see cref="FloatExtremes"/> of doubles.</summary>
    private static long DoubleExtremes(ReadOnlySpan<long> bits, out long low, out long high)
    {
        long nans = 0;
        long lowKey = long.MaxValue;
        long highKey = long.MinValue;
        int i = 0;
        if (Vector256.IsHardwareAccelerated && bits.Length >= Vector256<long>.Count)
        {
            ref long input = ref MemoryMarshal.GetReference(bits);
            Vector256<long> magnitude = Vector256.Create(0x7FFF_FFFF_FFFF_FFFFL);
            Vector256<long> infinity = Vector256.Create(0x7FF0_0000_0000_0000L);
            Vector256<long> top = Vector256.Create(long.MaxValue);
            Vector256<long> bottom = Vector256.Create(long.MinValue);
            Vector256<long> lows = top;
            Vector256<long> highs = bottom;
            for (; i <= bits.Length - Vector256<long>.Count; i += Vector256<long>.Count)
            {
                Vector256<long> value = Vector256.LoadUnsafe(ref input, (nuint)i);
                Vector256<long> nan = Vector256.GreaterThan(value & magnitude, infinity);
                Vector256<long> key = value ^ (Vector256.LessThan(value, Vector256<long>.Zero) & magnitude);
                nans += BitOperations.PopCount(nan.ExtractMostSignificantBits());
                lows = Vector256.Min(lows, Vector256.ConditionalSelect(nan, top, key));
                highs = Vector256.Max(highs, Vector256.ConditionalSelect(nan, bottom, key));
            }

            for (int lane = 0; lane < Vector256<long>.Count; lane++)
            {
                lowKey = Math.Min(lowKey, lows[lane]);
                highKey = Math.Max(highKey, highs[lane]);
            }
        }

        foreach (long value in bits[i..])
        {
            if ((value & 0x7FFF_FFFF_FFFF_FFFF) > 0x7FF0_0000_0000_0000)
            {
                nans++;
                continue;
            }

            long key = Key(value);
            lowKey = Math.Min(lowKey, key);
            highKey = Math.Max(highKey, key);
        }

        if (nans == bits.Length)
        {
            foreach (long value in bits)
            {
                lowKey = Math.Min(lowKey, Key(value));
                highKey = Math.Max(highKey, Key(value));
            }
        }

        low = Key(lowKey);
        high = Key(highKey);
        return nans;
    }

    /// <summary><see cref="FloatExtremes"/> of half floats.</summary>
    private static long HalfExtremes(ReadOnlySpan<short> bits, out short low, out short high)
    {
        long nans = 0;
        short lowKey = short.MaxValue;
        short highKey = short.MinValue;
        foreach (short value in bits)
        {
            if ((value & 0x7FFF) > 0x7C00)
            {
                nans++;
                continue;
            }

            short key = Key(value);
            lowKey = Math.Min(lowKey, key);
            highKey = Math.Max(highKey, key);
        }

        if (nans == bits.Length)
        {
            foreach (short value in bits)
            {
                lowKey = Math.Min(lowKey, Key(value));
                highKey = Math.Max(highKey, Key(value));
            }
        }

        low = Key(lowKey);
        high = Key(highKey);
        return nans;
    }

    // A float's bits as an integer whose signed order is IEEE 754's total order: a negative one's
    // magnitude bits flipped. The map is its own inverse.
    private static int Key(int bits) => bits < 0 ? bits ^ 0x7FFF_FFFF : bits;

    private static long Key(long bits) => bits < 0 ? bits ^ 0x7FFF_FFFF_FFFF_FFFF : bits;

    private static short Key(short bits) => bits < 0 ? (short)(bits ^ 0x7FFF) : bits;

    /// <summary>Two PLAIN bounds compared in the domain's order.</summary>
    private int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => _domain switch
    {
        StatisticsDomain.Signed32 => BinaryPrimitives.ReadInt32LittleEndian(a).CompareTo(BinaryPrimitives.ReadInt32LittleEndian(b)),
        StatisticsDomain.Unsigned32 => BinaryPrimitives.ReadUInt32LittleEndian(a).CompareTo(BinaryPrimitives.ReadUInt32LittleEndian(b)),
        StatisticsDomain.Signed64 => BinaryPrimitives.ReadInt64LittleEndian(a).CompareTo(BinaryPrimitives.ReadInt64LittleEndian(b)),
        StatisticsDomain.Unsigned64 => BinaryPrimitives.ReadUInt64LittleEndian(a).CompareTo(BinaryPrimitives.ReadUInt64LittleEndian(b)),
        StatisticsDomain.Float32 => Key(BinaryPrimitives.ReadInt32LittleEndian(a)).CompareTo(Key(BinaryPrimitives.ReadInt32LittleEndian(b))),
        StatisticsDomain.Float64 => Key(BinaryPrimitives.ReadInt64LittleEndian(a)).CompareTo(Key(BinaryPrimitives.ReadInt64LittleEndian(b))),
        StatisticsDomain.Float16 => Key(BinaryPrimitives.ReadInt16LittleEndian(a)).CompareTo(Key(BinaryPrimitives.ReadInt16LittleEndian(b))),
        StatisticsDomain.Decimal => SignedCompare(a, b),
        _ => a.SequenceCompareTo(b),
    };

    /// <summary>Big-endian two's complement of one length compared: the sign byte signed, the rest unsigned.</summary>
    private static int SignedCompare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        int sign = ((sbyte)a[0]).CompareTo((sbyte)b[0]);
        return sign != 0 ? sign : a[1..].SequenceCompareTo(b[1..]);
    }

    /// <summary>A lower bound of at most <see cref="BoundBytes"/>, cut for a byte array, and whether it is the value itself.</summary>
    private byte[] Lower(ReadOnlySpan<byte> value, out bool exact)
    {
        exact = true;
        if (_domain is not (StatisticsDomain.Binary or StatisticsDomain.Utf8) || value.Length <= BoundBytes)
        {
            return value.ToArray();
        }

        exact = false;
        int cut = _domain == StatisticsDomain.Utf8 ? CodePointBoundary(value, BoundBytes) : BoundBytes;
        return value[..cut].ToArray();
    }

    /// <summary>An upper bound of about <see cref="BoundBytes"/>, raised for a byte array, and whether it is the value itself.</summary>
    private byte[] Upper(ReadOnlySpan<byte> value, out bool exact)
    {
        exact = true;
        if (_domain is not (StatisticsDomain.Binary or StatisticsDomain.Utf8) || value.Length <= BoundBytes)
        {
            return value.ToArray();
        }

        byte[]? raised = _domain == StatisticsDomain.Utf8 ? RaiseText(value) : RaiseBytes(value);
        if (raised is null)
        {
            // Nothing in the prefix can be raised: the value itself is the bound.
            return value.ToArray();
        }

        exact = false;
        return raised;
    }

    /// <summary>The prefix of <see cref="BoundBytes"/> bytes with its last byte that is not 0xFF raised, those after it dropped; null when all are 0xFF.</summary>
    private static byte[]? RaiseBytes(ReadOnlySpan<byte> value)
    {
        for (int last = BoundBytes - 1; last >= 0; last--)
        {
            if (value[last] != 0xFF)
            {
                byte[] raised = value[..(last + 1)].ToArray();
                raised[last]++;
                return raised;
            }
        }

        return null;
    }

    /// <summary>The prefix cut at a code point, its last code point that can be raised raised past the surrogates; null when none can.</summary>
    private static byte[]? RaiseText(ReadOnlySpan<byte> value)
    {
        ReadOnlySpan<byte> prefix = value[..CodePointBoundary(value, BoundBytes)];
        List<int> starts = [];
        for (int at = 0; at < prefix.Length;)
        {
            if (System.Text.Rune.DecodeFromUtf8(prefix[at..], out _, out int consumed) != System.Buffers.OperationStatus.Done)
            {
                return null;
            }

            starts.Add(at);
            at += consumed;
        }

        for (int k = starts.Count - 1; k >= 0; k--)
        {
            System.Text.Rune.DecodeFromUtf8(prefix[starts[k]..], out System.Text.Rune rune, out _);
            if (rune.Value >= 0x10FFFF)
            {
                continue;
            }

            int next = rune.Value + 1;
            if (next is >= 0xD800 and <= 0xDFFF)
            {
                next = 0xE000;
            }

            Span<byte> encoded = stackalloc byte[4];
            int length = new System.Text.Rune(next).EncodeToUtf8(encoded);
            byte[] raised = new byte[starts[k] + length];
            prefix[..starts[k]].CopyTo(raised);
            encoded[..length].CopyTo(raised.AsSpan(starts[k]));
            return raised;
        }

        return null;
    }

    /// <summary>The longest prefix of at most <paramref name="limit"/> bytes that ends between code points.</summary>
    private static int CodePointBoundary(ReadOnlySpan<byte> value, int limit)
    {
        int cut = Math.Min(limit, value.Length);
        while (cut > 0 && cut < value.Length && (value[cut] & 0xC0) == 0x80)
        {
            cut--;
        }

        return cut;
    }
}
