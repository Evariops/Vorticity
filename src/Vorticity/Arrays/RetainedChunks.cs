using System;
using System.Collections.Generic;
using System.Threading;
using Vorticity.Buffers;

namespace Vorticity.Arrays;

/// <summary>
/// The chunks one scan has decoded and holds across the batches carved out of them, shared by every
/// context of the scan: its lanes, and the key source of a key-ordered walk.
/// </summary>
/// <remarks>
/// <para>
/// A chunk larger than a batch is decoded once and each batch borrows a window of it. The lanes of
/// a scan take consecutive batches in turn, so a table per lane would decode a chunk once per lane
/// that meets it; one table per scan decodes it once, and a context that asks for a chunk another
/// context is decoding waits for that decode instead of starting its own.
/// </para>
/// <para>
/// An entry may view another: a window of a dictionary views the dictionary's values, decoded once
/// for every window of the chunk under a key of their own and lent to each window that reads them.
/// A lent entry outlives every entry it is lent to, whatever its own age. A context holds at most
/// two claims, an entry and a child of the node it is decoding, and it waits only for an entry
/// while it holds none or for a child of the node it is decoding: every wait is for a node deeper
/// in the array than any node the waiting context holds, so the waits cannot form a cycle.
/// </para>
/// <para>
/// An entry is borrowed by every batch that touched it, and a batch is dead once the scan has
/// delivered a later one. <see cref="Release"/> is told the number of the batch just delivered and
/// drops every entry no batch from it on has touched: a chunk covers a contiguous run of rows and
/// the batches are numbered in the order the scan walks its rows, so an entry the delivered batch
/// did not touch is needed by no later batch, and every earlier batch is dead. A context that runs
/// alone advances the same floor itself, one batch behind, since it learns that a batch is dead only
/// when the next one begins.
/// </para>
/// <para>
/// Evicted entries keep their arena and are refilled, so a scan that walks many chunks allocates
/// none per chunk; an entry claimed and given back without a decode never had one. The table
/// allocates its dictionary at the first claim and nothing else, so a scan whose chunk is its batch
/// pays the object alone, and it is its own lock, since a claim waits on it.
/// </para>
/// </remarks>
internal sealed class RetainedChunks : IDisposable
{
    private readonly int _capacity;
    private readonly int _lendingCapacity;
    private Dictionary<long, RetainedChunk>? _entries;
    private RetainedChunk? _spares;
    private long _floor;
    private bool _prepare;
    private bool _lending;
    private bool _disposed;

    /// <param name="columns">
    /// How many columns the scan reads, from which the table is sized once: two entries a column,
    /// since at a chunk boundary the old chunk is still borrowed while the new one is claimed, and
    /// two for the answers an encoding gives to a pushed comparison. A table that grew instead
    /// would allocate in the middle of a scan.
    /// </param>
    /// <param name="lanes">
    /// How many contexts decode at once. Above one, a lane reaches the next chunk while the batch
    /// that last borrowed the previous one is still in flight, so the previous chunk cannot be
    /// evicted in time to be refilled and a second set of entries is needed at the first boundary:
    /// it is made as the first batch is released, sized by what that batch retained, rather than
    /// in the middle of the scan.
    /// </param>
    internal RetainedChunks(int columns, int lanes)
    {
        _capacity = Math.Max(2 * columns + 2, 4);

        // A column whose windows view a lent child holds two windows and two children at a chunk
        // boundary; the table grows to that once, at the first loan, which the first window makes.
        _lendingCapacity = Math.Max(4 * columns + 2, 6);
        _prepare = lanes > 1;
    }

