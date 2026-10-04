using System;
using System.Numerics;

namespace Vorticity.Aggregating;

/// <summary>
/// A group's first or last row in file order, kept as its position in the source: a range of rows
/// keeps its own, and the merge keeps the earlier range's first and the later range's last, so the
/// row is the same at every degree.
/// </summary>
internal sealed class RowSlot(bool last) : AggregateSlot<long>
{
    private long[] _rows = [];
    private int _groups;

    internal override void EnsureGroups(int groups)
    {
        if (groups > _rows.Length)
        {
            Array.Resize(ref _rows, Math.Max(groups, _rows.Length * 2));
        }

        _rows.AsSpan(_groups, Math.Max(groups - _groups, 0)).Fill(-1);
        _groups = Math.Max(_groups, groups);
    }

    internal override void StepRange(in BatchInput input, int start, int end, int group)
    {
        int row = last ? Last(input.Selection, start, end) : First(input.Selection, start, end);
        if (row >= 0)
        {
            Choose(ref _rows[group], input.StartRow + row);
        }
    }

    internal override void StepRows(in BatchInput input, ReadOnlySpan<int> groups)
    {
        long[] rows = _rows;
        RowCursor cursor = new RowCursor(input.Selection, 0, input.Rows);
        while (cursor.Next(out int row))
        {
            Choose(ref rows[groups[row]], input.StartRow + row);
        }
    }

    internal override void MergeFrom(AggregateSlot other, ReadOnlySpan<int> map)
    {
        RowSlot from = (RowSlot)other;
        for (int g = 0; g < from._groups; g++)
        {
            if (from._rows[g] >= 0)
            {
                Choose(ref _rows[map[g]], from._rows[g]);
            }
        }
    }

    internal override long Result(int group) => _rows[group];

    internal override void Keep(ReadOnlySpan<int> groups)
    {
        for (int i = 0; i < groups.Length; i++)
        {
            _rows[i] = _rows[groups[i]];
        }

        _groups = groups.Length;
    }

    /// <summary>The first selected row of [<paramref name="start"/>, <paramref name="end"/>), or -1.</summary>
    internal static int First(ReadOnlySpan<ulong> selection, int start, int end)
    {
        if (selection.IsEmpty)
        {
            return start < end ? start : -1;
        }

        RowCursor cursor = new RowCursor(selection, start, end);
        return cursor.Next(out int row) ? row : -1;
    }

    /// <summary>The last selected row of [<paramref name="start"/>, <paramref name="end"/>), or -1.</summary>
    internal static int Last(ReadOnlySpan<ulong> selection, int start, int end)
    {
        if (selection.IsEmpty)
        {
            return start < end ? end - 1 : -1;
        }

        for (int row = end - 1; row >= start; row--)
        {
            if (((selection[row >> 6] >> (row & 63)) & 1) != 0)
            {
                return row;
            }
        }

        return -1;
    }

    private void Choose(ref long kept, long row)
    {
        if (kept < 0 || (last ? row > kept : row < kept))
        {
            kept = row;
        }
    }
}

/// <summary>
/// The row of a group holding the smallest or the largest value of a fixed-width column, kept as
/// its position and the value: a null or a NaN is never one, and of equal values the first row in
/// file order wins, in a range as in the merge, so the row is the same at every degree.
/// </summary>
internal sealed class ChosenBySlot<TValue>(bool max, StorageKind kind) : AggregateSlot<long>
    where TValue : unmanaged, INumberBase<TValue>, IComparisonOperators<TValue, TValue, bool>
{
    private TValue[] _best = [];
    private long[] _rows = [];
    private int _groups;
    private ValuesCache<TValue> _values;
    private MaskCache _mask;

    internal override void EnsureGroups(int groups)
    {
        if (groups > _rows.Length)
        {
            int length = Math.Max(groups, _rows.Length * 2);
            Array.Resize(ref _rows, length);
            Array.Resize(ref _best, length);
        }

        _rows.AsSpan(_groups, Math.Max(groups - _groups, 0)).Fill(-1);
        _groups = Math.Max(_groups, groups);
    }

    internal override void StepRange(in BatchInput input, int start, int end, int group)
    {
        ReadOnlySpan<TValue> values = _values.Of(input.Arena, input.Batch, input.Node, kind, out ReadOnlySpan<ulong> valid);
        RowCursor cursor = new RowCursor(_mask.And(input, input.Selection, valid), start, end);
        while (cursor.Next(out int row))
        {
            Consider(group, values[row], input.StartRow + row);
        }
    }

    internal override void StepRows(in BatchInput input, ReadOnlySpan<int> groups)
    {
        ReadOnlySpan<TValue> values = _values.Of(input.Arena, input.Batch, input.Node, kind, out ReadOnlySpan<ulong> valid);
        RowCursor cursor = new RowCursor(_mask.And(input, input.Selection, valid), 0, input.Rows);
        while (cursor.Next(out int row))
        {
            Consider(groups[row], values[row], input.StartRow + row);
        }
    }

    internal override void MergeFrom(AggregateSlot other, ReadOnlySpan<int> map)
    {
        ChosenBySlot<TValue> from = (ChosenBySlot<TValue>)other;
        for (int g = 0; g < from._groups; g++)
        {
            if (from._rows[g] >= 0)
            {
                Consider(map[g], from._best[g], from._rows[g]);
            }
        }
    }

    internal override long Result(int group) => _rows[group];

    internal override void Keep(ReadOnlySpan<int> groups)
    {
        for (int i = 0; i < groups.Length; i++)
        {
            _rows[i] = _rows[groups[i]];
            _best[i] = _best[groups[i]];
        }

        _groups = groups.Length;
    }

    private void Consider(int group, TValue value, long row)
    {
        if (TValue.IsNaN(value))
        {
            return;
        }

        ref long kept = ref _rows[group];
        ref TValue best = ref _best[group];
        if (kept < 0 || (max ? value > best : value < best) || (value == best && row < kept))
        {
            best = value;
            kept = row;
        }
    }
}
