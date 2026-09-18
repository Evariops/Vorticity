// The ordered walk across a dataset's objects - docs/13-dataset.md §6.6: "a k-way merge of the
// level-0 objects, through their runs, and of the levels", at a cost of "≤ 8 + L cursors, bounded".
//
// THE COUNT IS THE CLAIM, so `Cursors` reports it and a test reads it. An object's cursor is opened
// only once it could hold the next key, which is what `KeyOrderedMerge` does for `InKeyOrder`: its
// leaf key is its exact minimum (§4.1), so an unopened object whose minimum lies above every open
// cursor's key cannot hold the next one. A SEEK goes further, level by level: inside a level above 0
// the objects are key-disjoint (§5.2), so the one that can hold the sought key is the last whose
// minimum is at or below it, and every object before it holds only smaller keys and is never opened
// for this seek. Level 0's objects overlap and are all candidates, but each still opens only once it
// could hold the next key. A seek therefore opens at most level 0's objects and one per level above
// it; a walk from there opens the next object of a level only when the walk reaches it.
//
// A LINEAR SCAN, NOT A HEAP, and deliberately. Choosing the smallest of k keys costs k comparisons
// here and log k with a heap, and k is bounded by a small constant; a heap would add the bookkeeping
// of re-sifting a cursor that advanced, for a saving that starts to matter somewhere past thirty
// cursors, which §5's invariant says never happens. The comparator is `KeyCursor.Compare`, the
// library's own total order, so a merge cannot disagree with the runs it merges; the bounds are the
// clustering key's encoding, which a cursor key is encoded into to be compared with them.
//
// AN OBJECT WITHOUT A KEY SOURCE IS A REFUSAL, not a skip. A dataset may hold a file it did not
// write (§3's import) with no run on the clustering key and no sorted column to stand in; a walk
// that quietly left its rows out would answer a question nobody asked. So opening it fails, names the
// object, and says what would fix it -- when the walk reaches it, which is the only moment its rows
// could have been left out -- and every other reader of the dataset is unaffected, because a scan
// reads objects rather than keys.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Expressions;
using Vorticity.Keys;

namespace Vorticity.Dataset;

/// <summary>A cursor over the keys of every object of one version, in key order.</summary>
public sealed class DatasetKeyCursor : IAsyncDisposable
{
    private readonly VortexDataset _dataset;
    private readonly ClusteringKey _key;
    private readonly Slot[] _slots;
    private readonly List<int>[] _levels;
    private FilterLiteral _target;
    private SeekOp _op;
    private bool _first;
    private int _current = -1;
    private bool _disposed;

    private DatasetKeyCursor(VortexDataset dataset, ClusteringKey key, Slot[] slots, List<int>[] levels)
    {
        _dataset = dataset;
        _key = key;
        _slots = slots;
        _levels = levels;
    }

    /// <summary>
    /// How many objects' cursors the walk has opened — §6.6's bounded number. A seek opens at most
    /// level 0's objects and one per level above it; a walk opens the rest as it reaches them.
    /// </summary>
    public int Cursors { get; private set; }

    /// <summary>Whether the cursor is positioned on an entry.</summary>
    public bool IsValid => !_disposed && _current >= 0;

    /// <summary>The current entry's key.</summary>
    /// <exception cref="InvalidOperationException">The cursor is not positioned.</exception>
    public FilterLiteral Key => _slots[Positioned()].Cursor!.Key;

    /// <summary>The current entry's key as bytes, empty for a key whose values are not bytes.</summary>
    /// <exception cref="InvalidOperationException">The cursor is not positioned.</exception>
    public ReadOnlySpan<byte> KeyBytes => _slots[Positioned()].Cursor!.KeyBytes;

    /// <summary>The current entry's row <em>within its own object</em>.</summary>
    /// <exception cref="InvalidOperationException">The cursor is not positioned.</exception>
    public long Row => _slots[Positioned()].Cursor!.Row;

    /// <summary>The object the current entry lives in.</summary>
    /// <exception cref="InvalidOperationException">The cursor is not positioned.</exception>
    public ObjectEntry Object => _slots[Positioned()].Entry;

