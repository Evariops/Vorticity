using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Vorticity.Arrays;

namespace Vorticity.Aggregating;

/// <summary>One column of one batch, as an aggregate is handed it.</summary>
internal readonly ref struct BatchInput
{
    internal BatchInput(long batch, CanonicalArena arena, int node, int rows, ReadOnlySpan<ulong> selection, long startRow = 0)
        : this(batch, arena, node, rows, selection, startRow, 0, rows)
    {
    }

    private BatchInput(long batch, CanonicalArena arena, int node, int rows, ReadOnlySpan<ulong> selection, long startRow, int start, int end)
    {
        Batch = batch;
        Arena = arena;
        Node = node;
        Rows = rows;
        Selection = selection;
        StartRow = startRow;
        Start = start;
        End = end;
    }

    /// <summary>
    /// The first row of the window <see cref="AggregateSlot.StepRows"/> folds, 0 for the batch: a
    /// window of rows whose states, keys and values every slot reads while they are in the first
    /// level of cache.
    /// </summary>
    internal int Start { get; }

    /// <summary>The row past the window <see cref="AggregateSlot.StepRows"/> folds, <see cref="Rows"/> for the batch.</summary>
    internal int End { get; }

    /// <summary>The same column, the rows of [<paramref name="start"/>, <paramref name="end"/>) folded alone.</summary>
    internal BatchInput Window(int start, int end) => new BatchInput(Batch, Arena, Node, Rows, Selection, StartRow, start, end);

    /// <summary>The source's row the batch's first row is: what a chosen row is kept as, its position.</summary>
    internal long StartRow { get; }

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

    /// <summary>Folds each selected row of the window [<see cref="BatchInput.Start"/>, <see cref="BatchInput.End"/>) into the group <paramref name="groups"/> gives it.</summary>
    internal abstract void StepRows(in BatchInput input, ReadOnlySpan<int> groups);

    /// <summary>
    /// Whether <see cref="StepRanges"/> folds ranges of a few rows of <paramref name="input"/> for
    /// less than a group per row: in one call, its column's form read once, and in less work per
    /// range than its rows would cost. Otherwise ranges that short are folded row by row.
    /// </summary>
    internal virtual bool FoldsRanges(in BatchInput input) => false;

    /// <summary>Folds the selected rows of each range into its group, the ranges ascending.</summary>
    internal virtual void StepRanges(in BatchInput input, GroupRanges ranges)
    {
        for (int r = 0; r < ranges.Count; r++)
        {
            StepRange(input, ranges.StartAt(r), ranges.EndAt(r), ranges.GroupAt(r));
        }
    }

    /// <summary>Merges the same aggregate of another partition, whose group <c>g</c> is this one's <c>map[g]</c>.</summary>
    internal void MergeFrom(AggregateSlot other, ReadOnlySpan<int> map) => MergeFrom(other, Numbers.Upto(map.Length), map);

    /// <summary>
    /// Merges groups <paramref name="from"/> of the same aggregate of another partition into this
    /// one's groups <paramref name="into"/>, pair by pair: the part of a partition a parallel merge
    /// hands one of its tasks.
    /// </summary>
    internal abstract void MergeFrom(AggregateSlot other, ReadOnlySpan<int> from, ReadOnlySpan<int> into);

    /// <summary>
    /// The groups of several partitions merged apart, read as one: group <c>g</c> is group
    /// <c>g - offsets[p]</c> of <c>parts[p]</c>, <c>p</c> the last whose offset is at or below it.
    /// </summary>
    internal abstract AggregateSlot Joined(AggregateSlot[] parts, int[] offsets);

    /// <summary>
    /// Keeps the states of <paramref name="groups"/> alone, group <c>groups[i]</c> becoming group
    /// <c>i</c>: the groups a streaming group by has not closed. <paramref name="groups"/> ascend.
    /// </summary>
    internal abstract void Keep(ReadOnlySpan<int> groups);

    /// <summary>The bytes of a group's state when it lies in the group's record (<see cref="GroupRecords"/>); 0 for a slot that keeps its states apart.</summary>
    internal virtual int StateBytes => 0;

    /// <summary>Writes the state a group starts from into <paramref name="state"/>, <see cref="StateBytes"/> long: a record of seeds.</summary>
    internal virtual void WriteSeed(Span<byte> state)
    {
    }

    /// <summary>Keeps the slot's states at byte <paramref name="offset"/> of <paramref name="records"/>, which its partition shares among its slots.</summary>
    internal virtual void Bind(GroupRecords records, int offset)
    {
    }
}

