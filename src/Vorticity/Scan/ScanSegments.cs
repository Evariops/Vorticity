using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using Vorticity.Buffers;
using Vorticity.IO;
using Vorticity.Serialization.Schemas;

namespace Vorticity.Scanning;

/// <summary>
/// The segments one scan has read, held for the batches that ask for them again, and shared by
/// every lane of the scan.
/// </summary>
/// <remarks>
/// <para>
/// A segment spans every block of its chunk and a batch is a block or less, so consecutive batches
/// ask for the same segments. Over a memory mapping the repetition is free, since a segment is a view;
/// over anything that copies -- a positional read, an object store -- it is the whole cost of the
/// scan. Lanes take consecutive batches in turn, so a set per lane would read a segment once per
/// lane that meets it, and a scan of four lanes would reread every segment on every batch.
/// </para>
/// <para>
/// Each segment is read by exactly one batch, the first to claim it. A batch that needs a segment
/// another batch has claimed and not yet read waits for that read instead of issuing its own: a
/// batch claims all it lacks in one step under the lock, so it can only wait on a batch that claimed
/// before it, and the waits cannot form a cycle. Before it waits, it reads and publishes what it
/// claimed itself (<see cref="ReadOwnAsync"/>), so that a wait for another's segment delays neither
/// its own read nor the batches after it that wait for its segments. Once a read fails the scan is
/// failing, and the batches still running read what they lack themselves.
/// </para>
/// <para>
/// A segment is needed by a contiguous run of the scan's batches, because it covers a contiguous
/// run of rows and the batches are numbered in the order the scan walks its rows, forward or back.
/// So once batch <c>n</c> is delivered, every batch before it has taken its own reference, and a
/// segment last asked for by a batch before <c>n</c> is needed by no batch from <c>n</c> on:
/// <see cref="Release"/> drops it. What is held is bounded by the batches in flight, whose
/// segments were live anyway, since every batch holds its own reference until it is released.
/// </para>
/// <para>
/// Over a source whose read is a round trip, a batch also reads along with its own the small segments
/// of the splits ahead, which it claims in the same step as its own and the scan holds for the batch
/// that asks for each, as its last use: a few kilobytes in all, where each split of a column coded in
/// runs asked for its few dozen bytes alone.
/// </para>
/// <para>
/// A batch looks for each segment it lacks by the hash of its offset and length, in chains threaded
/// through the entries themselves, so that a batch opening G segments while the scan holds E pays
/// G steps under the lock rather than G times E.
/// </para>
/// <para>
/// A set of one lane is its own lock: its lane never contends for it, and an uncontended monitor
/// allocates nothing. A set several lanes share takes a lock the process keeps (see
/// <see cref="LaneGates"/>), because a lock allocates the first time two threads meet in it or one
/// blocks on it, which a lock of the scan's own would pay part way, after its first batch.
/// </para>
/// </remarks>
internal sealed class ScanSegments : IDisposable
{
    /// <summary>The most entries a set keeps for its next scan: a wider table's goes to the collector with its scan.</summary>
    private const int MostKeptEntries = 4096;

    /// <summary>The table before the first claim lends one, and after the dispose gave it back: no entry, and never added to.</summary>
    private static readonly EntryTable s_unlent = new EntryTable(null);

    private readonly Lanes? _lanes;

    /// <summary>
    /// The entries and the set that lent them, the set of the batch whose claim came first, which keeps them between its
    /// scans (<see cref="SegmentReadArrays.KeptScanTable"/>).
    /// </summary>
    private EntryTable _table = s_unlent;
    private int _count;
    private long _tickets;
    private bool _failed;

    /// <param name="lanes">How many batches may be built at once; above one, a batch may wait for another's read.</param>
    internal ScanSegments(int lanes) => _lanes = lanes > 1 ? new Lanes(lanes) : null;

    /// <summary>The entries the scan holds, the first <see cref="_count"/> live or dead.</summary>
    private Entry[] Entries => _table.Items;

    /// <summary>The lock the process lent the set, when lanes share it; null for a set of one lane.</summary>
    internal Lock? SharedGate => _lanes?.Gate;

    /// <summary>
    /// The next batch number, for a scan whose batches are not numbered by a split plan: a
    /// key-ordered walk numbers the zones its key source decodes and the groups of each window in
    /// the order they happen, which is the order their rows are walked in.
    /// </summary>
    internal long NextTicket() => Interlocked.Increment(ref _tickets);

