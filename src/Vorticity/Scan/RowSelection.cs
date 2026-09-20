using System;

using Vorticity.File;

namespace Vorticity.Scan;

/// <summary>
/// The row indices a take asks for, sorted and deduplicated once when the scan is built so that
/// everything downstream is a binary search rather than a membership test. The caller's order is
/// not preserved: a scan yields batches in file order, and matching an arbitrary index list would
/// mean buffering the whole result. Splits the list never touches are skipped before any segment is
/// registered; a split it does touch is decoded whole and the wanted rows gathered out of it.
/// </summary>
internal sealed class RowSelection
{
    private readonly long[] _rows;
    private int _count;

    private RowSelection(long[] rows)
    {
        _rows = rows;
        _count = rows.Length;
    }

    /// <summary>How many distinct rows were asked for.</summary>
    internal int Count => _count;

    /// <summary>The range that covers every requested row.</summary>
    internal RowRange Bounds =>
        _count == 0 ? RowRange.Empty : new RowRange(_rows[0], _rows[_count - 1] + 1);

    /// <summary>
    /// A selection over <paramref name="buffer"/>, which its owner refills and hands back through
    /// <see cref="Reset"/>: the key-ordered scan's one selection per scan rather than per window.
    /// </summary>
    /// <param name="buffer">The rows, owned by the caller.</param>
    internal static RowSelection Over(long[] buffer) => new RowSelection(buffer) { _count = 0 };

    /// <summary>Selects the first <paramref name="count"/> rows of the buffer.</summary>
    /// <param name="count">How many; the caller has sorted, deduplicated and bounded them.</param>
    internal void Reset(int count) => _count = count;

    /// <summary>Sorts and deduplicates <paramref name="rows"/>, rejecting anything out of range.</summary>
    /// <param name="rows">The wanted rows, in any order.</param>
    /// <param name="rowCount">The file's row count.</param>
    /// <returns>The compiled selection.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A row is negative or beyond the file.</exception>
    internal static RowSelection Create(ReadOnlySpan<long> rows, long rowCount)
    {
        if (rows.Length == 0)
        {
            return new RowSelection([]);
        }

        long[] sorted = rows.ToArray();
        Array.Sort(sorted);

        int kept = 1;
        CheckRow(sorted[0], rowCount);
        for (int i = 1; i < sorted.Length; i++)
        {
            CheckRow(sorted[i], rowCount);
            if (sorted[i] != sorted[kept - 1])
            {
                sorted[kept++] = sorted[i];
            }
        }

        return new RowSelection(kept == sorted.Length ? sorted : sorted[..kept]);
    }

    /// <summary>
    /// The requested rows inside <paramref name="split"/>, rebased to be local to it.
    /// </summary>
    /// <param name="split">The split's row range, in file coordinates.</param>
    /// <param name="destination">Receives the local indices; must hold <see cref="Count"/>.</param>
    /// <returns>How many rows of the split were asked for.</returns>
    internal int LocalIndices(RowRange split, Span<int> destination)
    {
        int first = LowerBound(split.Start);
        int count = 0;
        for (int i = first; i < _count && _rows[i] < split.End; i++)
        {
            destination[count++] = (int)(_rows[i] - split.Start);
        }

        return count;
    }

    /// <summary>Whether any requested row falls inside <paramref name="split"/>.</summary>
    /// <param name="split">The split's row range.</param>
    internal bool Touches(RowRange split)
    {
        int first = LowerBound(split.Start);
        return first < _count && _rows[first] < split.End;
    }

    /// <summary>How many selected rows fall in <paramref name="split"/>: two binary searches.</summary>
    /// <param name="split">A row range, in file coordinates.</param>
    internal int CountIn(RowRange split) => LowerBound(split.End) - LowerBound(split.Start);

    /// <summary>The first index whose row is at or after <paramref name="row"/>.</summary>
    private int LowerBound(long row)
    {
        int low = 0;
        int high = _count;
        while (low < high)
        {
            int mid = (int)(((uint)low + (uint)high) >> 1);
            if (_rows[mid] < row)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private static void CheckRow(long row, long rowCount)
    {
        if (row < 0 || row >= rowCount)
        {
            throw new ArgumentOutOfRangeException(
                "rows", row, $"The file has {rowCount} rows.");
        }
    }
}