    /// <summary>
    /// The entry under <paramref name="key"/>, when it is published; otherwise a claim on the key
    /// for <paramref name="claimant"/>, who must decode it and <see cref="Publish"/> or
    /// <see cref="Abandon"/> the claim.
    /// </summary>
    /// <param name="key">The retention key.</param>
    /// <param name="claimant">The context asking.</param>
    /// <param name="batch">The number of the batch asking, which the entry is then borrowed by.</param>
    /// <param name="claim">The claim, when the entry is not published.</param>
    /// <param name="arena">The arena holding the entry, when it is published.</param>
    /// <param name="nodeIndex">The node's index in <paramref name="arena"/>, when it is published.</param>
    /// <returns><see langword="true"/> when the entry is published.</returns>
    /// <remarks>
    /// Waits while another context holds the claim: the chunk is about to be published, and
    /// decoding it a second time is what this table exists to avoid. The wait blocks the thread,
    /// because it happens inside a synchronous decode; it lasts one chunk decode at most, and only a
    /// scan above one lane can ever wait.
    /// </remarks>
    internal bool TryGet(
        long key, ScanContext claimant, long batch, out RetainedChunk? claim, out CanonicalArena arena, out int nodeIndex) =>
        TryGetCore(key, claimant, batch, borrower: null, out claim, out arena, out nodeIndex);

    /// <summary>
    /// As <see cref="TryGet(long, ScanContext, long, out RetainedChunk?, out CanonicalArena, out int)"/>,
    /// for a child of the entry <paramref name="borrower"/> whose decode is in flight: a published
    /// entry is lent to it on the spot, so that it cannot be evicted before the borrower is, and a
    /// claim is lent as it is published.
    /// </summary>
    /// <param name="key">The child's retention key.</param>
    /// <param name="claimant">The context asking, which holds <paramref name="borrower"/>'s claim.</param>
    /// <param name="batch">The number of the batch asking.</param>
    /// <param name="borrower">The entry being decoded, whose records will view the child.</param>
    /// <param name="claim">The claim, when the child is not published.</param>
    /// <param name="arena">The arena holding the child, when it is published.</param>
    /// <param name="nodeIndex">The child's node in <paramref name="arena"/>, when it is published.</param>
    /// <returns><see langword="true"/> when the child is published.</returns>
    internal bool TryGetFor(
        long key, ScanContext claimant, long batch, RetainedChunk borrower,
        out RetainedChunk? claim, out CanonicalArena arena, out int nodeIndex) =>
        TryGetCore(key, claimant, batch, borrower, out claim, out arena, out nodeIndex);

