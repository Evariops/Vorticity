using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Vorticity.Aggregating;

/// <summary>
/// The groups of a query held once, whatever its degree (PLAN-HIGH-CARDINALITY, H4, milestone 1): the
/// key space cut into <see cref="PartCount"/> parts by the top bits of a hash of the keys under
/// <see cref="MergeHash.Seed"/>, each part holding its groups in sub-tables of bounded size. Each lane
/// folds its rows into its own partition, a cache of bounded capacity; at two thirds of it the cache's
/// groups are copied, a record and a key each, into the lane's open batches, one a part, and the cache
/// is cleared. A full batch is deposited on its part's stack; past the part's threshold, α times its
/// groups, the lane that deposits applies the whole stack if no other lane holds the part, sub-table
/// by sub-table, splitting those grown past their bound. At the end the lanes' caches and open batches
/// are deposited and every part's stack applied, the parts side by side: the sub-tables are the
/// result, read as one.
/// </summary>
/// <remarks>
/// <para>
/// A query whose caches never fill runs as before, its lanes' partitions merged at the end. The core
/// has no governor yet (milestone 2): α is derived from the lanes and the bytes of a group and of an
/// entry, never lowered nor raised, and nothing spills.
/// </para>
/// <para>
/// A burst: the lane that deposits pushes its batch, adds its entries to the part's count, and past the
/// threshold tries the part's lock. The holder takes the whole stack, applies it, takes its entries off
/// the count and releases the lock, then reads the count again past a full fence, and retries while
/// it is past the threshold. A release alone would let that read pass it, and leave a batch pushed
/// meanwhile by a lane that failed the lock waiting for the end. No lane ever waits on the lock.
/// </para>
/// </remarks>
internal sealed partial class GroupCore
{
    /// <summary>The parts of the key space: a byte of the hash names one, and a lane's open batch of each stays a few lines of its first level of cache.</summary>
    internal const int PartCount = 256;

    /// <summary>The bits of the hash below a part's, its top byte's shift.</summary>
    internal const int PartShift = 64 - 8;

    /// <summary>The bytes of a batch: a deposit and its fences spread over hundreds of entries.</summary>
    private const int BatchBytes = 16 * 1024;

    /// <summary>
    /// The bytes of a batch of the core the governor turns to under pressure (milestone 2): a lane opens
    /// one on every part, 256 kilobytes a lane where sixteen made four megabytes, more than the tables
    /// of many a lane.
    /// </summary>
    private const int LeanBatchBytes = 1024;

    /// <summary>The entries a part of the core the governor turns to holds pending at least before it is applied.</summary>
    private const int LeanFloor = 256;

    /// <summary>The entries a part holds pending at least before it is applied: its first bursts fall on sub-tables still small, in cache.</summary>
    private const int DefaultFloor = 4_096;

    /// <summary>The bytes of a sub-table past which it splits, S: within the private cache of any current core.</summary>
    private const long TableBytes = 256 * 1024;

    /// <summary>The most α: past it, reading the sub-tables back weighs a few percent of the batches' bytes, and only the memory grows.</summary>
    private const int MostAlpha = 8;

    /// <summary>
    /// The lanes from which the core holds a query's groups. Measured on the Mac on 2026-10-06, each
    /// row of the core costs two to four times a row of a lane's table alone, its cache, its entry and
    /// its application, which a lane's table repays only when many lanes each build a table of every
    /// key and the merge rehashes them all: at one lane two to three times slower, at two 1.3 to 1.9,
    /// at four about even, at eight and fourteen faster by a sixth to a half. Below, the lanes' tables;
    /// to measure again with the governor, which bounds memory at every degree (H2), and on x64 (X1).
    /// </summary>
    internal const int DefaultLanes = 8;

    /// <summary>The most bits of the hash a part's directory takes past the part's: never reached below 2^31 groups.</summary>
    internal const int MostDepth = 24;

    /// <summary>
    /// The bytes of a lane's cache at more than one lane: its share of the private cache of the
    /// smallest core it may run on, a megabyte on current ones, less its open batches' lines and a
    /// window of rows (PLAN-HIGH-CARDINALITY, principle 14). A fixed default until the topology is read
    /// (H14).
    /// </summary>
    private const long LaneCacheBytes = 768 * 1024;

    /// <summary>The bytes of the cache of a lane alone, which has the level of cache its cluster shares to itself.</summary>
    private const long AloneCacheBytes = 8L * 1024 * 1024;

    /// <summary>
    /// The share of its rows a cache finds below which its lane bypasses it, ε: a row the cache misses
    /// costs an insertion, then an entry, where a row bypassed costs its entry alone, so that a cache
    /// that finds fewer than about half its rows costs more than it saves. To be measured (H14).
    /// </summary>
    private const double DefaultBypass = 0.5;

    private readonly AggregationPlan _plan;
    private readonly AggregateSlot?[] _settled;
    private readonly ColumnShape[] _columns;
    private readonly int[] _inputs;
    private readonly ScanSource? _source;
    private readonly CorePart[] _parts = new CorePart[PartCount];
    private readonly ArrayShelf _shelf;
    private readonly KeyFacts? _facts;
    private readonly QueryMemory? _memory;
    private readonly long _groupBytes;

    // The bytes a sub-table holds at most before it splits: what a split's two halves take at once, each.
    private readonly long _tableBytes;

    // The batches applied that their lanes did not keep, for any lane to fill again: a lane that never
    // holds a part takes back those others applied, so that the batches made follow the entries in
    // flight, not the deposits. Chains go in and out whole, a lock a slab's worth.
    private readonly Lock _freeGate = new Lock();
    private readonly List<ulong[]> _slabs = [];
    private PartBatch? _free;
    private int _freeCount;
    private readonly int _batchWords;
    private readonly int _batchEntries;
    private readonly long _tableGroups;

    private int _engaged;
    private long _flushes;
    private long _flushed;
    private long _bypassed;
    private long _bursts;
    private long _pending;
    private long _pendingPeak;
    private long _reloaded;
    private long _batches;
    private int _splits;
    private long _alone;

    // The entries applied and the groups they made, over the query so far: the share of an entry that
    // makes a group, which sizes a part's sub-tables ahead of a large application.
    private long _applied;
    private long _made;

    // Delivered part by part (H7): the groups of the parts applied and not let go yet, the most at once,
    // the sub-tables of the parts let go, and whether every part is applied.
    private long _heldGroups;
    private long _peakGroups;
    private int _tablesLet;
    private bool _allApplied;

    private GroupCore(
        AggregationPlan plan, AggregateSlot?[] settled, ColumnShape[] columns, int[] inputs, ScanSource? source, KeyFacts? facts, GroupKeys kind, RecordLayout? layout, int lanes,
        QueryMemory? memory, bool lean)
    {
        _plan = plan;
        _settled = settled;
        _columns = columns;
        _inputs = inputs;
        _source = source;
        _facts = facts;

        // Its sub-tables and slabs under the query's memory, when it counts it (H2): an array counted as
        // it enters the query, given back as it leaves; those on the shelf's piles stay counted.
        _memory = memory;
        _shelf = memory is null ? new ArrayShelf() : new ArrayShelf(memory, pooled: true);

        // A core that spills takes past its budget what a stack asks more, and its lane spills the
        // largest part before its next batch (H6).
        _shelf.Overdraws = memory is not null && plan.CoreSpills;
        Kind = kind;
        int keyBytes = kind.EntryBytes;
        Shape = EntryShape.Of(layout, keyBytes);
        long groupBytes = GroupBytes(keyBytes, (layout?.Stride ?? 0) * sizeof(ulong));
        _groupBytes = groupBytes;
        int entryBytes = Shape.Words * sizeof(ulong);

        // Under pressure (milestone 2), the core holds its groups once and as little else as it can: α at
        // its floor, and a part applied from 256 entries pending, a megabyte over the 256 parts where the
        // floor of 4 096 let sixteen wait; a cache a quarter of a lane's; batches of a kilobyte, which
        // open on every part of every lane. Bursts read the sub-tables more often: memory comes first.
        Alpha = plan.CoreAlpha ?? (lean ? 1 : (int)Math.Clamp((lanes - 1) * groupBytes / entryBytes, 1, MostAlpha));
        Floor = plan.CoreFloor ?? (lean ? LeanFloor : DefaultFloor);
        long cacheBytes = (lanes == 1 ? AloneCacheBytes : LaneCacheBytes) / (lean ? 4 : 1);
        long batchBytes = lean ? LeanBatchBytes : BatchBytes;

        // Under its budget, the lanes' caches and open batches take an eighth of its ceiling at most,
        // half each: the rest is for the groups. Small batches deposit more often, small caches flush
        // more often; a sub-table splits at a 256th of the ceiling, the two halves of a split a 128th.
        // Under pressure, memory comes first.
        long tableBytes = TableBytes;
        if (lean && memory is not null)
        {
            long ceiling = memory.Ceiling;
            long lane = ceiling / 8 / Math.Max(1, lanes);
            cacheBytes = Math.Min(cacheBytes, lane / 2);
            batchBytes = Math.Clamp(lane / 2 / PartCount, 4L * entryBytes, batchBytes);
            tableBytes = Math.Clamp(ceiling / PartCount, 64 * groupBytes, TableBytes);
        }

        Capacity = plan.CoreCapacity ?? (int)Math.Max(64, cacheBytes / groupBytes);
        FlushAt = Math.Max(1, (int)(2L * Capacity / 3));
        Bypass = plan.CoreBypass ?? DefaultBypass;
        _tableGroups = plan.CoreTableGroups ?? Math.Max(64, tableBytes / groupBytes);
        _tableBytes = _tableGroups * groupBytes;
        _batchEntries = plan.CoreBatchEntries ?? (int)Math.Max(1, batchBytes / entryBytes);
        _batchWords = _batchEntries * Shape.Words;
        LaneBytes = ((long)PartCount * _batchWords * sizeof(ulong)) + (Capacity * groupBytes);
        for (int p = 0; p < PartCount; p++)
        {
            _parts[p] = new CorePart();
        }
    }

    /// <summary>An empty index of the keys' kind, called for what it does on entries.</summary>
    internal GroupKeys Kind { get; }

    /// <summary>Where a record and a key lie in an entry.</summary>
    internal EntryShape Shape { get; }

    /// <summary>The multiple of a part's groups its pending entries pass before it is applied: ⌊(lanes − 1) × s / e⌋, from 1 to 8.</summary>
    internal int Alpha { get; }