/// <summary>An aggregate whose answer per group is a <typeparamref name="TResult"/>.</summary>
internal abstract class AggregateSlot<TResult> : AggregateSlot
{
    internal abstract TResult Result(int group);

    /// <summary>The answers of <paramref name="groups"/>, in order: a batch of a result column.</summary>
    internal virtual void Results(ReadOnlySpan<int> groups, Span<TResult> into)
    {
        for (int i = 0; i < groups.Length; i++)
        {
            into[i] = Result(groups[i]);
        }
    }

    internal override AggregateSlot Joined(AggregateSlot[] parts, int[] offsets) => new JoinedSlot<TResult>(parts, offsets);
}

/// <summary>The numbers 0, 1, 2 and on, shared: the groups of a whole partition for a merge that takes them all.</summary>
internal static class Numbers
{
    private static int[] s_numbers = [];

    /// <summary>The first <paramref name="count"/> numbers.</summary>
    internal static ReadOnlySpan<int> Upto(int count)
    {
        int[] numbers = s_numbers;
        if (numbers.Length < count)
        {
            // A wider array replaces the field whole: a reader holds one long enough, whichever.
            numbers = new int[Scratch.Capacity(count, numbers.Length)];
            for (int i = 0; i < numbers.Length; i++)
            {
                numbers[i] = i;
            }

            s_numbers = numbers;
        }

        return numbers.AsSpan(0, count);
    }
}

/// <summary>
/// The answers of an aggregate merged in parts, read as one slot: each group's answer from the part
/// that holds it, a mean too where the parts hold one.
/// </summary>
internal sealed class JoinedSlot<TResult>(AggregateSlot[] parts, int[] offsets) : AggregateSlot<TResult>, IMeanSlot
{
    private int[] _local = [];

    internal override TResult Result(int group)
    {
        int part = JoinedParts.PartOf(offsets, group);
        return ((AggregateSlot<TResult>)parts[part]).Result(group - offsets[part]);
    }

    /// <summary>The groups a run at a time of one part, their numbers in it: a batch in delivery order comes in long runs.</summary>
    internal override void Results(ReadOnlySpan<int> groups, Span<TResult> into)
    {
        Scratch.Grow(ref _local, groups.Length);
        int start = 0;
        while (start < groups.Length)
        {
            int part = JoinedParts.PartOf(offsets, groups[start]);
            int low = offsets[part];
            int high = part + 1 < offsets.Length ? offsets[part + 1] : int.MaxValue;
            int end = start;
            while (end < groups.Length && groups[end] >= low && groups[end] < high)
            {
                _local[end] = groups[end] - low;
                end++;
            }

            ((AggregateSlot<TResult>)parts[part]).Results(_local.AsSpan(start, end - start), into[start..end]);
            start = end;
        }
    }

    public double? Mean(int group)
    {
        int part = JoinedParts.PartOf(offsets, group);
        return ((IMeanSlot)parts[part]).Mean(group - offsets[part]);
    }

    internal override void EnsureGroups(int groups) => throw JoinedParts.Read();

    internal override void StepRange(in BatchInput input, int start, int end, int group) => throw JoinedParts.Read();

    internal override void StepRows(in BatchInput input, ReadOnlySpan<int> groups) => throw JoinedParts.Read();

    internal override void MergeFrom(AggregateSlot other, ReadOnlySpan<int> from, ReadOnlySpan<int> into) => throw JoinedParts.Read();

    internal override void Keep(ReadOnlySpan<int> groups) => throw JoinedParts.Read();
}

/// <summary>What the slots of distinct values share, whose pairs are keyed by group: a merge of some of their groups.</summary>
internal static class Distinct
{
    /// <summary>The group each of <paramref name="groups"/> groups merges into, -1 for one not merged, from the pairs <paramref name="from"/> and <paramref name="into"/>.</summary>
    internal static int[] Targets(int groups, ReadOnlySpan<int> from, ReadOnlySpan<int> into)
    {
        int[] targets = new int[groups];
        Array.Fill(targets, -1);
        for (int i = 0; i < from.Length; i++)
        {
            targets[from[i]] = into[i];
        }

        return targets;
    }
}

