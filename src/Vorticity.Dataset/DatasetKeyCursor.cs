using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Keys;

namespace Vorticity.Dataset;

/// <summary>
/// A cursor over the keys of one column across every object of one version, in key order: the
/// objects' own cursors merged. The merge picks the smallest key by a linear scan rather than a
/// heap, since the count of open cursors is a small constant on the clustering key.
/// </summary>
/// <remarks>
/// On the clustering key an object's cursor opens only once it could hold the next key, since its
/// tree key is an exact lower bound on its keys and the levels above 0 are key-disjoint; on any
/// other column every object's cursor opens at the first seek. The merge walks forward: a backward
/// step or seek is refused, and <see cref="SeekLastAsync"/> ends a walk rather than starting one.
/// </remarks>
internal sealed class DatasetKeyCursor : IKeyWalker
{
    private readonly DatasetSnapshot _version;
    private readonly ClusteringKey _key;
    private readonly bool _bounded;
    private readonly bool _distinct;
    private readonly bool _indexes;
    private readonly Slot[] _slots;
    private readonly List<int>[] _levels;
    private FilterLiteral _target;
    private SeekOp _op;
    private bool _first;
    private int _current = -1;
    private bool _disposed;

    private DatasetKeyCursor(
        DatasetSnapshot version, ClusteringKey key, bool bounded, bool distinct, bool indexes, Slot[] slots, List<int>[] levels)
    {
        _version = version;
        _key = key;
        _bounded = bounded;
        _distinct = distinct;
        _indexes = indexes;
        _slots = slots;
        _levels = levels;
    }

    /// <summary>
    /// How many objects' cursors the walk has opened. A seek on the clustering key opens at most
    /// level 0's objects and one per level above it; a walk opens the rest as it reaches them.
    /// </summary>
    public int Cursors { get; private set; }

    /// <summary>Whether the cursor is positioned on an entry.</summary>
    public bool IsValid => !_disposed && _current >= 0;

    /// <summary>Whether the entries carry rows, which every object's cursor does: the merge opens them over sources with rows.</summary>
    public bool HasRows => _current < 0 || _slots[_current].Cursor!.HasRows;

    /// <summary>The current entry's key.</summary>
    /// <exception cref="InvalidOperationException">The cursor is not positioned.</exception>
    public FilterLiteral Key => _slots[Positioned()].Cursor!.Key;

    /// <summary>The current entry's key as bytes, empty for a key whose values are not bytes.</summary>
    /// <exception cref="InvalidOperationException">The cursor is not positioned.</exception>
    public ReadOnlySpan<byte> KeyBytes => _slots[Positioned()].Cursor!.KeyBytes;

    /// <summary>The current entry's row among the dataset's, counted as a scan of the version counts them.</summary>
    /// <exception cref="InvalidOperationException">The cursor is not positioned.</exception>
    public long Row
    {
        get
        {
            Slot slot = _slots[Positioned()];
            return slot.FirstRow + slot.Cursor!.Row;
        }
    }

    /// <summary>The object the current entry lives in.</summary>
    /// <exception cref="InvalidOperationException">The cursor is not positioned.</exception>
    public ObjectEntry Object => _slots[Positioned()].Entry;

    /// <summary>
    /// Prepares a walk of the clustering key over the objects of the dataset's current version. The
    /// cursor comes back unpositioned, with no object opened; seek it first.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="dataset"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The dataset declares no clustering key.</exception>
    public static ValueTask<DatasetKeyCursor> OpenAsync(
        VortexDataset dataset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ClusteringKey key = dataset.Key ?? throw new InvalidOperationException(
            "A key-ordered walk needs a clustering key; this dataset is ordered by row position, " +
            "whose order a scan already delivers.");
        return OpenAsync(dataset.Snapshot, key, bounded: true, distinct: false, indexes: true, cancellationToken);
    }

    /// <summary>
    /// Prepares a walk of one column over the objects of <paramref name="version"/>: the clustering
    /// key's own walk when the column is that key, a walk that opens every object otherwise.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="path"/> names no column of the dataset.</exception>
    internal static ValueTask<DatasetKeyCursor> OpenAsync(
        VortexDataset dataset, DatasetSnapshot version, string path, bool distinct, bool indexes, CancellationToken cancellationToken)
    {
        bool clustering = dataset.Key is { IsComposite: false } key && string.Equals(key.Paths[0], path, StringComparison.Ordinal);
        ClusteringKey walked = clustering ? dataset.Key! : ClusteringKey.For([path], dataset.DType)!;
        return OpenAsync(version, walked, clustering, distinct, indexes, cancellationToken);
    }

