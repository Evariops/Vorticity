using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using Vorticity.Arrays;
using Vorticity.Writing;

namespace Vorticity.Aggregating;

/// <summary>
/// A built-in aggregate over a fixed-width column: each block is read in the form it arrives in,
/// a constant as one weighted value, a run-end block a weighted value per run, a dictionary block a
/// weighted value per distinct code, and a canonical block through the dense kernels.
/// </summary>
internal sealed class FixedSlot<TValue, TState, TOp, TResult> : RecordSlot<TState, TResult>, IMeanSlot
    where TValue : unmanaged
    where TOp : struct, IValueOp<TValue, TState>
{
    private readonly StorageKind _kind;
    private readonly Func<TState, TResult> _finish;
    private readonly TState _seed;
    private readonly TOp _op;
    private ValuesCache<TValue> _values;
    private MaskCache _rows;
    private int[] _counts = [];

    internal FixedSlot(StorageKind kind, Func<TState, TResult> finish)
        : this(kind, finish, default(TOp))
    {
    }

    /// <summary>A slot whose op holds what the run fixed for every group, as a variance its center.</summary>
    internal FixedSlot(StorageKind kind, Func<TState, TResult> finish, TOp op)
        : this(kind, finish, op.Seed(), op)
    {
    }

    /// <summary>A slot whose groups start from <paramref name="seed"/>: a value no row holds, for an extreme that keeps no flag.</summary>
    internal FixedSlot(StorageKind kind, Func<TState, TResult> finish, TState seed)
        : this(kind, finish, seed, default)
    {
    }

    private FixedSlot(StorageKind kind, Func<TState, TResult> finish, TState seed, TOp op)
    {
        _kind = kind;
        _finish = finish;
        _seed = seed;
        _op = op;
    }

    internal override TState Seed => _seed;

    internal override void StepRange(in BatchInput input, int start, int end, int group)
    {
        TOp op = _op;
        ref TState state = ref State(group);
        CanonicalArena arena = input.Arena;
        int node = input.Node;
        switch (FixedReader.EncodingOf(arena, node, _kind))
        {
            case ColumnEncoding.Constant:
            {
                int count = RowMasks.Count(_rows.And(input, input.Selection, ArenaWords.Validity(arena, node)), start, end);
                if (count > 0)
                {
                    op.AddWeighted(ref state, FixedReader.Constant<TValue>(arena, node, _kind), count);
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
                            op.AddWeighted(ref state, values[r], count);
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
                FoldCodes(in op, ref state, codes, dictionary, valid, _rows.And(input, input.Selection, ArenaWords.Validity(arena, node)), start, end);
                return;
            }

            default:
            {
                ReadOnlySpan<TValue> values = _values.Of(arena, input.Batch, node, _kind, out ReadOnlySpan<ulong> valid);
                Accumulate(in op, ref state, values, _rows.And(input, input.Selection, valid), start, end);
                return;
            }
        }
    }

    /// <summary>
    /// Folds the rows of [start, end) the mask holds, of a dictionary block: a value a row when the
    /// range has fewer rows than the dictionary has values, a weighted value per code met otherwise.
    /// </summary>
    private void FoldCodes(
        in TOp op, ref TState state, ReadOnlySpan<uint> codes, ReadOnlySpan<TValue> dictionary, ReadOnlySpan<ulong> valid, ReadOnlySpan<ulong> rows, int start, int end)
    {
        if (end - start < dictionary.Length)
        {
            // Fewer rows than distinct values: counting per code would cost more than it saves.
            RowCursor few = new RowCursor(rows, start, end);
            while (few.Next(out int row))
            {
                int code = (int)codes[row];
                if (StorageValues.IsValid(valid, code))
                {
                    op.Add(ref state, dictionary[code]);
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
                op.AddWeighted(ref state, dictionary[code], counts[code]);
            }
        }
    }

    internal override void StepRows(in BatchInput input, ReadOnlySpan<int> groups)
    {
        TOp op = _op;
        StateView<TState> states = States;
        CanonicalArena arena = input.Arena;
        int node = input.Node;
        switch (FixedReader.EncodingOf(arena, node, _kind))
        {
            case ColumnEncoding.Constant:
            {
                TValue value = FixedReader.Constant<TValue>(arena, node, _kind);
                RowCursor rows = new RowCursor(_rows.And(input, input.Selection, ArenaWords.Validity(arena, node)), input.Start, input.End);
                while (rows.Next(out int row))
                {
                    op.Add(ref states[groups[row]], value);
                }

                return;
            }

            case ColumnEncoding.Dictionary:
            {
                int entries = EncodedForms.Dictionary(arena, node, out ReadOnlySpan<uint> codes);
                ReadOnlySpan<TValue> dictionary = _values.Of(arena, input.Batch, entries, _kind, out ReadOnlySpan<ulong> valid);
                RowCursor rows = new RowCursor(_rows.And(input, input.Selection, ArenaWords.Validity(arena, node)), input.Start, input.End);
                while (rows.Next(out int row))
                {
                    int code = (int)codes[row];
                    if (StorageValues.IsValid(valid, code))
                    {
                        op.Add(ref states[groups[row]], dictionary[code]);
                    }
                }

                return;
            }

            default:
            {
                ReadOnlySpan<TValue> values = _values.Of(arena, input.Batch, node, _kind, out ReadOnlySpan<ulong> valid);
                RowCursor rows = new RowCursor(_rows.And(input, input.Selection, valid), input.Start, input.End);
                while (rows.Next(out int row))
                {
                    op.Add(ref states[groups[row]], values[row]);
                }

                return;
            }
        }
    }

    /// <summary>
    /// A run-end or a constant column folds a range as a weighted value per run it overlaps, where
    /// its rows would expand the column first: ranges however short. A dictionary or a canonical
    /// one costs a value per row either way, and a range of a few rows its setup on top.
    /// </summary>
    internal override bool FoldsRanges(in BatchInput input) =>
        FixedReader.EncodingOf(input.Arena, input.Node, _kind) is ColumnEncoding.RunEnd or ColumnEncoding.Constant;

    internal override void StepRanges(in BatchInput input, GroupRanges ranges)
    {
        TOp op = _op;
        StateView<TState> states = States;
        CanonicalArena arena = input.Arena;
        int node = input.Node;
        ReadOnlySpan<int> starts = ranges.Starts;
        ReadOnlySpan<int> ends = ranges.Ends;
        ReadOnlySpan<int> groups = ranges.Groups;
        switch (FixedReader.EncodingOf(arena, node, _kind))
        {
            case ColumnEncoding.Constant:
            {
                TValue value = FixedReader.Constant<TValue>(arena, node, _kind);
                ReadOnlySpan<ulong> rows = _rows.And(input, input.Selection, ArenaWords.Validity(arena, node));
                for (int r = 0; r < starts.Length; r++)
                {
                    int count = RowMasks.Count(rows, starts[r], ends[r]);
                    if (count > 0)
                    {
                        op.AddWeighted(ref states[groups[r]], value, count);
                    }
                }

                return;
            }

            case ColumnEncoding.RunEnd:
            {
                // The value's runs and the key's ranges both ascend: walked together, each range
                // folds the runs it overlaps, a weighted value each.
                int runs = EncodedForms.RunEnd(arena, node, out ReadOnlySpan<uint> runEnds);
                ReadOnlySpan<TValue> values = _values.Of(arena, input.Batch, runs, _kind, out ReadOnlySpan<ulong> valid);
                ReadOnlySpan<ulong> rows = _rows.And(input, input.Selection, ArenaWords.Validity(arena, node));
                int run = 0;
                int runStart = 0;
                for (int r = 0; r < starts.Length; r++)
                {
                    int start = starts[r];
                    int end = ends[r];
                    while (run < runEnds.Length && (int)runEnds[run] <= start)
                    {
                        runStart = (int)runEnds[run++];
                    }

                    ref TState state = ref states[groups[r]];
                    int at = run;
                    int atStart = runStart;
                    for (; at < runEnds.Length && atStart < end; at++)
                    {
                        int atEnd = Math.Min((int)runEnds[at], input.Rows);
                        if (StorageValues.IsValid(valid, at))
                        {
                            int count = RowMasks.Count(rows, Math.Max(atStart, start), Math.Min(atEnd, end));
                            if (count > 0)
                            {
                                op.AddWeighted(ref state, values[at], count);
                            }
                        }

                        atStart = atEnd;
                    }
                }

                return;
            }

            case ColumnEncoding.Dictionary:
            {
                int entries = EncodedForms.Dictionary(arena, node, out ReadOnlySpan<uint> codes);
                ReadOnlySpan<TValue> dictionary = _values.Of(arena, input.Batch, entries, _kind, out ReadOnlySpan<ulong> valid);
                ReadOnlySpan<ulong> rows = _rows.And(input, input.Selection, ArenaWords.Validity(arena, node));
                for (int r = 0; r < starts.Length; r++)
                {
                    FoldCodes(in op, ref states[groups[r]], codes, dictionary, valid, rows, starts[r], ends[r]);
                }

                return;
            }

            default:
            {
                ReadOnlySpan<TValue> values = _values.Of(arena, input.Batch, node, _kind, out ReadOnlySpan<ulong> valid);
                ReadOnlySpan<ulong> rows = _rows.And(input, input.Selection, valid);
                for (int r = 0; r < starts.Length; r++)
                {
                    Accumulate(in op, ref states[groups[r]], values, rows, starts[r], ends[r]);
                }

                return;
            }
        }
    }

    internal override void MergeFrom(AggregateSlot other, ReadOnlySpan<int> from, ReadOnlySpan<int> into)
    {
        TOp op = _op;
        StateView<TState> states = States;
        StateView<TState> others = StatesOf(other);
        for (int i = 0; i < from.Length; i++)
        {
            op.Merge(ref states[into[i]], in others[from[i]]);
        }
    }

    internal override TResult Result(int group) => _finish(State(group));

    public double? Mean(int group) => _op.Mean(in State(group));

    /// <summary>
    /// Folds the rows of [start, end) the mask holds: a run of words of <see cref="WordFold.Dense"/>
    /// rows or more at once, as a dense span where the run is full and selected where it is not; a
    /// value at a time elsewhere.
    /// </summary>
    [SkipLocalsInit]
    internal static void Accumulate(in TOp op, ref TState state, ReadOnlySpan<TValue> values, ReadOnlySpan<ulong> rows, int start, int end)
    {
        if (end <= start)
        {
            return;
        }

        if (rows.IsEmpty)
        {
            op.AddSpan(ref state, values[start..end]);
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
                    Fold(in op, ref state, values, run, w + 1 - count, full);
                    count = 0;
                    full = true;
                }

                continue;
            }

            if (count > 0)
            {
                Fold(in op, ref state, values, run[..count], w - count, full);
                count = 0;
                full = true;
            }

            while (word != 0)
            {
                op.Add(ref state, values[baseRow + BitOperations.TrailingZeroCount(word)]);
                word &= word - 1;
            }
        }

        if (count > 0)
        {
            Fold(in op, ref state, values, run[..count], last + 1 - count, full);
        }
    }

    /// <summary>The rows a run of words from word <paramref name="from"/> holds.</summary>
    private static void Fold(in TOp op, ref TState state, ReadOnlySpan<TValue> values, ReadOnlySpan<ulong> run, int from, bool full)
    {
        ReadOnlySpan<TValue> block = values.Slice(from << 6, run.Length << 6);
        if (full)
        {
            op.AddSpan(ref state, block);
        }
        else
        {
            op.AddWords(ref state, block, run);
        }
    }
}

