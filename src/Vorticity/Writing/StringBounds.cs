using System;
using System.Collections.Generic;
using System.Text;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>
/// One block's bounded string extremes. A null <c>Max</c> is the aggregate's <c>unknown</c>: no
/// bound could be built, and a reader prunes nothing on the maximum.
/// </summary>
internal readonly record struct ZoneString(bool Present, byte[]? Min, byte[]? Max);

/// <summary>A column writer's string bounds: the open block's accumulator and one entry per closed block.</summary>
internal sealed class StringZones
{
    private readonly List<ZoneString?> _closed = [];
    private StringBounds? _open;

    /// <param name="limit">The byte limit; at least 1.</param>
    internal StringZones(int limit) => Limit = limit;

    internal int Limit { get; }

    /// <summary>Folds a varbinview range into the open block.</summary>
    internal void Accumulate(CanonicalArena arena, CanonicalNode node, int start, int count)
    {
        _open ??= new StringBounds(Limit, node.DType.Kind == DTypeKind.Utf8);
        _open.Accumulate(arena, node, start, count);
    }

    /// <summary>Closes the open block; a column that never saw a string range records an empty zone.</summary>
    internal void Close() => _closed.Add(_open?.Close() ?? new ZoneString(false, null, null));

    /// <summary>Takes an old block's bounds, already cut; <see langword="null"/> when it has none.</summary>
    internal void Seed(ZoneString? zone) => _closed.Add(zone);

    /// <summary>Every block's bounds in order, or <see langword="null"/> when one block lacks them.</summary>
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

/// <summary>
/// A column's running string extremes over the open block. Keeping the first <c>limit + 1</c> bytes
/// of each extreme is exact: two values that agree on them cut to the same bound. A bound is cut at
/// close, from the block's own extreme, because cut bounds do not merge.
/// </summary>
internal sealed class StringBounds
{
    private readonly int _limit;
    private readonly bool _utf8;
    private readonly byte[] _min;
    private readonly byte[] _max;
    private int _minLength = -1;
    private int _maxLength = -1;

    /// <param name="limit">The byte limit; at least 1.</param>
    /// <param name="utf8">Whether the column is utf8, which decides where a cut may fall.</param>
    internal StringBounds(int limit, bool utf8)
    {
        _limit = limit;
        _utf8 = utf8;
        _min = new byte[limit + 1];
        _max = new byte[limit + 1];
    }

    /// <summary>Folds the valid rows <c>[start, start + count)</c> of a varbinview into the block.</summary>
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

    /// <summary>Adds one with carry, from the right; null when every byte wrapped.</summary>
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
    /// The last character becomes the next scalar value when that is a character of the same
    /// encoded width; nothing else is tried, and there is no carry into the character before.
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
