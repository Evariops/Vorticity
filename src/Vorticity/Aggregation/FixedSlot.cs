using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using Vorticity.Arrays;

namespace Vorticity.Aggregating;

/// <summary>
/// A built-in aggregate over a fixed-width column: each block is read in the form it arrives in,
/// a constant as one weighted value, a run-end block a weighted value per run, a dictionary block a
/// weighted value per distinct code, and a canonical block through the dense kernels.
/// </summary>
internal sealed class FixedSlot<TValue, TState, TOp, TResult> : AggregateSlot<TResult>
    where TValue : unmanaged
    where TOp : IValueOp<TValue, TState>
{
    private readonly StorageKind _kind;
    private readonly Func<TState, TResult> _finish;
    private TState[] _states = [];
    private int _groups;
    private ValuesCache<TValue> _values;
    private MaskCache _rows;
    private int[] _counts = [];

    internal FixedSlot(StorageKind kind, Func<TState, TResult> finish)
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
                ReadOnlySpan<TValue> values = _values.Of(arena, input.Batch, runs, _kind, out ReadOnlySpan<ulong> valid);
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
                ReadOnlySpan<TValue> dictionary = _values.Of(arena, input.Batch, entries, _kind, out ReadOnlySpan<ulong> valid);
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
                ReadOnlySpan<TValue> values = _values.Of(arena, input.Batch, node, _kind, out ReadOnlySpan<ulong> valid);
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
                ReadOnlySpan<TValue> dictionary = _values.Of(arena, input.Batch, entries, _kind, out ReadOnlySpan<ulong> valid);
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
                ReadOnlySpan<TValue> values = _values.Of(arena, input.Batch, node, _kind, out ReadOnlySpan<ulong> valid);
                RowCursor rows = new RowCursor(_rows.And(input, input.Selection, valid), 0, input.Rows);
                while (rows.Next(out int row))
                {
                    TOp.Add(ref states[groups[row]], values[row]);
                }

                return;
            }
        }
    }

    internal override void MergeFrom(AggregateSlot other, ReadOnlySpan<int> map)
    {
        FixedSlot<TValue, TState, TOp, TResult> from = (FixedSlot<TValue, TState, TOp, TResult>)other;
        for (int g = 0; g < from._groups; g++)
        {
            TOp.Merge(ref _states[map[g]], in from._states[g]);
        }
    }

    internal override TResult Result(int group) => _finish(_states[group]);

    /// <summary>
    /// Folds the rows of [start, end) the mask holds: a run of words of <see cref="WordFold.Dense"/>
    /// rows or more at once, as a dense span where the run is full and selected where it is not; a
    /// value at a time elsewhere.
    /// </summary>
    [SkipLocalsInit]
    internal static void Accumulate(ref TState state, ReadOnlySpan<TValue> values, ReadOnlySpan<ulong> rows, int start, int end)
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
        Span<ulong> run = stackalloc ulong[WordFold.Run];
        int count = 0;
        bool full = true;
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

            if (values.Length - baseRow >= 64 && BitOperations.PopCount(word) >= WordFold.Dense)
            {
                run[count++] = word;
                full &= word == ulong.MaxValue;
                if (count == WordFold.Run)
                {
                    Fold(ref state, values, run, w + 1 - count, full);
                    count = 0;
                    full = true;
                }

                continue;
            }

            if (count > 0)
            {
                Fold(ref state, values, run[..count], w - count, full);
                count = 0;
                full = true;
            }

            while (word != 0)
            {
                TOp.Add(ref state, values[baseRow + BitOperations.TrailingZeroCount(word)]);
                word &= word - 1;
            }
        }

        if (count > 0)
        {
            Fold(ref state, values, run[..count], last + 1 - count, full);
        }
    }

    /// <summary>The rows a run of words from word <paramref name="from"/> holds.</summary>
    private static void Fold(ref TState state, ReadOnlySpan<TValue> values, ReadOnlySpan<ulong> run, int from, bool full)
    {
        ReadOnlySpan<TValue> block = values.Slice(from << 6, run.Length << 6);
        if (full)
        {
            TOp.AddSpan(ref state, block);
        }
        else
        {
            TOp.AddWords(ref state, block, run);
        }
    }
}

