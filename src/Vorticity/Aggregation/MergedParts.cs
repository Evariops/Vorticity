using System;
using System.Buffers;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Vorticity.Scanning;

namespace Vorticity.Aggregating;

/// <summary>
/// The parts of a group by's result delivered one at a time, each built into its batches and let go
/// before it is handed to the reader (PLAN-HIGH-CARDINALITY, H6, H7, H14): a core's, or a merge's in
/// parts.
/// </summary>
internal abstract class ResultParts
{
    /// <summary>The parts written to the scratch, once known: none for a merge's.</summary>
    internal virtual int Count => 0;

    /// <summary>The batches of the next part, built, the part let go; null past the last.</summary>
    internal abstract ValueTask<PartResult?> NextAsync(CancellationToken cancellationToken);

    /// <summary>The workers stopped and awaited, a consumer leaving early: none touches the query's memory once it is given back.</summary>
    internal abstract ValueTask StopAsync();

    /// <summary>Every part done with, delivered or not.</summary>
    internal abstract void Close();
}

/// <summary>
/// The merge of the lanes' tables in parts (6d1), a part at a time: every partition's groups placed by
/// part beforehand, then each part merged from every partition into a table of its own, its groups
/// reserved before they come, taken from a queue by whoever merges next.
/// </summary>
internal sealed class PartMerge
{
    private readonly AggregationPartition[] _partitions;
    private readonly GroupKeys[] _keysOf;
    private readonly int[][] _placed;
    private readonly int[][] _starts;
    private readonly AggregationPlan _plan;
    private readonly AggregateSlot?[] _settled;
    private readonly int[] _inputs;
    private readonly ScanSource _source;
    private readonly QueryMemory _memory;
    private readonly bool _byValue;
    private readonly int _partBits;
    private readonly long _perGroup;
    private readonly Lock _gate = new Lock();
    private long _doneGroups;
    private long _doneEntries;
    private int _taken = -1;

    internal PartMerge(
        AggregationPartition[] partitions, GroupKeys[] keysOf, int[][] placed, int[][] starts, int parts, AggregationPlan plan, AggregateSlot?[] settled,
        int[] inputs, ScanSource source, QueryMemory memory, bool byValue, long perGroup)
    {
        _partitions = partitions;
        _keysOf = keysOf;
        _placed = placed;
        _starts = starts;
        Parts = parts;
        _plan = plan;
        _settled = settled;
        _inputs = inputs;
        _source = source;
        _memory = memory;
        _byValue = byValue;
        _partBits = System.Numerics.BitOperations.Log2((uint)parts);
        _perGroup = perGroup;
    }

    /// <summary>The parts the merge cuts its groups into.</summary>
    internal int Parts { get; }

    /// <summary>The partitions merged, whose tables every part reads until the last is merged.</summary>
    internal AggregationPartition[] Partitions => _partitions;

    /// <summary>The next part no one took; <see cref="Parts"/> past the last.</summary>
    internal int Take() => Math.Min(Interlocked.Increment(ref _taken), Parts);

    /// <summary>An empty result's keys and slots: the first outcome of a merge delivered part by part, which holds no group.</summary>
    internal (GroupKeys Keys, AggregateSlot[] Slots) Empty() => (_keysOf[0].ForPart(), AggregationPartition.NewSlots(_plan, _settled, _source));

    /// <summary>The lanes' keys and the places of their groups let go: every part is merged, or none will be.</summary>
    internal void Release()
    {
        Array.Clear(_keysOf);
        Array.Clear(_placed);
        Array.Clear(_starts);
    }