    private bool TryGetCore(
        long key, ScanContext claimant, long batch, RetainedChunk? borrower,
        out RetainedChunk? claim, out CanonicalArena arena, out int nodeIndex)
    {
        lock (this)
        {
            while (true)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_entries is not null && _entries.TryGetValue(key, out RetainedChunk? entry))
                {
                    if (entry.NodeIndex >= 0)
                    {
                        if (entry.LastTouched < batch)
                        {
                            entry.LastTouched = batch;
                        }

                        borrower?.Borrow(entry);
                        claim = null;
                        arena = entry.Arena!;
                        nodeIndex = entry.NodeIndex;
                        return true;
                    }

                    if (ReferenceEquals(entry.Claimant, claimant))
                    {
                        claim = entry;
                        arena = null!;
                        nodeIndex = -1;
                        return false;
                    }

                    Monitor.Wait(this);
                    continue;
                }

                RetainedChunk taken = Take();
                taken.Key = key;
                taken.Claimant = claimant;
                taken.NodeIndex = -1;
                taken.LastTouched = batch;
                _entries ??= new Dictionary<long, RetainedChunk>(_capacity);
                if (borrower is not null && !_lending)
                {
                    _lending = true;
                    _entries.EnsureCapacity(_lendingCapacity);
                }

                _entries[key] = taken;
                claim = taken;
                arena = null!;
                nodeIndex = -1;
                return false;
            }
        }
    }

    /// <summary>The published entry under <paramref name="key"/>, without claiming or waiting.</summary>
    /// <param name="key">The retention key.</param>
    /// <param name="batch">The number of the batch asking.</param>
    /// <param name="arena">The arena holding the entry, when it is published.</param>
    /// <param name="nodeIndex">The node's index in <paramref name="arena"/>, when it is published.</param>
    /// <returns><see langword="true"/> when the entry is published.</returns>
    internal bool Peek(long key, long batch, out CanonicalArena arena, out int nodeIndex)
    {
        lock (this)
        {
            if (_entries is not null && _entries.TryGetValue(key, out RetainedChunk? entry) && entry.NodeIndex >= 0)
            {
                if (entry.LastTouched < batch)
                {
                    entry.LastTouched = batch;
                }

                arena = entry.Arena!;
                nodeIndex = entry.NodeIndex;
                return true;
            }

            arena = null!;
            nodeIndex = -1;
            return false;
        }
    }

    /// <summary>Publishes a claimed entry, decoded, and wakes whoever waits for it.</summary>
    /// <param name="claim">The claim <see cref="TryGet"/> handed out.</param>
    /// <param name="nodeIndex">The decoded node's index in the claim's arena.</param>
    /// <param name="batch">The number of the batch that decoded it.</param>
    internal void Publish(RetainedChunk claim, int nodeIndex, long batch) =>
        PublishCore(claim, nodeIndex, batch, borrower: null);

    /// <summary>Publishes a claimed child, decoded, lends it to <paramref name="borrower"/>, and wakes whoever waits for it.</summary>
    /// <param name="claim">The claim <see cref="TryGetFor"/> handed out.</param>
    /// <param name="nodeIndex">The decoded node's index in the claim's arena.</param>
    /// <param name="batch">The number of the batch that decoded it.</param>
    /// <param name="borrower">The entry being decoded, whose records will view the child.</param>
    internal void PublishFor(RetainedChunk claim, int nodeIndex, long batch, RetainedChunk borrower) =>
        PublishCore(claim, nodeIndex, batch, borrower);

    private void PublishCore(RetainedChunk claim, int nodeIndex, long batch, RetainedChunk? borrower)
    {
        lock (this)
        {
            claim.NodeIndex = nodeIndex;
            claim.Claimant = null;
            if (claim.LastTouched < batch)
            {
                claim.LastTouched = batch;
            }

            claim.RetainSegments();
            borrower?.Borrow(claim);
            Monitor.PulseAll(this);
        }
    }

    /// <summary>Gives up a claim whose decode did not publish, and wakes whoever waits for it.</summary>
    /// <param name="claim">The claim <see cref="TryGet"/> handed out.</param>
    /// <remarks>What the claim had borrowed is given back, and a lender it was the last to borrow is evicted when its own time has passed.</remarks>
    internal void Abandon(RetainedChunk claim)
    {
        lock (this)
        {
            if (_entries is not null && _entries.TryGetValue(claim.Key, out RetainedChunk? entry) && ReferenceEquals(entry, claim))
            {
                _entries.Remove(claim.Key);
            }

            claim.Arena?.Reset();
            claim.Forget();
            ReturnLoans(claim);
            Recycle(claim);
            Monitor.PulseAll(this);
        }
    }

    /// <summary>
    /// Drops every published entry no batch from <paramref name="batch"/> on has touched, once
    /// <paramref name="batch"/> has been executed and every batch before it is dead.
    /// </summary>
    /// <param name="batch">The number of the batch just delivered.</param>
    internal void Release(long batch)
    {
        lock (this)
        {
            if (_entries is null)
            {
                return;
            }

            if (_prepare)
            {
                // The second set, for the lanes that reach a chunk boundary ahead of the eviction:
                // one spare per chunk the first batch retained, made now, while the scan is in its
                // first batch.
                _prepare = false;
                int retained = _entries.Count;
                for (int i = 0; i < retained; i++)
                {
                    RetainedChunk spare = RetainedChunkPool.Rent();
                    spare.Arena ??= new CanonicalArena();
                    Recycle(spare);
                }
            }

            if (batch <= _floor)
            {
                return;
            }

            _floor = batch;
            foreach (KeyValuePair<long, RetainedChunk> held in _entries)
            {
                RetainedChunk entry = held.Value;
                if (Dead(entry))
                {
                    // Removing while enumerating is what a dictionary allows, the removal of a
                    // lender the eviction frees included.
                    _entries.Remove(held.Key);
                    Evict(entry);
                }
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="entry"/> is published, touched by no batch from the floor on, and
    /// viewed by no live entry: what makes it evictable.
    /// </summary>
    private bool Dead(RetainedChunk entry) =>
        entry.NodeIndex >= 0 && entry.Borrowers == 0 && entry.LastTouched < _floor;

    /// <summary>
    /// Releases every entry, and gives it and every spare to <see cref="RetainedChunkPool"/> for the
    /// scans that follow.
    /// </summary>
    public void Dispose()
    {
        lock (this)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_entries is not null)
            {
                foreach (RetainedChunk entry in _entries.Values)
                {
                    entry.Arena?.Reset();
                    if (entry.NodeIndex >= 0)
                    {
                        entry.ReleaseSegments();
                    }
                    else
                    {
                        entry.Forget();
                    }

                    entry.ForgetLoans();
                    Recycle(entry);
                    RetainedChunkPool.Return(entry);
                }

                _entries.Clear();
            }

            while (_spares is { } spare)
            {
                _spares = spare.NextSpare;
                spare.NextSpare = null;
                RetainedChunkPool.Return(spare);
            }

            Monitor.PulseAll(this);
        }
    }

    private RetainedChunk Take()
    {
        RetainedChunk? spare = _spares;
        if (spare is null)
        {
            return RetainedChunkPool.Rent();
        }

        _spares = spare.NextSpare;
        spare.NextSpare = null;
        return spare;
    }

    private void Evict(RetainedChunk entry)
    {
        entry.Arena?.Reset();
        entry.ReleaseSegments();
        ReturnLoans(entry);
        Recycle(entry);
    }

    /// <summary>
    /// Gives back every entry <paramref name="entry"/> borrowed, evicting a lender no one borrows
    /// any more once its own time has passed; the lender is a child, deeper than any entry that
    /// borrows it, so the walk ends.
    /// </summary>
    private void ReturnLoans(RetainedChunk entry)
    {
        if (entry.Borrowed is { } lender)
        {
            entry.Borrowed = null;
            Returned(lender);
        }

        if (entry.MoreBorrowed is { Count: > 0 } more)
        {
            foreach (RetainedChunk other in more)
            {
                Returned(other);
            }

            more.Clear();
        }
    }

    private void Returned(RetainedChunk lender)
    {
        lender.Borrowers--;
        if (Dead(lender) && _entries is not null &&
            _entries.TryGetValue(lender.Key, out RetainedChunk? held) && ReferenceEquals(held, lender))
        {
            _entries.Remove(lender.Key);
            Evict(lender);
        }
    }

    private void Recycle(RetainedChunk entry)
    {
        entry.NodeIndex = -1;
        entry.Claimant = null;
        if (!_disposed)
        {
            entry.NextSpare = _spares;
            _spares = entry;
        }
    }
}

