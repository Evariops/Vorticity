using System;
using System.Numerics;

namespace Vorticity.Aggregating;

/// <summary>
/// A group's first or last row in file order, kept as its position in the source: a range of rows
/// keeps its own, and the merge keeps the earlier range's first and the later range's last, so the
/// row is the same at every degree.
/// </summary>
internal sealed class RowSlot(bool last) : RecordSlot<long, long>
{
    internal override long Seed => -1;

    internal override void StepRange(in BatchInput input, int start, int end, int group)
    {
        int row = last ? Last(input.Selection, start, end) : First(input.Selection, start, end);
        if (row >= 0)
        {
            Choose(ref State(group), input.StartRow + row);
        }
    }

    internal override void StepRows(in BatchInput input, ReadOnlySpan<int> groups)
    {
        StateView<long> rows = States;
        RowCursor cursor = new RowCursor(input.Selection, 0, input.Rows);
        while (cursor.Next(out int row))
        {
            Choose(ref rows[groups[row]], input.StartRow + row);
        }
    }

    internal override void MergeFrom(AggregateSlot other, ReadOnlySpan<int> from, ReadOnlySpan<int> into)
    {
        StateView<long> rows = States;
        StateView<long> others = StatesOf(other);
        for (int i = 0; i < from.Length; i++)
        {
            long row = others[from[i]];
            if (row >= 0)
            {
                Choose(ref rows[into[i]], row);
            }
        }
    }

    internal override long Result(int group) => State(group);

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
internal sealed class ChosenBySlot<TValue>(bool max, StorageKind kind) : RecordSlot<ChosenRow<TValue>, long>
    where TValue : unmanaged, INumberBase<TValue>, IComparisonOperators<TValue, TValue, bool>
{
    private ValuesCache<TValue> _values;
    private MaskCache _mask;

    internal override ChosenRow<TValue> Seed => new ChosenRow<TValue> { Row = -1 };

    internal override void StepRange(in BatchInput input, int start, int end, int group)
    {
        ReadOnlySpan<TValue> values = _values.Of(input.Arena, input.Batch, input.Node, kind, out ReadOnlySpan<ulong> valid);
        RowCursor cursor = new RowCursor(_mask.And(input, input.Selection, valid), start, end);
        ref ChosenRow<TValue> chosen = ref State(group);
        while (cursor.Next(out int row))
        {
            Consider(ref chosen, values[row], input.StartRow + row);
        }
    }

    internal override void StepRows(in BatchInput input, ReadOnlySpan<int> groups)
    {
        ReadOnlySpan<TValue> values = _values.Of(input.Arena, input.Batch, input.Node, kind, out ReadOnlySpan<ulong> valid);
        RowCursor cursor = new RowCursor(_mask.And(input, input.Selection, valid), 0, input.Rows);
        StateView<ChosenRow<TValue>> states = States;
        while (cursor.Next(out int row))
        {
            Consider(ref states[groups[row]], values[row], input.StartRow + row);
        }
    }

    internal override void MergeFrom(AggregateSlot other, ReadOnlySpan<int> from, ReadOnlySpan<int> into)
    {
        StateView<ChosenRow<TValue>> states = States;
        StateView<ChosenRow<TValue>> others = StatesOf(other);
        for (int i = 0; i < from.Length; i++)
        {
            ref ChosenRow<TValue> source = ref others[from[i]];
            if (source.Row >= 0)
            {
                Consider(ref states[into[i]], source.Best, source.Row);
            }
        }
    }

    internal override long Result(int group) => State(group).Row;

    private void Consider(ref ChosenRow<TValue> chosen, TValue value, long row)
    {
        if (TValue.IsNaN(value))
        {
            return;
        }

        if (chosen.Row < 0 || (max ? value > chosen.Best : value < chosen.Best) || (value == chosen.Best && row < chosen.Row))
        {
            chosen.Best = value;
            chosen.Row = row;
        }
    }
}

/// <summary>A group's chosen row so far, its position in the source, -1 before any, and the value that chose it.</summary>
internal struct ChosenRow<TValue>
    where TValue : unmanaged
{
    internal long Row;
    internal TValue Best;
}