    /// <summary>
    /// The part <paramref name="part"/> merged from every partition: its keys and slots, and what it
    /// reserved and measured of the query's memory, twice its groups' bytes beside the lanes' tables.
    /// </summary>
    /// <exception cref="VortexMemoryException">The budget does not grant the part's table.</exception>
    internal (GroupKeys Keys, AggregateSlot[] Slots, long Reserved, long Measured) Merge(int part)
    {
        int entries = 0;
        int most = 0;
        for (int p = 0; p < _partitions.Length; p++)
        {
            int sent = _starts[p][part + 1] - _starts[p][part];
            entries += sent;
            most = Math.Max(most, sent);
        }

        // As many groups as its largest partition sends while no part is done, then its entries at the
        // rate of groups to entries the parts done had.
        int reserve = most;
        lock (_gate)
        {
            if (_doneEntries > 0)
            {
                reserve = (int)Math.Clamp(entries * 1.1 * _doneGroups / _doneEntries, most, entries);
            }
        }

        // The part's table reserved before it is built, twice what its groups cost the lanes: a
        // doubling's room, as a lane's table keeps.
        long ahead = 2L * reserve * _perGroup;
        if (!_memory.TryGrow(ahead))
        {
            throw _memory.Exceeded("merge of a group by", reserve, ahead);
        }

        GroupKeys keys = _byValue ? _keysOf[0].ForValuePart(part, _partBits) : _keysOf[0].ForPart();
        keys.Reserve(reserve);
        AggregateSlot[] slots = AggregationPartition.NewSlots(_plan, _settled, _source);
        foreach (AggregateSlot slot in slots)
        {
            slot.EnsureGroups(reserve);
        }

        int[] map = ArrayPool<int>.Shared.Rent(Math.Max(1, most));
        try
        {
            for (int p = 0; p < _partitions.Length; p++)
            {
                int from = _starts[p][part];
                int count = _starts[p][part + 1] - from;
                if (count == 0)
                {
                    continue;
                }

                ReadOnlySpan<int> groups = _placed[p].AsSpan(from, count);
                Span<int> into = map.AsSpan(0, count);
                _keysOf[p].MergeInto(keys, groups, into);
                for (int s = 0; s < slots.Length; s++)
                {
                    slots[s].EnsureGroups(keys.Count);
                    if (_inputs[s] != AggregationPartition.Settled)
                    {
                        slots[s].MergeFrom(_partitions[p].Slots[s], groups, into);
                    }
                }
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(map);
        }

        // Twice what the part's table came to, beside the lanes', which live until the merge is done.
        long built = keys.Footprint + AggregateSlot.FootprintOf(slots);
        _memory.Measure(built);
        long more = (2 * built) - ahead;
        if (more > 0 && !_memory.TryGrow(more))
        {
            _memory.Shrink(ahead);
            _memory.Measure(-built);
            throw _memory.Exceeded("merge of a group by", keys.Count, more);
        }

        lock (_gate)
        {
            _doneGroups += keys.Count;
            _doneEntries += entries;
        }

        return (keys, slots, ahead + Math.Max(0, more), built);
    }
}

/// <summary>
/// The parts of a merge in parts delivered one at a time (PLAN-HIGH-CARDINALITY, H14), as a core's are
/// (H7): each part merged and built into its batches by the worker that took it, while the others
/// merge, then let go, its batches handed to the result; and by the result's reader, which merges and
/// builds the next part no worker took rather than wait for one. The lanes' tables go once the last
/// part is merged.
/// </summary>
/// <remarks>
/// A result built on the reader's thread from the merged parts had that thread alone for a sixth to a
/// third of a query at fourteen lanes, once the merge was done; here the first batch comes once the
/// first part is merged.
/// </remarks>
internal sealed class MergedParts : ResultParts
{
    private readonly PartMerge _merge;
    private readonly PartBuilder _builder;
    private readonly AggregationPlan _plan;
    private readonly QueryMemory _memory;
    private readonly long _cut;
    private readonly long _started;
    private readonly CancellationTokenSource _stopping;
    private ChannelReader<Built>? _built;
    private Built? _handed;
    private long _groups;
    private int _merged;
    private int _lanesGone;
    private bool _closed;

    /// <summary>
    /// The parts of <paramref name="merge"/> merged and built by <paramref name="workers"/> in the
    /// background, a part each ahead of the reader at most; <paramref name="cut"/> bytes of the query's
    /// memory, the places of the lanes' groups, given back with the lanes' tables.
    /// </summary>
    internal MergedParts(
        PartMerge merge, PartBuilder builder, int workers, AggregationPlan plan, QueryMemory memory, long cut, long started, CancellationToken cancellationToken)
    {
        _merge = merge;
        _builder = builder;
        _plan = plan;
        _memory = memory;
        _cut = cut;
        _started = started;
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Channel<Built> built = Channel.CreateBounded<Built>(new BoundedChannelOptions(Math.Max(1, workers)) { SingleReader = true });
        _built = built.Reader;
        CancellationToken token = _stopping.Token;
        Task[] running = new Task[workers];
        for (int w = 0; w < workers; w++)
        {
            running[w] = Task.Run(() => PublishAsync(built.Writer, token), token);
        }

        _ = CompleteAsync(running, built.Writer);
    }

    internal override async ValueTask<PartResult?> NextAsync(CancellationToken cancellationToken)
    {
        // The part handed before is done with: the memory its table held, which its batches stood for, goes.
        GiveBack(_handed);
        _handed = null;
        if (_built is not { } built)
        {
            return null;
        }

        while (true)
        {
            // A part a worker built; or else the next no worker took, merged and built here rather than
            // wait; or else, every part taken, the wait for those the workers still build. A worker's
            // failure comes out of the wait, the channel completed with it.
            if (built.TryRead(out Built? ready))
            {
                _handed = ready;
                return ready.Result;
            }

            int part = _merge.Take();
            if (part < _merge.Parts)
            {
                _handed = await BuildAsync(part, cancellationToken).ConfigureAwait(false);
                return _handed.Result;
            }

            if (!await built.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                break;
            }
        }

        // The channel completed: the workers are done, and every part was merged.
        _built = null;
        return null;
    }