    /// <summary>The entries a part holds pending at least before it is applied.</summary>
    internal int Floor { get; }

    /// <summary>The groups of a lane's cache.</summary>
    internal int Capacity { get; }

    /// <summary>
    /// What a lane holds in the core whatever its rows: a batch open for every part and its cache. A lane
    /// turns to the core under pressure only when its own table holds more (milestone 2): one that
    /// holds less would hold more once it turned.
    /// </summary>
    internal long LaneBytes { get; }

    /// <summary>The groups a lane's cache holds when it is copied into its batches: two thirds of its capacity.</summary>
    internal int FlushAt { get; }

    /// <summary>The share of its rows a lane's cache finds, once it has filled, below which the lane bypasses it, ε.</summary>
    internal double Bypass { get; }

    /// <summary>Whether a lane's cache has filled, which makes the core the query's state.</summary>
    internal bool Engaged => Volatile.Read(ref _engaged) != 0;

    /// <summary>
    /// The pressure the core was made under, by the governor (milestone 2); null for a core the plan
    /// chose. Its shelf then takes past the budget what a stack it judged room for asks more, while a
    /// lane still holds a table it gives back.
    /// </summary>
    internal CorePressure? Pressure
    {
        get => _shelf.Pressure;
        set => _shelf.Pressure = value;
    }

    internal AggregationPlan Plan => _plan;

    internal AggregateSlot?[] Settled => _settled;

    internal ScanSource? Source => _source;

    /// <summary>
    /// The core of a query, or null when it runs on its lanes' tables alone: the switch is off, the
    /// lanes are fewer than <see cref="DefaultLanes"/>, or the core cannot hold its groups (<see cref="Holding"/>).
    /// </summary>
    internal static GroupCore? Of(
        AggregationPlan plan, AggregateSlot?[] settled, ColumnShape[] columns, int[] inputs, ScanSource source, KeyFacts? facts, bool sorted, KeyTop? top, int lanes,
        QueryMemory? memory = null) =>
        plan.Core && lanes >= (plan.CoreLanes ?? DefaultLanes) ? Holding(plan, settled, columns, inputs, source, facts, sorted, top, lanes, memory, lean: plan.CoreLean) : null;

    /// <summary>
    /// A core that holds the query's groups, or null when it cannot: the key does not travel in
    /// batches (<see cref="GroupKeys.EntryBytes"/>), a state lies apart from the records, the key is
    /// sorted, or a top keeps a lane's best groups. Its arrays reserved under <paramref name="memory"/>;
    /// <paramref name="lean"/> when the governor turns to it, which holds as little past the groups as it can.
    /// </summary>
    internal static GroupCore? Holding(
        AggregationPlan plan, AggregateSlot?[] settled, ColumnShape[] columns, int[] inputs, ScanSource source, KeyFacts? facts, bool sorted, KeyTop? top, int lanes,
        QueryMemory? memory, bool lean)
    {
        if (!plan.Grouped || sorted || top is not null)
        {
            return null;
        }

        GroupKeys kind = plan.CreateKeys(sorted: false, CacheFacts(facts));
        if (kind.EntryBytes == 0)
        {
            return null;
        }

        AggregateSlot[] slots = AggregationPartition.NewSlots(plan, settled, source, out GroupRecords? records);
        foreach (AggregateSlot slot in slots)
        {
            if (slot.StateBytes == 0)
            {
                return null;
            }
        }

        return new GroupCore(plan, settled, columns, inputs, source, CacheFacts(facts), kind.ForPart(), records?.Layout, lanes, memory, lean);
    }

    /// <summary>
    /// A lane's cache, under the query's <paramref name="memory"/>: the small table a lane goes on with
    /// once its own has emptied into the core (PLAN-HIGH-CARDINALITY, H4, milestone 2).
    /// </summary>
    internal AggregationPartition Cache(QueryMemory? memory) =>
        new AggregationPartition(_plan, _settled, _columns, _inputs, sorted: false, source: _source, facts: _facts, memory: memory);

    /// <summary>A partition that folds each row of a batch into a group of its own, which a lane bypassing its cache flushes after each batch.</summary>
    internal AggregationPartition RowPartition() =>
        new AggregationPartition(_plan, _settled, _columns, _inputs, sorted: false, source: _source, keys: Kind.Appending());

    /// <summary>Counts rows a lane folded apart from its cache.</summary>
    internal void Bypassed(int rows) => Interlocked.Add(ref _bypassed, rows);

    /// <summary>
    /// What the statistics say of the key, as a lane's cache reads it: no table of groups by value
    /// sized on the source's rows, which every lane would fill whole over its pass; one of 2^16
    /// values stays.
    /// </summary>
    internal static KeyFacts? CacheFacts(KeyFacts? facts) => facts is { } known ? known with { Rows = -1 } : null;

    /// <summary>A lane's side of the core.</summary>
    internal LaneCore Lane() => new LaneCore(this);

    /// <summary>Whether slot <paramref name="slot"/> folds rows, and so merges.</summary>
    internal bool Folds(int slot) => _inputs[slot] != AggregationPartition.Settled;

    /// <summary>A group's bytes in a table: its slot at the table's load, its key, its record.</summary>
    private static long GroupBytes(int keyBytes, int recordBytes) => ((keyBytes + sizeof(int)) * 5L / 3) + keyBytes + recordBytes;

    /// <summary>Marks the core as the query's state: a lane's cache filled.</summary>
    internal void Engage()
    {
        if (Volatile.Read(ref _engaged) == 0)
        {
            Interlocked.Exchange(ref _engaged, 1);
        }
    }

    /// <summary>Counts a cache copied into its batches, and its groups.</summary>
    internal void Flushed(int groups)
    {
        Interlocked.Increment(ref _flushes);
        Interlocked.Add(ref _flushed, groups);
    }

    /// <summary>Counts the bytes of a sub-table a burst reads again.</summary>
    internal void Reloaded(long bytes) => Interlocked.Add(ref _reloaded, bytes);

    /// <summary>The batches of a slab, cut from one array.</summary>
    internal const int SlabBatches = 64;

    /// <summary>
    /// A slab of new batches, linked first to last, for <paramref name="lane"/> to fill: one large
    /// array, which the collector neither copies nor clears, from the shelf, which takes it back at the
    /// end; past the query's budget for a lane that turned to the core under pressure (milestone 2).
    /// </summary>
    internal PartBatch NewSlab(LaneCore lane)
    {
        Interlocked.Add(ref _batches, SlabBatches);
        ulong[] slab = _shelf.Take<ulong>(SlabBatches * _batchWords, zeroed: false, overdraw: lane.Pressed);
        lock (_freeGate)
        {
            _slabs.Add(slab);
        }

        PartBatch? next = null;
        for (int b = SlabBatches - 1; b >= 0; b--)
        {
            next = new PartBatch(slab, b * _batchWords, _batchEntries) { Next = next };
        }

        return next!;
    }

    /// <summary>
    /// A batch of <paramref name="entries"/> entries in an array of its own, past the budget: a part's
    /// share of a lane's table emptied into the core under pressure (milestone 2), which its part gives
    /// back once applied (<see cref="Drop"/>).
    /// </summary>
    internal PartBatch NewAlone(int entries)
    {
        ulong[] words = _shelf.Take<ulong>(entries * Shape.Words, zeroed: false, overdraw: true);
        Interlocked.Add(ref _alone, words.Length * sizeof(ulong));
        return new PartBatch(words, 0, entries) { Alone = true };
    }

    /// <summary>A batch of its own applied: its array leaves the query's count, to the next collection.</summary>
    internal void Drop(PartBatch batch)
    {
        _shelf.Drop(batch.Words);
        Interlocked.Add(ref _alone, -batch.Words.Length * sizeof(ulong));
    }

    /// <summary>The bytes of the batches of their own not applied yet: memory that comes back as their parts apply them.</summary>
    internal long AloneBytes => Interlocked.Read(ref _alone);

    /// <summary>Every batch lanes gave back for any lane, linked, and their count; null when there is none.</summary>
    internal PartBatch? TakeShared(out int count)
    {
        lock (_freeGate)
        {
            PartBatch? first = _free;
            count = _freeCount;
            _free = null;
            _freeCount = 0;
            return first;
        }
    }

    /// <summary>Gives <paramref name="count"/> batches, <paramref name="first"/> to <paramref name="last"/> linked, to any lane.</summary>
    internal void GiveShared(PartBatch first, PartBatch last, int count)
    {
        lock (_freeGate)
        {
            last.Next = _free;
            _free = first;
            _freeCount += count;
        }
    }

    /// <summary>A sub-table of a part, empty, holding the groups whose hashes share <paramref name="depth"/> bits past the part's.</summary>
    internal SubTable NewTable(int depth)
    {
        AggregateSlot[] slots = AggregationPartition.NewSlots(_plan, _settled, _source, out GroupRecords? records, _shelf);
        return new SubTable(Kind.ForTable(_shelf), slots, records, depth);
    }

    /// <summary>The groups past which a sub-table splits.</summary>
    internal long TableGroups => _tableGroups;

    /// <summary>
    /// Pushes a full batch onto its part's stack and counts its entries; past the part's threshold, a
    /// lane applies the stack if no other lane holds the part. At the end, <paramref name="lane"/> is
    /// null: the batch only waits.
    /// </summary>
    internal void Deposit(int index, PartBatch batch, LaneCore? lane)
    {
        CorePart part = _parts[index];
        PartBatch? head = Volatile.Read(ref part.Head.Value);
        while (true)
        {
            batch.Next = head;
            PartBatch? seen = Interlocked.CompareExchange(ref part.Head.Value, batch, head);
            if (ReferenceEquals(seen, head))
            {
                break;
            }

            head = seen;
        }

        long pending = Interlocked.Add(ref part.Pending.Value, batch.Count);
        if (lane is null)
        {
            Pend(batch.Count);
            return;
        }

        lane.Deposited(batch.Count);

        // A lane turning to the core deposits its table and bursts nothing: the sub-tables would grow
        // before the table they replace is given back (milestone 2). The next lane's deposit applies them.
        if (pending > Threshold(part) && !lane.Turning)
        {
            if (Waits(pending))
            {
                lane.Starved = true;
                return;
            }

            Burst(part, lane.Applier);
        }
    }

