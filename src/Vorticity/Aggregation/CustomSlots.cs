using System;
using System.Runtime.CompilerServices;
using Vorticity.Arrays;

namespace Vorticity.Aggregating;

/// <summary>
/// A caller's aggregator, handed the canonical form of each block; its state in the group's record
/// when it holds no reference.
/// </summary>
internal sealed class CustomSlot<T, TAggregator, TState> : RecordSlot<TState, TState>
    where T : unmanaged
    where TAggregator : IAggregator<T, TState>
{
    private T[] _values = [];
    private MaskCache _rows;
    private ulong[] _clip = [];

    internal override TState Seed => TAggregator.Seed();

    internal override void StepRange(in BatchInput input, int start, int end, int group)
    {
        ReadOnlySpan<T> values = FixedReader.Values(input.Arena, input.Node, StorageKind.Primitive, ref _values, out ReadOnlySpan<ulong> valid);
        if (start == 0 && end == input.Rows && input.Selection.IsEmpty)
        {
            TAggregator.Step(ref State(group), values, valid, new Selection(input.Rows));
            return;
        }

        Selection rows = RowMasks.Window(input.Selection, input.Rows, start, end, ref _clip);
        if (rows.Count > 0)
        {
            TAggregator.Step(ref State(group), values, valid, rows);
        }

        RowMasks.Unclip(_clip, start, end);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal override void StepRows(in BatchInput input, ReadOnlySpan<int> groups)
    {
        ReadOnlySpan<T> values = FixedReader.Values(input.Arena, input.Node, StorageKind.Primitive, ref _values, out ReadOnlySpan<ulong> valid);
        RowCursor rows = new RowCursor(_rows.And(input, input.Selection, valid), input.Start, input.End);
        StateView<TState> states = States;
        while (rows.Next(out int row))
        {
            TAggregator.Step(ref states[groups[row]], values.Slice(row, 1), default, new Selection(1));
        }
    }

    internal override void MergeFrom(AggregateSlot other, ReadOnlySpan<int> from, ReadOnlySpan<int> into)
    {
        StateView<TState> states = States;
        StateView<TState> others = StatesOf(other);
        for (int i = 0; i < from.Length; i++)
        {
            TAggregator.Merge(ref states[into[i]], in others[from[i]]);
        }
    }

    internal override TState Result(int group) => State(group);
}

/// <summary>A caller's aggregator that reads the encoded forms: a constant, a run-end or a dictionary block reaches it undecoded.</summary>
internal sealed class EncodedCustomSlot<T, TAggregator, TState> : RecordSlot<TState, TState>
    where T : unmanaged
    where TAggregator : IEncodedAggregator<T, TState>
{
    private T[] _values = [];
    private MaskCache _rows;
    private ulong[] _clip = [];

    internal override TState Seed => TAggregator.Seed();

    internal override void StepRange(in BatchInput input, int start, int end, int group)
    {
        ref TState state = ref State(group);
        CanonicalArena arena = input.Arena;
        int node = input.Node;
        switch (EncodedForms.EncodingOf(arena, node))
        {
            case ColumnEncoding.Constant:
            {
                int count = RowMasks.Count(_rows.And(input, input.Selection, ArenaWords.Validity(arena, node)), start, end);
                if (count > 0)
                {
                    TAggregator.StepConstant(ref state, FixedReader.Constant<T>(arena, node, StorageKind.Primitive), count);
                }

                return;
            }

            case ColumnEncoding.RunEnd:
            {
                int runs = EncodedForms.RunEnd(arena, node, out ReadOnlySpan<uint> ends);
                if (ArenaWords.NullCount(arena, EncodedForms.Canonical(arena, runs)) > 0)
                {
                    break;
                }

                ReadOnlySpan<T> values = FixedReader.Values(arena, runs, StorageKind.Primitive, ref _values, out _);
                ReadOnlySpan<ulong> rows = _rows.And(input, input.Selection, ArenaWords.Validity(arena, node));
                if (start == 0 && end == input.Rows && rows.IsEmpty)
                {
                    TAggregator.StepRunEnd(ref state, ends, values, new Selection(input.Rows));
                    return;
                }

                // The runs the range overlaps, not the block's: a range costs its own runs.
                Selection window = RowMasks.Window(rows, input.Rows, start, end, ref _clip);
                if (window.Count > 0)
                {
                    int first = Runs.FirstEndingAfter(ends, start);
                    int past = Math.Min(Runs.FirstEndingAfter(ends, end - 1) + 1, ends.Length);
                    TAggregator.StepRunEnd(ref state, ends[first..past], values[first..past], window);
                }

                RowMasks.Unclip(_clip, start, end);
                return;
            }

            case ColumnEncoding.Dictionary:
            {
                int entries = EncodedForms.Dictionary(arena, node, out ReadOnlySpan<uint> codes);
                if (ArenaWords.NullCount(arena, EncodedForms.Canonical(arena, entries)) > 0)
                {
                    break;
                }

                ReadOnlySpan<T> dictionary = FixedReader.Values(arena, entries, StorageKind.Primitive, ref _values, out _);
                ReadOnlySpan<ulong> rows = _rows.And(input, input.Selection, ArenaWords.Validity(arena, node));
                if (start == 0 && end == input.Rows && rows.IsEmpty)
                {
                    TAggregator.StepDictionary(ref state, codes, dictionary, new Selection(input.Rows));
                    return;
                }

                Selection window = RowMasks.Window(rows, input.Rows, start, end, ref _clip);
                if (window.Count > 0)
                {
                    TAggregator.StepDictionary(ref state, codes, dictionary, window);
                }

                RowMasks.Unclip(_clip, start, end);
                return;
            }

            default:
                break;
        }

        // The canonical form, and the encoded blocks whose distinct or run values hold a null.
        ReadOnlySpan<T> canonical = FixedReader.Values(arena, node, StorageKind.Primitive, ref _values, out ReadOnlySpan<ulong> valid);
        if (start == 0 && end == input.Rows && input.Selection.IsEmpty)
        {
            TAggregator.Step(ref state, canonical, valid, new Selection(input.Rows));
            return;
        }

        Selection kept = RowMasks.Window(input.Selection, input.Rows, start, end, ref _clip);
        if (kept.Count > 0)
        {
            TAggregator.Step(ref state, canonical, valid, kept);
        }

        RowMasks.Unclip(_clip, start, end);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal override void StepRows(in BatchInput input, ReadOnlySpan<int> groups)
    {
        CanonicalArena arena = input.Arena;
        int node = input.Node;
        StateView<TState> states = States;
        switch (EncodedForms.EncodingOf(arena, node))
        {
            case ColumnEncoding.Constant:
            {
                T value = FixedReader.Constant<T>(arena, node, StorageKind.Primitive);
                RowCursor rows = new RowCursor(_rows.And(input, input.Selection, ArenaWords.Validity(arena, node)), input.Start, input.End);
                while (rows.Next(out int row))
                {
                    TAggregator.StepConstant(ref states[groups[row]], value, 1);
                }

                return;
            }

            case ColumnEncoding.Dictionary:
            {
                int entries = EncodedForms.Dictionary(arena, node, out ReadOnlySpan<uint> codes);
                ReadOnlySpan<T> dictionary = FixedReader.Values(arena, entries, StorageKind.Primitive, ref _values, out ReadOnlySpan<ulong> valid);
                RowCursor rows = new RowCursor(_rows.And(input, input.Selection, ArenaWords.Validity(arena, node)), input.Start, input.End);
                while (rows.Next(out int row))
                {
                    int code = (int)codes[row];
                    if (StorageValues.IsValid(valid, code))
                    {
                        TAggregator.StepConstant(ref states[groups[row]], dictionary[code], 1);
                    }
                }

                return;
            }

            default:
            {
                ReadOnlySpan<T> values = FixedReader.Values(arena, node, StorageKind.Primitive, ref _values, out ReadOnlySpan<ulong> valid);
                RowCursor rows = new RowCursor(_rows.And(input, input.Selection, valid), input.Start, input.End);
                while (rows.Next(out int row))
                {
                    TAggregator.StepConstant(ref states[groups[row]], values[row], 1);
                }

                return;
            }
        }
    }

    internal override void MergeFrom(AggregateSlot other, ReadOnlySpan<int> from, ReadOnlySpan<int> into)
    {
        StateView<TState> states = States;
        StateView<TState> others = StatesOf(other);
        for (int i = 0; i < from.Length; i++)
        {
            TAggregator.Merge(ref states[into[i]], in others[from[i]]);
        }
    }

    internal override TState Result(int group) => State(group);
}