    internal override async ValueTask StopAsync()
    {
        GiveBack(_handed);
        _handed = null;
        if (_built is not { } built)
        {
            return;
        }

        // The channel drained until the workers complete it, the batches built given back: the consumer
        // left, and a worker's failure past that point is no one's to see.
        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            while (await built.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
            {
                while (built.TryRead(out Built? ready))
                {
                    _builder.Give(ready.Result);
                    GiveBack(ready);
                }
            }
        }
        catch (Exception)
        {
        }

        _built = null;
    }

    internal override void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        GiveBack(_handed);
        _handed = null;
        LetLanesGo();
        _stopping.Dispose();
    }

    /// <summary>The parts a worker takes, merged and built, each published once built: the next waits for the reader past a part each ahead.</summary>
    private async Task PublishAsync(ChannelWriter<Built> writer, CancellationToken cancellationToken)
    {
        int part;
        while ((part = _merge.Take()) < _merge.Parts)
        {
            Built built = await BuildAsync(part, cancellationToken).ConfigureAwait(false);
            try
            {
                await writer.WriteAsync(built, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _builder.Give(built.Result);
                GiveBack(built);
                throw;
            }

            // The pool's queue: the reader's continuation behind the workers' own gets its turn.
            await Task.Yield();
        }
    }

    /// <summary>
    /// The part merged, then built into its batches and let go; the lanes' tables once the last part is
    /// merged. What its table held of the query's memory stays held until the reader is done with its
    /// batches, which the session's pool holds outside any query's count.
    /// </summary>
    private async ValueTask<Built> BuildAsync(int part, CancellationToken cancellationToken)
    {
        (GroupKeys keys, AggregateSlot[] slots, long reserved, long measured) = _merge.Merge(part);
        long groups = Interlocked.Add(ref _groups, keys.Count);
        if (Interlocked.Increment(ref _merged) == _merge.Parts)
        {
            LetLanesGo();
            _plan.LastGroups = groups;
            _plan.PeakGroups = Math.Max(_plan.PeakGroups, groups);
            if (_plan.LastRun is { } run)
            {
                _plan.LastRun = run with { MergeTicks = Stopwatch.GetTimestamp() - _started };
            }
        }

        Built built = new Built(reserved, measured);
        try
        {
            AggregationOutcome outcome = new AggregationOutcome(_plan, slots, keys, AggregationEngine.Shuffled(keys.Order(sorted: false))) { Memory = _memory };
            built.Result = await _builder.BuildAsync(outcome, cancellationToken).ConfigureAwait(false);
            return built;
        }
        catch
        {
            GiveBack(built);
            throw;
        }
        finally
        {
            keys.Release();
        }
    }

    /// <summary>What a part's table held of the query's memory given back, once: its batches delivered or let go.</summary>
    private void GiveBack(Built? built)
    {
        if (built is null || Interlocked.Exchange(ref built.Reserved, 0) is not (> 0 and long reserved))
        {
            return;
        }

        _memory.Shrink(reserved);
        _memory.Measure(-built.Measured);
    }

    /// <summary>A part's batches, and what its table held of the query's memory until they are done with.</summary>
    private sealed class Built(long reserved, long measured)
    {
        internal long Reserved = reserved;

        internal long Measured { get; } = measured;

        internal PartResult Result { get; set; } = null!;
    }

    /// <summary>The lanes' tables let go with what they held of the query's memory, and the places of their groups: once, every part merged or the result done with.</summary>
    private void LetLanesGo()
    {
        if (Interlocked.Exchange(ref _lanesGone, 1) != 0)
        {
            return;
        }

        foreach (AggregationPartition partition in _merge.Partitions)
        {
            partition.LetGo();
        }

        _merge.Release();
        _memory.Shrink(_cut);
        _memory.Measure(-_cut);
    }

    /// <summary>The workers awaited, the result's channel completed, with their failure if any, which its reader then throws. It never throws itself.</summary>
    private async Task CompleteAsync(Task[] workers, ChannelWriter<Built> writer)
    {
        Exception? failure = null;
        try
        {
            await AggregationEngine.GuardedAsync(workers, _stopping).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            failure = error;
        }

        writer.TryComplete(failure);
    }
}