    /// <summary>
    /// Whether a part's stack of <paramref name="pending"/> entries waits for room: under pressure, while
    /// a lane still holds a table it gives back, or when the core spills, when the budget could not take
    /// a group an entry, twice over for a doubling, and a split's two halves at the sub-tables' bound.
    /// Its lane waits for the next table given back, or spills, before its next batch.
    /// </summary>
    private bool Waits(long pending) =>
        _memory is { } memory && !memory.CanGrow(Growth(pending)) && (Pressure is { Holding: true } || CanSpill);

    /// <summary>The most a part's application of <paramref name="pending"/> entries may take: a group an entry, twice over for a doubling, and a split's two halves.</summary>
    private long Growth(long pending) => (2 * pending * _groupBytes) + (2 * _tableBytes);

    /// <summary>The entries pending past which a part is applied: its floor, or α times its groups.</summary>
    private long Threshold(CorePart part) => Math.Max(Floor, (long)Alpha * Volatile.Read(ref part.Groups));

    /// <summary>Applies the part's stack while the lock is free and the count past the threshold.</summary>
    private void Burst(CorePart part, CoreApplier applier)
    {
        while (part.Gate.TryEnter())
        {
            try
            {
                if (_plan.CoreBurstSpin is int spin)
                {
                    Thread.SpinWait(spin);
                }

                int applied = applier.Apply(part, Interlocked.Exchange(ref part.Head.Value, null), burst: true);
                Interlocked.Add(ref part.Pending.Value, -applied);
                Interlocked.Increment(ref _bursts);

                // A burst that found the stack empty makes no progress: a lane that failed while it
                // applied took the stack and left its count, and the next would spin on the lock forever.
                if (applied == 0)
                {
                    return;
                }
            }
            finally
            {
                part.Gate.Exit();
            }

            // A full fence: the release alone would let the read pass it.
            Interlocked.MemoryBarrier();
            if (Volatile.Read(ref part.Pending.Value) <= Threshold(part))
            {
                return;
            }
        }
    }

    /// <summary>Counts entries deposited, or applied when negative, and the most pending at once: a lane's deposits come a few batches at a time, which leaves the peak a few batches a lane short.</summary>
    internal void Pend(int entries)
    {
        long now = Interlocked.Add(ref _pending, entries);
        long peak = Volatile.Read(ref _pendingPeak);
        while (now > peak)
        {
            long seen = Interlocked.CompareExchange(ref _pendingPeak, now, peak);
            if (seen == peak)
            {
                break;
            }

            peak = seen;
        }
    }

    /// <summary>Counts entries applied and the groups they made.</summary>
    internal void Applied(int entries, int groups)
    {
        Interlocked.Add(ref _applied, entries);
        Interlocked.Add(ref _made, groups);
    }

    /// <summary>
    /// Before the end applies <paramref name="entries"/> entries to a part: its sub-tables split down to
    /// the depth the groups they will make need, at the share of an entry that made a group over the
    /// pass (every one when none was applied), each to three quarters of its bound. A group then goes
    /// once to its sub-table, rather than through every split on its way: keys met once went through
    /// three or four.
    /// </summary>
    internal void Presplit(CorePart part, int entries, CoreApplier applier)
    {
        long applied = Interlocked.Read(ref _applied);
        double share = applied > 0 ? (double)Interlocked.Read(ref _made) / applied : 1;
        long expected = part.Groups + (long)(entries * share);
        long target = Math.Max(1, _tableGroups * 3 / 4);
        if (expected <= part.Tables.Count * target)
        {
            return;
        }

        int depth = Math.Min(MostDepth, (int)Math.Ceiling(Math.Log2((double)expected / target)));
        for (int t = 0; t < part.Tables.Count; t++)
        {
            while (part.Tables[t].Depth < depth)
            {
                Split(part, t, applier);
            }
        }
    }

    /// <summary>Splits the sub-table at <paramref name="index"/> of the part, and its halves, while one holds more groups than a sub-table's bound.</summary>
    internal void SplitPast(CorePart part, int index, CoreApplier applier)
    {
        SubTable table = part.Tables[index];
        if (table.Keys.Count <= _tableGroups || table.Depth >= MostDepth)
        {
            return;
        }

        int upper = Split(part, index, applier);
        SplitPast(part, index, applier);
        SplitPast(part, upper, applier);
    }

    /// <summary>
    /// Splits a sub-table on the next bit of its keys' hashes, past those its place in the directory
    /// takes: the groups whose bit is clear stay at <paramref name="index"/>, the others go to a sub-table
    /// added at the end, the directory doubled first when the sub-table took all its bits.
    /// </summary>
    /// <returns>The index of the sub-table added.</returns>
    internal int Split(CorePart part, int index, CoreApplier applier)
    {
        SubTable table = part.Tables[index];
        int depth = table.Depth;
        if (depth == part.Depth)
        {
            part.Grow();
        }

        // Each half made at the bound, with a batch's room past it: it then never grows before it
        // splits in turn, and its arrays are the lengths the shelf holds from the split before.
        applier.Halves(table.Keys, PartShift - 1 - depth, out ReadOnlySpan<int> clear, out ReadOnlySpan<int> set);
        SubTable lower = NewTable(depth + 1);
        SubTable upper = NewTable(depth + 1);
        int room = (int)_tableGroups + _batchEntries;
        lower.Reserve(room);
        upper.Reserve(room);
        Move(table, clear, lower, applier);
        Move(table, set, upper, applier);
        table.Release();
        int added = part.Tables.Count;
        part.Tables[index] = lower;
        part.Tables.Add(upper);

        // The sub-table's places in the directory share its bits; the half of them past the next bit
        // go to the upper half.
        int bit = part.Depth - depth - 1;
        int[] directory = part.Directory;
        for (int j = 0; j < directory.Length; j++)
        {
            if (directory[j] == index && ((j >> bit) & 1) != 0)
            {
                directory[j] = added;
            }
        }

        Interlocked.Increment(ref _splits);
        return added;
    }

    /// <summary>The groups <paramref name="groups"/> of a sub-table merged into another, keys and states.</summary>
    private void Move(SubTable from, ReadOnlySpan<int> groups, SubTable into, CoreApplier applier)
    {
        Span<int> map = applier.Map(groups.Length);
        from.Keys.MergeInto(into.Keys, groups, map);
        Merge(from.Slots, groups, into, map);
    }

    /// <summary>The states of <paramref name="groups"/> of <paramref name="from"/> merged into <paramref name="into"/>'s groups <paramref name="map"/>.</summary>
    internal void Merge(AggregateSlot[] from, ReadOnlySpan<int> groups, SubTable into, ReadOnlySpan<int> map)
    {
        AggregateSlot[] slots = into.Slots;
        int count = into.Keys.Count;
        for (int s = 0; s < slots.Length; s++)
        {
            slots[s].EnsureGroups(count);
            if (Folds(s))
            {
                slots[s].MergeFrom(from[s], groups, map);
            }
        }
    }

    /// <summary>
    /// The end of the pass: every lane's cache and open batches deposited, every part's stack applied,
    /// the parts taken from a queue by up to <paramref name="degree"/> workers, the lanes' null groups
    /// merged into the first part, and the sub-tables read as one. A part spilled, or one its budget
    /// cannot apply its stack into, writes its stack to the scratch instead, and comes back when it is
    /// delivered, after the parts held in memory (H6).
    /// </summary>
    /// <returns>
    /// The groups' keys and slots of the parts held in memory, the bytes the core held at the end past
    /// the lanes' caches, its sub-tables and every batch made, and the parts spilled.
    /// </returns>
    internal async ValueTask<(GroupKeys Keys, AggregateSlot[] Slots, long Bytes, CorePart[] Spilled)> FinishAsync(
        AggregationPartition[] lanes, int degree, CancellationToken cancellationToken)
    {
        using CancellationTokenSource failed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken token = failed.Token;
        await CloseAsync(lanes, failed).ConfigureAwait(false);

        int[] queue = [-1];
        int workers = Math.Clamp(degree, 1, PartCount);
        if (workers == 1)
        {
            await ApplyPartsAsync(queue, token).ConfigureAwait(false);
        }
        else
        {
            Task[] applying = new Task[workers];
            for (int w = 0; w < workers; w++)
            {
                applying[w] = Task.Run(() => ApplyPartsAsync(queue, token).AsTask(), token);
            }

            await AggregationEngine.GuardedAsync(applying, failed).ConfigureAwait(false);
        }

        long bytes = (Interlocked.Read(ref _batches) * _batchWords * sizeof(ulong)) + MergeNulls(lanes);
        List<GroupKeys> keys = [];
        List<AggregateSlot[]> slots = [];
        List<CorePart> spilled = [];
        foreach (CorePart part in _parts)
        {
            if (part.Runs is not null)
            {
                spilled.Add(part);
                continue;
            }

            foreach (SubTable table in part.Tables)
            {
                keys.Add(table.Keys);
                slots.Add(table.Slots);
                bytes += table.Footprint;
            }
        }

        if (keys.Count == 0)
        {
            SubTable empty = NewTable(0);
            keys.Add(empty.Keys);
            slots.Add(empty.Slots);
        }

        // The slabs, every batch applied, and the arrays the sub-tables left go to the process's shelf,
        // for the next query: the result keeps this shelf, not what it held. A core with parts spilled
        // keeps its shelf for the parts it brings back, each exact under its budget, and hands it on
        // once the last is delivered.
        LetSlabs();
        if (spilled.Count == 0)
        {
            _shelf.Clear();
        }
        else
        {
            _shelf.Overdraws = false;
        }

        (GroupKeys joined, AggregateSlot[] joinedSlots, _) = AggregationEngine.Joined([.. keys], [.. slots], keys.Count);
        return (joined, joinedSlots, bytes, [.. spilled]);
    }

    /// <summary>The slabs of the batches given to the shelf, every batch applied or spilled: no lane fills one again.</summary>
    private void LetSlabs()
    {
        lock (_freeGate)
        {
            _free = null;
            foreach (ulong[] slab in _slabs)
            {
                _shelf.Give(slab);
            }

            _slabs.Clear();
        }
    }

    /// <summary>
    /// The groups of a part applied, or let go when negative, delivered part by part (H7): the most the
    /// parts held at once is the result's peak.
    /// </summary>
    internal void Held(long groups)
    {
        long held = Interlocked.Add(ref _heldGroups, groups);
        long peak;
        while (held > (peak = Volatile.Read(ref _peakGroups)) && Interlocked.CompareExchange(ref _peakGroups, held, peak) != peak)
        {
        }
    }

