using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>
/// One block's bounded string extremes. A null <c>Max</c> is the aggregate's <c>unknown</c>: no
/// bound could be built, and a reader prunes nothing on the maximum.
/// </summary>
internal readonly record struct ZoneString(bool Present, ReadOnlyMemory<byte> Min, ReadOnlyMemory<byte>? Max);

/// <summary>A column writer's string bounds: the open block's accumulator and one entry per closed block.</summary>
/// <remarks>
/// The bounds' bytes are slices of chunks this owns and never copies: a zone's two bounds are a
/// few dozen bytes, and an array each would be two objects per zone of every text column. A chunk
/// is only ever appended to, so a slice handed out stays valid whatever the chunks after it do.
/// </remarks>
internal sealed class StringZones : IReadOnlyList<ZoneString>
{
    /// <summary>The largest chunk, past which the chunks stop doubling.</summary>
    private const int MaxChunk = 1 << 16;

    private readonly AppendList<ZoneString?> _closed = new AppendList<ZoneString?>();
    private StringBounds? _open;
    private byte[] _chunk = [];
    private int _chunkUsed;

    /// <summary>Every chunk rented so far, to give back at <see cref="Release"/>.</summary>
    private byte[][] _chunks = [];
    private int _chunkCount;

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
    internal void Close() => _closed.Add(_open?.Close(this) ?? new ZoneString(false, default, null));

    /// <summary>Takes an old block's bounds, already cut; <see langword="null"/> when it has none.</summary>
    internal void Seed(ZoneString? zone) => _closed.Add(zone);

    /// <summary>
    /// Every block's bounds in order, read through this list, or <see langword="null"/> when one
    /// block lacks them.
    /// </summary>
    internal IReadOnlyList<ZoneString>? All(int blocks)
    {
        if (_closed.Count != blocks)
        {
            return null;
        }

        for (int i = 0; i < blocks; i++)
        {
            if (_closed[i] is null)
            {
                return null;
            }
        }

        return this;
    }

    /// <summary>A copy of <paramref name="bytes"/> in the chunks, for a bound that outlives the accumulator.</summary>
    internal ReadOnlyMemory<byte> Keep(ReadOnlySpan<byte> bytes)
    {
        if (_chunk.Length - _chunkUsed < bytes.Length)
        {
            // The chunk before stays where it is: the slices already handed out point into it. The
            // first holds one zone's two bounds, which is all a column of one block ever needs.
            int first = 2 * (Limit + 1);
            _chunk = ArrayPool<byte>.Shared.Rent(Math.Max(bytes.Length, Math.Min(Math.Max(_chunk.Length * 2, first), MaxChunk)));
            _chunkUsed = 0;
            if (_chunkCount == _chunks.Length)
            {
                byte[][] grown = ArrayPool<byte[]>.Shared.Rent(Math.Max(4, _chunks.Length * 2));
                _chunks.AsSpan(0, _chunkCount).CopyTo(grown);
                if (_chunks.Length != 0)
                {
                    ArrayPool<byte[]>.Shared.Return(_chunks, clearArray: true);
                }

                _chunks = grown;
            }

            _chunks[_chunkCount++] = _chunk;
        }

        Memory<byte> slice = _chunk.AsMemory(_chunkUsed, bytes.Length);
        bytes.CopyTo(slice.Span);
        _chunkUsed += bytes.Length;
        return slice;
    }

    /// <summary>
    /// Gives the chunks and the list back to the shared pools, once the file is done: the bounds
    /// handed out are slices of those chunks, and nothing reads them any more.
    /// </summary>
    internal void Release()
    {
        _closed.Release();
        for (int i = 0; i < _chunkCount; i++)
        {
            ArrayPool<byte>.Shared.Return(_chunks[i]);
        }

        if (_chunks.Length != 0)
        {
            ArrayPool<byte[]>.Shared.Return(_chunks, clearArray: true);
        }

        _chunks = [];
        _chunkCount = 0;
        _chunk = [];
        _chunkUsed = 0;
    }

    /// <summary>Closed blocks, once <see cref="All"/> has vouched that every one has its bounds.</summary>
    public int Count => _closed.Count;