    /// <summary>Prepares a walk over the objects of <paramref name="dataset"/>'s current version.</summary>
    /// <param name="dataset">The dataset, which must declare a clustering key (§4.1).</param>
    /// <param name="cancellationToken">Cancels the tree's reads.</param>
    /// <returns>The cursor, unpositioned; seek it first. No object is opened yet.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="dataset"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The dataset declares no clustering key.</exception>
    public static async ValueTask<DatasetKeyCursor> OpenAsync(
        VortexDataset dataset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ClusteringKey key = dataset.Key ?? throw new InvalidOperationException(
            "A key-ordered walk needs a clustering key; this dataset is ordered by row position " +
            "(13 §4.1), whose order a scan already delivers.");

        List<Slot> slots = [];
        List<List<int>> levels = [];
        await foreach (PositionedObject held in dataset
            .WalkAsync(null, 0, long.MaxValue, null, cancellationToken).ConfigureAwait(false))
        {
            while (levels.Count <= held.Level)
            {
                levels.Add([]);
            }

            levels[held.Level].Add(slots.Count);
            slots.Add(new Slot(held.Entry, VortexDataset.OrderOf(held.TreeKey).ToArray()));
        }

        // Each level by its objects' minima: the tree's own order on the clustering key.
        Slot[] all = [.. slots];
        foreach (List<int> level in levels)
        {
            level.Sort((left, right) =>
            {
                int order = all[left].Bound.AsSpan().SequenceCompareTo(all[right].Bound);
                return order != 0 ? order : left.CompareTo(right);
            });
        }

        return new DatasetKeyCursor(dataset, key, all, [.. levels]);
    }

    /// <summary>Positions on the smallest key of the dataset.</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>Whether any object holds an entry.</returns>
    public async ValueTask<bool> SeekFirstAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _first = true;
        foreach (Slot slot in _slots)
        {
            slot.Live = false;
            slot.Pending = true;
        }

