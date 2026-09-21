using System;
using System.Buffers;
using System.Collections.Generic;
using Vorticity.Indexes;

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
internal sealed class RowKeys : IEncodedKeys
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
}
