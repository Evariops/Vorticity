// The built-in aggregate over a fixed-width column as it read a block at every range a batch is
// folded in, widening a narrow decimal or a uuid again each time: the original that
// `WidenedSlotBenchmarks` measures the library against.
using System;
using System.Numerics;

using Vorticity.Aggregating;
using Vorticity.Arrays;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>
/// A built-in aggregate over a fixed-width column: each block is read in the form it arrives in,
/// a constant as one weighted value, a run-end block a weighted value per run, a dictionary block a
/// weighted value per distinct code, and a canonical block through the dense kernels.
/// </summary>
internal sealed class FixedSlotBefore<TValue, TState, TOp, TResult> : AggregateSlot<TResult>
    where TValue : unmanaged
    where TOp : IValueOp<TValue, TState>
{
    private readonly StorageKind _kind;
    private readonly Func<TState, TResult> _finish;
    private TState[] _states = [];
    private int _groups;
    private TValue[] _values = [];
    private MaskCache _rows;
    private int[] _counts = [];

    internal FixedSlotBefore(StorageKind kind, Func<TState, TResult> finish)
    {
        _kind = kind;
        _finish = finish;
    }

    internal override void EnsureGroups(int groups)
    {
        if (groups > _states.Length)
        {
            Array.Resize(ref _states, Math.Max(groups, _states.Length * 2));
        }

        for (int g = _groups; g < groups; g++)
        {
            _states[g] = TOp.Seed();
        }

        _groups = Math.Max(_groups, groups);
    }

    internal override void StepRange(in BatchInput input, int start, int end, int group)
    {
        ref TState state = ref _states[group];
        CanonicalArena arena = input.Arena;
        int node = input.Node;
        switch (FixedReader.EncodingOf(arena, node, _kind))
        {
            case ColumnEncoding.Constant:
            {
                int count = RowMasks.Count(_rows.And(input, input.Selection, ArenaWords.Validity(arena, node)), start, end);
                if (count > 0)
                {
                    TOp.AddWeighted(ref state, FixedReader.Constant<TValue>(arena, node, _kind), count);
                }

                return;
            }

            case ColumnEncoding.RunEnd:
            {
                int runs = EncodedForms.RunEnd(arena, node, out ReadOnlySpan<uint> ends);
                ReadOnlySpan<TValue> values = FixedReader.Values(arena, runs, _kind, ref _values, out ReadOnlySpan<ulong> valid);
                ReadOnlySpan<ulong> rows = _rows.And(input, input.Selection, ArenaWords.Validity(arena, node));
                int r = Runs.FirstEndingAfter(ends, start);
                int runStart = r == 0 ? 0 : (int)ends[r - 1];
                for (; r < ends.Length && runStart < end; r++)
                {
                    int runEnd = Math.Min((int)ends[r], input.Rows);
                    if (StorageValues.IsValid(valid, r))
                    {
                        int count = RowMasks.Count(rows, Math.Max(runStart, start), Math.Min(runEnd, end));
                        if (count > 0)
                        {
                            TOp.AddWeighted(ref state, values[r], count);
                        }
                    }

                    runStart = runEnd;
                }

                return;
            }

            case ColumnEncoding.Dictionary:
            {
                int entries = EncodedForms.Dictionary(arena, node, out ReadOnlySpan<uint> codes);
                ReadOnlySpan<TValue> dictionary = FixedReader.Values(arena, entries, _kind, ref _values, out ReadOnlySpan<ulong> valid);
                ReadOnlySpan<ulong> rows = _rows.And(input, input.Selection, ArenaWords.Validity(arena, node));
                if (end - start < dictionary.Length)
                {
                    // Fewer rows than distinct values: counting per code would cost more than it saves.
                    RowCursor few = new RowCursor(rows, start, end);
                    while (few.Next(out int row))
                    {
                        int code = (int)codes[row];
                        if (StorageValues.IsValid(valid, code))
                        {
                            TOp.Add(ref state, dictionary[code]);
                        }
                    }

                    return;
                }

                Scratch.Grow(ref _counts, dictionary.Length);
                Span<int> counts = _counts.AsSpan(0, dictionary.Length);
                counts.Clear();
                RowCursor all = new RowCursor(rows, start, end);
                while (all.Next(out int row))
                {
                    counts[(int)codes[row]]++;
                }

                for (int code = 0; code < counts.Length; code++)
                {
                    if (counts[code] > 0 && StorageValues.IsValid(valid, code))
                    {
                        TOp.AddWeighted(ref state, dictionary[code], counts[code]);
                    }
                }

                return;
            }

            default:
            {
                ReadOnlySpan<TValue> values = FixedReader.Values(arena, node, _kind, ref _values, out ReadOnlySpan<ulong> valid);
                Accumulate(ref state, values, _rows.And(input, input.Selection, valid), start, end);
                return;
            }
        }
    }

    internal override void StepRows(in BatchInput input, ReadOnlySpan<int> groups)
    {
        TState[] states = _states;
        CanonicalArena arena = input.Arena;
        int node = input.Node;
        switch (FixedReader.EncodingOf(arena, node, _kind))
        {
            case ColumnEncoding.Constant:
            {
                TValue value = FixedReader.Constant<TValue>(arena, node, _kind);
                RowCursor rows = new RowCursor(_rows.And(input, input.Selection, ArenaWords.Validity(arena, node)), 0, input.Rows);
                while (rows.Next(out int row))
                {
                    TOp.Add(ref states[groups[row]], value);
                }

                return;
            }

            case ColumnEncoding.Dictionary:
            {
                int entries = EncodedForms.Dictionary(arena, node, out ReadOnlySpan<uint> codes);
                ReadOnlySpan<TValue> dictionary = FixedReader.Values(arena, entries, _kind, ref _values, out ReadOnlySpan<ulong> valid);
                RowCursor rows = new RowCursor(_rows.And(input, input.Selection, ArenaWords.Validity(arena, node)), 0, input.Rows);
                while (rows.Next(out int row))
                {
                    int code = (int)codes[row];
                    if (StorageValues.IsValid(valid, code))
                    {
                        TOp.Add(ref states[groups[row]], dictionary[code]);
                    }
                }

                return;
            }

            default:
            {
                ReadOnlySpan<TValue> values = FixedReader.Values(arena, node, _kind, ref _values, out ReadOnlySpan<ulong> valid);
                RowCursor rows = new RowCursor(_rows.And(input, input.Selection, valid), 0, input.Rows);
                while (rows.Next(out int row))
                {
                    TOp.Add(ref states[groups[row]], values[row]);
                }

                return;
            }
        }
    }

    internal override void MergeFrom(AggregateSlot other, ReadOnlySpan<int> from, ReadOnlySpan<int> into)
    {
        TState[] states = ((FixedSlotBefore<TValue, TState, TOp, TResult>)other)._states;
        for (int i = 0; i < from.Length; i++)
        {
            TOp.Merge(ref _states[into[i]], in states[from[i]]);
        }
    }

    internal override TResult Result(int group) => _finish(_states[group]);

    // The benchmarks fold and merge; no group by of theirs streams.
    internal override void Keep(ReadOnlySpan<int> groups) => throw new NotSupportedException("The original slot predates the group by that streams.");

    /// <summary>Folds the rows of [start, end) the mask holds: a dense span where the mask is full, a value at a time where it is not.</summary>
    private static void Accumulate(ref TState state, ReadOnlySpan<TValue> values, ReadOnlySpan<ulong> rows, int start, int end)
    {
        if (end <= start)
        {
            return;
        }

        if (rows.IsEmpty)
        {
            TOp.AddSpan(ref state, values[start..end]);
            return;
        }

        int first = start >> 6;
        int last = (end - 1) >> 6;
        for (int w = first; w <= last; w++)
        {
            ulong word = rows[w];
            int baseRow = w << 6;
            if (w == first)
            {
                word &= ulong.MaxValue << (start & 63);
            }

            if (w == last && end - baseRow < 64)
            {
                word &= (1UL << (end - baseRow)) - 1;
            }

            if (word == ulong.MaxValue)
            {
                TOp.AddSpan(ref state, values.Slice(baseRow, 64));
                continue;
            }

            while (word != 0)
            {
                TOp.Add(ref state, values[baseRow + BitOperations.TrailingZeroCount(word)]);
                word &= word - 1;
            }
        }
    }
}