    /// <summary>Block <paramref name="index"/>'s bounds.</summary>
    public ZoneString this[int index] => _closed[index] ?? throw new InvalidOperationException(
        $"Block {index} has no string bounds; All should have refused the column.");

    /// <inheritdoc/>
    public IEnumerator<ZoneString> GetEnumerator()
    {
        for (int i = 0; i < _closed.Count; i++)
        {
            yield return this[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
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

    /// <summary>The first eight bytes of the minimum and of the maximum, as <see cref="Key"/> reads them.</summary>
    private ulong _minKey;
    private ulong _maxKey;

    /// <summary>The minimum and the maximum ranked whole, as <see cref="Within"/> compares them; set with the bounds.</summary>
    private Rank _lowRank;
    private Rank _highRank;

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
        ReadOnlySpan<VortexBuffer> buffers = node.DataBuffers;
        ReadOnlySpan<byte> bits = mask.AllValid ? default : mask.Bits;
        int end = start + count;
        for (int row = start; row < end; row++)
        {
            row = Settled(views, heap, bits, mask.BitOffset, row, end);
            if (row == end)
            {
                break;
            }

            Fold(buffers, views, in mask, row);
        }
    }

    /// <summary>The most bytes a bound keeps for which <see cref="Within"/> orders values whole.</summary>
    private const int ExactBytes = 17;

    /// <summary>
    /// Whether a row whose key ties a bound's lies between the bounds, its kept bytes ranked whole
    /// as its first eight bytes, its next eight, its seventeenth and its length; false when its
    /// bytes cannot all be read as words, for <see cref="Fold"/> to decide.
    /// </summary>
    /// <remarks>
    /// Each part is big-endian and padded with zeros, so the four compare in order as the bytes do,
    /// and a value that is a prefix of another sorts first by its length. A column whose values
    /// share their first eight bytes, as URLs and paths do, then stops only at a new extreme,
    /// rather than at every row.
    /// </remarks>
    private static bool Within(
        ReadOnlySpan<byte> view, int size, ReadOnlySpan<byte> heap, int keep, in Rank low, in Rank high)
    {
        ReadOnlySpan<ulong> masks = KeyMasks;
        int length = size < keep ? size : keep;
        Rank rank;
        if (size <= 12)
        {
            rank = new Rank(
                BinaryPrimitives.ReadUInt64BigEndian(view[4..]) & masks[Math.Min(length, 8)],
                ((ulong)BinaryPrimitives.ReadUInt32BigEndian(view[12..]) << 32) & masks[Math.Max(length - 8, 0)],
                0,
                length);
        }
        else
        {
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(view[12..]);
            if ((ulong)offset + 16 > (ulong)heap.Length)
            {
                return false;
            }

            ReadOnlySpan<byte> value = heap[(int)offset..];
            rank = new Rank(
                BinaryPrimitives.ReadUInt64BigEndian(value) & masks[Math.Min(length, 8)],
                BinaryPrimitives.ReadUInt64BigEndian(value[8..]) & masks[Math.Clamp(length - 8, 0, 8)],
                length == ExactBytes ? value[16] : 0u,
                length);
        }

        return !(rank.Below(low) | high.Below(rank));
    }

    /// <summary>A value's first seventeen bytes and its length, cut to the bound, as <see cref="Within"/> orders them.</summary>
    private readonly struct Rank(ulong head, ulong next, uint last, int length)
    {
        private readonly ulong _head = head;
        private readonly ulong _next = next;
        private readonly uint _last = last;
        private readonly int _length = length;

        /// <summary>The rank of a bound's kept bytes.</summary>
        internal static Rank Of(ReadOnlySpan<byte> bound)
        {
            Span<byte> padded = stackalloc byte[24];
            padded.Clear();
            // Ranks are compared only under a limit that keeps bounds within seventeen bytes; a
            // longer bound, under a larger limit, is cut so that it fits and is never compared.
            bound[..Math.Min(bound.Length, ExactBytes)].CopyTo(padded);
            return new Rank(
                BinaryPrimitives.ReadUInt64BigEndian(padded),
                BinaryPrimitives.ReadUInt64BigEndian(padded[8..]),
                bound.Length == ExactBytes ? padded[16] : 0u,
                bound.Length);
        }

        /// <summary>Whether this value sorts before <paramref name="other"/>, with no branch.</summary>
        internal bool Below(Rank other) =>
            (_head < other._head) | ((_head == other._head) &
                ((_next < other._next) | ((_next == other._next) &
                    ((_last < other._last) | ((_last == other._last) & (_length < other._length))))));
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

        // With bounds short enough to rank whole, a row stopped by a key it shares with a bound is
        // ranked in full before it is let go, against the bounds' ranks Fold keeps.
        int keep = _limit + 1;
        bool exact = keep <= ExactBytes;

        // Where there are 512-bit vectors, four views are one register, and a row of four bytes or
        // more whose first four, big-endian, lie strictly between the bounds' is settled from its
        // view: its key does too. Four rows so settled, or null, pass together; otherwise the four
        // go through the loop below one at a time, and a group that did not pass holds the vectors
        // back for twice as many rows as the one before, up to 64: a column whose rows mostly tie a
        // bound's prefix pays a compare a stretch, not one every four rows. Bounds whose prefixes
        // have none between them -- a column of rows opening alike, which is common -- settle no
        // row this way, and the vectors are not tried at all.
        uint low = (uint)(minKey >> 32);
        uint high = (uint)(maxKey >> 32);
        bool lanes = Vector512.IsHardwareAccelerated && Avx512BW.IsSupported && high > low && high - low > 1;
        int scalarUntil = row;
        int backoff = 4;
        for (; row < end; row++)
        {
            if (lanes && row >= scalarUntil)
            {
                int settled = SettledGroups(views, bits, bitOffset, row, end, low, high);
                if (settled != row)
                {
                    backoff = 4;
                    row = settled;
                    if (row == end)
                    {
                        break;
                    }
                }

                scalarUntil = row + backoff;
                backoff = Math.Min(backoff * 2, 64);
            }

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
                if (exact && key >= minKey && key <= maxKey && Within(view, size, heap, keep, in _lowRank, in _highRank))
                {
                    continue;
                }

                return row;
            }
        }

        return end;
    }

    /// <summary>
    /// The rows from <paramref name="row"/> that pass four at a time, each null or of four bytes or
    /// more whose first four, big-endian, lie strictly between <paramref name="low"/> and
    /// <paramref name="high"/>; returns the first row of the first four that do not all pass.
    /// </summary>
    /// <remarks>
    /// A method of its own so that the row loop, which calls it once a stretch, holds none of its
    /// vectors: sharing a body with them pushed that loop's state to the stack.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int SettledGroups(
        ReadOnlySpan<byte> views, ReadOnlySpan<byte> bits, int bitOffset, int row, int end, uint low, uint high)
    {
        ref uint words = ref Unsafe.As<byte, uint>(ref MemoryMarshal.GetReference(views));
        Vector512<uint> lowPrefix = Vector512.Create(low);
        Vector512<uint> highPrefix = Vector512.Create(high);
        Vector512<byte> prefixBytes = Vector512.Create(PrefixBytes);
        for (; row <= end - 4; row += 4)
        {
            Vector512<uint> four = Vector512.LoadUnsafe(ref words, (nuint)(row * 4));
            // Both shuffles stay inside each view's 128-bit lane: `vpshufb` and `vpshufd`, named, since
            // a general shuffle the JIT did not see as constant was emulated, at a quarter the speed.
            Vector512<uint> prefix = Avx512BW.Shuffle(four.AsByte(), prefixBytes).AsUInt32();
            Vector512<uint> lengths = Avx512F.Shuffle(four, 0);
            uint inside = (uint)(Vector512.GreaterThanOrEqual(lengths, Vector512.Create(4u))
                & Vector512.GreaterThan(prefix, lowPrefix) & Vector512.LessThan(prefix, highPrefix))
                .ExtractMostSignificantBits();

            // The prefix lanes are 1, 5, 9 and 13; a null row passes at 0, 4, 8 or 12 once its
            // validity bit is moved there.
            uint passed = inside >> 1;
            if (!bits.IsEmpty)
            {
                uint valid = (uint)BitWords.Load(bits, bitOffset + row) & 0xF;
                passed |= ~((valid & 1) | ((valid & 2) << 3) | ((valid & 4) << 6) | ((valid & 8) << 9));
            }

            if ((passed & 0x1111) != 0x1111)
            {
                break;
            }
        }

        return row;
    }