    /// <summary>
    /// Which key source a walk of <paramref name="path"/> would use, summed over the objects: each
    /// one is opened for its tail, and the first that has no source ends the answer, since the walk
    /// would refuse it.
    /// </summary>
    internal static async ValueTask<KeyPlan> ExplainAsync(
        DatasetSnapshot version, string path, bool indexes, CancellationToken cancellationToken)
    {
        KeySourceKind? source = null;
        int runs = 0;
        long? entries = 0;
        bool hasRows = true;
        List<KeySourceRejection> rejected = [];
        await foreach (PositionedObject held in version
            .WalkAsync(null, 0, long.MaxValue, null, cancellationToken).ConfigureAwait(false))
        {
            KeyPlan plan;
            ObjectLease lease = await version.RentAsync(held.Entry, cancellationToken).ConfigureAwait(false);
            await using (lease.ConfigureAwait(false))
            {
                KeyCursorBuilder keys = lease.File.Keys(path);
                plan = await (indexes ? keys : keys.WithSource(KeySourceKind.SortedColumn))
                    .ExplainAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (KeySourceRejection rejection in plan.Rejected)
            {
                KeySourceRejection named = rejection with { Reason = $"'{held.Entry.Key}': {rejection.Reason}" };
                if (plan.Source == KeySourceKind.None || !rejected.Exists(known => known.Source == rejection.Source))
                {
                    rejected.Add(named);
                }
            }

            if (plan.Source == KeySourceKind.None)
            {
                return new KeyPlan(path, KeySourceKind.None, runs, null, false, rejected);
            }

            // Objects served by different sources report the more general one, which sorted runs are.
            source = source is null || source == plan.Source ? plan.Source : KeySourceKind.SortedRuns;
            runs += plan.Runs;
            entries = entries is { } known && plan.EntryCount is { } count ? known + count : null;
            hasRows &= plan.HasRows;
        }

        return new KeyPlan(path, source ?? KeySourceKind.None, runs, entries, hasRows, rejected);
    }

    /// <summary>Positions on the smallest key.</summary>
    public async ValueTask<bool> SeekFirstAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _first = true;
        foreach (Slot slot in _slots)
        {
            slot.Live = false;
            slot.Walked = false;
            slot.Pending = true;
        }

        await ResolveAsync(cancellationToken).ConfigureAwait(false);
        return Choose();
    }

    /// <summary>
    /// Positions on the largest key's last entry, which ends the walk: the merge does not step
    /// backwards, so the next <see cref="NextAsync"/> answers false.
    /// </summary>
    public async ValueTask<bool> SeekLastAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _current = -1;
        foreach (Slot slot in _slots)
        {
            slot.Live = false;
            slot.Walked = false;
            slot.Pending = false;
        }

        // On the clustering key the largest key is in level 0 or in the last object of a level
        // above it; anywhere else it may be in any object.
        List<int> candidates = [];
        for (int level = 0; level < _levels.Length; level++)
        {
            List<int> ordered = _levels[level];
            if (!_bounded || level == 0)
            {
                candidates.AddRange(ordered);
            }
            else if (ordered.Count > 0)
            {
                candidates.Add(ordered[^1]);
            }
        }

        int last = -1;
        foreach (int candidate in candidates)
        {
            Slot slot = _slots[candidate];
            KeyCursor cursor = await CursorOfAsync(slot, cancellationToken).ConfigureAwait(false);
            slot.Walked = true;
            if (await cursor.SeekLastAsync(cancellationToken).ConfigureAwait(false)
                && (last < 0 || KeyCursor.Compare(cursor.Key, _slots[last].Cursor!.Key) >= 0))
            {
                last = candidate;
            }
        }

        if (last < 0)
        {
            return false;
        }

