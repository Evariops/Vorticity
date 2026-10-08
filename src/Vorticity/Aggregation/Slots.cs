using System;
using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Writing;

namespace Vorticity.Aggregating;

/// <summary>One column of one batch, as an aggregate is handed it.</summary>
internal readonly ref struct BatchInput
{
    internal BatchInput(long batch, CanonicalArena arena, int node, int rows, ReadOnlySpan<ulong> selection, long startRow = 0, bool settled = true)
        : this(batch, arena, node, rows, selection, startRow, 0, rows, settled)
    {
    }

    private BatchInput(long batch, CanonicalArena arena, int node, int rows, ReadOnlySpan<ulong> selection, long startRow, int start, int end, bool settled)
    {
        Batch = batch;
        Arena = arena;
        Node = node;
        Rows = rows;
        Selection = selection;
        StartRow = startRow;
        Start = start;
        End = end;
        Settled = settled;
    }

    /// <summary>
    /// Whether the partition's groups have each seen many rows, their extremes moved rarely: a slot then
    /// folds with a branch on the value, which predicts, rather than <see cref="IValueOp{TValue,TState}.AddSelected"/>.
    /// </summary>
    internal bool Settled { get; }

    /// <summary>
    /// The first row of the window <see cref="AggregateSlot.StepRows"/> folds, 0 for the batch: a
    /// window of rows whose states, keys and values every slot reads while they are in the first
    /// level of cache.
    /// </summary>
    internal int Start { get; }

    /// <summary>The row past the window <see cref="AggregateSlot.StepRows"/> folds, <see cref="Rows"/> for the batch.</summary>
    internal int End { get; }

    /// <summary>The same column, the rows of [<paramref name="start"/>, <paramref name="end"/>) folded alone.</summary>
    internal BatchInput Window(int start, int end) => new BatchInput(Batch, Arena, Node, Rows, Selection, StartRow, start, end, Settled);

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
    /// As <see cref="StepRows"/>, the rows counted into <paramref name="count"/>'s groups in the same
    /// pass, the record of a row's group reached once for both: when every
    /// row of the window is selected and none is null. False when it cannot, nothing folded nor counted.
    /// </summary>
    internal virtual bool StepRowsCounted(in BatchInput input, ReadOnlySpan<int> groups, AggregateSlot count) => false;

    /// <summary>Whether the slot can count rows in its own pass (<see cref="StepRowsCounted"/>).</summary>
    internal virtual bool CarriesCount => false;

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

    /// <summary>The records the slot's states lie in, which its partition shares among its slots; null for a slot that keeps its states apart.</summary>
    internal virtual GroupRecords? Bound => null;

    /// <summary>
    /// The shelf the slot's own arrays grow from from now on: a lane's, under its query's memory, or a
    /// core's. Called once, as its partition makes the
    /// slot. Nothing for a slot whose states all lie in records its partition shares.
    /// </summary>
    internal virtual void Govern(ArrayShelf shelf)
    {
    }

    /// <summary>
    /// Tells the slot, as its partition makes it and before it steps a row, that it holds group 0
    /// alone: an aggregate over the whole scan, with no key. Nothing for a slot whose states take no
    /// shape of their own for one group.
    /// </summary>
    internal virtual void Ungrouped()
    {
    }

    /// <summary>
    /// Tells the slot, as its partition makes it and before it steps a row, that its group by streams
    /// and the statistics bound its key to few groups (<see cref="AggregationPartition.FewGroups"/>).
    /// Nothing for a slot whose states take no other shape for them.
    /// </summary>
    internal virtual void FewGroups()
    {
    }

    /// <summary>
    /// Reserves, for a slot of group 0 alone (<see cref="Ungrouped"/>), what its first
    /// <paramref name="rows"/> rows foretell for the <paramref name="expected"/> rows its partition
    /// expects, as <paramref name="memory"/> grants it on each of <paramref name="lanes"/> lanes at once,
    /// which foretell together. Nothing for a slot whose state does not grow with the values it meets.
    /// </summary>
    internal virtual void Foretell(long rows, long expected, QueryMemory? memory, int lanes)
    {
    }

    /// <summary>
    /// The bytes the slot holds apart from the records it shares (<see cref="Bound"/>): its arrays at
    /// their capacity and, for a state of a variable size, the bytes it counts as it takes them. Zero for
    /// a slot whose states all lie in records.
    /// </summary>
    internal virtual long Footprint => 0;

    /// <summary>
    /// The bytes the slot's arrays would take more were <paramref name="more"/> rows to come, a new group
    /// or a new value each at most: what a lane whose table spills asks the budget for before a batch.
    /// Nothing for a slot whose states lie in the records, which its partition asks.
    /// </summary>
    internal virtual long GrowthFor(int more) => 0;

    /// <summary>
    /// Whether the slot's states go to a lane's spill with its keys and come back: in the records, as
    /// their bytes, or written apart (<see cref="WriteStates"/>). Not a state with references, a caller's
    /// aggregator's, which no byte holds.
    /// </summary>
    internal virtual bool SpillsStates => StateBytes > 0;

    /// <summary>
    /// Writes the states of <paramref name="groups"/>, in order, that the slot keeps apart from the
    /// records: what <see cref="ReadStates"/> reads back. Nothing for a slot whose states lie in records.
    /// </summary>
    internal virtual void WriteStates(ReadOnlySpan<int> groups, SpillBuffer buffer)
    {
    }

    /// <summary>
    /// Reads <paramref name="count"/> states written by <see cref="WriteStates"/> into groups 0 to
    /// <paramref name="count"/> − 1 of the slot, a new one, which then merges them into a part's slot as
    /// a lane's would. Nothing for a slot whose states lie in records, which its partition reads.
    /// </summary>
    internal virtual void ReadStates(ref SpillReader reader, int count)
    {
    }

    /// <summary>
    /// Whether the slot spills on its own when its partition's budget holds it no more
    /// (<see cref="SpillAsync"/>): a distinct count over the whole scan, whose set is all that grows.
    /// </summary>
    internal virtual bool SpillsAlone => false;

    /// <summary>Whether the slot wrote a run on its own (<see cref="SpillsAlone"/>).</summary>
    internal virtual bool Spilled => false;

    /// <summary>The bytes the slot may take more folding <paramref name="rows"/> rows, a set that would double; 0 when it would not grow (<see cref="SpillsAlone"/>).</summary>
    internal virtual long GrowthAhead(int rows) => 0;

    /// <summary>The slot's values written to its lane's scratch as a run, the slot emptied and its arrays given back (<see cref="SpillsAlone"/>).</summary>
    internal virtual ValueTask SpillAsync(SpillScope scope, SpillBuffer buffer, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This aggregate does not spill on its own.");

    /// <summary>
    /// The answer over every lane's slot of <paramref name="lanes"/>, this one among them, once one of them
    /// spilled: their runs read back part by part with what each holds still, under
    /// <paramref name="memory"/>, the answer left in this slot and the others emptied.
    /// </summary>
    internal virtual Task MergeSpilledAsync(AggregateSlot[] lanes, SpillScope scope, int degree, QueryMemory memory, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This aggregate does not spill on its own.");

    /// <summary>The bytes <paramref name="slots"/> hold, the records they share counted once.</summary>
    internal static long FootprintOf(ReadOnlySpan<AggregateSlot> slots)
    {
        long bytes = 0;
        GroupRecords? counted = null;
        foreach (AggregateSlot slot in slots)
        {
            bytes += slot.Footprint;
            if (slot.Bound is { } records && !ReferenceEquals(records, counted))
            {
                bytes += records.Footprint;
                counted = records;
            }
        }

        return bytes;
    }
}

/// <summary>The bytes of the runtime's collections at their capacity: a bucket and an entry an item, the entry laid out field for field as the runtime's.</summary>
internal static class Footprints
{
    /// <summary>A <see cref="System.Collections.Generic.HashSet{T}"/> of <paramref name="capacity"/>.</summary>
    internal static long Set<T>(int capacity) => (long)capacity * (sizeof(int) + Unsafe.SizeOf<SetEntry<T>>());

    /// <summary>A <see cref="System.Collections.Generic.Dictionary{TKey, TValue}"/> of <paramref name="capacity"/>.</summary>
    internal static long Map<TKey, TValue>(int capacity) => (long)capacity * (sizeof(int) + Unsafe.SizeOf<MapEntry<TKey, TValue>>());

#pragma warning disable CS0169, IDE0051 // Laid out to be measured, never read.
    private struct SetEntry<T>
    {
        private int _hashCode;
        private int _next;
        private T _value;
    }

    private struct MapEntry<TKey, TValue>
    {
        private uint _hashCode;
        private int _next;
        private TKey _key;
        private TValue _value;
    }
#pragma warning restore CS0169, IDE0051
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

/// <summary>
/// The numbers 0, 1, 2 and on: the groups of a whole partition, for a merge that takes them all. The
/// numbers every thread shares stop at <see cref="Shared"/>; past them a merge numbers an array of its
/// own, which goes with it: no array the size of the largest
/// merge a process ever ran stays for its life.
/// </summary>
internal static class Numbers
{
    /// <summary>The most numbers every thread shares: 256 KiB of them.</summary>
    internal const int Shared = 1 << 16;

    private static int[] s_numbers = [];

    /// <summary>The numbers every thread shares now, at most <see cref="Shared"/>.</summary>
    internal static int SharedCount => s_numbers.Length;

    /// <summary>The first <paramref name="count"/> numbers: shared, or past <see cref="Shared"/> in an array of the call's own.</summary>
    internal static ReadOnlySpan<int> Upto(int count)
    {
        int[]? own = null;
        return Upto(count, ref own);
    }

    /// <summary>The first <paramref name="count"/> numbers: shared, or past <see cref="Shared"/> in <paramref name="own"/>, made or grown as they need.</summary>
    internal static ReadOnlySpan<int> Upto(int count, scoped ref int[]? own)
    {
        if (count <= Shared)
        {
            int[] numbers = s_numbers;
            if (numbers.Length < count)
            {
                // A wider array replaces the field whole: a reader holds one long enough, whichever.
                numbers = Numbered(Math.Min(Shared, Scratch.Capacity(count, numbers.Length)));
                s_numbers = numbers;
            }

            return numbers.AsSpan(0, count);
        }

        if (own is null || own.Length < count)
        {
            own = Numbered(Scratch.Capacity(count, own?.Length ?? 0));
        }

        return own.AsSpan(0, count);
    }

    private static int[] Numbered(int length)
    {
        int[] numbers = new int[length];
        for (int i = 0; i < numbers.Length; i++)
        {
            numbers[i] = i;
        }

        return numbers;
    }
}

/// <summary>
/// The answers of an aggregate merged in parts, read as one slot: each group's answer from the part
/// that holds it, a mean too where the parts hold one.
/// </summary>
internal sealed class JoinedSlot<TResult>(AggregateSlot[] parts, int[] offsets) : AggregateSlot<TResult>, IMeanSlot, IBytesResults
{
    public bool HoldsBytes => parts.Length > 0 && parts[0] is IBytesResults { HoldsBytes: true };

    /// <summary>The bytes of the groups' answers, a run at a time of one part, as <see cref="Results"/> reads them.</summary>
    public void AppendBytes(VarBinStore store, ReadOnlySpan<int> groups)
    {
        int[] local = ArrayPool<int>.Shared.Rent(groups.Length);
        try
        {
            int start = 0;
            while (start < groups.Length)
            {
                int part = JoinedParts.PartOf(offsets, groups[start]);
                int low = offsets[part];
                int high = part + 1 < offsets.Length ? offsets[part + 1] : int.MaxValue;
                int end = start;
                while (end < groups.Length && groups[end] >= low && groups[end] < high)
                {
                    local[end] = groups[end] - low;
                    end++;
                }

                ((IBytesResults)parts[part]).AppendBytes(store, local.AsSpan(start, end - start));
                start = end;
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(local);
        }
    }
    internal override TResult Result(int group)
    {
        int part = JoinedParts.PartOf(offsets, group);
        return ((AggregateSlot<TResult>)parts[part]).Result(group - offsets[part]);
    }

    /// <summary>
    /// The groups a run at a time of one part, their numbers in it: a batch in delivery order comes in
    /// long runs. The numbers in a buffer of the read's own, which the chunks of a top-k read at once.
    /// </summary>
    internal override void Results(ReadOnlySpan<int> groups, Span<TResult> into)
    {
        int[] local = ArrayPool<int>.Shared.Rent(groups.Length);
        try
        {
            int start = 0;
            while (start < groups.Length)
            {
                int part = JoinedParts.PartOf(offsets, groups[start]);
                int low = offsets[part];
                int high = part + 1 < offsets.Length ? offsets[part + 1] : int.MaxValue;
                int end = start;
                while (end < groups.Length && groups[end] >= low && groups[end] < high)
                {
                    local[end] = groups[end] - low;
                    end++;
                }

                ((AggregateSlot<TResult>)parts[part]).Results(local.AsSpan(start, end - start), into[start..end]);
                start = end;
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(local);
        }
    }

    public double? Mean(int group)
    {
        int part = JoinedParts.PartOf(offsets, group);
        return ((IMeanSlot)parts[part]).Mean(group - offsets[part]);
    }

    /// <summary>The means a run at a time of one part, as <see cref="Results"/> reads the answers.</summary>
    public void Means(ReadOnlySpan<int> groups, Span<double?> into)
    {
        int[] local = ArrayPool<int>.Shared.Rent(groups.Length);
        try
        {
            int start = 0;
            while (start < groups.Length)
            {
                int part = JoinedParts.PartOf(offsets, groups[start]);
                int low = offsets[part];
                int high = part + 1 < offsets.Length ? offsets[part + 1] : int.MaxValue;
                int end = start;
                while (end < groups.Length && groups[end] >= low && groups[end] < high)
                {
                    local[end] = groups[end] - low;
                    end++;
                }

                ((IMeanSlot)parts[part]).Means(local.AsSpan(start, end - start), into[start..end]);
                start = end;
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(local);
        }
    }

    internal override void EnsureGroups(int groups) => throw JoinedParts.Read();

    internal override void StepRange(in BatchInput input, int start, int end, int group) => throw JoinedParts.Read();

    internal override void StepRows(in BatchInput input, ReadOnlySpan<int> groups) => throw JoinedParts.Read();

    internal override void MergeFrom(AggregateSlot other, ReadOnlySpan<int> from, ReadOnlySpan<int> into) => throw JoinedParts.Read();

    internal override void Keep(ReadOnlySpan<int> groups) => throw JoinedParts.Read();
}

/// <summary>
/// A slot whose states are (group, value) pairs, a distinct count's: merged in series, its pairs would
/// pour every lane's into one table, on one thread, however many lanes read them. It merges in parts of
/// its pairs instead, side by side.
/// </summary>
internal interface IPairedSlot
{
    /// <summary>The pairs the slot holds.</summary>
    long Pairs { get; }

    /// <summary>
    /// Merges the slots of every lane, this one first, by parts of their pairs taken side by side: a
    /// pair's part the top bits of its hash under its group's target, so that equal pairs of every
    /// lane meet in one part, each part's pairs made distinct by a worker, and the counts of every part
    /// summed by group into this slot. Group <c>g</c> of <c>slots[p]</c> is group <c>maps[p][g]</c>
    /// here; the pairs, counted, are let go. A part's table is reserved in <paramref name="memory"/> for
    /// as long as it lives.
    /// </summary>
    Task MergeInPartsAsync(AggregateSlot[] slots, int[][] maps, int groups, int parts, int degree, QueryMemory? memory, CancellationToken cancellationToken);
}

/// <summary>Work items run side by side, a worker taking the next item as it finishes one.</summary>
internal static class SideBySide
{
    /// <summary>Runs <paramref name="work"/> on items 0 to <paramref name="count"/> − 1, on up to <paramref name="degree"/> workers; one failure cancels the others.</summary>
    internal static async Task RunAsync(int count, Action<int> work, int degree, CancellationToken cancellationToken)
    {
        int workers = Math.Clamp(degree, 1, Math.Max(1, count));
        int[] next = [-1];
        using CancellationTokenSource failed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken token = failed.Token;
        Task[] tasks = new Task[workers];
        for (int w = 0; w < workers; w++)
        {
            tasks[w] = Task.Run(
                () =>
                {
                    int item;
                    while ((item = Interlocked.Increment(ref next[0])) < count)
                    {
                        token.ThrowIfCancellationRequested();
                        work(item);
                    }
                },
                token);
        }

        await AggregationEngine.GuardedAsync(tasks, failed).ConfigureAwait(false);
    }

    /// <summary>As <see cref="RunAsync(int, Action{int}, int, CancellationToken)"/>, for work that waits on reads: on tasks, never a thread.</summary>
    internal static async Task RunAsync(int count, Func<int, CancellationToken, ValueTask> work, int degree, CancellationToken cancellationToken)
    {
        int workers = Math.Clamp(degree, 1, Math.Max(1, count));
        int[] next = [-1];
        using CancellationTokenSource failed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken token = failed.Token;
        Task[] tasks = new Task[workers];
        for (int w = 0; w < workers; w++)
        {
            tasks[w] = Task.Run(
                async () =>
                {
                    int item;
                    while ((item = Interlocked.Increment(ref next[0])) < count)
                    {
                        token.ThrowIfCancellationRequested();
                        await work(item, token).ConfigureAwait(false);
                    }
                },
                token);
        }

        await AggregationEngine.GuardedAsync(tasks, failed).ConfigureAwait(false);
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

    /// <summary>The means of <paramref name="groups"/>, in order: a batch of a result column.</summary>
    void Means(ReadOnlySpan<int> groups, Span<double?> into);
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

    internal override void Results(ReadOnlySpan<int> groups, Span<double?> into) => sum.Means(groups, into);
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
/// source's rows stay below 2^32, which no group's count can then pass, 64 where they are not known.
/// </summary>
internal sealed class CountSlot<TCount> : RecordSlot<TCount, long>
    where TCount : unmanaged, IBinaryInteger<TCount>
{
    internal override TCount Seed => TCount.Zero;

    /// <summary>The counts, for a slot that counts the rows it folds into them (<see cref="AggregateSlot.StepRowsCounted"/>).</summary>
    internal StateView<TCount> Counts => States;

    internal override void StepRange(in BatchInput input, int start, int end, int group) =>
        State(group) += TCount.CreateTruncating(RowMasks.Count(input.Selection, start, end));

    internal override void StepRows(in BatchInput input, ReadOnlySpan<int> groups)
    {
        StateView<TCount> counts = States;
        if (input.Selection.IsEmpty)
        {
            // Every row: a loop with nothing but the count, the cursor's test of its mask out of it.
            for (int row = input.Start; row < input.End; row++)
            {
                counts[groups[row]]++;
            }

            return;
        }

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