    /// <summary>
    /// For a view in a 128-bit lane, its prefix word, bytes 4 to 7, reversed in place so that it
    /// reads big-endian; the other bytes are don't-cares.
    /// </summary>
    private static Vector128<byte> PrefixBytes => Vector128.Create((byte)0, 0, 0, 0, 7, 6, 5, 4, 0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>Per rank, the bits of a key that belong to the value: its first bytes, up to eight.</summary>
    private static ReadOnlySpan<ulong> KeyMasks =>
    [
        0x0000_0000_0000_0000, 0xFF00_0000_0000_0000, 0xFFFF_0000_0000_0000, 0xFFFF_FF00_0000_0000,
        0xFFFF_FFFF_0000_0000, 0xFFFF_FFFF_FF00_0000, 0xFFFF_FFFF_FFFF_0000, 0xFFFF_FFFF_FFFF_FF00,
        0xFFFF_FFFF_FFFF_FFFF, 0xFFFF_FFFF_FFFF_FFFF,
    ];

    /// <summary>Folds one row into the bounds: a row <see cref="Settled"/> stopped at.</summary>
    private void Fold(ReadOnlySpan<VortexBuffer> buffers, ReadOnlySpan<byte> views, in ValidityMask mask, int row)
    {
        if (!mask.AllValid && !mask.IsValid(row))
        {
            return;
        }

        // The value is read once, and a key that ties a bound's is settled by integer compares of
        // the bytes past it, since the bounds hold at most `limit + 1` of them.
        ReadOnlySpan<byte> view = views.Slice(row * 16, 16);
        int size = BinaryPrimitives.ReadInt32LittleEndian(view);
        int length = Math.Min(size, _limit + 1);
        ReadOnlySpan<byte> whole = size <= 12 ? view.Slice(4, 12) : BlockStatsPass.Value(buffers, views, row);
        ReadOnlySpan<byte> value = whole[..length];
        ulong key = Key(whole, length);
        if (_minLength < 0
            || key < _minKey
            || (key == _minKey && Tie(value, _min.AsSpan(0, _minLength)) < 0))
        {
            value.CopyTo(_min);
            _minLength = length;
            _minKey = key;
            _lowRank = Rank.Of(value);
        }

        if (_maxLength < 0
            || key > _maxKey
            || (key == _maxKey && Tie(value, _max.AsSpan(0, _maxLength)) > 0))
        {
            value.CopyTo(_max);
            _maxLength = length;
            _maxKey = key;
            _highRank = Rank.Of(value);
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

    /// <summary>How a row's value, cut to the bound's length, compares with a bound whose key it shares.</summary>
    /// <remarks>
    /// A key holds the whole of a value of eight bytes or fewer, so when the shorter of the two is
    /// that short, equal keys make it a prefix of the other: the shorter sorts first, and equal
    /// lengths are equal values. Only two longer values compare bytes, and only past the eighth:
    /// eight at a time as big-endian words, then byte by byte, with no call.
    /// </remarks>
    private static int Tie(ReadOnlySpan<byte> value, ReadOnlySpan<byte> bound)
    {
        int shorter = Math.Min(value.Length, bound.Length);
        int i = sizeof(ulong);
        for (; i <= shorter - sizeof(ulong); i += sizeof(ulong))
        {
            ulong mine = BinaryPrimitives.ReadUInt64BigEndian(value[i..]);
            ulong theirs = BinaryPrimitives.ReadUInt64BigEndian(bound[i..]);
            if (mine != theirs)
            {
                return mine < theirs ? -1 : 1;
            }
        }

        for (; i < shorter; i++)
        {
            if (value[i] != bound[i])
            {
                return value[i] < bound[i] ? -1 : 1;
            }
        }

        return value.Length.CompareTo(bound.Length);
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
