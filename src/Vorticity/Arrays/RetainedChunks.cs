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
/// context is decoding waits for that decode instead of starting its own. A context holds at most
/// one claim at a time and never waits while it holds one, so the waits cannot form a cycle.
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
    private Dictionary<long, RetainedChunk>? _entries;
    private RetainedChunk? _spares;
    private long _floor;
    private bool _prepare;
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
        long key, ScanContext claimant, long batch, out RetainedChunk? claim, out CanonicalArena arena, out int nodeIndex)
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
                (_entries ??= new Dictionary<long, RetainedChunk>(_capacity))[key] = taken;
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
    internal void Publish(RetainedChunk claim, int nodeIndex, long batch)
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
            Monitor.PulseAll(this);
        }
    }

    /// <summary>Gives up a claim whose decode did not publish, and wakes whoever waits for it.</summary>
    /// <param name="claim">The claim <see cref="TryGet"/> handed out.</param>
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
                    Recycle(new RetainedChunk { Arena = new CanonicalArena() });
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
                if (entry.NodeIndex >= 0 && entry.LastTouched < batch)
                {
                    // Removing while enumerating is what a dictionary allows.
                    _entries.Remove(held.Key);
                    Evict(entry);
                }
            }
        }
    }

    /// <summary>Releases every entry and every spare arena.</summary>
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
                    if (entry.NodeIndex >= 0)
                    {
                        Evict(entry);
                    }
                    else
                    {
                        entry.Arena?.Reset();
                        entry.Forget();
                    }
                }

                _entries.Clear();
            }

            _spares = null;
            Monitor.PulseAll(this);
        }
    }

    private RetainedChunk Take()
    {
        RetainedChunk? spare = _spares;
        if (spare is null)
        {
            return new RetainedChunk();
        }

        _spares = spare.NextSpare;
        spare.NextSpare = null;
        return spare;
    }

    private void Evict(RetainedChunk entry)
    {
        entry.Arena?.Reset();
        entry.ReleaseSegments();
        Recycle(entry);
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
