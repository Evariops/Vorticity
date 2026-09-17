// The bounded string extremes of docs/11-write-strategy.md §3.2, "str_min, str_max bounded
// prefixes (strings, policy)": per block, the smallest and the largest value of a utf8 or binary
// column, cut to at most `n` bytes the way the reference cuts them, for the zone map's
// `vortex.bounded_min(n)` and `vortex.bounded_max(n)`.
//
// THE RULES ARE THE REFERENCE'S, LINE FOR LINE (vortex-array-0.86.1 scalar/truncation.rs and
// scalar/typed_view/utf8.rs):
//   * a value of at most `n` bytes is its own bound, both ways;
//   * the lower bound of a longer value is its prefix, cut for utf8 at the last character boundary
//     in [n - 3, n];
//   * the upper bound of a longer binary value is its first `n` bytes plus one, the carry running
//     leftward and the bytes it wrapped left at zero; all of them wrapping is no bound;
//   * the upper bound of a longer utf8 value is its cut prefix with the LAST CHARACTER replaced by
//     the next scalar value — and no bound at all when that value is not a character, or does not
//     encode on as many bytes. The reference does not carry into the character before; neither
//     does this.
// "No bound" is the aggregate's `unknown`, which a reader takes as "prune nothing on the maximum".
//
// ONLY n + 1 BYTES OF EACH EXTREME ARE KEPT, and that is exact rather than approximate. Two values
// that differ in their first n + 1 bytes are ordered by them; two that agree on them are both
// longer than n, and both cuts read no byte past index n — the boundary test at n included — so
// they cut to the same bound whichever of the two is kept. A value of at most n bytes is kept
// whole. So the accumulator holds two buffers of n + 1 bytes and never a row of the batch, whose
// arena is recycled before the block closes.
//
// PER BLOCK, FROM THE BLOCK'S EXACT EXTREMES: the bound is cut once, at close. Merging cut bounds
// is not the same thing for the maximum — "ab" and "abc" cut to "ac" and "abd" — and the reference
// cuts each zone's own extreme when a zone arrives as one array, which the zoned writer's
// repartitioning makes the case.
using System;
using System.Collections.Generic;
using System.Text;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>One block's bounded string extremes, as the zone map writes them.</summary>
/// <param name="Present">Whether the block held a valid value; an absent block's partials are null.</param>
/// <param name="Min">The lower bound; meaningful when <paramref name="Present"/>.</param>
/// <param name="Max">The upper bound, or <see langword="null"/> when none fits: the aggregate's <c>unknown</c>.</param>
internal readonly record struct ZoneString(bool Present, byte[]? Min, byte[]? Max);

/// <summary>A column writer's string bounds: the open block's accumulator and one entry per closed block.</summary>
internal sealed class StringZones
{
    private readonly List<ZoneString?> _closed = [];
    private StringBounds? _open;

    /// <summary>Creates the state for bounds of at most <paramref name="limit"/> bytes.</summary>
    /// <param name="limit">The byte limit; at least 1.</param>
    internal StringZones(int limit) => Limit = limit;

    /// <summary>The byte limit.</summary>
    internal int Limit { get; }

    /// <summary>Folds a varbinview range into the open block.</summary>
    /// <param name="arena">The arena holding the batch.</param>
    /// <param name="node">The column.</param>
    /// <param name="start">First row of the range.</param>
    /// <param name="count">How many rows.</param>
    internal void Accumulate(CanonicalArena arena, CanonicalNode node, int start, int count)
    {
        _open ??= new StringBounds(Limit, node.DType.Kind == DTypeKind.Utf8);
        _open.Accumulate(arena, node, start, count);
    }

    /// <summary>Closes the open block; a column that never saw a string range records an empty zone.</summary>
    internal void Close() => _closed.Add(_open?.Close() ?? new ZoneString(false, null, null));

    /// <summary>Takes an old block's bounds, already cut; <see langword="null"/> when it has none.</summary>
    /// <param name="zone">The old zone's entry.</param>
    internal void Seed(ZoneString? zone) => _closed.Add(zone);

    /// <summary>Every block's bounds, or <see langword="null"/> when one block lacks them.</summary>
    /// <param name="blocks">How many blocks the column has closed.</param>
    /// <returns>The entries, in block order.</returns>
    internal ZoneString[]? All(int blocks)
    {
        if (_closed.Count != blocks)
        {
            return null;
        }

        ZoneString[] zones = new ZoneString[blocks];
        for (int i = 0; i < blocks; i++)
        {
            if (_closed[i] is not { } zone)
            {
                return null;
            }

            zones[i] = zone;
        }

        return zones;
    }
}

/// <summary>A column's running string extremes over the open block.</summary>
internal sealed class StringBounds
{
    private readonly int _limit;
    private readonly bool _utf8;
    private readonly byte[] _min;
    private readonly byte[] _max;
    private int _minLength = -1;
    private int _maxLength = -1;

    /// <summary>Creates an accumulator for bounds of at most <paramref name="limit"/> bytes.</summary>
    /// <param name="limit">The <c>n</c> of <c>vortex.bounded_min(n)</c>; at least 1.</param>
    /// <param name="utf8">Whether the column is utf8, which decides where a cut may fall.</param>
    internal StringBounds(int limit, bool utf8)
    {
        _limit = limit;
        _utf8 = utf8;
        _min = new byte[limit + 1];
        _max = new byte[limit + 1];
    }

