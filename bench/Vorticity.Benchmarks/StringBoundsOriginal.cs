using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Writing;

namespace Vorticity.Benchmarks;

/// <summary>
/// The library's <c>StringBounds</c> as it was, copied whole and kept here unchanged as the baseline
/// every change to it is measured against in the same process. What follows is the original's
/// own description: a column's running string extremes over the open block. Keeping the first
/// <c>limit + 1</c> bytes of each extreme is exact: two values that agree on them cut to the same
/// bound. A bound is cut at close, from the block's own extreme, because cut bounds do not merge.
/// </summary>
internal sealed class StringBoundsOriginal
{
    private readonly int _limit;
    private readonly bool _utf8;
    private readonly byte[] _min;
    private readonly byte[] _max;
    private int _minLength = -1;
    private int _maxLength = -1;

    /// <summary>The first eight bytes of the minimum and of the maximum, as <see cref="Key"/> reads them.</summary>
    private ulong _minKey;
    private ulong _maxKey;

    /// <param name="limit">The byte limit; at least 1.</param>
    /// <param name="utf8">Whether the column is utf8, which decides where a cut may fall.</param>
    internal StringBoundsOriginal(int limit, bool utf8)
    {
        _limit = limit;
        _utf8 = utf8;
        _min = new byte[limit + 1];
        _max = new byte[limit + 1];
    }

    /// <summary>Folds the valid rows <c>[start, start + count)</c> of a varbinview into the block.</summary>
    /// <remarks>
    /// <para>
    /// A row is compared with the bounds by key first: two strings whose keys differ compare as
    /// their keys do, so a column of short or diverse values is decided by two integer compares a
    /// row. A string of twelve bytes or fewer is read from its view, one word for its key, and never
    /// as a span; a key that ties a bound's is settled by the lengths while either value is eight
    /// bytes or fewer, and by the bytes past the eighth otherwise.
    /// </para>
    /// <para>
    /// Nearly every row changes nothing, and <see cref="Settled"/> passes over those in a loop
    /// that calls nothing, so that what it holds stays in registers rather than going through the
    /// stack around a call it would almost never make. The row it stops at is folded here.
    /// </para>
    /// </remarks>
    internal void Accumulate(CanonicalArena arena, CanonicalNode node, int start, int count)
    {
        ValidityMask mask = ValidityMask.From(arena, node.Validity);
        if (mask.AllInvalid)
        {
            return;
        }

        ReadOnlySpan<byte> views = node.Views.Span;
        ReadOnlySpan<byte> heap = node.DataBufferCount == 1 ? node.GetDataBuffer(0).Span : default;
        ReadOnlySpan<byte> bits = mask.AllValid ? default : mask.Bits;
        int end = start + count;
        for (int row = start; row < end; row++)
        {
            row = Settled(views, heap, bits, mask.BitOffset, row, end);
            if (row == end)
            {
                break;
            }

            Fold(node, views, in mask, row);
        }
    }

    /// <summary>The rank of a length past which equal keys no longer settle an order.</summary>
    private const int Unsettled = sizeof(ulong) + 1;

