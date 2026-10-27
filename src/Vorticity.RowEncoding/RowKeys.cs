using System;
using System.Buffers;
using System.Collections.Generic;

namespace Vorticity.RowEncoding;

/// <summary>
/// Row keys produced by <see cref="RowEncoder"/>: byte strings whose <c>memcmp</c> order is the
/// tuple order of the columns they were built from. A key carries neither terminator nor length
/// prefix, so <see cref="Sizes"/> is part of the representation and not a convenience.
/// </summary>
/// <remarks>
/// Not a durable format: the byte layout may change between Vortex releases, so these bytes are
/// comparable only among keys produced by one version and never in a persisted index. The three
/// buffers come from <see cref="ArrayPool{T}"/>, and <see cref="Dispose"/> returns them and
/// invalidates every span handed out.
/// </remarks>
public sealed class RowKeys : IEncodedKeys
{
    private byte[]? _elements;
    private int[]? _offsets;
    private int[]? _sizes;
    private readonly int _rowCount;
    private readonly int _totalBytes;

    internal RowKeys(byte[] elements, int[] offsets, int[] sizes, int rowCount, int totalBytes)
    {
        _elements = elements;
        _offsets = offsets;
        _sizes = sizes;
        _rowCount = rowCount;
        _totalBytes = totalBytes;
    }

    /// <summary>How many rows were encoded.</summary>
    public int RowCount => _rowCount;

    /// <summary>The total number of encoded bytes across all rows.</summary>
    public int TotalBytes => _totalBytes;

    /// <summary>Every row's bytes, concatenated in row order.</summary>
    public ReadOnlySpan<byte> Elements => Live().AsSpan(0, _totalBytes);

    /// <summary>Where each row begins in <see cref="Elements"/>.</summary>
    public ReadOnlySpan<int> Offsets => _offsets is null ? ThrowDisposedInts() : _offsets.AsSpan(0, _rowCount);

    /// <summary>How many bytes each row occupies.</summary>
    public ReadOnlySpan<int> Sizes => _sizes is null ? ThrowDisposedInts() : _sizes.AsSpan(0, _rowCount);