        await ResolveAsync(cancellationToken).ConfigureAwait(false);
        return Choose();
    }

    /// <summary>Positions relative to <paramref name="key"/>, as every object's cursor would.</summary>
    /// <param name="key">The sought key, in the key's domain: the row encoding of the tuple for a composite key.</param>
    /// <param name="op">Where to land.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>Whether an entry was found.</returns>
    /// <remarks>
    /// Forward operations only: this merge walks one way, so a backward seek would leave the
    /// cursors pointing where the next <see cref="NextAsync"/> could not merge them.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="op"/> looks backwards.</exception>
    public async ValueTask<bool> SeekAsync(
        FilterLiteral key, SeekOp op = SeekOp.AtOrAfter, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (op is not (SeekOp.AtOrAfter or SeekOp.After or SeekOp.Exact))
        {
            throw new ArgumentOutOfRangeException(
                nameof(op), op, "A merged cursor walks forward; seek to a lower bound and walk.");
        }

        // EVERY CURSOR GOES TO THE LOWER BOUND, even for an exact seek, and the exactness is decided
        // on the winner afterwards. Seeking each cursor `Exact` would invalidate the ones whose
        // object does not hold the key, and the walk that follows would then be missing their rows
        // for every key after this one -- a seek that quietly truncates the rest of the merge.
        _first = false;
        _target = key;
        _op = op == SeekOp.Exact ? SeekOp.AtOrAfter : op;
        byte[] sought = _key.IsComposite ? key.BytesValue.ToArray() : _key.Encode([key]);
        for (int level = 0; level < _levels.Length; level++)
        {
            // Above level 0 the objects are disjoint: before the last one whose minimum is at or
            // below the sought key, every object holds only smaller keys.
            int from = 0;
            if (level > 0)
            {
                List<int> ordered = _levels[level];
                for (int i = 0; i < ordered.Count; i++)
                {
                    if (_slots[ordered[i]].Bound.AsSpan().SequenceCompareTo(sought) <= 0)
                    {
                        from = i;
                    }
                }
            }

            for (int i = 0; i < _levels[level].Count; i++)
            {
                Slot slot = _slots[_levels[level][i]];
                slot.Live = false;
                slot.Pending = i >= from;
            }
        }

        await ResolveAsync(cancellationToken).ConfigureAwait(false);
        if (!Choose())
        {
            return false;
        }

        if (op == SeekOp.Exact && KeyCursor.Compare(Key, key) != 0)
        {
            _current = -1;
            return false;
        }

        return true;
    }

    /// <summary>Moves to the next entry in key order.</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>Whether there is one.</returns>
    public async ValueTask<bool> NextAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_current < 0)
        {
            return false;
        }

        // Only the cursor that was chosen has been consumed; the others still hold their entry.
        Slot slot = _slots[_current];
        slot.Live = await slot.Cursor!.NextAsync(cancellationToken).ConfigureAwait(false);
        await ResolveAsync(cancellationToken).ConfigureAwait(false);
        return Choose();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _current = -1;
        foreach (Slot slot in _slots)
        {
            if (slot.Cursor is not null)
            {
                await slot.Cursor.DisposeAsync().ConfigureAwait(false);
            }

            if (slot.Lease is not null)
            {
                await slot.Lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Opens and positions every pending object that could hold the next key: while one's minimum
    /// is at or below the smallest key an open cursor holds, or no cursor holds one.
    /// </summary>
    private async ValueTask ResolveAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            int next = -1;
            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i].Pending
                    && (next < 0 || _slots[i].Bound.AsSpan().SequenceCompareTo(_slots[next].Bound) < 0))
                {
                    next = i;
                }
            }

            if (next < 0)
            {
                return;
            }

            if (Smallest() is { } smallest && _slots[next].Bound.AsSpan().SequenceCompareTo(smallest) > 0)
            {
                return;
            }

            Slot slot = _slots[next];
            slot.Pending = false;
            KeyCursor cursor = await CursorOfAsync(slot, cancellationToken).ConfigureAwait(false);
            slot.Live = _first
                ? await cursor.SeekFirstAsync(cancellationToken).ConfigureAwait(false)
                : await cursor.SeekAsync(_target, _op, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The smallest key a live cursor holds, encoded as the bounds are; null when none is live.</summary>
    private byte[]? Smallest()
    {
        int smallest = -1;
        for (int i = 0; i < _slots.Length; i++)
        {
            if (_slots[i].Live
                && (smallest < 0 || KeyCursor.Compare(_slots[i].Cursor!.Key, _slots[smallest].Cursor!.Key) < 0))
            {
                smallest = i;
            }
        }

        if (smallest < 0)
        {
            return null;
        }

        KeyCursor cursor = _slots[smallest].Cursor!;
        return _key.IsComposite ? cursor.KeyBytes.ToArray() : _key.Encode([cursor.Key]);
    }

    /// <summary>The object's cursor, opened on first use and kept for the life of the walk.</summary>
    private async ValueTask<KeyCursor> CursorOfAsync(Slot slot, CancellationToken cancellationToken)
    {
        if (slot.Cursor is { } open)
        {
            return open;
        }

        slot.Lease = await _dataset.RentAsync(slot.Entry, cancellationToken).ConfigureAwait(false);
        slot.Cursor = await _key.TryOpenAsync(slot.Lease.File, cancellationToken).ConfigureAwait(false)
            ?? throw new VortexUnsupportedException(
                slot.Entry.Key,
                "clustering key",
                "The object has no run on the clustering key and no sorted column to stand in, so a " +
                "key-ordered walk would leave its rows out. Rewrite it through the dataset, or index it " +
                "(13 §6.1's mandatory run).");
        Cursors++;
        return slot.Cursor;
    }

    /// <summary>The live cursor holding the smallest key; ties go to the earlier object.</summary>
    /// <remarks>
    /// The tie-break is the tree's own order, so two objects holding the same key are walked in the
    /// order the dataset holds them and a walk is a function of the version, not of a race.
    /// </remarks>
    private bool Choose()
    {
        _current = -1;
        for (int i = 0; i < _slots.Length; i++)
        {
            if (!_slots[i].Live)
            {
                continue;
            }

            if (_current < 0 || KeyCursor.Compare(_slots[i].Cursor!.Key, _slots[_current].Cursor!.Key) < 0)
            {
                _current = i;
            }
        }

        return _current >= 0;
    }

    private int Positioned()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_current < 0)
        {
            throw new InvalidOperationException("The cursor is not positioned on an entry.");
        }

        return _current;
    }

    /// <summary>One object of the walk: its entry, its bound, and its cursor once opened.</summary>
    private sealed class Slot(ObjectEntry entry, byte[] bound)
    {
        internal ObjectEntry Entry { get; } = entry;

        /// <summary>Its leaf key's order: the encoded minimum of its clustering key (§4.1).</summary>
        internal byte[] Bound { get; } = bound;

        internal ObjectLease? Lease { get; set; }

        internal KeyCursor? Cursor { get; set; }

        /// <summary>Whether its cursor is positioned on an entry that has not gone out.</summary>
        internal bool Live { get; set; }

        /// <summary>Whether it may hold a key of the current walk and is not positioned yet.</summary>
        internal bool Pending { get; set; }
    }
}