/// <summary>The distinct non-null values of each group, one set for every group of the partition.</summary>
internal sealed class FixedDistinctSlot<TValue> : AggregateSlot<long>
    where TValue : unmanaged, IEquatable<TValue>
{
    private readonly StorageKind _kind;
    private readonly HashSet<DistinctEntry<TValue>> _seen = [];
    private long[] _counts = [];
    private int _groups;
    private ValuesCache<TValue> _values;
    private MaskCache _rows;
    private CodeSet _distinct;

    internal FixedDistinctSlot(StorageKind kind) => _kind = kind;

    internal override void EnsureGroups(int groups)
    {
        if (groups > _counts.Length)
        {
            Array.Resize(ref _counts, Math.Max(groups, _counts.Length * 2));
        }

        _groups = Math.Max(_groups, groups);
    }

    internal override void StepRange(in BatchInput input, int start, int end, int group)
    {
        CanonicalArena arena = input.Arena;
        int node = input.Node;
        switch (FixedReader.EncodingOf(arena, node, _kind))
        {
            case ColumnEncoding.Constant:
                if (RowMasks.Count(_rows.And(input, input.Selection, ArenaWords.Validity(arena, node)), start, end) > 0)
                {
                    Add(group, FixedReader.Constant<TValue>(arena, node, _kind));
                }

                return;

            case ColumnEncoding.RunEnd:
            {
                int runs = EncodedForms.RunEnd(arena, node, out ReadOnlySpan<uint> ends);
                ReadOnlySpan<TValue> values = _values.Of(arena, input.Batch, runs, _kind, out ReadOnlySpan<ulong> valid);
                ReadOnlySpan<ulong> rows = _rows.And(input, input.Selection, ArenaWords.Validity(arena, node));
                int r = Runs.FirstEndingAfter(ends, start);
                int runStart = r == 0 ? 0 : (int)ends[r - 1];
                for (; r < ends.Length && runStart < end; r++)
                {
                    int runEnd = Math.Min((int)ends[r], input.Rows);
                    if (StorageValues.IsValid(valid, r) && RowMasks.Count(rows, Math.Max(runStart, start), Math.Min(runEnd, end)) > 0)
                    {
                        Add(group, values[r]);
                    }

                    runStart = runEnd;
                }

                return;
            }

            case ColumnEncoding.Dictionary:
            {
                int entries = EncodedForms.Dictionary(arena, node, out ReadOnlySpan<uint> codes);
                ReadOnlySpan<TValue> dictionary = _values.Of(arena, input.Batch, entries, _kind, out ReadOnlySpan<ulong> valid);
                ReadOnlySpan<ulong> rows = _rows.And(input, input.Selection, ArenaWords.Validity(arena, node));
                if (end - start < dictionary.Length)
                {
                    FewCodes(codes, rows, start, end, group, dictionary, valid);
                }
                else
                {
                    ManyCodes(codes, rows, start, end, group, dictionary, valid);
                }

                return;
            }

            default:
            {
                ReadOnlySpan<TValue> values = _values.Of(arena, input.Batch, node, _kind, out ReadOnlySpan<ulong> valid);
                RowCursor rows = new RowCursor(_rows.And(input, input.Selection, valid), start, end);
                while (rows.Next(out int row))
                {
                    Add(group, values[row]);
                }

                return;
            }
        }
    }

    internal override void StepRows(in BatchInput input, ReadOnlySpan<int> groups)
    {
        CanonicalArena arena = input.Arena;
        int node = input.Node;
        if (FixedReader.EncodingOf(arena, node, _kind) == ColumnEncoding.Dictionary)
        {
            int entries = EncodedForms.Dictionary(arena, node, out ReadOnlySpan<uint> codes);
            ReadOnlySpan<TValue> dictionary = _values.Of(arena, input.Batch, entries, _kind, out ReadOnlySpan<ulong> valid);
            RowCursor coded = new RowCursor(_rows.And(input, input.Selection, ArenaWords.Validity(arena, node)), 0, input.Rows);
            while (coded.Next(out int row))
            {
                int code = (int)codes[row];
                if (StorageValues.IsValid(valid, code))
                {
                    Add(groups[row], dictionary[code]);
                }
            }

            return;
        }

        ReadOnlySpan<TValue> values = _values.Of(arena, input.Batch, node, _kind, out ReadOnlySpan<ulong> validity);
        RowCursor rows = new RowCursor(_rows.And(input, input.Selection, validity), 0, input.Rows);
        while (rows.Next(out int row))
        {
            Add(groups[row], values[row]);
        }
    }

    internal override void MergeFrom(AggregateSlot other, ReadOnlySpan<int> map)
    {
        foreach (DistinctEntry<TValue> entry in ((FixedDistinctSlot<TValue>)other)._seen)
        {
            Add(map[entry.Group], entry.Value);
        }
    }

    internal override long Result(int group) => _counts[group];

    /// <summary>The values of a range of fewer rows than its dictionary has codes, each once.</summary>
    /// <remarks>Each walk of a dictionary range is a method of its own, so that neither loop takes its shape from the other.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void FewCodes(
        ReadOnlySpan<uint> codes, ReadOnlySpan<ulong> rows, int start, int end, int group, ReadOnlySpan<TValue> dictionary, ReadOnlySpan<ulong> valid)
    {
        foreach (int code in _distinct.Few(codes, rows, start, end, dictionary.Length))
        {
            if (StorageValues.IsValid(valid, code))
            {
                Add(group, dictionary[code]);
            }
        }
    }

    /// <summary>The values of a range as long as its dictionary or longer, each once, in code order.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ManyCodes(
        ReadOnlySpan<uint> codes, ReadOnlySpan<ulong> rows, int start, int end, int group, ReadOnlySpan<TValue> dictionary, ReadOnlySpan<ulong> valid)
    {
        Span<byte> present = _distinct.Table(dictionary.Length);
        RowCursor all = new RowCursor(rows, start, end);
        while (all.Next(out int row))
        {
            present[(int)codes[row]] = 1;
        }

        for (int code = 0; code < present.Length; code++)
        {
            if (present[code] != 0 && StorageValues.IsValid(valid, code))
            {
                Add(group, dictionary[code]);
            }
        }
    }

    private void Add(int group, TValue value)
    {
        if (_seen.Add(new DistinctEntry<TValue>(group, value)))
        {
            _counts[group]++;
        }
    }
}

/// <summary>A value seen by one group.</summary>
internal readonly record struct DistinctEntry<TValue>(int Group, TValue Value)
    where TValue : unmanaged, IEquatable<TValue>;