/// <summary>What the slots and keys merged in parts share: which part holds a group.</summary>
internal static class JoinedParts
{
    /// <summary>The part holding <paramref name="group"/>: the last whose offset is at or below it.</summary>
    internal static int PartOf(int[] offsets, int group)
    {
        int low = 0;
        int high = offsets.Length - 1;
        while (low < high)
        {
            int middle = (low + high + 1) >> 1;
            if (offsets[middle] <= group)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return low;
    }

    /// <summary>A joined slot or index is read, never stepped nor merged again.</summary>
    internal static InvalidOperationException Read() =>
        new InvalidOperationException("The groups merged in parts are read, not stepped or merged again.");
}

/// <summary>A slot whose states are a sum's, which hold a mean as well.</summary>
internal interface IMeanSlot
{
    /// <summary>The mean of <paramref name="group"/>: its total over its count, null with no value.</summary>
    double? Mean(int group);
}

/// <summary>
/// A mean read from the slot of the sum of the same column and the same rows: the sum's states hold
/// its total and its count, so the column is folded once for both.
/// </summary>
internal sealed class MeanView(IMeanSlot sum) : AggregateSlot<double?>
{
    internal override void EnsureGroups(int groups)
    {
    }

    internal override void StepRange(in BatchInput input, int start, int end, int group)
    {
    }

    internal override void StepRows(in BatchInput input, ReadOnlySpan<int> groups)
    {
    }

    internal override void MergeFrom(AggregateSlot other, ReadOnlySpan<int> from, ReadOnlySpan<int> into)
    {
    }

    internal override void Keep(ReadOnlySpan<int> groups)
    {
    }

    internal override double? Result(int group) => sum.Mean(group);
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

    internal override void MergeFrom(AggregateSlot other, ReadOnlySpan<int> from, ReadOnlySpan<int> into)
    {
    }

    internal override void Keep(ReadOnlySpan<int> groups)
    {
    }

    internal override TResult Result(int group) => _value;
}

/// <summary>
/// The rows of each group, nulls included, counted in <typeparamref name="TCount"/>: 32 bits where the
/// source's rows stay below 2^32, which no group's count can then pass (PLAN-HIGH-CARDINALITY, H1,
/// reduction 2), 64 where they are not known.
/// </summary>
internal sealed class CountSlot<TCount> : RecordSlot<TCount, long>
    where TCount : unmanaged, IBinaryInteger<TCount>
{
    internal override TCount Seed => TCount.Zero;

    internal override void StepRange(in BatchInput input, int start, int end, int group) =>
        State(group) += TCount.CreateTruncating(RowMasks.Count(input.Selection, start, end));

    internal override void StepRows(in BatchInput input, ReadOnlySpan<int> groups)
    {
        StateView<TCount> counts = States;
        RowCursor rows = new RowCursor(input.Selection, input.Start, input.End);
        while (rows.Next(out int row))
        {
            counts[groups[row]]++;
        }
    }

    /// <summary>A range's count is its length, or its selected rows: a word or two whatever its rows.</summary>
    internal override bool FoldsRanges(in BatchInput input) => true;

    internal override void StepRanges(in BatchInput input, GroupRanges ranges)
    {
        StateView<TCount> counts = States;
        ReadOnlySpan<int> starts = ranges.Starts;
        ReadOnlySpan<int> ends = ranges.Ends;
        ReadOnlySpan<int> groups = ranges.Groups;
        ReadOnlySpan<ulong> selection = input.Selection;
        if (selection.IsEmpty)
        {
            for (int r = 0; r < starts.Length; r++)
            {
                counts[groups[r]] += TCount.CreateTruncating(ends[r] - starts[r]);
            }

            return;
        }

        for (int r = 0; r < starts.Length; r++)
        {
            counts[groups[r]] += TCount.CreateTruncating(RowMasks.Count(selection, starts[r], ends[r]));
        }
    }

    internal override void MergeFrom(AggregateSlot other, ReadOnlySpan<int> from, ReadOnlySpan<int> into)
    {
        StateView<TCount> counts = States;
        StateView<TCount> others = StatesOf(other);
        for (int i = 0; i < from.Length; i++)
        {
            counts[into[i]] += others[from[i]];
        }
    }

    internal override long Result(int group) => long.CreateTruncating(State(group));

    internal override void Results(ReadOnlySpan<int> groups, Span<long> into)
    {
        StateView<TCount> counts = States;
        for (int i = 0; i < groups.Length; i++)
        {
            into[i] = long.CreateTruncating(counts[groups[i]]);
        }
    }
}

/// <summary>
/// Whether a group holds a row of those its filter keeps: <c>Any(p)</c> over the rows where
/// <c>p</c> is true, and <c>All(p)</c>, which is no row where it is not.
/// </summary>
internal sealed class ExistsSlot(bool all) : RecordSlot<bool, bool>
{
    internal override bool Seed => false;