    /// <summary>Folds the valid rows <c>[start, start + count)</c> of a varbinview into the block.</summary>
    /// <param name="arena">The arena holding the batch.</param>
    /// <param name="node">The column.</param>
    /// <param name="start">First row of the range.</param>
    /// <param name="count">How many rows.</param>
    internal void Accumulate(CanonicalArena arena, CanonicalNode node, int start, int count)
    {
        ValidityMask mask = ValidityMask.From(arena, node.Validity);
        ReadOnlySpan<byte> views = node.Views.Span;
        int keep = _limit + 1;
        for (int row = start; row < start + count; row++)
        {
            if (!mask.IsValid(row))
            {
                continue;
            }

            ReadOnlySpan<byte> value = BlockStatsPass.Value(node, views, row);
            ReadOnlySpan<byte> prefix = value.Length > keep ? value[..keep] : value;
            if (_minLength < 0 || prefix.SequenceCompareTo(_min.AsSpan(0, _minLength)) < 0)
            {
                prefix.CopyTo(_min);
                _minLength = prefix.Length;
            }

            if (_maxLength < 0 || prefix.SequenceCompareTo(_max.AsSpan(0, _maxLength)) > 0)
            {
                prefix.CopyTo(_max);
                _maxLength = prefix.Length;
            }
        }
    }

    /// <summary>The block's bounds, cut; the accumulator starts the next block empty.</summary>
    /// <returns>The zone's entry.</returns>
    internal ZoneString Close()
    {
        if (_minLength < 0)
        {
            return new ZoneString(false, null, null);
        }

        ZoneString zone = new ZoneString(
            true,
            LowerBound(_min.AsSpan(0, _minLength), _limit, _utf8),
            UpperBound(_max.AsSpan(0, _maxLength), _limit, _utf8));
        _minLength = -1;
        _maxLength = -1;
        return zone;
    }

    /// <summary>
    /// A value of at most <paramref name="limit"/> bytes that sorts at or below
    /// <paramref name="value"/>: the reference's <c>lower_bound</c>.
    /// </summary>
    /// <param name="value">The value, or its first <c>limit + 1</c> bytes.</param>
    /// <param name="limit">The byte limit.</param>
    /// <param name="utf8">Whether the cut must fall on a character boundary.</param>
    /// <returns>The bound.</returns>
    internal static byte[] LowerBound(ReadOnlySpan<byte> value, int limit, bool utf8) =>
        value.Length <= limit ? value.ToArray() : value[..Cut(value, limit, utf8)].ToArray();

    /// <summary>
    /// A value of at most <paramref name="limit"/> bytes that sorts at or above
    /// <paramref name="value"/>, or <see langword="null"/> when none is built: the reference's
    /// <c>upper_bound</c>.
    /// </summary>
    /// <param name="value">The value, or its first <c>limit + 1</c> bytes.</param>
    /// <param name="limit">The byte limit.</param>
    /// <param name="utf8">Whether the value is utf8, which increments a character rather than a byte.</param>
    /// <returns>The bound, or <see langword="null"/>.</returns>
    internal static byte[]? UpperBound(ReadOnlySpan<byte> value, int limit, bool utf8)
    {
        if (value.Length <= limit)
        {
            return value.ToArray();
        }

        byte[] bound = value[..Cut(value, limit, utf8)].ToArray();
        return utf8 ? IncrementLastCharacter(bound) : IncrementBytes(bound);
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

        // `is_char_boundary(p)` for p < len: p is 0, or byte p is not a continuation byte. A
        // valid string has a boundary within any four consecutive positions.
        for (int p = limit; p >= Math.Max(limit - 3, 0); p--)
        {
            if (p == 0 || (value[p] & 0xC0) != 0x80)
            {
                return p;
            }
        }

        throw new InvalidOperationException("A utf8 value has no character boundary within four bytes.");
    }

    /// <summary>The reference's binary <c>upper_bound</c> step: add one with carry, from the right.</summary>
    private static byte[]? IncrementBytes(byte[] bound)
    {
        for (int i = bound.Length - 1; i >= 0; i--)
        {
            bound[i] = unchecked((byte)(bound[i] + 1));
            if (bound[i] != 0)
            {
                return bound;
            }
        }

        return null;
    }

    /// <summary>
    /// The reference's <c>BufferString::increment</c>: the last character becomes the next scalar
    /// value when that is a character of the same encoded width, and nothing else is tried.
    /// </summary>
    private static byte[]? IncrementLastCharacter(byte[] bound)
    {
        if (bound.Length == 0)
        {
            return null;
        }

        int last = bound.Length - 1;
        while (last > 0 && (bound[last] & 0xC0) == 0x80)
        {
            last--;
        }

        if (Rune.DecodeFromUtf8(bound.AsSpan(last), out Rune rune, out int width) != System.Buffers.OperationStatus.Done
            || last + width != bound.Length)
        {
            throw new InvalidOperationException("A utf8 bound does not end on a whole character.");
        }

        if (!Rune.IsValid(rune.Value + 1))
        {
            return null;
        }

        Rune next = new Rune(rune.Value + 1);
        if (next.Utf8SequenceLength != width)
        {
            return null;
        }

        next.EncodeToUtf8(bound.AsSpan(last));
        return bound;
    }
}
