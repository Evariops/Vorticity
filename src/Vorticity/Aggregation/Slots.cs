using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Vorticity.Arrays;

namespace Vorticity.Aggregating;

/// <summary>One column of one batch, as an aggregate is handed it.</summary>
internal readonly ref struct BatchInput
{
    internal BatchInput(long batch, CanonicalArena arena, int node, int rows, ReadOnlySpan<ulong> selection)
    {
        Batch = batch;
        Arena = arena;
        Node = node;
        Rows = rows;
        Selection = selection;
    }

    /// <summary>The batch's number in its partition, from 1: what a slot keys its per-batch work on.</summary>
    internal long Batch { get; }

    internal CanonicalArena Arena { get; }

    /// <summary>The column's node, its storage when it is an extension; -1 for an aggregate that reads no column.</summary>
    internal int Node { get; }

    internal int Rows { get; }

    /// <summary>The rows the scan kept; empty when it kept every row.</summary>
    internal ReadOnlySpan<ulong> Selection { get; }
}

/// <summary>
/// One aggregate of one partition of a scan: a state per group, stepped a block at a time, merged
/// with the same aggregate of the other partitions at the end.
/// </summary>
internal abstract class AggregateSlot
{
    /// <summary>Makes room for groups up to <paramref name="groups"/>, each seeded.</summary>
    internal abstract void EnsureGroups(int groups);

    /// <summary>Folds the selected rows of [<paramref name="start"/>, <paramref name="end"/>) into one group.</summary>
    internal abstract void StepRange(in BatchInput input, int start, int end, int group);

    /// <summary>Folds each selected row into the group <paramref name="groups"/> gives it.</summary>
    internal abstract void StepRows(in BatchInput input, ReadOnlySpan<int> groups);

    /// <summary>Merges the same aggregate of another partition, whose group <c>g</c> is this one's <c>map[g]</c>.</summary>
    internal abstract void MergeFrom(AggregateSlot other, ReadOnlySpan<int> map);
}

/// <summary>An aggregate whose answer per group is a <typeparamref name="TResult"/>.</summary>
internal abstract class AggregateSlot<TResult> : AggregateSlot
{
    internal abstract TResult Result(int group);
}

/// <summary>An aggregate answered before the scan, from the file statistics or a count: nothing to step.</summary>
internal sealed class SettledSlot<TResult> : AggregateSlot<TResult>
{
    private readonly TResult _value;

    internal SettledSlot(TResult value) => _value = value;

    internal override void EnsureGroups(int groups)
    {
    }

    internal override void StepRange(in BatchInput input, int start, int end, int group)
    {
    }

    internal override void StepRows(in BatchInput input, ReadOnlySpan<int> groups)
    {
    }

    internal override void MergeFrom(AggregateSlot other, ReadOnlySpan<int> map)
    {
    }

    internal override TResult Result(int group) => _value;
}

/// <summary>The rows of each group, nulls included.</summary>
internal sealed class CountSlot : AggregateSlot<long>
{
    private long[] _counts = [];
    private int _groups;

    internal override void EnsureGroups(int groups)
    {
        if (groups > _counts.Length)
        {
            Array.Resize(ref _counts, Math.Max(groups, _counts.Length * 2));
        }

        _groups = Math.Max(_groups, groups);
    }

    internal override void StepRange(in BatchInput input, int start, int end, int group) =>
        _counts[group] += RowMasks.Count(input.Selection, start, end);

    internal override void StepRows(in BatchInput input, ReadOnlySpan<int> groups)
    {
        long[] counts = _counts;
        RowCursor rows = new RowCursor(input.Selection, 0, input.Rows);
        while (rows.Next(out int row))
        {
            counts[groups[row]]++;
        }
    }

    internal override void MergeFrom(AggregateSlot other, ReadOnlySpan<int> map)
    {
        CountSlot from = (CountSlot)other;
        for (int g = 0; g < from._groups; g++)
        {
            _counts[map[g]] += from._counts[g];
        }
    }

    internal override long Result(int group) => _counts[group];
}

/// <summary>
/// The conjunction of two masks of one batch's column, worked out once and reused by every range
/// the batch is folded in; either mask when the other is every row.
/// </summary>
internal struct MaskCache
{
    private ulong[]? _words;
    private long _batch;
    private int _node;
    private byte _source;

    internal ReadOnlySpan<ulong> And(in BatchInput input, ReadOnlySpan<ulong> left, ReadOnlySpan<ulong> right)
    {
        if (_batch != input.Batch || _node != input.Node)
        {
            _batch = input.Batch;
            _node = input.Node;
            if (left.IsEmpty)
            {
                _source = right.IsEmpty ? (byte)0 : (byte)2;
            }
            else if (right.IsEmpty)
            {
                _source = 1;
            }
            else
            {
                ulong[] words = _words ?? [];
                RowMasks.And(left, right, input.Rows, ref words);
                _words = words;
                _source = 3;
            }
        }

        return _source switch
        {
            0 => default,
            1 => left,
            2 => right,
            _ => _words.AsSpan(0, (input.Rows + 63) >> 6),
        };
    }
}

/// <summary>Run lookups over the exclusive run ends of a run-end block.</summary>
internal static class Runs
{
    /// <summary>The first run that ends after <paramref name="row"/>.</summary>
    internal static int FirstEndingAfter(ReadOnlySpan<uint> ends, int row)
    {
        int found = ends.BinarySearch((uint)row);
        return found >= 0 ? found + 1 : ~found;
    }
}

/// <summary>Walks the rows of a mask inside [start, end) by <see cref="BitOperations.TrailingZeroCount(ulong)"/>; every row when the mask is empty.</summary>
internal ref struct RowCursor
{
    private readonly ReadOnlySpan<ulong> _mask;
    private readonly int _end;
    private readonly int _lastWord;
    private int _row;
    private int _word;
    private ulong _bits;

    internal RowCursor(ReadOnlySpan<ulong> mask, int start, int end)
    {
        _mask = mask;
        _end = end;
        _row = start - 1;
        _lastWord = end <= start ? -1 : Math.Min((end - 1) >> 6, mask.Length - 1);
        _word = start >> 6;
        _bits = mask.IsEmpty || _word > _lastWord ? 0 : mask[_word] & (ulong.MaxValue << (start & 63));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool Next(out int row)
    {
        if (_mask.IsEmpty)
        {
            row = ++_row;
            return row < _end;
        }

        while (_bits == 0)
        {
            if (++_word > _lastWord)
            {
                row = -1;
                return false;
            }

            _bits = _mask[_word];
        }

        row = (_word << 6) + BitOperations.TrailingZeroCount(_bits);
        _bits &= _bits - 1;
        return row < _end;
    }
}