    /// <summary>
    /// Fills the unread slots of a batch's registered set from what the scan holds, and claims for
    /// the batch the segments nobody holds or is reading, which the batch then reads itself.
    /// </summary>
    /// <param name="requests">The batch's set, registered and not yet read.</param>
    /// <param name="batch">The batch's number in the scan, in the order the scan walks its rows.</param>
    /// <param name="waiter">The batch's waiter, or null when no other batch runs beside it.</param>
    /// <param name="along">
    /// Segments of the splits after the batch's that it reads along with its own, each held for the batch
    /// <paramref name="alongBatches"/> names, which asks for it: one the scan holds or another batch reads is
    /// held for that batch, and the others are added to the set and claimed by this batch, in the same step
    /// as its own, so that a batch still waits only on one that claimed before it. Given at the first claim alone.
    /// </param>
    /// <param name="alongBatches">The batch that asks for each of <paramref name="along"/>.</param>
    /// <returns>
    /// True when another batch is reading a segment this one needs: the waiter is then parked, and
    /// the caller awaits it and claims again.
    /// </returns>
    internal bool Claim(
        SegmentRequestSet requests, long batch, SegmentWaiter? waiter, ReadOnlySpan<SegmentSpec> along = default,
        ReadOnlySpan<long> alongBatches = default) =>
        Claim(requests, batch, waiter, along, alongBatches, out _);

    /// <summary>
    /// <see cref="Claim(SegmentRequestSet, long, SegmentWaiter?, ReadOnlySpan{SegmentSpec}, ReadOnlySpan{long})"/>, which
    /// says in <paramref name="claimed"/> the bytes of the segments this call claimed for the batch, its own and those it
    /// reads along: what the batch is about to read, as the file's footer says it, for a scan that reserves a read's
    /// bytes before it makes it.
    /// </summary>
    internal bool Claim(
        SegmentRequestSet requests, long batch, SegmentWaiter? waiter, ReadOnlySpan<SegmentSpec> along,
        ReadOnlySpan<long> alongBatches, out long claimed)
    {
        claimed = 0;
        using (Enter())
        {
            // The entries hold their segments' owners, which no block of the pool holds, and are read without the lock
            // through a snapshot of the array: an array the first batch's set lends, which its context keeps between scans,
            // or one made for that set when a scan beside this one holds the set's.
            if (ReferenceEquals(_table, s_unlent))
            {
                _table = Interlocked.Exchange(ref requests.ReadArrays.KeptScanTable, null) ?? new EntryTable(requests);
            }

            // Each slot the batch has not filled, looked up by its hash among what the scan holds: filled from a segment held,
            // waited for when another batch reads it, claimed when nobody does. The batch's slots, not every segment the
            // batches in flight hold: at 64 columns and 16 lanes, a thousand entries walked at each step under the lock.
            bool blocked = false;
            for (int slot = 0; slot < requests.Count; slot++)
            {
                if (requests.IsFilled(slot))
                {
                    continue;
                }

                SegmentSpec spec = requests.GetSpec(slot);
                int hash = SegmentRequestSet.HashOf(spec.Offset, spec.Length);
                int held = Find(spec.Offset, spec.Length, hash);
                if (held < 0)
                {
                    if (!_failed)
                    {
                        Add(new Entry { Offset = spec.Offset, Length = spec.Length, Hash = hash, Claimant = batch, LastUse = batch });
                    }

                    claimed += spec.Length;
                    continue;
                }

                ref Entry entry = ref Entries[held];
                entry.LastUse = Math.Max(entry.LastUse, batch);
                if (entry.Owner is { } owner)
                {
                    requests.SetSharedResult(slot, owner, entry.View);
                }
                else if (entry.Claimant != batch)
                {
                    blocked = true;
                }
            }

            if (!_failed)
            {
                for (int a = 0; a < along.Length; a++)
                {
                    SegmentSpec spec = along[a];
                    int hash = SegmentRequestSet.HashOf(spec.Offset, spec.Length);
                    int held = Find(spec.Offset, spec.Length, hash);
                    if (held >= 0)
                    {
                        Entries[held].LastUse = Math.Max(Entries[held].LastUse, alongBatches[a]);
                        continue;
                    }

                    requests.Add(in spec);
                    Add(new Entry { Offset = spec.Offset, Length = spec.Length, Hash = hash, Claimant = batch, LastUse = alongBatches[a], Along = true });
                    claimed += spec.Length;
                }
            }

            if (!blocked || waiter is null || _failed)
            {
                return false;
            }

            // A waiter means lanes beside this one, so the set was made with room to park it.
            SegmentWaiter?[] parked = _lanes!.Parked;
            waiter.Park(requests, batch);
            parked[Array.IndexOf(parked, null)] = waiter;
            return true;
        }
    }

