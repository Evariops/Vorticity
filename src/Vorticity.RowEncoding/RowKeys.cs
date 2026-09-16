// The encoded rows: one contiguous byte buffer, plus where each row starts and how long it is.
//
// Rows are NOT self-delimiting - a row key carries no terminator and no length prefix, because
// either would have to sort somewhere and would perturb the very order the format exists to
// preserve. `Sizes` is therefore not a convenience, it is part of the representation.
using System;
using System.Buffers;
using System.Collections.Generic;
using Vorticity.Indexes;

namespace Vorticity.RowEncoding;

/// <summary>
/// Row keys produced by <see cref="RowEncoder"/>: byte strings whose <c>memcmp</c> order is the
/// tuple order of the columns they were built from.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not a durable format.</b> Upstream marks the row encoding experimental and reserves the
/// right to change its byte layout between Vortex releases. These bytes are safe to compare
/// within one process, or across a cluster running one version; they are NOT safe in a persisted
/// index, a checkpoint, or anything else that outlives the library version that produced it. The
/// version they follow is <see cref="RowEncoder.VortexVersion"/>.
/// </para>
/// <para>
/// The three buffers come from <see cref="ArrayPool{T}"/>; <see cref="Dispose"/> returns them, and
/// every span this type hands out is invalid afterwards.
/// </para>
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

    /// <summary>The key of row <paramref name="index"/>.</summary>
    /// <param name="index">0-based row index, below <see cref="RowCount"/>.</param>
    /// <returns>The row's bytes, borrowed from this instance.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The index is out of range.</exception>
    /// <exception cref="ObjectDisposedException">This instance has been disposed.</exception>
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
    /// <param name="left">0-based row index.</param>
    /// <param name="right">0-based row index.</param>
    /// <returns>Negative, zero or positive, as <see cref="ReadOnlySpan{T}"/> comparison gives it.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Either index is out of range.</exception>
    /// <exception cref="ObjectDisposedException">This instance has been disposed.</exception>
    public int Compare(int left, int right) => Row(left).SequenceCompareTo(Row(right));

    /// <summary>
    /// Sorts <paramref name="indices"/> so that the rows they name are in ascending key order -
    /// which, by construction, is the tuple order the columns and their
    /// <see cref="RowSortField"/>s describe.
    /// </summary>
    /// <param name="indices">Row indices to reorder; each must be below <see cref="RowCount"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">An index is out of range.</exception>
    /// <exception cref="ObjectDisposedException">This instance has been disposed.</exception>
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