    /// <summary>
    /// The first row from <paramref name="row"/> on that might move a bound, or <paramref name="end"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A row is ranked by its key and then by its length, clamped at <see cref="Unsettled"/>: with
    /// a key tied, a value of eight bytes or fewer sorts by its length, and two longer ones sort by
    /// bytes the key does not hold -- which is <see cref="Fold"/>'s work, not this loop's. A row
    /// ranked between the bounds, or equal to a short one, is a value between them or equal to
    /// one, and changes nothing.
    /// </para>
    /// <para>
    /// A bound longer than eight bytes has its rank moved once, out of the loop, so that the
    /// comparison stops what only bytes can settle: the minimum's above every rank, so that any row
    /// of its key stops; the maximum's to eight, so that a long row of its key stops and a short
    /// one, a prefix of it, passes. The conditions are combined without short-circuits: on a
    /// column of few values a row ties a bound often and in no pattern.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int Settled(
        ReadOnlySpan<byte> views, ReadOnlySpan<byte> heap, ReadOnlySpan<byte> bits, int bitOffset,
        int row, int end)
    {
        if (_minLength < 0)
        {
            return row;
        }

        int cap = Math.Min(_limit + 1, Unsettled);
        ulong minKey = _minKey;
        ulong maxKey = _maxKey;
        int minRank = Math.Min(_minLength, Unsettled);
        int maxRank = Math.Min(_maxLength, Unsettled);
        minRank = minRank == Unsettled ? Unsettled + 1 : minRank;
        maxRank = maxRank == Unsettled ? sizeof(ulong) : maxRank;
        ReadOnlySpan<ulong> masks = KeyMasks;
        for (; row < end; row++)
        {
            if (!bits.IsEmpty && !CanonicalSupport.BitAt(bits, bitOffset + row))
            {
                continue;
            }

            ReadOnlySpan<byte> view = views.Slice(row * 16, 16);
            int size = BinaryPrimitives.ReadInt32LittleEndian(view);
            ReadOnlySpan<byte> first;
            if (size <= 12)
            {
                first = view.Slice(4, 8);
            }
            else if (!heap.IsEmpty)
            {
                first = heap.Slice(BinaryPrimitives.ReadInt32LittleEndian(view[12..]), 8);
            }
            else
            {
                return row;
            }

            int rank = size < cap ? size : cap;
            ulong key = BinaryPrimitives.ReadUInt64BigEndian(first) & masks[rank];
            if ((key < minKey) | ((key == minKey) & (rank < minRank))
                | (key > maxKey) | ((key == maxKey) & (rank > maxRank)))
            {
                return row;
            }
        }

        return end;
    }

    /// <summary>Per rank, the bits of a key that belong to the value: its first bytes, up to eight.</summary>
    private static ReadOnlySpan<ulong> KeyMasks =>
    [
        0x0000_0000_0000_0000, 0xFF00_0000_0000_0000, 0xFFFF_0000_0000_0000, 0xFFFF_FF00_0000_0000,
        0xFFFF_FFFF_0000_0000, 0xFFFF_FFFF_FF00_0000, 0xFFFF_FFFF_FFFF_0000, 0xFFFF_FFFF_FFFF_FF00,
        0xFFFF_FFFF_FFFF_FFFF, 0xFFFF_FFFF_FFFF_FFFF,
    ];

    /// <summary>Folds one row into the bounds: a row <see cref="Settled"/> stopped at.</summary>
    private void Fold(CanonicalNode node, ReadOnlySpan<byte> views, in ValidityMask mask, int row)
    {
        if (!mask.IsValid(row))
        {
            return;
        }

        ReadOnlySpan<byte> view = views.Slice(row * 16, 16);
        int size = BinaryPrimitives.ReadInt32LittleEndian(view);
        int length = Math.Min(size, _limit + 1);
        ulong key = Key(size <= 12 ? view.Slice(4, 8) : BlockStatsPass.Value(node, views, row), length);
        if (_minLength < 0
            || key < _minKey
            || (key == _minKey && Tie(node, views, row, length, _min.AsSpan(0, _minLength)) < 0))
        {
            BlockStatsPass.Value(node, views, row)[..length].CopyTo(_min);
            _minLength = length;
            _minKey = key;
        }

        if (_maxLength < 0
            || key > _maxKey
            || (key == _maxKey && Tie(node, views, row, length, _max.AsSpan(0, _maxLength)) > 0))
        {
            BlockStatsPass.Value(node, views, row)[..length].CopyTo(_max);
            _maxLength = length;
            _maxKey = key;
        }
    }

    /// <summary>
    /// The first eight of a value's first <paramref name="length"/> bytes, big-endian, a shorter
    /// value padded with zeros.
    /// </summary>
    /// <param name="bytes">At least eight bytes starting with the value's; those past its length are ignored.</param>
    /// <param name="length">The value's length, cut to the bound's.</param>
    /// <remarks>
    /// Where two keys differ, the values compare as the keys do: the first byte that differs is
    /// either a byte of both, or a padding zero against a byte of the longer value, and a value
    /// that is a prefix of another sorts first. Equal keys decide nothing -- "a" and "a\0" share
    /// one -- and <see cref="Tie"/> then does.
    /// </remarks>
    private static ulong Key(ReadOnlySpan<byte> bytes, int length) =>
        BinaryPrimitives.ReadUInt64BigEndian(bytes) & KeyMasks[Math.Min(length, Unsettled)];

    /// <summary>How a row compares with a bound whose key it shares.</summary>
    /// <remarks>
    /// A key holds the whole of a value of eight bytes or fewer, so when the shorter of the two is
    /// that short, equal keys make it a prefix of the other: the shorter sorts first, and equal
    /// lengths are equal values. Only two longer values compare bytes, and only past the eighth.
    /// </remarks>
    private static int Tie(
        CanonicalNode node, ReadOnlySpan<byte> views, int row, int length, ReadOnlySpan<byte> bound)
    {
        if (Math.Min(length, bound.Length) <= sizeof(ulong))
        {
            return length.CompareTo(bound.Length);
        }

        return BlockStatsPass.Value(node, views, row)[sizeof(ulong)..length]
            .SequenceCompareTo(bound[sizeof(ulong)..]);
    }

    /// <summary>The block's bounds, cut and kept in <paramref name="zones"/>; the accumulator starts the next block empty.</summary>
    /// <remarks>
    /// The upper bound is cut and incremented in the accumulator's own bytes, which the next block
    /// overwrites anyway, so the only copy made is the one <paramref name="zones"/> keeps.
    /// </remarks>
    internal ZoneString Close(StringZones zones)
    {
        if (_minLength < 0)
        {
            return new ZoneString(false, default, null);
        }

        ReadOnlySpan<byte> min = _min.AsSpan(0, _minLength);
        ReadOnlyMemory<byte> lower = zones.Keep(min.Length <= _limit ? min : min[..Cut(min, _limit, _utf8)]);

        // No `cond ? kept : null` here: its type would be the memory rather than the nullable, and
        // the null would pass through the conversion from an array and come out an empty bound
        // instead of an unknown one.
        Span<byte> max = _max.AsSpan(0, _maxLength);
        ReadOnlyMemory<byte>? upper = null;
        if (max.Length <= _limit)
        {
            upper = zones.Keep(max);
        }
        else
        {
            Span<byte> bound = max[..Cut(max, _limit, _utf8)];
            if (Increment(bound, _utf8))
            {
                upper = zones.Keep(bound);
            }
        }

        _minLength = -1;
        _maxLength = -1;
        return new ZoneString(true, lower, upper);
    }

    /// <summary>A value of at most <paramref name="limit"/> bytes that sorts at or below this one.</summary>
    internal static byte[] LowerBound(ReadOnlySpan<byte> value, int limit, bool utf8) =>
        value.Length <= limit ? value.ToArray() : value[..Cut(value, limit, utf8)].ToArray();

    /// <summary>
    /// A value of at most <paramref name="limit"/> bytes that sorts at or above this one, or
    /// <see langword="null"/> when none can be built.
    /// </summary>
    internal static byte[]? UpperBound(ReadOnlySpan<byte> value, int limit, bool utf8)
    {
        if (value.Length <= limit)
        {
            return value.ToArray();
        }

        byte[] bound = value[..Cut(value, limit, utf8)].ToArray();
        return Increment(bound, utf8) ? bound : null;
    }

    /// <summary>
    /// Where a value longer than <paramref name="limit"/> is cut: <paramref name="limit"/> for
    /// binary, and for utf8 the last character boundary in <c>[limit - 3, limit]</c>.
    /// </summary>
    private static int Cut(ReadOnlySpan<byte> value, int limit, bool utf8)
    {
        if (!utf8)
        {
            return limit;
        }

        // A position is a boundary when it is 0 or its byte is not a continuation byte, and a valid
        // string has one within any four consecutive positions.
        for (int p = limit; p >= Math.Max(limit - 3, 0); p--)
        {
            if (p == 0 || (value[p] & 0xC0) != 0x80)
            {
                return p;
            }
        }

        throw new InvalidOperationException("A utf8 value has no character boundary within four bytes.");
    }

    /// <summary>Turns a cut value into the upper bound, in place; false when there is none.</summary>
    private static bool Increment(Span<byte> bound, bool utf8) =>
        utf8 ? IncrementLastCharacter(bound) : IncrementBytes(bound);

    /// <summary>Adds one with carry, from the right; false when every byte wrapped.</summary>
    private static bool IncrementBytes(Span<byte> bound)
    {
        for (int i = bound.Length - 1; i >= 0; i--)
        {
            bound[i] = unchecked((byte)(bound[i] + 1));
            if (bound[i] != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The last character becomes the next scalar value when that is a character of the same
    /// encoded width; nothing else is tried, and there is no carry into the character before.
    /// </summary>
    private static bool IncrementLastCharacter(Span<byte> bound)
    {
        if (bound.Length == 0)
        {
            return false;
        }

        int last = bound.Length - 1;
        while (last > 0 && (bound[last] & 0xC0) == 0x80)
        {
            last--;
        }

        if (Rune.DecodeFromUtf8(bound[last..], out Rune rune, out int width) != System.Buffers.OperationStatus.Done
            || last + width != bound.Length)
        {
            throw new InvalidOperationException("A utf8 bound does not end on a whole character.");
        }

        if (!Rune.IsValid(rune.Value + 1))
        {
            return false;
        }

        Rune next = new Rune(rune.Value + 1);
        if (next.Utf8SequenceLength != width)
        {
            return false;
        }

        next.EncodeToUtf8(bound[last..]);
        return true;
    }
}