    /// <summary>The most groups the parts applied and not let go held at once, delivered part by part (H7).</summary>
    internal long PeakGroups => Volatile.Read(ref _peakGroups);

    /// <summary>Every lane's cache and open batches deposited, the lanes side by side, for the end to apply.</summary>
    private static async ValueTask CloseAsync(AggregationPartition[] lanes, CancellationTokenSource failed)
    {
        if (lanes.Length == 1)
        {
            lanes[0].Core!.Close(lanes[0]);
            return;
        }

        Task[] closing = new Task[lanes.Length];
        for (int l = 0; l < lanes.Length; l++)
        {
            AggregationPartition lane = lanes[l];
            closing[l] = Task.Run(() => lane.Core!.Close(lane), failed.Token);
        }

        await AggregationEngine.GuardedAsync(closing, failed).ConfigureAwait(false);
    }

    /// <summary>
    /// A lane's cache, and the partition it bypasses it with, keep their null group, which no batch
    /// carries: they meet in the first part's first sub-table, where the parts cut the null to.
    /// </summary>
    /// <returns>The bytes of the partitions the lanes bypassed their caches with.</returns>
    private long MergeNulls(AggregationPartition[] lanes)
    {
        long bytes = 0;
        foreach (AggregationPartition lane in lanes)
        {
            MergeNull(lane);
            if (lane.Core!.Rows is { } apart)
            {
                MergeNull(apart);
                bytes += apart.Footprint;
            }
        }

        return bytes;
    }

    /// <summary>
    /// The end of the pass delivered part by part (PLAN-HIGH-CARDINALITY, H7), when no order needs every
    /// group at once: every lane's cache and open batches deposited, then the parts applied by up to
    /// <paramref name="degree"/> less one workers in the background, each built into its batches by
    /// <paramref name="builder"/> and let go by the worker that applied it, its batches handed to the
    /// result; and by the result's reader, which applies and builds the next part no worker took rather
    /// than wait for one; the null groups in a part of their own, and the parts spilled after.
    /// </summary>
    internal async ValueTask<CoreParts> FinishPartedAsync(
        AggregationPartition[] lanes, int degree, AggregationPlan plan, PartBuilder builder, CancellationToken cancellationToken)
    {
        CancellationTokenSource stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await CloseAsync(lanes, stopping).ConfigureAwait(false);
        }
        catch
        {
            stopping.Dispose();
            throw;
        }

        // The null groups join a part of their own now, while the lanes' partitions still hold them. Each
        // part delivered gives its arrays to the shelf, which the parts still applied take again.
        CorePart? nulls = NullPart(lanes);

        // The parts built ahead of the reader, a worker's each at most, then the workers wait for it.
        int workers = Math.Clamp(degree - 1, 0, PartCount);
        Channel<PartResult> built = Channel.CreateBounded<PartResult>(new BoundedChannelOptions(Math.Max(1, workers)) { SingleReader = true });
        int[] queue = [-1];
        CancellationToken token = stopping.Token;
        Task[] applying = new Task[workers];
        for (int w = 0; w < workers; w++)
        {
            applying[w] = Task.Run(() => PublishPartsAsync(queue, built.Writer, builder, token).AsTask(), token);
        }