        _slots[last].Live = true;
        _current = last;
        return true;
    }

    /// <summary>
    /// Positions relative to a key, given in the key's domain: the row encoding of the tuple for a
    /// composite key. Forward operations only, since this merge walks one way and a backward seek
    /// would leave the cursors where <see cref="NextAsync"/> could not merge them.
    /// </summary>
    /// <exception cref="NotSupportedException"><paramref name="op"/> looks backwards.</exception>
    public async ValueTask<bool> SeekAsync(
        FilterLiteral key, SeekOp op = SeekOp.AtOrAfter, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (op is not (SeekOp.AtOrAfter or SeekOp.After or SeekOp.Exact))
        {
            throw new NotSupportedException(
                $"A cursor over a dataset walks forward and cannot seek {op}; seek to a lower bound and walk.");
        }

        // Every cursor goes to the lower bound even for an exact seek, exactness being decided on
        // the winner afterwards: seeking each one exactly would invalidate the cursors whose object
        // does not hold the key, and truncate their rows out of the rest of the merge.
        _first = false;
        _target = key;
        _op = op == SeekOp.Exact ? SeekOp.AtOrAfter : op;
        byte[]? sought = _bounded ? Encoded(key) : null;
        for (int level = 0; level < _levels.Length; level++)
        {
            // Above level 0 the objects are disjoint on the clustering key, so everything before the
            // last one whose minimum is at or below the sought key holds only smaller keys.
            int from = 0;
            if (sought is not null && level > 0)
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
                slot.Walked = false;
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

    /// <summary>
    /// Positions on the entry of rank <paramref name="rank"/> in key order, by walking to it from
    /// the first: the ranks of a merge are known only by counting its entries.
    /// </summary>
    public async ValueTask<bool> SeekRankAsync(long rank, CancellationToken cancellationToken = default)
    {
        if (rank < 0)
        {
            _current = -1;
            return false;
        }

        if (!await SeekFirstAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        for (long step = 0; step < rank; step++)
        {
            if (!await StepAsync(cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Moves to the next entry in key order; on a distinct walk, to the next key.</summary>
    public ValueTask<bool> NextAsync(CancellationToken cancellationToken = default) =>
        _distinct ? NextKeyAsync(cancellationToken) : StepAsync(cancellationToken);

    /// <summary>Refused: the merge walks forward.</summary>
    /// <exception cref="NotSupportedException">Always.</exception>
    public ValueTask<bool> PrevAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("A cursor over a dataset walks forward; seek to a lower bound and walk instead.");

    /// <summary>
    /// Moves to the first entry of the next distinct key: every object positioned on the current
    /// key steps past it, and the others stay where they are.
    /// </summary>
    public async ValueTask<bool> NextKeyAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_current < 0)
        {
            return false;
        }

        FilterLiteral key = Key;
        foreach (Slot slot in _slots)
        {
            if (slot.Live && KeyCursor.Compare(slot.Cursor!.Key, key) == 0)
            {
                slot.Live = await slot.Cursor.NextKeyAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await ResolveAsync(cancellationToken).ConfigureAwait(false);
        return Choose();
    }

    /// <summary>Refused: the merge walks forward.</summary>
    /// <exception cref="NotSupportedException">Always.</exception>
    public ValueTask<bool> PrevKeyAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("A cursor over a dataset walks forward; seek to a lower bound and walk instead.");

    /// <summary>
    /// How many entries across the objects have a key below <paramref name="key"/>; an object whose
    /// lower bound is at or above it is counted as none without being opened.
    /// </summary>
    public async ValueTask<long> RankAsync(FilterLiteral key, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[]? sought = _bounded ? Encoded(key) : null;
        long rank = 0;
        foreach (Slot slot in _slots)
        {
            if (sought is not null && slot.Bound.AsSpan().SequenceCompareTo(sought) >= 0)
            {
                continue;
            }

            KeyCursor cursor = await CursorOfAsync(slot, cancellationToken).ConfigureAwait(false);
            rank += await cursor.RankAsync(key, cancellationToken).ConfigureAwait(false);
        }

        return rank;
    }

    /// <summary>
    /// How many entries across the objects share the current key. An object positioned on it counts
    /// its own; one that has stepped past it counts the entries between it and its position, which
    /// are all that key; one not walked holds none.
    /// </summary>
    /// <exception cref="InvalidOperationException">The cursor is not positioned.</exception>
    public async ValueTask<long> KeyCountAsync(CancellationToken cancellationToken = default)
    {
        FilterLiteral key = Key;
        long count = 0;
        foreach (Slot slot in _slots)
        {
            if (slot.Cursor is not { } cursor || !slot.Walked)
            {
                continue;
            }

            if (slot.Live)
            {
                int order = KeyCursor.Compare(cursor.Key, key);
                count += order == 0
                    ? await cursor.KeyCountAsync(cancellationToken).ConfigureAwait(false)
                    : order > 0
                        ? await cursor.RankAsync(cursor.Key, cancellationToken).ConfigureAwait(false)
                            - await cursor.RankAsync(key, cancellationToken).ConfigureAwait(false)
                        : 0;
            }
            else if (cursor.EntryCount is { } total)
            {
                count += total - await cursor.RankAsync(key, cancellationToken).ConfigureAwait(false);
            }
        }

        return count;
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

    private static async ValueTask<DatasetKeyCursor> OpenAsync(
        DatasetSnapshot version, ClusteringKey key, bool bounded, bool distinct, bool indexes, CancellationToken cancellationToken)
    {
        List<Slot> slots = [];
        List<List<int>> levels = [];
        await foreach (PositionedObject held in version
            .WalkAsync(null, 0, long.MaxValue, null, cancellationToken).ConfigureAwait(false))
        {
            while (levels.Count <= held.Level)
            {
                levels.Add([]);
            }

            levels[held.Level].Add(slots.Count);
            byte[] bound = bounded ? VortexDataset.OrderOf(held.TreeKey).ToArray() : [];
            slots.Add(new Slot(held.Entry, held.FirstRow, bound));
        }

        Slot[] all = [.. slots];
        foreach (List<int> level in levels)
        {
            level.Sort((left, right) =>
            {
                int order = all[left].Bound.AsSpan().SequenceCompareTo(all[right].Bound);
                return order != 0 ? order : left.CompareTo(right);
            });
        }

        return new DatasetKeyCursor(version, key, bounded, distinct, indexes, all, [.. levels]);
    }

    /// <summary>One step of the merge: the object that was chosen moves on, the others keep their entry.</summary>
    private async ValueTask<bool> StepAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_current < 0)
        {
            return false;
        }

        Slot slot = _slots[_current];
        slot.Live = await slot.Cursor!.NextAsync(cancellationToken).ConfigureAwait(false);
        await ResolveAsync(cancellationToken).ConfigureAwait(false);
        return Choose();
    }

    /// <summary>The key as the bounds encode it: the tuple's row encoding for a composite key.</summary>
    private byte[] Encoded(FilterLiteral key) => _key.IsComposite ? key.BytesValue.ToArray() : _key.Encode([key]);

    /// <summary>Opens and positions every pending object whose minimum is at or below the smallest
    /// key an open cursor holds, so none that could hold the next key stays closed.</summary>
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

            if (_bounded && Smallest() is { } smallest && _slots[next].Bound.AsSpan().SequenceCompareTo(smallest) > 0)
            {
                return;
            }

            Slot slot = _slots[next];
            slot.Pending = false;
            slot.Walked = true;
            KeyCursor cursor = await CursorOfAsync(slot, cancellationToken).ConfigureAwait(false);
            slot.Live = _first
                ? await cursor.SeekFirstAsync(cancellationToken).ConfigureAwait(false)
                : await cursor.SeekAsync(_target, _op, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The smallest key a live cursor holds, encoded as the bounds are.</summary>
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

    /// <summary>The object's cursor, opened on first use and kept for the life of the walk. An
    /// object with no key source is refused rather than skipped, since skipping it would silently
    /// leave its rows out of the walk.</summary>
    private async ValueTask<KeyCursor> CursorOfAsync(Slot slot, CancellationToken cancellationToken)
    {
        if (slot.Cursor is { } open)
        {
            return open;
        }

        slot.Lease = await _version.RentAsync(slot.Entry, cancellationToken).ConfigureAwait(false);
        slot.Cursor = await _key.TryOpenAsync(slot.Lease.File, _indexes, cancellationToken).ConfigureAwait(false)
            ?? throw new VortexUnsupportedException(
                slot.Entry.Key,
                ComponentKind.Index,
                $"The object has no run on '{string.Join(", ", _key.Paths)}' and no sorted column to stand in, so a " +
                "key-ordered walk would leave its rows out. Rewrite it through the dataset, or index it.");
        Cursors++;
        return slot.Cursor;
    }

    /// <summary>The live cursor holding the smallest key; ties go to the earlier object, so that a
    /// walk is a function of the version and not of a race.</summary>
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

    private sealed class Slot(ObjectEntry entry, long firstRow, byte[] bound)
    {
        internal ObjectEntry Entry { get; } = entry;

        /// <summary>Where the object's rows start among the dataset's.</summary>
        internal long FirstRow { get; } = firstRow;

        /// <summary>The encoded minimum of the object's key, or empty when none is known.</summary>
        internal byte[] Bound { get; } = bound;

        internal ObjectLease? Lease { get; set; }

        internal KeyCursor? Cursor { get; set; }

        internal bool Live { get; set; }

        /// <summary>It may hold a key of the current walk and is not positioned yet.</summary>
        internal bool Pending { get; set; }

        /// <summary>It was positioned by the current walk, rather than only opened to rank a key.</summary>
        internal bool Walked { get; set; }
    }
}