    /// <summary>
    /// Reads, while batch <paramref name="batch"/> waits for a segment another batch is reading, the segments it claimed
    /// itself, and publishes them before it waits: a batch held back by another's small segment then makes its own read at
    /// once rather than after the wait, and the batches waiting for what it claimed are not held back by its wait. Its
    /// slots in <paramref name="requests"/> are filled from that read, made through <paramref name="own"/>, emptied after.
    /// </summary>
    /// <param name="requests">The batch's set, claimed, and blocked on another batch's read.</param>
    /// <param name="batch">The batch's number.</param>
    /// <param name="source">What the scan reads.</param>
    /// <param name="own">An empty set of the batch's.</param>
    /// <param name="metrics">The scan's counters, or null.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    internal async ValueTask ReadOwnAsync(
        SegmentRequestSet requests, long batch, ISegmentReader source, SegmentRequestSet own, ScanCounters? metrics, CancellationToken cancellationToken)
    {
        if (!Owned(requests, batch, own))
        {
            return;
        }

        try
        {
            if (ScanCounters.Note(metrics, own))
            {
                await source.ReadManyAsync(own, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                own.Complete();
            }

            ScanCounters.Served(metrics, own);
            Publish(own, batch);
            for (int slot = 0; slot < own.Count; slot++)
            {
                SegmentSpec spec = own.GetSpec(slot);
                requests.SetSharedResult(requests.IndexOf(spec.Offset, spec.Length), own.GetOwner(slot), own.GetBuffer(slot));
            }
        }
        finally
        {
            own.Release();
        }
    }

    /// <summary>
    /// Registers in <paramref name="own"/> the segments of <paramref name="requests"/> that batch <paramref name="batch"/>
    /// claimed and nobody holds yet; false when there are none.
    /// </summary>
    private bool Owned(SegmentRequestSet requests, long batch, SegmentRequestSet own)
    {
        using (Enter())
        {
            // The batch's unfilled slots, each looked up by its hash, in the order of its set.
            for (int slot = 0; slot < requests.Count; slot++)
            {
                if (requests.IsFilled(slot))
                {
                    continue;
                }

                SegmentSpec spec = requests.GetSpec(slot);
                int held = Find(spec.Offset, spec.Length, SegmentRequestSet.HashOf(spec.Offset, spec.Length));
                if (held >= 0 && Entries[held].Owner is null && Entries[held].Claimant == batch)
                {
                    own.AddTouched(spec, requests.TouchesLittle(slot) ? 0 : long.MaxValue);
                }
            }
        }

        return own.Count > 0;
    }

    /// <summary>Holds what a batch read for the segments it claimed, and wakes the batches waiting for them.</summary>
    /// <param name="requests">The batch's set, read.</param>
    /// <param name="batch">The batch's number.</param>
    internal void Publish(SegmentRequestSet requests, long batch)
    {
        using (Enter())
        {
            // The batch's slots, each looked up by its hash: those it claimed take what it read.
            for (int slot = 0; slot < requests.Count; slot++)
            {
                SegmentSpec spec = requests.GetSpec(slot);
                int held = Find(spec.Offset, spec.Length, SegmentRequestSet.HashOf(spec.Offset, spec.Length));
                if (held < 0)
                {
                    continue;
                }

                ref Entry entry = ref Entries[held];
                if (entry.Owner is not null || entry.Claimant != batch)
                {
                    continue;
                }

                // A segment read along for a batch ahead, a view of a run of this batch's read, is copied into a block
                // of its own: the run then goes back with this batch, not with the last batch the segment waits for.
                SegmentOwner owner = requests.GetOwner(slot);
                VortexBuffer view = requests.GetBuffer(slot);
                if (entry.Along && owner.Buffer.Length > view.Length)
                {
                    BlockOwner copy = SegmentCache.Copy(view, requests.Account);
                    entry.Owner = copy;
                    entry.View = copy.Buffer;
                }
                else
                {
                    entry.Owner = owner.Retain();
                    entry.View = view;
                }
            }

            // The batch publishes the set it claimed with, so each of its claims is among the slots just walked: one left
            // would stay claimed and unread, and every batch waiting for it parked. Checked in debug builds, where the
            // walk of every entry costs nothing a release pays.
            AssertNoClaimLeft(batch);
            WakeUnblocked();
        }
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private void AssertNoClaimLeft(long batch)
    {
        for (int i = 0; i < _count; i++)
        {
            System.Diagnostics.Debug.Assert(
                Entries[i].Dead || Entries[i].Owner is not null || Entries[i].Claimant != batch,
                $"Batch {batch} published without the segment it claimed at {Entries[i].Offset}: its set is not the one it claimed with.");
        }
    }

    /// <summary>Gives up the claims of a batch whose read failed, and lets every waiting batch read for itself.</summary>
    /// <param name="batch">The batch's number.</param>
    internal void Abandon(long batch)
    {
        using (Enter())
        {
            _failed = true;
            int kept = 0;
            for (int i = 0; i < _count; i++)
            {
                if (Entries[i].Dead || (Entries[i].Owner is null && Entries[i].Claimant == batch))
                {
                    continue;
                }

                Keep(i, ref kept);
            }

            Truncate(kept);
            WakeUnblocked();
        }
    }

    /// <summary>
    /// Gives up the claims of a batch that reads nothing now and will ask again, its split left to run later: the batches
    /// waiting for them claim them in their turn, and the scan goes on sharing its reads, which <see cref="Abandon"/>
    /// ends for good.
    /// </summary>
    /// <param name="batch">The batch's number.</param>
    internal void Withdraw(long batch)
    {
        using (Enter())
        {
            int kept = 0;
            for (int i = 0; i < _count; i++)
            {
                if (Entries[i].Dead || (Entries[i].Owner is null && Entries[i].Claimant == batch))
                {
                    continue;
                }

                Keep(i, ref kept);
            }

            Truncate(kept);
            WakeUnblocked();
        }
    }

    /// <summary>Drops what no batch from <paramref name="batch"/> on can ask for, once <paramref name="batch"/> is delivered.</summary>
    /// <param name="batch">The batch just delivered.</param>
    internal void Release(long batch)
    {
        if (!MayRelease(batch))
        {
            return;
        }

        using (Enter())
        {
            // Released in place and marked dead; dropped from the array once the dead are half of it. A batch drops its
            // predecessors' segments, the oldest, at the front: moving every other entry down and chaining them anew at
            // each batch cost the whole held set under the lock, a thousand entries at 64 columns and 16 lanes. The dead
            // are counted here rather than kept in a field, which would make every scan's set 8 bytes larger.
            int dead = 0;
            for (int i = 0; i < _count; i++)
            {
                ref Entry entry = ref Entries[i];
                if (entry.Dead)
                {
                    dead++;
                }
                else if (entry.Owner is { } owner && entry.LastUse < batch)
                {
                    owner.Release();
                    entry.Owner = null;
                    entry.View = default;
                    entry.Dead = true;
                    dead++;
                }
            }

            if (dead * 2 > _count)
            {
                int kept = 0;
                for (int i = 0; i < _count; i++)
                {
                    if (!Entries[i].Dead)
                    {
                        Keep(i, ref kept);
                    }
                }

                Truncate(kept);
            }
        }
    }

    /// <summary>
    /// Keeps entry <paramref name="i"/>, moving it down to <paramref name="kept"/> when entries before
    /// it were dropped; its chain is mended by <see cref="Truncate"/>, which follows every such loop.
    /// </summary>
    private void Keep(int i, ref int kept)
    {
        if (kept != i)
        {
            Entries[kept] = Entries[i];
        }

        kept++;
    }

    /// <summary>Enters the set's lock: the one made with it when lanes share it, its monitor when one lane has it to itself.</summary>
    private Gate Enter()
    {
        if (_lanes is { } lanes)
        {
            lanes.Gate.Enter();
        }
        else
        {
            Monitor.Enter(this);
        }

        return new Gate(this);
    }

    /// <summary>Leaves the lock <see cref="Enter"/> took.</summary>
    private readonly ref struct Gate(ScanSegments set)
    {
        public void Dispose()
        {
            if (set._lanes is { } lanes)
            {
                lanes.Gate.Exit();
            }
            else
            {
                Monitor.Exit(set);
            }
        }
    }

    /// <summary>
    /// What a set several lanes share has and a set of one lane does not: the lock the process lends
    /// it, and the batches parked on another's read. One field for both, so that a set of one lane is
    /// no larger for them.
    /// </summary>
    private sealed class Lanes(int lanes)
    {
        internal Lock Gate { get; } = LaneGates.RentLock();

        /// <summary>Whether the lock is still the set's, given back once, at the dispose.</summary>
        internal bool Lent { get; set; } = true;

        internal SegmentWaiter?[] Parked { get; } = new SegmentWaiter?[lanes];
    }

    /// <summary>Releases everything held and gives the entries back to the set that lent them, cleared, then the lock, once no batch runs.</summary>
    public void Dispose()
    {
        using (Enter())
        {
            for (int i = 0; i < _count; i++)
            {
                Entries[i].Owner?.Release();
            }

            _count = 0;
            EntryTable table = _table;
            _table = s_unlent;
            if (table.Lender is { } lender)
            {
                // Whole: the chains' heads lie past the entries.
                Array.Clear(table.Items);
                if (table.Items.Length <= MostKeptEntries)
                {
                    Interlocked.CompareExchange(ref lender.ReadArrays.KeptScanTable, table, null);
                }
            }
        }

        if (_lanes is { Lent: true } lanes)
        {
            lanes.Lent = false;
            LaneGates.Return(lanes.Gate);
        }
    }

    /// <summary>
    /// Whether a segment the scan holds was last asked for before <paramref name="batch"/>, looked
    /// at without the lock: most batches ask for the segments their predecessor asked for, and have
    /// nothing to drop.
    /// </summary>
    /// <remarks>
    /// A batch of another lane may raise a last use or publish a segment meanwhile. The first can
    /// only make this say yes for a segment the lock then keeps; a segment published by a batch
    /// before <paramref name="batch"/> was published before that batch was delivered, so before
    /// this call; and a segment a torn read misses is dropped by the next release, or when the scan
    /// ends.
    /// </remarks>
    private bool MayRelease(long batch)
    {
        Entry[] entries = Volatile.Read(ref Volatile.Read(ref _table).Items);
        int count = Math.Min(Volatile.Read(ref _count), entries.Length);
        for (int i = 0; i < count; i++)
        {
            if (entries[i].Owner is not null && entries[i].LastUse < batch)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The entry of the segment at <paramref name="offset"/>, whose hash is <paramref name="hash"/>, or -1, by the chain of its bucket.</summary>
    private int Find(ulong offset, uint length, int hash)
    {
        Entry[] entries = Entries;
        if (entries.Length == 0)
        {
            return -1;
        }

        int bucket = hash & (Buckets(entries) - 1);
        for (int e = entries[bucket].Head - 1; e >= 0; e = entries[e].Next - 1)
        {
            if (entries[e].Offset == offset && entries[e].Length == length && !entries[e].Dead)
            {
                return e;
            }
        }

        return -1;
    }

    private void Add(Entry entry)
    {
        if (_count == Entries.Length)
        {
            // Grown once for the set that lent it, whose next scans borrow it: it is the same size on every batch of a
            // scan, and a scan's allocation is held to a ceiling counted in bytes.
            Entry[] bigger = new Entry[Math.Max(16, Entries.Length * 2)];
            Array.Copy(Entries, bigger, _count);
            _table.Items = bigger;
            Relink();
        }

        // The slot's head belongs to the bucket its index names, not to the entry stored in it.
        ref Entry slot = ref Entries[_count];
        entry.Head = slot.Head;
        slot = entry;
        Link(_count++);
    }

    /// <summary>
    /// Drops the entries from <paramref name="kept"/> on, the loop before it having moved the ones it
    /// keeps down to the front, and rechains them all when any moved.
    /// </summary>
    private void Truncate(int kept)
    {
        if (kept == _count)
        {
            return;
        }

        Array.Clear(Entries, kept, _count - kept);
        _count = kept;
        Relink();
    }

    /// <summary>
    /// The number of hash buckets of <paramref name="entries"/>: its length, down to a power of two.
    /// Bucket <c>b</c>'s chain starts at the <see cref="Entry.Head"/> of entry <c>b</c>, so the
    /// buckets cost no array of their own and are never fewer than the entries.
    /// </summary>
    private static int Buckets(Entry[] entries) => 1 << BitOperations.Log2((uint)entries.Length);

    /// <summary>Puts entry <paramref name="e"/> at the head of the chain of its segment's bucket.</summary>
    private void Link(int e)
    {
        Entry[] entries = Entries;
        ref Entry entry = ref entries[e];
        int bucket = entry.Hash & (Buckets(entries) - 1);
        entry.Next = entries[bucket].Head;
        entries[bucket].Head = e + 1;
    }

    /// <summary>Chains every entry anew, once entries have moved.</summary>
    private void Relink()
    {
        Entry[] entries = Entries;
        int buckets = Buckets(entries);
        for (int b = 0; b < buckets; b++)
        {
            entries[b].Head = 0;
        }

        for (int e = 0; e < _count; e++)
        {
            Link(e);
        }
    }

    /// <summary>Wakes each parked batch no unread claim of another batch holds back any more; every one, once the scan has failed.</summary>
    private void WakeUnblocked()
    {
        if (_lanes is not { } lanes)
        {
            return;
        }

        SegmentWaiter?[] parked = lanes.Parked;
        for (int w = 0; w < parked.Length; w++)
        {
            SegmentWaiter? waiter = parked[w];
            if (waiter is null || (!_failed && Blocked(waiter)))
            {
                continue;
            }

            parked[w] = null;

            // The continuation runs on the thread pool, so waking under the lock runs nothing under it.
            waiter.Wake();
        }
    }

    /// <summary>Whether a slot <paramref name="waiter"/> has not filled names a segment another batch claimed and has not read: its slots, each looked up by its hash.</summary>
    private bool Blocked(SegmentWaiter waiter)
    {
        SegmentRequestSet requests = waiter.Requests!;
        for (int slot = 0; slot < requests.Count; slot++)
        {
            if (requests.IsFilled(slot))
            {
                continue;
            }

            SegmentSpec spec = requests.GetSpec(slot);
            int held = Find(spec.Offset, spec.Length, SegmentRequestSet.HashOf(spec.Offset, spec.Length));
            if (held >= 0 && Entries[held].Owner is null && Entries[held].Claimant != waiter.Batch)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The entries of a scan's table and the set that lent them, which keeps the table between its scans: the set its context
    /// holds outlives them, and a table grown for one scan serves the next as it is.
    /// </summary>
    internal sealed class EntryTable(SegmentRequestSet? lender)
    {
        /// <summary>The entries, grown by doubling; read without the lock through a snapshot.</summary>
        internal Entry[] Items = [];

        /// <summary>The set the table goes back to; null for the empty table no scan holds.</summary>
        internal SegmentRequestSet? Lender { get; } = lender;
    }

    /// <summary>A segment the scan holds, or one a batch has claimed and is reading when it has no owner yet.</summary>
    internal struct Entry
    {
        internal ulong Offset;
        internal uint Length;

        /// <summary>The hash of the segment, <see cref="SegmentRequestSet.HashOf"/>, kept for the rechaining after a move.</summary>
        internal int Hash;

        /// <summary>The next entry of the chain of this segment's hash bucket, plus one; zero ends the chain.</summary>
        internal int Next;

        /// <summary>The batch that reads the segment.</summary>
        internal long Claimant;

        /// <summary>The last batch, in the order of the walk, that asked for the segment.</summary>
        internal long LastUse;

        /// <summary>Whether the claimant reads the segment along with its own, for a batch after it.</summary>
        internal bool Along;

        /// <summary>Whether it was released, and waits in the array for the next compaction (<see cref="Release"/>), which no lookup finds.</summary>
        internal bool Dead;

        internal SegmentOwner? Owner;
        internal VortexBuffer View;

        /// <summary>
        /// The first entry of the chain of the hash bucket this entry's index names, plus one; zero
        /// when the bucket is empty. It belongs to the slot, whichever entry the slot holds.
        /// </summary>
        internal int Head;
    }
}

/// <summary>A batch under construction that can wait for a segment another batch of its scan is reading.</summary>
internal abstract class SegmentWaiter : IValueTaskSource
{
    private ManualResetValueTaskSourceCore<bool> _read = new() { RunContinuationsAsynchronously = true };

    /// <summary>The set whose slots the batch waits on, while it waits.</summary>
    internal SegmentRequestSet? Requests { get; private set; }

    /// <summary>The number of the batch that waits.</summary>
    internal long Batch { get; private set; }

    /// <summary>Completes once no segment the batch needs is being read by another batch.</summary>
    internal ValueTask ReadByOthersAsync() => new ValueTask(this, _read.Version);

    internal void Park(SegmentRequestSet requests, long batch)
    {
        _read.Reset();
        Requests = requests;
        Batch = batch;
    }

    internal void Wake()
    {
        Requests = null;
        _read.SetResult(true);
    }

    void IValueTaskSource.GetResult(short token) => _read.GetResult(token);

    ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _read.GetStatus(token);

    void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
        _read.OnCompleted(continuation, state, token, flags);
}