    /// <summary>The key of the 0-based row <paramref name="index"/>, borrowed from this instance.</summary>
    public ReadOnlySpan<byte> Row(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _rowCount);
        byte[] elements = Live();
        int[] offsets = _offsets!;
        int[] sizes = _sizes!;
        return elements.AsSpan(offsets[index], sizes[index]);
    }

    /// <summary>Compares two rows the way a sort would: by their bytes.</summary>
    public int Compare(int left, int right) => Row(left).SequenceCompareTo(Row(right));

    /// <summary>
    /// Sorts <paramref name="indices"/> into ascending key order, which by construction is the
    /// tuple order the columns and their <see cref="RowSortField"/>s describe.
    /// </summary>
    public void SortIndices(Span<int> indices)
    {
        _ = Live();
        for (int i = 0; i < indices.Length; i++)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(indices[i]);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(indices[i], _rowCount);
        }

        indices.Sort(new ByKey(this));
    }

    /// <summary>
    /// Sorts <paramref name="indices"/>, rows of these keys given in ascending order, into ascending key order, rows of
    /// one key in ascending order: the order of a stable sort, as <c>memcmp</c> orders the keys.
    /// </summary>
    /// <remarks>
    /// By eight bytes at a time: each row's next eight bytes read as a big-endian number, zeros past its end, and the rows
    /// sorted by them with a stable radix sort, which skips the digits no row changes; then each run of rows that share
    /// those bytes and go on past them sorted by the next eight. A run of a few rows is sorted by comparison, its rows'
    /// bytes from there on, then their places. A key shared by many rows costs a pass a level, not a comparison a row.
    /// </remarks>
    internal void SortStable(Span<int> indices)
    {
        byte[] elements = Live();
        if (indices.Length <= ComparedRows)
        {
            Compared(indices, elements, 0);
            return;
        }

        ulong[] prefixes = ArrayPool<ulong>.Shared.Rent(indices.Length);
        ulong[] spare = ArrayPool<ulong>.Shared.Rent(indices.Length);
        int[] places = ArrayPool<int>.Shared.Rent(indices.Length);
        try
        {
            Radix(indices, prefixes.AsSpan(0, indices.Length), spare.AsSpan(0, indices.Length), places.AsSpan(0, indices.Length), elements, 0);
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(prefixes);
            ArrayPool<ulong>.Shared.Return(spare);
            ArrayPool<int>.Shared.Return(places);
        }
    }

    // The rows of a run sorted by comparison rather than by another radix pass.
    private const int ComparedRows = 48;

    /// <summary>The rows of <paramref name="indices"/> sorted by their bytes from <paramref name="at"/> on, then by their places.</summary>
    private void Radix(Span<int> indices, Span<ulong> prefixes, Span<ulong> spare, Span<int> places, byte[] elements, int at)
    {
        int[] offsets = _offsets!;
        int[] sizes = _sizes!;
        for (int i = 0; i < indices.Length; i++)
        {
            prefixes[i] = Prefix(elements, offsets[indices[i]], sizes[indices[i]], at);
        }

        RadixSort(prefixes, indices, spare, places);
        for (int start = 0, end; start < indices.Length; start = end)
        {
            end = start + 1;
            while (end < indices.Length && prefixes[end] == prefixes[start])
            {
                end++;
            }

            if (end - start < 2)
            {
                continue;
            }

            // Rows that share these eight bytes: all go on past them, or every one ends within them, the same key unless
            // one is a prefix of another, which a row encoding never makes; both are settled by comparison otherwise.
            int longer = 0;
            for (int i = start; i < end; i++)
            {
                longer += sizes[indices[i]] > at + sizeof(ulong) ? 1 : 0;
            }

            Span<int> run = indices[start..end];
            if (longer == run.Length && run.Length > ComparedRows)
            {
                Radix(run, prefixes[start..end], spare[start..end], places[start..end], elements, at + sizeof(ulong));
            }
            else if (longer > 0 || !SameSizes(run, sizes))
            {
                Compared(run, elements, at);
            }
        }
    }

    /// <summary>Whether the rows of <paramref name="run"/> hold keys of one size.</summary>
    private static bool SameSizes(ReadOnlySpan<int> run, int[] sizes)
    {
        int size = sizes[run[0]];
        foreach (int row in run)
        {
            if (sizes[row] != size)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The eight bytes of a key from <paramref name="at"/>, big-endian, zeros past its end.</summary>
    private static ulong Prefix(byte[] elements, int offset, int size, int at)
    {
        int left = size - at;
        if (left >= sizeof(ulong))
        {
            return System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(elements.AsSpan(offset + at, sizeof(ulong)));
        }

        ulong prefix = 0;
        for (int i = 0; i < left; i++)
        {
            prefix |= (ulong)elements[offset + at + i] << (56 - (8 * i));
        }

        return prefix;
    }

    /// <summary>
    /// Sorts <paramref name="keys"/>, and <paramref name="places"/> with them, by a stable least-significant-digit radix
    /// sort, through the spare arrays; the digits no key changes are skipped. Digits of sixteen bits for many keys, eight
    /// for fewer, whose counts would outweigh them.
    /// </summary>
    private static void RadixSort(Span<ulong> keys, Span<int> places, Span<ulong> spareKeys, Span<int> sparePlaces)
    {
        ulong first = keys[0];
        ulong varying = 0;
        foreach (ulong key in keys)
        {
            varying |= key ^ first;
        }

        if (varying == 0)
        {
            return;
        }

        int digitBits = keys.Length >= 1 << 16 ? 16 : 8;
        int[] counts = ArrayPool<int>.Shared.Rent(1 << digitBits);
        try
        {
            Span<ulong> fromKeys = keys;
            Span<int> fromPlaces = places;
            Span<ulong> toKeys = spareKeys;
            Span<int> toPlaces = sparePlaces;
            ulong digitMask = (1UL << digitBits) - 1;
            for (int shift = 0; shift < 64; shift += digitBits)
            {
                if (((varying >> shift) & digitMask) == 0)
                {
                    continue;
                }

                Span<int> count = counts.AsSpan(0, 1 << digitBits);
                count.Clear();
                foreach (ulong key in fromKeys)
                {
                    count[(int)((key >> shift) & digitMask)]++;
                }

                int sum = 0;
                for (int digit = 0; digit < count.Length; digit++)
                {
                    int held = count[digit];
                    count[digit] = sum;
                    sum += held;
                }

                for (int i = 0; i < fromKeys.Length; i++)
                {
                    int to = count[(int)((fromKeys[i] >> shift) & digitMask)]++;
                    toKeys[to] = fromKeys[i];
                    toPlaces[to] = fromPlaces[i];
                }

                Span<ulong> keysWere = fromKeys;
                Span<int> placesWere = fromPlaces;
                fromKeys = toKeys;
                fromPlaces = toPlaces;
                toKeys = keysWere;
                toPlaces = placesWere;
            }

            // An odd number of passes leaves the sorted keys in the spare arrays.
            if (fromKeys != keys)
            {
                fromKeys.CopyTo(keys);
                fromPlaces.CopyTo(places);
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(counts);
        }
    }

    /// <summary>
    /// Sorts the rows of <paramref name="run"/> by their bytes from <paramref name="at"/> on, then their indices: by
    /// insertion when they are few, by an introsort otherwise.
    /// </summary>
    private void Compared(Span<int> run, byte[] elements, int at)
    {
        ByKeyThenIndex order = new ByKeyThenIndex(elements, _offsets!, _sizes!, at);
        if (run.Length > ComparedRows)
        {
            run.Sort(order);
            return;
        }

        for (int i = 1; i < run.Length; i++)
        {
            int row = run[i];
            int j = i - 1;
            while (j >= 0 && order.Compare(run[j], row) > 0)
            {
                run[j + 1] = run[j];
                j--;
            }

            run[j + 1] = row;
        }
    }

    /// <summary>
    /// Whether rows <paramref name="left"/> and <paramref name="right"/> hold one key: their bytes, which end where a key's
    /// last value ends.
    /// </summary>
    internal bool Same(int left, int right)
    {
        byte[] elements = Live();
        return elements.AsSpan(_offsets![left], _sizes![left]).SequenceEqual(elements.AsSpan(_offsets[right], _sizes[right]));
    }

    /// <summary>Returns the pooled buffers. Every span handed out becomes invalid.</summary>
    public void Dispose()
    {
        byte[]? elements = _elements;
        int[]? offsets = _offsets;
        int[]? sizes = _sizes;
        _elements = null;
        _offsets = null;
        _sizes = null;

        if (elements is not null)
        {
            ArrayPool<byte>.Shared.Return(elements);
        }

        if (offsets is not null)
        {
            ArrayPool<int>.Shared.Return(offsets);
        }

        if (sizes is not null)
        {
            ArrayPool<int>.Shared.Return(sizes);
        }
    }

    private byte[] Live()
    {
        ObjectDisposedException.ThrowIf(_elements is null, this);
        return _elements;
    }

    private ReadOnlySpan<int> ThrowDisposedInts() => throw new ObjectDisposedException(nameof(RowKeys));

    /// <summary>A struct comparer, so sorting allocates nothing.</summary>
    private readonly struct ByKey(RowKeys keys) : IComparer<int>
    {
        public int Compare(int left, int right) => keys.Compare(left, right);
    }

    /// <summary>
    /// A struct comparer over the keys' buffers, unchecked, of their bytes from <c>at</c> on, the bytes before it equal; the
    /// rows of one key by their index.
    /// </summary>
    private readonly struct ByKeyThenIndex(byte[] elements, int[] offsets, int[] sizes, int at) : IComparer<int>
    {
        public int Compare(int left, int right)
        {
            int order = elements.AsSpan(offsets[left] + at, sizes[left] - at).SequenceCompareTo(elements.AsSpan(offsets[right] + at, sizes[right] - at));
            return order != 0 ? order : left.CompareTo(right);
        }
    }
}