/// <summary>One retained chunk: the arena that owns it, the node, who is decoding it, and when it was last borrowed.</summary>
internal sealed class RetainedChunk
{
    /// <summary>The arena the chunk is decoded into, made when its first decode begins; an entry claimed and given back has none.</summary>
    internal CanonicalArena? Arena;

    internal long Key;

    /// <summary>The decoded node, or -1 while the entry is claimed and not yet published.</summary>
    internal int NodeIndex = -1;

    /// <summary>The number of the last batch that borrowed this entry.</summary>
    internal long LastTouched;

    /// <summary>The context decoding the chunk, until it is published.</summary>
    internal ScanContext? Claimant;

    /// <summary>The next evicted entry, while this one waits to be refilled.</summary>
    internal RetainedChunk? NextSpare;

    /// <summary>
    /// The segment its records view. A decode can hand back views onto the segment it read, a
    /// primitive's values for one, and the batch that read the segment releases it at
    /// <see cref="ScanContext.ResetBatch"/> while this entry lives on, so the entry holds a
    /// reference of its own from the moment it is published until it is evicted.
    /// </summary>
    internal SegmentOwner? Segment;

    /// <summary>Any further segment the decode read, when it read more than one.</summary>
    internal List<SegmentOwner>? MoreSegments;

    /// <summary>How many live entries view this one's arena: it outlives every one of them.</summary>
    internal int Borrowers;

