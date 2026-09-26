// The scan's table of retained chunks with its entries in a dense array searched in order, kept
// line for line as the baseline of RetainedLookupBenchmarks: `RetainedChunks` in the library is the
// same table with its entries hashed by key.
using System;
using System.Collections.Generic;
using System.Threading;
using Vorticity.Buffers;

namespace Vorticity.Benchmarks.Complexity;

using Vorticity.Arrays;

/// <summary>The scan's table of retained chunks, its entries searched in order.</summary>
internal sealed class RetainedChunksBefore : IDisposable
{
    private readonly int _capacity;
    private readonly int _lendingCapacity;
    private RetainedChunk?[] _entries = [];
    private int _count;
    private RetainedChunk? _spares;
    private long _floor;
    private int _waiters;
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
    internal RetainedChunksBefore(int columns, int lanes)
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
                if (Find(key) is { } entry)
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

                    _waiters++;
                    try
                    {
                        Monitor.Wait(this);
                    }
                    finally
                    {
                        _waiters--;
                    }

                    continue;
                }

                RetainedChunk taken = Take();
                taken.Key = key;
                taken.Claimant = claimant;
                taken.NodeIndex = -1;
                taken.LastTouched = batch;
                if (_entries.Length == 0)
                {
                    _entries = Tables.Rent(_capacity);
                }

                if (borrower is not null && !_lending)
                {
                    _lending = true;
                    Reserve(_lendingCapacity);
                }

                Add(taken);
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
        // An empty table is looked at without the lock: a scan whose batches decode their own rows
        // retains nothing and still looks on every batch. An entry another context is publishing
        // meanwhile is missed as it would have been a moment earlier: a look is followed by a
        // claim, which takes the lock, or by a decode of the caller's own.
        if (Volatile.Read(ref _count) == 0)
        {
            arena = null!;
            nodeIndex = -1;
            return false;
        }

        lock (this)
        {
            if (Find(key) is { NodeIndex: >= 0 } entry)
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
            Wake();
        }
    }

    /// <summary>Gives up a claim whose decode did not publish, and wakes whoever waits for it.</summary>
    /// <param name="claim">The claim <see cref="TryGet"/> handed out.</param>
    /// <remarks>What the claim had borrowed is given back, and a lender it was the last to borrow is evicted when its own time has passed.</remarks>
    internal void Abandon(RetainedChunk claim)
    {
        lock (this)
        {
            Remove(claim);
            claim.Arena?.Reset();
            claim.Forget();
            ReturnLoans(claim);
            Recycle(claim);
            Wake();
        }
    }

    /// <summary>
    /// Drops every published entry no batch from <paramref name="batch"/> on has touched, once
    /// <paramref name="batch"/> has been executed and every batch before it is dead.
    /// </summary>
    /// <param name="batch">The number of the batch just delivered.</param>
    internal void Release(long batch)
    {
        // Nothing to evict, and no lock to take: an entry another context is publishing meanwhile
        // belongs to a batch not delivered yet, which this release would keep.
        if (Volatile.Read(ref _count) == 0)
        {
            return;
        }

        lock (this)
        {
            if (_count == 0)
            {
                return;
            }

            if (_prepare)
            {
                // The second set, for the lanes that reach a chunk boundary ahead of the eviction:
                // one spare per chunk the first batch retained, made now, while the scan is in its
                // first batch.
                _prepare = false;
                int retained = _count;
                for (int i = 0; i < retained; i++)
                {
                    RetainedChunk spare = RetainedChunkPool.Shared.Rent();
                    spare.Arena ??= new CanonicalArena();
                    Recycle(spare);
                }
            }

            if (batch <= _floor)
            {
                return;
            }

            // From the last entry down, since a removal moves the last entry into the hole: an
            // entry moved below the walk is seen once more, which only asks it again whether it is
            // dead, and a lender the eviction frees is removed wherever it sits.
            _floor = batch;
            for (int i = _count - 1; i >= 0; i--)
            {
                if (i < _count && _entries[i] is { } entry && Dead(entry))
                {
                    RemoveAt(i);
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
            for (int i = 0; i < _count; i++)
            {
                RetainedChunk entry = _entries[i]!;
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
                RetainedChunkPool.Shared.Return(entry);
            }

            if (_entries.Length != 0)
            {
                Array.Clear(_entries, 0, _count);
                Tables.Return(_entries);
                _entries = [];
            }

            _count = 0;

            while (_spares is { } spare)
            {
                _spares = spare.NextSpare;
                spare.NextSpare = null;
                RetainedChunkPool.Shared.Return(spare);
            }

            Wake();
        }
    }

    private RetainedChunk Take()
    {
        RetainedChunk? spare = _spares;
        if (spare is null)
        {
            return RetainedChunkPool.Shared.Rent();
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
        if (Dead(lender) && Remove(lender))
        {
            Evict(lender);
        }
    }

    /// <summary>The entry under <paramref name="key"/>, or null; the table's lock is held.</summary>
    /// <remarks>A scan holds two entries a column or so, which a walk in order finds sooner than a hash would.</remarks>
    private RetainedChunk? Find(long key)
    {
        RetainedChunk?[] entries = _entries;
        for (int i = 0; i < _count; i++)
        {
            RetainedChunk entry = entries[i]!;
            if (entry.Key == key)
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>Adds <paramref name="entry"/>, doubling the table when it is full; the table's lock is held.</summary>
    private void Add(RetainedChunk entry)
    {
        if (_count == _entries.Length)
        {
            Reserve(_count * 2);
        }

        _entries[_count++] = entry;
    }

    /// <summary>Removes <paramref name="entry"/> when the table holds it; the table's lock is held.</summary>
    private bool Remove(RetainedChunk entry)
    {
        for (int i = 0; i < _count; i++)
        {
            if (ReferenceEquals(_entries[i], entry))
            {
                RemoveAt(i);
                return true;
            }
        }

        return false;
    }

    /// <summary>Removes the entry at <paramref name="index"/>, moving the last one into its place.</summary>
    private void RemoveAt(int index)
    {
        _entries[index] = _entries[--_count];
        _entries[_count] = null;
    }

    /// <summary>Grows the table to hold <paramref name="capacity"/> entries.</summary>
    private void Reserve(int capacity)
    {
        if (_entries.Length < capacity)
        {
            RetainedChunk?[] grown = new RetainedChunk?[capacity];
            Array.Copy(_entries, grown, _count);
            _entries = grown;
        }
    }

    /// <summary>Wakes the contexts waiting for a claim, when there are some; the table's lock is held.</summary>
    private void Wake()
    {
        if (_waiters != 0)
        {
            Monitor.PulseAll(this);
        }
    }

    /// <summary>
    /// The entry tables scans gave back, for the scans that follow: a table grows once to what the
    /// scans of the process hold, and a scan allocates none.
    /// </summary>
    private static class Tables
    {
        private const int Capacity = 16;

        private static readonly RetainedChunk?[]?[] Kept = new RetainedChunk?[Capacity][];
        private static readonly Lock Gate = new Lock();
        private static int _count;

        /// <summary>A kept table of at least <paramref name="length"/> entries, or a new one.</summary>
        internal static RetainedChunk?[] Rent(int length)
        {
            RetainedChunk?[]? table = null;
            lock (Gate)
            {
                if (_count > 0)
                {
                    table = Kept[--_count];
                    Kept[_count] = null;
                }
            }

            return table is not null && table.Length >= length ? table : new RetainedChunk?[length];
        }

        /// <summary>Keeps <paramref name="table"/>, emptied, for a later scan, or drops it past the bound.</summary>
        internal static void Return(RetainedChunk?[] table)
        {
            lock (Gate)
            {
                if (_count < Capacity)
                {
                    Kept[_count++] = table;
                }
            }
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