/// <summary>The distinct non-null values of each group, one set for every group of the partition.</summary>
internal sealed class FixedDistinctSlot<TValue> : AggregateSlot<long>
    where TValue : unmanaged, IEquatable<TValue>
{
    private readonly StorageKind _kind;
    private readonly DistinctPairs<TValue> _pairs = new DistinctPairs<TValue>();
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
            Array.Resize(ref _counts, Scratch.Capacity(groups, _counts.Length));
        }

        _pairs.EnsureGroups(groups);
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
            RowCursor coded = new RowCursor(_rows.And(input, input.Selection, ArenaWords.Validity(arena, node)), input.Start, input.End);
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
        RowCursor rows = new RowCursor(_rows.And(input, input.Selection, validity), input.Start, input.End);
        while (rows.Next(out int row))
        {
            Add(groups[row], values[row]);
        }
    }

    internal override void MergeFrom(AggregateSlot other, ReadOnlySpan<int> from, ReadOnlySpan<int> into)
    {
        // Every group of the other: its pairs in the order they lie, each to its group's target. Some
        // of them, a part of a parallel merge: their chains alone (PLAN-HIGH-CARDINALITY, H9).
        FixedDistinctSlot<TValue> source = (FixedDistinctSlot<TValue>)other;
        if (from.Length == source._groups)
        {
            _pairs.MergeAll(source._pairs, into, _counts);
            return;
        }

        for (int i = 0; i < from.Length; i++)
        {
            _counts[into[i]] += _pairs.MergeGroup(source._pairs, from[i], into[i]);
        }
    }

    internal override long Result(int group) => _counts[group];

    /// <summary>The (group, value) pairs and the counts.</summary>
    internal override long Footprint => _pairs.Footprint + ((long)_counts.Length * sizeof(long));

    internal override void Keep(ReadOnlySpan<int> groups)
    {
        _pairs.Keep(groups);
        for (int i = 0; i < groups.Length; i++)
        {
            _counts[i] = _counts[groups[i]];
        }

        _counts.AsSpan(groups.Length, _groups - groups.Length).Clear();
        _groups = groups.Length;
    }

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
        if (_pairs.Add(group, value))
        {
            _counts[group]++;
        }
    }
}

