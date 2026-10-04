using System;
using Vorticity.Arrays;

namespace Vorticity.Aggregating;

/// <summary>A caller's aggregator, handed the canonical form of each block.</summary>
internal sealed class CustomSlot<T, TAggregator, TState> : AggregateSlot<TState>
    where T : unmanaged
    where TAggregator : IAggregator<T, TState>
{
    private TState[] _states = [];
    private int _groups;
    private T[] _values = [];
    private MaskCache _rows;
    private ulong[] _clip = [];

    internal override void EnsureGroups(int groups)
    {
        if (groups > _states.Length)
        {
            Array.Resize(ref _states, Scratch.Capacity(groups, _states.Length));
        }

        for (int g = _groups; g < groups; g++)
        {
            _states[g] = TAggregator.Seed();
        }

        _groups = Math.Max(_groups, groups);
    }

    internal override void StepRange(in BatchInput input, int start, int end, int group)
    {
        ReadOnlySpan<T> values = FixedReader.Values(input.Arena, input.Node, StorageKind.Primitive, ref _values, out ReadOnlySpan<ulong> valid);
        if (start == 0 && end == input.Rows && input.Selection.IsEmpty)
        {
            TAggregator.Step(ref _states[group], values, valid, new Selection(input.Rows));
            return;
        }

        Selection rows = RowMasks.Window(input.Selection, input.Rows, start, end, ref _clip);
        if (rows.Count > 0)
        {
            TAggregator.Step(ref _states[group], values, valid, rows);
        }

        RowMasks.Unclip(_clip, start, end);
    }

    internal override void StepRows(in BatchInput input, ReadOnlySpan<int> groups)
    {
        ReadOnlySpan<T> values = FixedReader.Values(input.Arena, input.Node, StorageKind.Primitive, ref _values, out ReadOnlySpan<ulong> valid);
        RowCursor rows = new RowCursor(_rows.And(input, input.Selection, valid), 0, input.Rows);
        while (rows.Next(out int row))
        {
            TAggregator.Step(ref _states[groups[row]], values.Slice(row, 1), default, new Selection(1));
        }
    }

    internal override void MergeFrom(AggregateSlot other, ReadOnlySpan<int> map)
    {
        CustomSlot<T, TAggregator, TState> from = (CustomSlot<T, TAggregator, TState>)other;
        for (int g = 0; g < from._groups; g++)
        {
            TAggregator.Merge(ref _states[map[g]], in from._states[g]);
        }
    }

    internal override TState Result(int group) => _states[group];

    internal override void Keep(ReadOnlySpan<int> groups)
    {
        for (int i = 0; i < groups.Length; i++)
        {
            _states[i] = _states[groups[i]];
        }

        // The groups past them are seeded again when they are made.
        _groups = groups.Length;
    }
}

/// <summary>A caller's aggregator that reads the encoded forms: a constant, a run-end or a dictionary block reaches it undecoded.</summary>
internal sealed class EncodedCustomSlot<T, TAggregator, TState> : AggregateSlot<TState>
    where T : unmanaged
    where TAggregator : IEncodedAggregator<T, TState>
{
    private TState[] _states = [];
    private int _groups;
    private T[] _values = [];
    private MaskCache _rows;
    private ulong[] _clip = [];

    internal override void EnsureGroups(int groups)
    {
        if (groups > _states.Length)
        {
            Array.Resize(ref _states, Scratch.Capacity(groups, _states.Length));
        }

        for (int g = _groups; g < groups; g++)
        {
            _states[g] = TAggregator.Seed();
        }

        _groups = Math.Max(_groups, groups);
    }

    internal override void StepRange(in BatchInput input, int start, int end, int group)
    {
        ref TState state = ref _states[group];
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

    internal override void StepRows(in BatchInput input, ReadOnlySpan<int> groups)
    {
        CanonicalArena arena = input.Arena;
        int node = input.Node;
        switch (EncodedForms.EncodingOf(arena, node))
        {
            case ColumnEncoding.Constant:
            {
                T value = FixedReader.Constant<T>(arena, node, StorageKind.Primitive);
                RowCursor rows = new RowCursor(_rows.And(input, input.Selection, ArenaWords.Validity(arena, node)), 0, input.Rows);
                while (rows.Next(out int row))
                {
                    TAggregator.StepConstant(ref _states[groups[row]], value, 1);
                }

                return;
            }

            case ColumnEncoding.Dictionary:
            {
                int entries = EncodedForms.Dictionary(arena, node, out ReadOnlySpan<uint> codes);
                ReadOnlySpan<T> dictionary = FixedReader.Values(arena, entries, StorageKind.Primitive, ref _values, out ReadOnlySpan<ulong> valid);
                RowCursor rows = new RowCursor(_rows.And(input, input.Selection, ArenaWords.Validity(arena, node)), 0, input.Rows);
                while (rows.Next(out int row))
                {
                    int code = (int)codes[row];
                    if (StorageValues.IsValid(valid, code))
                    {
                        TAggregator.StepConstant(ref _states[groups[row]], dictionary[code], 1);
                    }
                }

                return;
            }

            default:
            {
                ReadOnlySpan<T> values = FixedReader.Values(arena, node, StorageKind.Primitive, ref _values, out ReadOnlySpan<ulong> valid);
                RowCursor rows = new RowCursor(_rows.And(input, input.Selection, valid), 0, input.Rows);
                while (rows.Next(out int row))
                {
                    TAggregator.StepConstant(ref _states[groups[row]], values[row], 1);
                }

                return;
            }
        }
    }

    internal override void MergeFrom(AggregateSlot other, ReadOnlySpan<int> map)
    {
        EncodedCustomSlot<T, TAggregator, TState> from = (EncodedCustomSlot<T, TAggregator, TState>)other;
        for (int g = 0; g < from._groups; g++)
        {
            TAggregator.Merge(ref _states[map[g]], in from._states[g]);
        }
    }

    internal override TState Result(int group) => _states[group];

    internal override void Keep(ReadOnlySpan<int> groups)
    {
        for (int i = 0; i < groups.Length; i++)
        {
            _states[i] = _states[groups[i]];
        }

        // The groups past them are seeded again when they are made.
        _groups = groups.Length;
    }
}