    internal override void StepRange(in BatchInput input, int start, int end, int group)
    {
        ref bool seen = ref State(group);
        if (!seen && RowMasks.Count(input.Selection, start, end) > 0)
        {
            seen = true;
        }
    }

    internal override void StepRows(in BatchInput input, ReadOnlySpan<int> groups)
    {
        StateView<bool> seen = States;
        RowCursor rows = new RowCursor(input.Selection, input.Start, input.End);
        while (rows.Next(out int row))
        {
            seen[groups[row]] = true;
        }
    }

    internal override void MergeFrom(AggregateSlot other, ReadOnlySpan<int> from, ReadOnlySpan<int> into)
    {
        StateView<bool> seen = States;
        StateView<bool> others = StatesOf(other);
        for (int i = 0; i < from.Length; i++)
        {
            seen[into[i]] |= others[from[i]];
        }
    }

    internal override bool Result(int group) => State(group) != all;
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

/// <summary>
/// The distinct codes the rows of a range of a dictionary block name, for an aggregate that takes
/// each value once, marked in a table of the dictionary's size. A range of fewer rows than the
/// dictionary has codes lists its codes as it meets them and unmarks them from the list, so that it
/// pays for its rows and not for the dictionary; a longer one clears the table, marks it and sweeps
/// it, which costs the dictionary once and its rows no more than a store each.
/// </summary>
internal struct CodeSet
{
    private byte[]? _marked;
    private int[]? _met;

    // Whether the table may hold marks: left so by a long range, or by a short one that did not
    // finish unmarking.
    private bool _dirty;

    /// <summary>
    /// The codes of the rows of <paramref name="rows"/> in [<paramref name="start"/>,
    /// <paramref name="end"/>), each once, in the order they are met: for a range of fewer rows
    /// than <paramref name="entries"/>.
    /// </summary>
    /// <param name="codes">The block's codes, one per row.</param>
    /// <param name="rows">The rows to read, every row when empty.</param>
    /// <param name="start">The range's first row.</param>
    /// <param name="end">The row past the range.</param>
    /// <param name="entries">The dictionary's size, above every code.</param>
    internal ReadOnlySpan<int> Few(ReadOnlySpan<uint> codes, ReadOnlySpan<ulong> rows, int start, int end, int entries)
    {
        Span<byte> marked = Marks(entries);
        if (_dirty)
        {
            // Past the dictionary too: a longer one read before may have left marks there.
            _marked.AsSpan().Clear();
        }

        int[] met = _met ??= [];
        Scratch.Grow(ref met, Math.Min(entries, Math.Max(end - start, 0)) + 1);
        _met = met;
        _dirty = true;
        int count = 0;
        RowCursor cursor = new RowCursor(rows, start, end);
        while (cursor.Next(out int row))
        {
            // Every row writes its code at the end of the list and moves the end past it only the
            // first time: no branch on whether the code was met, which random codes would
            // mispredict at nearly every new one.
            int code = (int)codes[row];
            met[count] = code;
            count += 1 - marked[code];
            marked[code] = 1;
        }

        for (int i = 0; i < count; i++)
        {
            marked[met[i]] = 0;
        }

        _dirty = false;
        return met.AsSpan(0, count);
    }

    /// <summary>
    /// The table cleared, for a range as long as the dictionary or longer to mark with its codes and
    /// sweep: nonzero at a code the range names.
    /// </summary>
    /// <param name="entries">The dictionary's size.</param>
    internal Span<byte> Table(int entries)
    {
        Span<byte> marked = Marks(entries);
        marked.Clear();
        _dirty = true;
        return marked;
    }

    private Span<byte> Marks(int entries)
    {
        byte[] marked = _marked ??= [];
        Scratch.Grow(ref marked, entries);
        _marked = marked;
        return marked.AsSpan(0, entries);
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