/// <summary>A value seen by one group.</summary>
/// <remarks>
/// Hashed under seeds drawn once a process (<see cref="KeyHash.Pair"/>), the group the tag: the
/// default hash of a 64-bit value folds its halves together, and a prime bucket count takes an
/// integer's multiples to one bucket, so values could be built to share one run of a table and make
/// every insert a walk of the values before it. Equal values hash alike: every NaN and both zeros of
/// a float are equal, so they are given one pattern of bits first.
/// </remarks>
internal readonly record struct DistinctEntry<TValue>(int Group, TValue Value)
    where TValue : unmanaged, IEquatable<TValue>
{
    /// <summary>The hash of the pair (<paramref name="group"/>, <paramref name="value"/>), for a table that probes linearly: good in its low bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Hash(int group, TValue value)
    {
        (ulong low, ulong high) = KeyWords.Of(value);
        return KeyHash.Pair(low, high, group);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode() => (int)Hash(Group, Value);
}

/// <summary>A fixed-width value as the words a hash reads.</summary>
internal static class KeyWords
{
    /// <summary>A value's bits as two words, zero-extended, a float's made one pattern per value.</summary>
    /// <remarks>Cast rather than read through a reference, which would take the value through memory before the multiply.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static (ulong Low, ulong High) Of<TValue>(TValue value)
        where TValue : unmanaged
    {
        if (typeof(TValue) == typeof(double))
        {
            double d = Unsafe.BitCast<TValue, double>(value);
            return (double.IsNaN(d) ? 0x7FF8_0000_0000_0000UL : d == 0 ? 0 : BitConverter.DoubleToUInt64Bits(d), 0);
        }

        if (typeof(TValue) == typeof(float))
        {
            float f = Unsafe.BitCast<TValue, float>(value);
            return (float.IsNaN(f) ? 0x7FC0_0000U : f == 0 ? 0 : BitConverter.SingleToUInt32Bits(f), 0);
        }

        if (typeof(TValue) == typeof(Half))
        {
            Half h = Unsafe.BitCast<TValue, Half>(value);
            return (Half.IsNaN(h) ? (ushort)0x7E00 : h == Half.Zero ? (ushort)0 : BitConverter.HalfToUInt16Bits(h), 0);
        }

        if (Unsafe.SizeOf<TValue>() == 32)
        {
            // A decimal of 256 bits: the high words folded into the low ones through a multiply,
            // so that values differing only above bit 127 do not share a chain.
            ref ulong words = ref Unsafe.As<TValue, ulong>(ref value);
            return (words ^ (Unsafe.Add(ref words, 2) * 0x9E3779B97F4A7C15UL), Unsafe.Add(ref words, 1) ^ (Unsafe.Add(ref words, 3) * 0xC2B2AE3D27D4EB4FUL));
        }

        return Unsafe.SizeOf<TValue>() switch
        {
            1 => (Unsafe.BitCast<TValue, byte>(value), 0),
            2 => (Unsafe.BitCast<TValue, ushort>(value), 0),
            4 => (Unsafe.BitCast<TValue, uint>(value), 0),
            8 => (Unsafe.BitCast<TValue, ulong>(value), 0),
            _ => ((ulong)Unsafe.BitCast<TValue, UInt128>(value), (ulong)(Unsafe.BitCast<TValue, UInt128>(value) >> 64)),
        };
    }
}