        _ = CompleteAsync(applying, built.Writer, stopping);
        return new CoreParts(this, built.Reader, queue, stopping, plan, builder, nulls);
    }

    /// <summary>
    /// The workers awaited, the result's channel completed, with their failure if any, which its reader
    /// then throws. It never throws itself.
    /// </summary>
    private static async Task CompleteAsync(Task[] workers, ChannelWriter<PartResult> writer, CancellationTokenSource stopping)
    {
        try
        {
            await AggregationEngine.GuardedAsync(workers, stopping).ConfigureAwait(false);
            writer.Complete();
        }
        catch (Exception failure)
        {
            writer.Complete(failure);
        }
    }

    /// <summary>
    /// The next part no worker took, applied by the result's reader rather than wait for a worker (H7):
    /// whether there was one to take, and the part when it holds groups in memory, null when it spilled.
    /// </summary>
    internal async ValueTask<(bool Taken, CorePart? Part)> ApplyNextAsync(int[] queue, CoreApplier applier, CancellationToken cancellationToken)
    {
        int index = Interlocked.Increment(ref queue[0]);
        if (index >= PartCount)
        {
            return (false, null);
        }

        CorePart part = _parts[index];
        await ApplyPartAsync(part, applier, cancellationToken).ConfigureAwait(false);
        return (true, part.Runs is null && part.Tables.Count > 0 ? part : null);
    }

    /// <summary>
    /// Every part applied, by the workers and the reader (H7): no batch is read again, and the slabs and
    /// what the shelf kept of the pass leave the query's count, the parts delivered from now on with
    /// them, and the parts spilled brought back under the budget alone.
    /// </summary>
    internal void AppliedAll()
    {
        LetSlabs();
        _shelf.Clear();
        _shelf.Overdraws = false;
        Volatile.Write(ref _allApplied, true);
    }

    /// <summary>
    /// A worker's parts applied, as <see cref="ApplyPartsAsync"/> does, each built into its batches, let
    /// go, and its batches handed to the result, unless it spilled.
    /// </summary>
    private async ValueTask PublishPartsAsync(int[] queue, ChannelWriter<PartResult> writer, PartBuilder builder, CancellationToken cancellationToken)
    {
        CoreApplier applier = new CoreApplier(this, lane: null);
        int index;
        while ((index = Interlocked.Increment(ref queue[0])) < PartCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CorePart part = _parts[index];
            await ApplyPartAsync(part, applier, cancellationToken).ConfigureAwait(false);
            if (part.Runs is not null || part.Tables.Count == 0)
            {
                continue;
            }

            PartResult result = await BuildAsync(part, builder, cancellationToken).ConfigureAwait(false);
            try
            {
                await writer.WriteAsync(result, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                builder.Give(result);
                throw;
            }

            // The result's reader goes on in the pool's queue, behind the workers, which hold every
            // thread of it: each worker queues itself behind it after each part.
            await Task.Yield();
        }
    }

    /// <summary>
    /// A part in memory built into its batches by <paramref name="builder"/>, its operators applied, then
    /// let go (H7): its groups held until then.
    /// </summary>
    internal async ValueTask<PartResult> BuildAsync(CorePart part, PartBuilder builder, CancellationToken cancellationToken)
    {
        (GroupKeys keys, AggregateSlot[] slots) = Joined(part);
        Held(keys.Count);
        try
        {
            AggregationOutcome outcome = new AggregationOutcome(_plan, slots, keys, AggregationEngine.Shuffled(keys.Order(sorted: false))) { Memory = _memory };
            return await builder.BuildAsync(outcome, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Held(-keys.Count);
            Let(part);
        }
    }

    /// <summary>The parts written to the scratch, to bring back after those held in memory.</summary>
    internal CorePart[] SpilledList() => Array.FindAll(_parts, part => part.Runs is not null);

    /// <summary>A part's sub-tables read as one, once it is back in memory (H6): its keys and its slots.</summary>
    internal (GroupKeys Keys, AggregateSlot[] Slots) Joined(CorePart part)
    {
        List<GroupKeys> keys = [];
        List<AggregateSlot[]> slots = [];
        foreach (SubTable table in part.Tables)
        {
            keys.Add(table.Keys);
            slots.Add(table.Slots);
        }

        if (keys.Count == 0)
        {
            SubTable empty = NewTable(0);
            keys.Add(empty.Keys);
            slots.Add(empty.Slots);
        }

        (GroupKeys joined, AggregateSlot[] joinedSlots, _) = AggregationEngine.Joined([.. keys], [.. slots], keys.Count);
        return (joined, joinedSlots);
    }

    /// <summary>The null group of a lane's partition, if it has one, merged into the first part's first sub-table.</summary>
    private void MergeNull(AggregationPartition partition)
    {
        if (partition.Keys!.NullNumber < 0)
        {
            return;
        }

        CorePart first = _parts[0];
        if (first.Tables.Count == 0)
        {
            first.Tables.Add(NewTable(0));
        }

        MergeNull(partition, first.Tables[first.Directory[0]]);
    }

    /// <summary>The null group of a lane's partition, if it has one, merged into <paramref name="home"/>; told to the emitter the first time (H13).</summary>
    private void MergeNull(AggregationPartition partition, SubTable home)
    {
        int nullGroup = partition.Keys!.NullNumber;
        if (nullGroup < 0)
        {
            return;
        }

        int before = home.Keys.Count;
        ReadOnlySpan<int> groups = [nullGroup];
        Span<int> map = stackalloc int[1];
        partition.Keys.MergeInto(home.Keys, groups, map);
        Merge(partition.Slots, groups, home, map);
        if (Emitter is { } emitter && home.Keys.Count > before)
        {
            emitter.EmitAlone(home.Keys, before, home.Keys.Count);
        }
    }

    /// <summary>What the core tells of each group it makes, a <c>Distinct</c>'s reader (H13); null for none.</summary>
    internal CoreEmitter? Emitter => _plan.Emitter;

    /// <summary>
    /// The lanes' null groups, and those of the partitions they bypassed their caches with, merged into a
    /// part of their own, apart from the parts the workers apply and spill: delivered as one more part;
    /// null when no lane met a null key.
    /// </summary>
    private CorePart? NullPart(AggregationPartition[] lanes)
    {
        CorePart? nulls = null;
        foreach (AggregationPartition lane in lanes)
        {
            foreach (AggregationPartition? partition in (AggregationPartition?[])[lane, lane.Core!.Rows])
            {
                if (partition?.Keys is { NullNumber: >= 0 })
                {
                    nulls ??= new CorePart();
                    if (nulls.Tables.Count == 0)
                    {
                        nulls.Tables.Add(NewTable(0));
                    }

                    MergeNull(partition, nulls.Tables[0]);
                }
            }
        }

        return nulls;
    }

    /// <summary>
    /// Applies every part's stack now, with <paramref name="lane"/>'s applier: a lane turned at the end of
    /// the pass under pressure (milestone 2), whose entries become groups while the memory its table gave
    /// back is free, and whose batches the next lane fills again.
    /// </summary>
    internal void ApplyAll(LaneCore lane)
    {
        CoreApplier applier = lane.Applier;
        foreach (CorePart part in _parts)
        {
            if (Volatile.Read(ref part.Head.Value) is null)
            {
                continue;
            }

            part.Gate.Enter();
            try
            {
                int applied = applier.Apply(part, Interlocked.Exchange(ref part.Head.Value, null), burst: true);
                Interlocked.Add(ref part.Pending.Value, -applied);
            }
            finally
            {
                part.Gate.Exit();
            }
        }
    }

    /// <summary>
    /// Applies the parts a worker takes from the queue, the last taken in <paramref name="queue"/>'s one
    /// element, each its whole stack. A part spilled writes its stack to the scratch; one whose budget
    /// cannot take its stack's groups is spilled first (H6).
    /// </summary>
    private async ValueTask ApplyPartsAsync(int[] queue, CancellationToken cancellationToken)
    {
        CoreApplier applier = new CoreApplier(this, lane: null);
        int index;
        while ((index = Interlocked.Increment(ref queue[0])) < PartCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ApplyPartAsync(_parts[index], applier, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A part's whole stack applied; a part spilled, or one whose budget cannot take its stack's groups once it spills, writes the stack to the scratch instead (H6).</summary>
    private async ValueTask ApplyPartAsync(CorePart part, CoreApplier applier, CancellationToken cancellationToken)
    {
        if (Spills && (part.Runs is not null || !Affords(Volatile.Read(ref part.Pending.Value))))
        {
            await _spilling.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (part.Runs is null && part.Tables.Count > 0)
                {
                    await EvictAsync(part, cancellationToken).ConfigureAwait(false);
                }

                await WritePendingAsync(part, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _spilling.Release();
            }

            return;
        }

        int applied = applier.Apply(part, Interlocked.Exchange(ref part.Head.Value, null), burst: false);
        Interlocked.Add(ref part.Pending.Value, -applied);
    }

    /// <summary>Whether the budget could take the groups of <paramref name="pending"/> entries applied at once (<see cref="Growth"/>).</summary>
    private bool Affords(long pending) => _memory is null || _memory.CanGrow(Growth(pending));

    /// <summary>What the core did, for the plan's last run (PLAN-HIGH-CARDINALITY, R5a).</summary>
    internal CoreRun Run()
    {
        int tables = Volatile.Read(ref _tablesLet);
        foreach (CorePart part in _parts)
        {
            tables += part.Tables.Count;
        }

        return new CoreRun(
            Alpha,
            Capacity,
            Interlocked.Read(ref _flushes),
            Interlocked.Read(ref _flushed),
            Interlocked.Read(ref _bypassed),
            Interlocked.Read(ref _bursts),
            Interlocked.Read(ref _pendingPeak) * Shape.Words * sizeof(ulong),
            Interlocked.Read(ref _reloaded),
            tables,
            Volatile.Read(ref _splits),
            Interlocked.Read(ref _batches) * _batchWords * sizeof(ulong))
        {
            SpilledParts = SpilledParts,
            SpilledBytes = SpilledBytes,
        };
    }
}

/// <summary>
/// What the core of a run did (PLAN-HIGH-CARDINALITY, R5a): what the plan chose, then what the pass
/// counted, read by the tests and the bench.
/// </summary>
/// <param name="Alpha">The multiple of a part's groups its pending entries passed before a burst.</param>
/// <param name="Capacity">The groups of a lane's cache.</param>
/// <param name="Flushes">The times a lane's cache was copied into its batches.</param>
/// <param name="FlushedGroups">The groups the caches copied, an entry each.</param>
/// <param name="BypassedRows">The rows the lanes folded apart from their caches, a group each.</param>
/// <param name="Bursts">The stacks a lane applied during the pass.</param>
/// <param name="PendingPeakBytes">The most bytes of entries deposited and not yet applied at once.</param>
/// <param name="ReloadedBytes">The bytes of sub-tables a burst read again, holding groups already.</param>
/// <param name="Tables">The sub-tables at the end.</param>
/// <param name="Splits">The sub-tables split.</param>
/// <param name="BatchBytes">The bytes of every batch made.</param>
internal sealed record CoreRun(
    int Alpha, int Capacity, long Flushes, long FlushedGroups, long BypassedRows, long Bursts, long PendingPeakBytes, long ReloadedBytes, int Tables, int Splits,
    long BatchBytes)
{
    /// <summary>The parts the core wrote to its scratch (H6).</summary>
    internal int SpilledParts { get; init; }

    /// <summary>The bytes it wrote there.</summary>
    internal long SpilledBytes { get; init; }
}

/// <summary>Where a group's record and its key lie in an entry of a part's batch (PLAN-HIGH-CARDINALITY, H4).</summary>
/// <param name="RecordWords">The words of a record, copied whole from a lane's cache.</param>
/// <param name="Words">The words of an entry.</param>
/// <param name="KeyOffset">The byte of an entry its key starts at: in the record's padding when it fits there, past the record otherwise.</param>
internal readonly record struct EntryShape(int RecordWords, int Words, int KeyOffset)
{
    /// <summary>The entries of records laid out as <paramref name="layout"/>, or of none, and keys of <paramref name="keyBytes"/>, a power of two.</summary>
    internal static EntryShape Of(RecordLayout? layout, int keyBytes)
    {
        int recordWords = layout?.Stride ?? 0;
        int alignment = Math.Min(sizeof(ulong), keyBytes);
        int keyOffset = ((layout?.Width ?? 0) + alignment - 1) & -alignment;
        int bytes = Math.Max(recordWords * sizeof(ulong), keyOffset + keyBytes);
        return new EntryShape(recordWords, (bytes + sizeof(ulong) - 1) / sizeof(ulong), keyOffset);
    }
}

/// <summary>
/// A lane's entries for one part (PLAN-HIGH-CARDINALITY, H4): a group of its cache each, its record then
/// its key; deposited on the part's stack once full, linked there through <see cref="Next"/>, and taken
/// whole by the lane that applies the stack, which fills it again. Batches are cut from slabs, a lane's
/// at a time: a large array, which the collector neither copies nor clears.
/// </summary>
internal sealed class PartBatch
{
    internal PartBatch(ulong[] slab, int start, int capacity)
    {
        Words = slab;
        Start = start;
        Capacity = capacity;
    }

    /// <summary>The slab the entries lie in, at <see cref="EntryShape.Words"/> words each from <see cref="Start"/>.</summary>
    internal ulong[] Words { get; }

    /// <summary>The slab's word the batch's first entry starts at.</summary>
    internal int Start { get; }

    /// <summary>The entries the batch holds at most.</summary>
    internal int Capacity { get; }

    /// <summary>The entries it holds.</summary>
    internal int Count;

    /// <summary>The batch below it on a stack, or in a lane's batches to fill.</summary>
    internal PartBatch? Next;

    /// <summary>
    /// Whether the batch is an array of its own, a part's share of a lane's table emptied into the core
    /// under pressure (milestone 2): given back to the budget once applied, never filled again.
    /// </summary>
    internal bool Alone { get; init; }
}

/// <summary>A part's groups whose hashes share the bits of its places in the part's directory: their keys and their records, final (PLAN-HIGH-CARDINALITY, H4).</summary>
internal sealed class SubTable(GroupKeys keys, AggregateSlot[] slots, GroupRecords? records, int depth)
{
    internal GroupKeys Keys { get; } = keys;

    internal AggregateSlot[] Slots { get; } = slots;

    /// <summary>The bits of the hash past the part's its groups share.</summary>
    internal int Depth { get; } = depth;

    internal long Footprint => Keys.Footprint + AggregateSlot.FootprintOf(Slots);

    /// <summary>Makes room for <paramref name="groups"/> groups at once: keys and records.</summary>
    internal void Reserve(int groups)
    {
        Keys.Reserve(groups);
        records?.Reserve(groups);
    }

    /// <summary>Gives the sub-table's arrays back to the query's shelf: it split, and its halves hold its groups.</summary>
    internal void Release()
    {
        Keys.Release();
        records?.Release();
    }

    /// <summary>The groups from <paramref name="from"/> on copied into entries of <paramref name="shape"/>, as many as <paramref name="entries"/> holds (H6).</summary>
    /// <returns>The group to copy next.</returns>
    internal int CopyEntries(EntryShape shape, int from, Span<ulong> entries, out int written) =>
        Keys.CopyEntries(records is null ? default : records.Made, shape, from, entries, out written);
}

/// <summary>A part's groups written to the query's scratch as it was spilled (H6): their entries, one after the other, from <paramref name="Offset"/>.</summary>
/// <param name="Offset">Where its entries start in the scratch.</param>
/// <param name="Entries">Its entries.</param>
/// <param name="Emitted">
/// Whether its keys were told to the core's emitter as their groups were made (H13): the part's
/// sub-tables, evicted; not its pending entries, written as they came.
/// </param>
internal readonly record struct PartRun(long Offset, long Entries, bool Emitted = false);

/// <summary>
/// What hears of the groups a core makes as it makes them (PLAN-HIGH-CARDINALITY, H13): the reader of a
/// <c>Distinct</c>, each value delivered once it enters its part's set, once, as the rows come.
/// </summary>
internal abstract class CoreEmitter
{
    /// <summary>
    /// The groups [<paramref name="from"/>, <paramref name="to"/>) of <paramref name="keys"/>, a sub-table's,
    /// new to it, copied out for <paramref name="applier"/>'s next flush; told under the part's lock.
    /// </summary>
    internal abstract void Emit(CoreApplier applier, GroupKeys keys, int from, int to);

    /// <summary>What <paramref name="applier"/> copied out since its last flush, handed on.</summary>
    internal abstract void Flush(CoreApplier applier);

    /// <summary>The groups [<paramref name="from"/>, <paramref name="to"/>) of <paramref name="keys"/> copied out and handed on at once: the null group, which no batch carries.</summary>
    internal abstract void EmitAlone(GroupKeys keys, int from, int to);
}

/// <summary>
/// One of the parts of the key space (PLAN-HIGH-CARDINALITY, H4): the lock a lane applies it under, the
/// stack of batches the lanes deposited, the count of their entries, and the directory of its
/// sub-tables, indexed by the bits of the hash past the part's. The lanes write the stack's head and the
/// count, each on a line of 128 bytes of its own; the holder alone writes the rest.
/// </summary>
internal sealed class CorePart
{
    internal readonly Lock Gate = new Lock();

    internal HeadLine Head;

    internal CountLine Pending;

    /// <summary>The sub-tables, each once.</summary>
    internal readonly List<SubTable> Tables = [];

    /// <summary>The sub-table of each value of the bits the directory takes, an index into <see cref="Tables"/>.</summary>
    internal int[] Directory = [0];

    /// <summary>The bits of the hash past the part's the directory takes.</summary>
    internal int Depth;

    /// <summary>The groups of the sub-tables, as the last application left them.</summary>
    internal int Groups;

    /// <summary>
    /// The part's groups written to the scratch, one run each time it was spilled (H6); null for a
    /// part never spilled. Written by the eviction alone, which one task runs at a time.
    /// </summary>
    internal List<PartRun>? Runs;

    /// <summary>
    /// Whether the groups the part makes are told to the core's emitter (PLAN-HIGH-CARDINALITY, H13)
    /// no longer: once evicted, a key new to its sub-tables may be one it wrote to the scratch, and
    /// waits for the end, where the runs it wrote come back first.
    /// </summary>
    internal bool Silent;

    /// <summary>Doubles the directory: one more bit of the hash, each sub-table at both places its old one gave.</summary>
    internal void Grow()
    {
        int[] grown = new int[Directory.Length * 2];
        for (int j = 0; j < grown.Length; j++)
        {
            grown[j] = Directory[j >> 1];
        }

        Directory = grown;
        Depth++;
    }
}

/// <summary>
/// The core a query turns to under pressure (PLAN-HIGH-CARDINALITY, H4, milestone 2, decision 12): made
/// by the first lane whose table its budget cannot let grow, never before, so that a query its budget
/// holds allocates nothing for it; null for a query the core cannot hold.
/// </summary>
/// <remarks>
/// Memory comes back only as lanes give back their tables, one at a time: a lane waits for its turn
/// without folding a row, and a lane whose rows the core cannot take yet waits for the next table given
/// back. Both wait on tasks, never on a thread.
/// </remarks>
internal sealed class CorePressure(Func<GroupCore?> make, int lanes)
{
    private readonly Lock _gate = new Lock();
    private GroupCore? _core;
    private bool _made;
    private bool _turned;

    // The turn, made with the core: a query its budget holds allocates neither.
    private SemaphoreSlim? _turn;

    // The lanes that still run with a table they may give back, and the lanes' tables not given back
    // yet; what completes when either count falls, made when a lane waits for it.
    private int _running = lanes;
    private int _holding = lanes;
    private TaskCompletionSource? _room;

    /// <summary>The core, made at the first call; null when it cannot hold the query's groups. Past it, a read, no lock.</summary>
    internal GroupCore? Core
    {
        get
        {
            if (Volatile.Read(ref _made))
            {
                return _core;
            }

            lock (_gate)
            {
                if (!_made)
                {
                    _core = make();
                    if (_core is not null)
                    {
                        _core.Pressure = this;
                        _turn = new SemaphoreSlim(1, 1);
                    }

                    Volatile.Write(ref _turned, _core is not null);
                    Volatile.Write(ref _made, true);
                }

                return _core;
            }
        }
    }

    /// <summary>
    /// Whether a lane turned to the core: the others turn at their next batch, rather than each when its
    /// own table can no longer grow, which would turn them all at once, each emptying its table while
    /// the budget is full. A read, no lock.
    /// </summary>
    internal bool Turned => Volatile.Read(ref _turned);

    /// <summary>
    /// Whether a lane still holds a table it gives back during the pass or at its end: until none does,
    /// a part's stack whose groups the budget cannot take waits for it.
    /// </summary>
    internal bool Holding => Volatile.Read(ref _holding) > 0;

    /// <summary>
    /// Whether memory is still to come back: a lane's table, or the batches of their own a table emptied
    /// into, which come back as their parts apply them. Meanwhile the core's shelf takes past the budget
    /// what a stack asks more.
    /// </summary>
    internal bool Owed => Holding || (_core is { } core && core.AloneBytes > 0);

    /// <summary>The lanes that still run with a table they may give back.</summary>
    internal int Running => Volatile.Read(ref _running);

    /// <summary>The query's lanes, whose tables the lanes that run out of rows give back during the pass.</summary>
    internal AggregationPartition[] Lanes { get; set; } = [];

    /// <summary>
    /// The lane's turn, a lane at a time: the memory a table takes to empty into the core given back with
    /// the table before the next one empties. Awaited, not blocked on.
    /// </summary>
    internal Task EnterAsync(CancellationToken cancellationToken) => _turn!.WaitAsync(cancellationToken);

    /// <summary>The end of a lane's turn: the next may turn.</summary>
    internal void Exit() => _turn!.Release();

    /// <summary>
    /// A lane runs no more with a table it may give back: it gave back its own, holds too little to turn,
    /// or ran out of rows. The lanes waiting for room stop waiting for it.
    /// </summary>
    /// <returns>The lanes left that run with one.</returns>
    internal int Leave()
    {
        int left = Interlocked.Decrement(ref _running);
        Signal();
        return left;
    }

    /// <summary>A lane's table given back: emptied into the core, merged into another, or too small to count.</summary>
    internal void GaveBack()
    {
        Interlocked.Decrement(ref _holding);
        Signal();
    }

    /// <summary>The pass's end gave every table back: the core's stacks apply under the budget alone, and fail past it.</summary>
    internal void Settle()
    {
        Volatile.Write(ref _holding, 0);
        Volatile.Write(ref _running, 0);
        Signal();
    }

    /// <summary>
    /// Completes once a lane leaves the count of those that may give back a table during the pass; at
    /// once when none is left in it.
    /// </summary>
    internal Task RoomAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource? room = Volatile.Read(ref _room);
        if (room is null)
        {
            TaskCompletionSource fresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            room = Interlocked.CompareExchange(ref _room, fresh, null) ?? fresh;
        }

        // The count read after the task: a lane that leaves past this read completes it.
        return Volatile.Read(ref _running) > 0 ? room.Task.WaitAsync(cancellationToken) : Task.CompletedTask;
    }

    private void Signal() => Interlocked.Exchange(ref _room, null)?.TrySetResult();

    /// <summary>The core a lane made, or null when none needed it.</summary>
    internal GroupCore? Made
    {
        get
        {
            lock (_gate)
            {
                return _core;
            }
        }
    }
}

/// <summary>The head of a part's stack, alone on its line.</summary>
[StructLayout(LayoutKind.Explicit, Size = 128)]
internal struct HeadLine
{
    [FieldOffset(0)]
    internal PartBatch? Value;
}

/// <summary>A part's count of pending entries, alone on its line.</summary>
[StructLayout(LayoutKind.Explicit, Size = 128)]
internal struct CountLine
{
    [FieldOffset(0)]
    internal long Value;
}

/// <summary>
/// A lane's side of the core (PLAN-HIGH-CARDINALITY, H4): its open batch of each part, the batches it
/// fills again, and what it applies a part's stack with when it takes one.
/// </summary>
internal sealed class LaneCore
{
    /// <summary>The rows over which a lane measures its cache's hit rate, in capacities of the cache.</summary>
    private const int MeasuredCaches = 4;

    /// <summary>The rows a lane bypasses its cache for before it measures it again, in capacities: keys that change as the rows go find it useful again.</summary>
    private const int BypassedCaches = 32;

    /// <summary>The entries a lane deposits before it adds them to the query's count of pending ones.</summary>
    private const int PendingReport = 16 * 1024;

    private readonly GroupCore _core;
    private readonly PartBatch?[] _open = new PartBatch?[GroupCore.PartCount];
    private readonly int _entryWords;
    private CoreApplier? _applier;

    // The batches the lane fills next, linked, and their count; the entries it deposited since it last
    // added them to the query's count.
    private PartBatch? _free;
    private int _freeCount;
    private int _deposited;

    // The cache's hit rate over MeasuredCaches capacities of rows, judged once it has filled: the rows
    // it folded and the groups they added; the rows left to bypass it for.
    private bool _filled;
    private long _measured;
    private long _added;
    private long _bypassing;

    internal LaneCore(GroupCore core)
    {
        _core = core;
        Shape = core.Shape;
        _entryWords = Shape.Words;
    }

    /// <summary>The core the lane's side belongs to.</summary>
    internal GroupCore Core => _core;

    internal EntryShape Shape { get; }

    /// <summary>
    /// Whether the lane is turning to the core, its own table emptying into its batches (milestone 2):
    /// its deposits burst nothing until its table is given back.
    /// </summary>
    internal bool Turning { get; set; }

    /// <summary>
    /// Whether the lane turned to the core under pressure (milestone 2): its slabs come past the
    /// query's budget when they must, as its cache, bounded by α times the groups pending; the
    /// sub-tables, which hold the groups themselves, are what its budget refuses.
    /// </summary>
    internal bool Pressed { get; set; }

    /// <summary>Whether a stack the lane would have applied waits for room: the lane waits for the next table given back before its next batch.</summary>
    internal bool Starved { get; set; }

    /// <summary>What the lane applies a part's stack with: made at its first burst.</summary>
    internal CoreApplier Applier => _applier ??= new CoreApplier(_core, this);

    /// <summary>The partition the lane folds its rows into while it bypasses its cache, a group a row; null before it does.</summary>
    internal AggregationPartition? Rows { get; private set; }

    /// <summary>
    /// Before a batch: whether the lane bypasses its cache for it, the batch then folded a group a row
    /// and copied into the open batches at once, a flush with no probe.
    /// </summary>
    internal bool Bypasses(RecordBatch batch)
    {
        if (_bypassing <= 0)
        {
            return false;
        }

        _bypassing -= batch.SelectedRows;
        _core.Bypassed(batch.SelectedRows);
        AggregationPartition rows = Rows ??= _core.RowPartition();
        rows.Process(batch);
        Flush(rows);
        return true;
    }

    /// <summary>
    /// After the cache folded a batch of <paramref name="rows"/> rows, from <paramref name="before"/>
    /// groups: the cache copied into the open batches and cleared once it holds two thirds of its
    /// capacity; once it has, its hit rate measured, and the cache bypassed for a while below ε.
    /// </summary>
    internal void Fold(AggregationPartition cache, int rows, int before)
    {
        GroupKeys keys = cache.Keys!;
        _measured += rows;
        _added += keys.Count - before;
        if (keys.Count >= _core.FlushAt)
        {
            Flush(cache);
            _filled = true;
        }

        // Only a cache that has filled is judged: under its capacity, every key stays in it.
        if (_filled && _measured >= (long)MeasuredCaches * _core.Capacity)
        {
            if (_added > (1 - _core.Bypass) * _measured)
            {
                _bypassing = (long)BypassedCaches * _core.Capacity;
            }

            _measured = 0;
            _added = 0;
        }
    }

    /// <summary>At the end of the pass: the cache and every open batch deposited, for the end to apply.</summary>
    internal void Close(AggregationPartition cache)
    {
        GroupKeys keys = cache.Keys!;
        if (keys.Count > (keys.NullNumber >= 0 ? 1 : 0))
        {
            Flush(cache);
        }

        for (int p = 0; p < _open.Length; p++)
        {
            if (_open[p] is { Count: > 0 } batch)
            {
                _core.Deposit(p, batch, lane: null);
            }

            _open[p] = null;
        }

        _core.Pend(_deposited);
        _deposited = 0;
        _free = null;
        _freeCount = 0;
    }

    /// <summary>Counts entries the lane deposited, added to the query's count of pending ones a few batches at a time.</summary>
    internal void Deposited(int entries)
    {
        _deposited += entries;
        if (_deposited >= PendingReport)
        {
            _core.Pend(_deposited);
            _deposited = 0;
        }
    }

    /// <summary>
    /// Takes back the <paramref name="count"/> batches of a stack the lane applied, <paramref name="first"/>
    /// to <paramref name="last"/> linked; past two slabs' worth, they go to any lane instead.
    /// </summary>
    internal void Free(PartBatch first, PartBatch last, int count)
    {
        if (_freeCount + count > 2 * GroupCore.SlabBatches)
        {
            _core.GiveShared(first, last, count);
            return;
        }

        last.Next = _free;
        _free = first;
        _freeCount += count;
    }

    /// <summary>A batch to fill: the lane's own, those other lanes gave back, or a new slab's.</summary>
    private PartBatch Take()
    {
        PartBatch? batch = _free;
        if (batch is null)
        {
            batch = _core.TakeShared(out _freeCount) ?? _core.NewSlab(this);
            if (_freeCount == 0)
            {
                _freeCount = GroupCore.SlabBatches;
            }
        }

        _free = batch.Next;
        _freeCount--;
        batch.Next = null;
        batch.Count = 0;
        return batch;
    }

    /// <summary>
    /// A lane's own table emptied into the core, its null group aside, which it keeps alone: the lane
    /// turning to the core when its budget cannot let the table grow (milestone 2). Its groups counted
    /// by part first, each part's share goes into a batch of its own, exactly its size, deposited whole:
    /// given back as its part applies it, where batches the lanes fill again would stay held to the end.
    /// </summary>
    internal void Empty(AggregationPartition table)
    {
        Span<int> counts = stackalloc int[GroupCore.PartCount];
        counts.Clear();
        table.Keys!.CountParts(counts);
        for (int p = 0; p < counts.Length; p++)
        {
            if (counts[p] > 0)
            {
                if (_open[p] is { Count: > 0 } open)
                {
                    _core.Deposit(p, open, this);
                }

                _open[p] = _core.NewAlone(counts[p]);
            }
        }

        Flush(table);
        for (int p = 0; p < counts.Length; p++)
        {
            if (_open[p] is { Alone: true } alone)
            {
                _open[p] = null;
                _core.Deposit(p, alone, this);
            }
        }
    }

    /// <summary>The cache's groups copied into the open batches, its null group aside, which it keeps alone.</summary>
    private void Flush(AggregationPartition cache)
    {
        _core.Engage();
        GroupKeys keys = cache.Keys!;
        int nullGroup = keys.NullNumber;
        _core.Flushed(keys.Count - (nullGroup >= 0 ? 1 : 0));
        keys.Scatter(cache.Records is { } records ? records.Made : default, this);
        if (nullGroup >= 0)
        {
            cache.Keep([nullGroup]);
        }
        else
        {
            cache.Keep([]);
        }
    }

    /// <summary>The first word of a new entry in the open batch of part <paramref name="part"/>, the full one deposited first.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref ulong Entry(int part)
    {
        PartBatch? batch = _open[part];
        if (batch is null || batch.Count == batch.Capacity)
        {
            batch = Turn(part);
        }

        return ref batch.Words[batch.Start + (batch.Count++ * _entryWords)];
    }

    /// <summary>Deposits the part's full batch, if any, and opens another.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private PartBatch Turn(int part)
    {
        if (_open[part] is { } full)
        {
            _open[part] = null;
            _core.Deposit(part, full, this);
        }

        PartBatch fresh = Take();
        _open[part] = fresh;
        return fresh;
    }
}

/// <summary>
/// What applies a part's batches (PLAN-HIGH-CARDINALITY, H4): a lane's, for its bursts, or a worker's at
/// the end. Its slots read a batch's entries as records, at the entry's stride; its scratch cuts the
/// entries by sub-table.
/// </summary>
internal sealed class CoreApplier
{
    private readonly GroupCore _core;
    private readonly LaneCore? _lane;
    private readonly AggregateSlot[] _entries;
    private readonly GroupRecords? _view;
    private readonly int _keyBytes;
    private PartBatch[] _taken = new PartBatch[16];
    private int[] _map = [];
    private ulong[] _keys = [];
    private int[] _tableOf = [];
    private int[] _starts = [];
    private int[] _next = [];
    private int[] _entryAt = [];
    private int[] _batchAt = [];
    private byte[] _bits = [];
    private int[] _clear = [];
    private int[] _set = [];
    private int[] _setEntries = [];
    private int[] _setBatches = [];

    // Whether the part applied tells its new groups to the core's emitter (H13).
    private bool _emitting;

    /// <summary>What the core's emitter keeps of this applier between two flushes (H13): the batch of values it copies them into.</summary>
    internal object? Emission { get; set; }

    internal CoreApplier(GroupCore core, LaneCore? lane)
    {
        _core = core;
        _lane = lane;
        _keyBytes = core.Kind.EntryBytes;
        _entries = AggregationPartition.NewSlots(core.Plan, core.Settled, core.Source, out GroupRecords? records);
        if (records is not null)
        {
            RecordLayout widened = records.Layout.Widened(core.Shape.Words);
            _view = new GroupRecords(widened);
            for (int s = 0; s < _entries.Length; s++)
            {
                if (widened.Offsets[s] >= 0)
                {
                    _entries[s].Bind(_view, widened.Offsets[s]);
                }
            }
        }
    }

    /// <summary>
    /// Applies a stack of batches to the part: every entry's key found or added in its sub-table, its
    /// record merged into its group's, a sub-table at a time; then the sub-tables past their bound
    /// split. The batches go back to the lane, to fill again.
    /// </summary>
    /// <returns>The entries applied.</returns>
    internal int Apply(CorePart part, PartBatch? stack, bool burst)
    {
        int count = 0;
        int entries = 0;
        for (PartBatch? batch = stack; batch is not null; batch = batch.Next)
        {
            if (count == _taken.Length)
            {
                Array.Resize(ref _taken, count * 2);
            }

            _taken[count++] = batch;
            entries += batch.Count;
        }

        if (entries > 0)
        {
            // A part's first sub-table made at once for the groups its first entries may hold: it
            // would otherwise double some ten times, rehashing, through its first bursts.
            if (part.Tables.Count == 0)
            {
                SubTable first = _core.NewTable(0);
                first.Reserve((int)Math.Min(entries, _core.TableGroups));
                part.Tables.Add(first);
            }

            // A split appends its upper half: the sub-tables cut here are the first ones. Only the end
            // splits ahead: early in the pass nearly every entry makes a group, which no later burst
            // follows, and the parts would split for groups that never come.
            int before = part.Groups;
            if (!burst)
            {
                _core.Presplit(part, entries, this);
            }

            int tables = part.Tables.Count;
            Cut(part, count, entries);
            _emitting = _core.Emitter is not null && !part.Silent;
            for (int t = 0; t < tables; t++)
            {
                if (_starts[t] < _starts[t + 1])
                {
                    Share(part, t, _starts[t], _starts[t + 1], burst);
                }
            }

            if (_emitting)
            {
                _core.Emitter!.Flush(this);
            }

            int groups = 0;
            foreach (SubTable table in part.Tables)
            {
                groups += table.Keys.Count;
            }

            Volatile.Write(ref part.Groups, groups);
            _core.Pend(-entries);
            _core.Applied(entries, groups - before);
        }

        // The batches applied, linked again as the stack held them, for the lane to fill again; at the
        // end, nobody fills them. Those a lane's emptied table came in go back to the budget.
        int kept = 0;
        for (int i = 0; i < count; i++)
        {
            PartBatch batch = _taken[i];
            if (batch.Alone)
            {
                _core.Drop(batch);
                continue;
            }

            if (kept > 0)
            {
                _taken[kept - 1].Next = batch;
            }

            _taken[kept++] = batch;
        }

        if (kept > 0)
        {
            _lane?.Free(_taken[0], _taken[kept - 1], kept);
        }

        Array.Clear(_taken, 0, count);
        return entries;
    }

    /// <summary>
    /// Each entry's place among its sub-table's, by a count of each one's, the batches in their order:
    /// the batch and the entry at each place, from each sub-table's start. A part of one sub-table takes
    /// its entries as they come.
    /// </summary>
    private void Cut(CorePart part, int count, int entries)
    {
        int tables = part.Tables.Count;
        Scratch.Grow(ref _entryAt, entries);
        Scratch.Grow(ref _batchAt, entries);
        Scratch.Grow(ref _starts, tables + 1);
        Span<int> starts = _starts.AsSpan(0, tables + 1);
        starts.Clear();
        if (tables == 1)
        {
            int place = 0;
            for (int i = 0; i < count; i++)
            {
                int length = _taken[i].Count;
                for (int e = 0; e < length; e++, place++)
                {
                    _entryAt[place] = e;
                    _batchAt[place] = i;
                }
            }

            starts[1] = entries;
            return;
        }

        int shift = GroupCore.PartShift - part.Depth;
        int mask = (1 << part.Depth) - 1;
        int[] directory = part.Directory;
        Scratch.Grow(ref _tableOf, entries);
        int at = 0;
        for (int i = 0; i < count; i++)
        {
            PartBatch batch = _taken[i];
            Span<int> of = _tableOf.AsSpan(at, batch.Count);
            _core.Kind.TablesOf(batch, _core.Shape, shift, mask, of);
            for (int e = 0; e < of.Length; e++)
            {
                int table = directory[of[e]];
                of[e] = table;
                starts[table + 1]++;
            }

            at += batch.Count;
        }

        for (int t = 0; t < tables; t++)
        {
            starts[t + 1] += starts[t];
        }

        Scratch.Grow(ref _next, tables);
        Span<int> next = _next.AsSpan(0, tables);
        starts[..tables].CopyTo(next);
        at = 0;
        for (int i = 0; i < count; i++)
        {
            int length = _taken[i].Count;
            for (int e = 0; e < length; e++)
            {
                int place = next[_tableOf[at + e]]++;
                _entryAt[place] = e;
                _batchAt[place] = i;
            }

            at += length;
        }
    }

    /// <summary>
    /// The entries [<paramref name="start"/>, <paramref name="end"/>) of the cut into the sub-table at
    /// <paramref name="index"/>, a run of one batch at a time while the sub-table is in cache. Past its
    /// bound it splits at once, and the rest of its share goes to either half by the split's bit: no
    /// sub-table grows far past its bound, out of cache, to split again and again.
    /// </summary>
    private void Share(CorePart part, int index, int start, int end, bool burst)
    {
        SubTable table = part.Tables[index];
        if (burst && table.Keys.Count > 0)
        {
            _core.Reloaded(table.Footprint);
        }

        while (start < end)
        {
            int batch = _batchAt[start];
            int run = start + 1;
            while (run < end && _batchAt[run] == batch)
            {
                run++;
            }

            Apply(table, _taken[batch], _entryAt.AsSpan(start, run - start));
            start = run;
            if (start < end && table.Keys.Count > _core.TableGroups && table.Depth < GroupCore.MostDepth)
            {
                int shift = GroupCore.PartShift - 1 - table.Depth;
                int upper = _core.Split(part, index, this);
                int middle = Partition(start, end, shift);
                Share(part, index, start, middle, burst: false);
                Share(part, upper, middle, end, burst: false);
                return;
            }
        }

        _core.SplitPast(part, index, this);
    }

    /// <summary>
    /// Moves the entries of [<paramref name="start"/>, <paramref name="end"/>) whose key's hash has bit
    /// <paramref name="shift"/> set past those where it is clear, each half in its order.
    /// </summary>
    /// <returns>Where the entries whose bit is set start.</returns>
    private int Partition(int start, int end, int shift)
    {
        Scratch.Grow(ref _setEntries, end - start);
        Scratch.Grow(ref _setBatches, end - start);
        GroupKeys kind = _core.Kind;
        EntryShape shape = _core.Shape;
        int clear = start;
        int set = 0;
        for (int i = start; i < end; i++)
        {
            int entry = _entryAt[i];
            int batch = _batchAt[i];
            if (((kind.HashAt(_taken[batch], shape, entry) >> shift) & 1) == 0)
            {
                _entryAt[clear] = entry;
                _batchAt[clear] = batch;
                clear++;
            }
            else
            {
                _setEntries[set] = entry;
                _setBatches[set] = batch;
                set++;
            }
        }

        _setEntries.AsSpan(0, set).CopyTo(_entryAt.AsSpan(clear));
        _setBatches.AsSpan(0, set).CopyTo(_batchAt.AsSpan(clear));
        return clear;
    }

    /// <summary>The entries <paramref name="entries"/> of a batch into a sub-table: their keys found or added, their records merged.</summary>
    private void Apply(SubTable table, PartBatch batch, ReadOnlySpan<int> entries)
    {
        int before = table.Keys.Count;
        Span<int> map = Map(entries.Length);
        Scratch.Grow(ref _keys, GroupKeys.EntryScratch(entries.Length, _keyBytes));
        table.Keys.GroupsOf(batch, _core.Shape, entries, map, _keys);
        _view?.Over(batch.Words, batch.Start, batch.Count);
        _core.Merge(_entries, entries, table, map);

        // The keys new to the part's set, before a split moves them (H13).
        if (_emitting && table.Keys.Count > before)
        {
            _core.Emitter!.Emit(this, table.Keys, before, table.Keys.Count);
        }
    }

    /// <summary>Room for <paramref name="length"/> groups, the applier's own.</summary>
    internal Span<int> Map(int length)
    {
        Scratch.Grow(ref _map, length);
        return _map.AsSpan(0, length);
    }

    /// <summary>A sub-table's groups by bit <paramref name="shift"/> of their keys' hashes: those where it is clear, and those where it is set.</summary>
    internal void Halves(GroupKeys keys, int shift, out ReadOnlySpan<int> clearGroups, out ReadOnlySpan<int> setGroups)
    {
        int count = keys.Count;
        Scratch.Grow(ref _bits, count);
        Scratch.Grow(ref _clear, count);
        Scratch.Grow(ref _set, count);
        Span<byte> bits = _bits.AsSpan(0, count);
        keys.Parts(MergeHash.Seed, shift, bits);
        int clear = 0;
        int set = 0;
        for (int g = 0; g < count; g++)
        {
            if ((bits[g] & 1) == 0)
            {
                _clear[clear++] = g;
            }
            else
            {
                _set[set++] = g;
            }
        }

        clearGroups = _clear.AsSpan(0, clear);
        setGroups = _set.AsSpan(0, set);
    }
}

/// <summary>What the keys of one fixed width do on entries (PLAN-HIGH-CARDINALITY, H4): a fixed-width column's value, or the word of a tuple.</summary>
internal static class EntryKeys
{
    /// <summary>A key's hash under <paramref name="seed"/>: a mix of one word, or of two for a wider key.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Hash<TKey>(TKey key, ulong seed)
        where TKey : unmanaged
    {
        (ulong low, ulong high) = KeyWords.Of(key);
        return Unsafe.SizeOf<TKey>() <= sizeof(ulong) ? MergeHash.Of(low, seed) : MergeHash.Of(low, high, seed);
    }

    /// <summary>
    /// Each key but group <paramref name="skip"/>'s into an entry of the lane's batch of its part: the
    /// group's record, <see cref="EntryShape.RecordWords"/> words of <paramref name="records"/>, then
    /// the key.
    /// </summary>
    internal static void Scatter<TKey>(ReadOnlySpan<TKey> keys, int skip, ReadOnlySpan<ulong> records, LaneCore lane)
        where TKey : unmanaged
    {
        EntryShape shape = lane.Shape;
        int stride = shape.RecordWords;
        nint keyOffset = shape.KeyOffset;
        ulong seed = MergeHash.Seed;
        if (records.Length < keys.Length * stride)
        {
            throw new InvalidOperationException("A cache's records are fewer than its groups.");
        }

        ref ulong first = ref MemoryMarshal.GetReference(records);
        for (int g = 0; g < keys.Length; g++)
        {
            if (g == skip)
            {
                continue;
            }

            TKey key = keys[g];
            ref ulong entry = ref lane.Entry((int)(Hash(key, seed) >> GroupCore.PartShift));
            ref ulong record = ref Unsafe.Add(ref first, (nint)g * stride);
            for (int w = 0; w < stride; w++)
            {
                Unsafe.Add(ref entry, w) = Unsafe.Add(ref record, w);
            }

            // The key last: it may lie in the record's padding.
            Unsafe.WriteUnaligned(ref Unsafe.AddByteOffset(ref Unsafe.As<ulong, byte>(ref entry), keyOffset), key);
        }
    }

    /// <summary>
    /// The groups from <paramref name="from"/> on, but group <paramref name="skip"/>, copied into entries
    /// of <paramref name="shape"/> one after the other, as many as <paramref name="entries"/> holds: a
    /// sub-table written to the scratch as a part is spilled (H6).
    /// </summary>
    /// <returns>The group to copy next.</returns>
    internal static int Copy<TKey>(ReadOnlySpan<TKey> keys, int skip, ReadOnlySpan<ulong> records, EntryShape shape, int from, Span<ulong> entries, out int written)
        where TKey : unmanaged
    {
        int stride = shape.RecordWords;
        int words = shape.Words;
        nint keyOffset = shape.KeyOffset;
        int room = entries.Length / words;
        int g = from;
        int n = 0;
        for (; g < keys.Length && n < room; g++)
        {
            if (g == skip)
            {
                continue;
            }

            Span<ulong> entry = entries.Slice(n * words, words);
            entry.Clear();
            records.Slice(g * stride, stride).CopyTo(entry);

            // The key last: it may lie in the record's padding.
            Unsafe.WriteUnaligned(ref Unsafe.AddByteOffset(ref Unsafe.As<ulong, byte>(ref MemoryMarshal.GetReference(entry)), keyOffset), keys[g]);
            n++;
        }

        written = n;
        return g;
    }

    /// <summary>Counts each key but group <paramref name="skip"/>'s by its part, as <see cref="Scatter"/> would place it.</summary>
    internal static void CountParts<TKey>(ReadOnlySpan<TKey> keys, int skip, Span<int> counts)
        where TKey : unmanaged
    {
        ulong seed = MergeHash.Seed;
        for (int g = 0; g < keys.Length; g++)
        {
            if (g != skip)
            {
                counts[(int)(Hash(keys[g], seed) >> GroupCore.PartShift)]++;
            }
        }
    }

    /// <summary>The bits of each entry's key's hash from <paramref name="shift"/> up, under <paramref name="mask"/>.</summary>
    internal static void TablesOf<TKey>(PartBatch batch, EntryShape shape, int shift, int mask, Span<int> tables)
        where TKey : unmanaged
    {
        ulong seed = MergeHash.Seed;
        tables = tables[..batch.Count];
        for (int i = 0; i < tables.Length; i++)
        {
            tables[i] = (int)(Hash(KeyAt<TKey>(batch, shape, i), seed) >> shift) & mask;
        }
    }

    /// <summary>The key of entry <paramref name="entry"/> of a batch.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static TKey KeyAt<TKey>(PartBatch batch, EntryShape shape, int entry)
        where TKey : unmanaged
    {
        if ((uint)entry >= (uint)batch.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(entry));
        }

        ref byte first = ref Unsafe.As<ulong, byte>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(batch.Words), (nint)batch.Start));
        return Unsafe.ReadUnaligned<TKey>(ref Unsafe.AddByteOffset(ref first, ((nint)entry * shape.Words * sizeof(ulong)) + shape.KeyOffset));
    }
}