    /// <summary>The first entry this one's records view, a child decoded once for every window of its chunk.</summary>
    internal RetainedChunk? Borrowed;

    /// <summary>Any further entry this one's records view, when it views more than one.</summary>
    internal List<RetainedChunk>? MoreBorrowed;

    /// <summary>Records that this entry views <paramref name="lender"/>, once; the table's lock is held.</summary>
    internal void Borrow(RetainedChunk lender)
    {
        if (ReferenceEquals(lender, this) || ReferenceEquals(Borrowed, lender) ||
            (MoreBorrowed is { } more && more.Contains(lender)))
        {
            return;
        }

        lender.Borrowers++;
        if (Borrowed is null)
        {
            Borrowed = lender;
            return;
        }

        (MoreBorrowed ??= []).Add(lender);
    }

    /// <summary>Clears the loans in both directions without giving anything back, for a table being disposed.</summary>
    internal void ForgetLoans()
    {
        Borrowers = 0;
        Borrowed = null;
        MoreBorrowed?.Clear();
    }

    /// <summary>Adds a segment the decode depends on, once; no reference is taken here.</summary>
    internal void Depend(SegmentOwner? owner)
    {
        if (owner is null || ReferenceEquals(Segment, owner))
        {
            return;
        }

        if (Segment is null)
        {
            Segment = owner;
            return;
        }

        MoreSegments ??= [];
        if (!MoreSegments.Contains(owner))
        {
            MoreSegments.Add(owner);
        }
    }

    /// <summary>Takes a reference on every segment gathered, as the entry is published.</summary>
    internal void RetainSegments()
    {
        Segment?.Retain();
        if (MoreSegments is { } more)
        {
            foreach (SegmentOwner owner in more)
            {
                owner.Retain();
            }
        }
    }

    /// <summary>Drops the references <see cref="RetainSegments"/> took.</summary>
    internal void ReleaseSegments()
    {
        Segment?.Release();
        if (MoreSegments is { } more)
        {
            foreach (SegmentOwner owner in more)
            {
                owner.Release();
            }
        }

        Forget();
    }

    /// <summary>Clears what the entry gathered without releasing it, for a decode that published nothing.</summary>
    internal void Forget()
    {
        Segment = null;
        MoreSegments?.Clear();
    }
}

/// <summary>
/// The entries scans give back when they end, for the scans that follow: an entry keeps its arena's
/// record tables through a reset, so a scan taking one allocates none of what a retained chunk
/// needs, the second set a chunk boundary claims included.
/// </summary>
/// <remarks>
/// Process-wide, because an entry belongs to no file: its arena's storage went back to the memory
/// pool with the reset, and what is kept is its managed tables, a few kilobytes an entry. The bound
/// is what several scans of wide projections hold at once; an entry past it is left to the collector.
/// </remarks>
internal static class RetainedChunkPool
{
    private const int Capacity = 256;

    private static readonly RetainedChunk?[] Entries = new RetainedChunk?[Capacity];
    private static readonly Lock Gate = new Lock();
    private static int _count;

    /// <summary>An entry given back by an earlier scan, or a new one when none waits.</summary>
    internal static RetainedChunk Rent()
    {
        lock (Gate)
        {
            if (_count > 0)
            {
                RetainedChunk entry = Entries[--_count]!;
                Entries[_count] = null;
                return entry;
            }
        }

        return new RetainedChunk();
    }

    /// <summary>Keeps an entry for a later scan; its arena is reset and it holds no claim, segment or loan.</summary>
    internal static void Return(RetainedChunk entry)
    {
        lock (Gate)
        {
            if (_count < Capacity)
            {
                Entries[_count++] = entry;
            }
        }
    }
}
