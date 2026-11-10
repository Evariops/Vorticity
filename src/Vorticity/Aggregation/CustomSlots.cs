using System;
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

    /// <summary>The values, the masks joined and the window's words, back to the process's shelf.</summary>
    private protected override void ReleaseScratch()
    {
        Scratch.Return(ref _values);
        _rows.Release();
        Scratch.Return(ref _clip);
    }

    internal override void StepRange(in BatchInput input, int start, int end, int group)
    {
        ReadOnlySpan<T> values = FixedReader.Values(input.Arena, input.Node, StorageKind.Primitive, ref _values, out ReadOnlySpan<ulong> valid);
        if (start == 0 && end == input.Rows && input.Selection.IsEmpty)
        {
            TAggregator.Step(ref State(group), values, valid, new Selection(input.Rows));
            return;
        }

        // Every row of the range kept and none null: the range's own values, every one of them, where a window of
        // words over the whole block was built and read a bit at a time.
        if (input.Selection.IsEmpty && valid.IsEmpty)
        {
            TAggregator.Step(ref State(group), values[start..end], default, new Selection(end - start));
            return;
        }

        Selection rows = RowMasks.Window(input.Selection, input.Rows, start, end, ref _clip);
        if (rows.Count > 0)
        {
            TAggregator.Step(ref State(group), values, valid, rows);
        }

        RowMasks.Unclip(_clip, start, end);
    }

    internal override void StepRows(in BatchInput input, ReadOnlySpan<int> groups)
    {
        ReadOnlySpan<T> values = FixedReader.Values(input.Arena, input.Node, StorageKind.Primitive, ref _values, out ReadOnlySpan<ulong> valid);
        ReadOnlySpan<ulong> kept = _rows.And(input, input.Selection, valid);
        StateView<TState> states = States;
        if (kept.IsEmpty)
        {
            // Every row kept: a run's end is the first row of another group, found over vectors once the next row
            // shares the group, and a row whose next does not is a step of its own.
            int row = input.Start;
            int last = input.End;
            while (row < last)
            {
                int owner = groups[row];
                int stop = row + 1;
                if (stop < last && groups[stop] == owner)
                {
                    int past = groups[stop..last].IndexOfAnyExcept(owner);
                    stop = past < 0 ? last : stop + past;
                }

                TAggregator.Step(ref states[owner], values[row..stop], default, new Selection(stop - row));
                row = stop;
            }

            return;
        }

        RowCursor rows = new RowCursor(kept, input.Start, input.End);

        // A run of rows one after the other, all kept and of one group, is one step: the rows of a key that comes in
        // blocks, which a step a row handed over one at a time.
        int start = -1;
        int end = -1;
        int group = -1;
        while (rows.Next(out int row))
        {
            int next = groups[row];
            if (row == end && next == group)
            {
                end++;
                continue;
            }

            if (start >= 0)
            {
                TAggregator.Step(ref states[group], values[start..end], default, new Selection(end - start));
            }

            start = row;
            end = row + 1;
            group = next;
        }

        if (start >= 0)
        {
            TAggregator.Step(ref states[group], values[start..end], default, new Selection(end - start));
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

    /// <summary>The values, the masks joined and the window's words, back to the process's shelf.</summary>
    private protected override void ReleaseScratch()
    {
        Scratch.Return(ref _values);
        _rows.Release();
        Scratch.Return(ref _clip);
    }

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

                // Every row of the range kept: the range's own codes, every one of them.
                if (rows.IsEmpty)
                {
                    TAggregator.StepDictionary(ref state, codes[start..end], dictionary, new Selection(end - start));
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

        // Every row of the range kept and none null: the range's own values, every one of them.
        if (input.Selection.IsEmpty && valid.IsEmpty)
        {
            TAggregator.Step(ref state, canonical[start..end], default, new Selection(end - start));
            return;
        }

        Selection kept = RowMasks.Window(input.Selection, input.Rows, start, end, ref _clip);
        if (kept.Count > 0)
        {
            TAggregator.Step(ref state, canonical, valid, kept);
        }

        RowMasks.Unclip(_clip, start, end);
    }

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
                ReadOnlySpan<ulong> kept = _rows.And(input, input.Selection, valid);
                if (kept.IsEmpty)
                {
                    // Every row kept: a run of one group's rows is one step, as the plain slot takes it.
                    int row = input.Start;
                    int last = input.End;
                    while (row < last)
                    {
                        int owner = groups[row];
                        int stop = row + 1;
                        if (stop < last && groups[stop] == owner)
                        {
                            int past = groups[stop..last].IndexOfAnyExcept(owner);
                            stop = past < 0 ? last : stop + past;
                        }

                        TAggregator.Step(ref states[owner], values[row..stop], default, new Selection(stop - row));
                        row = stop;
                    }

                    return;
                }

                RowCursor rows = new RowCursor(kept, input.Start, input.End);
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
