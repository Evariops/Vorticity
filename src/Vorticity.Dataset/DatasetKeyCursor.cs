using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Keys;

namespace Vorticity.Dataset;

/// <summary>
/// A cursor over the keys of one column across every object of one version, in key order: the
/// objects' own cursors merged, the open ones in a heap by their key, so that a step costs the
/// logarithm of the cursors open rather than a pass over every object.
/// </summary>
/// <remarks>
/// On the clustering key an object's cursor opens only once it could hold the next key, since its
/// tree key is an exact lower bound on its keys and the levels above 0 are key-disjoint; on any
/// other column every object's cursor opens at the first seek. The objects of a level wait in the
/// order of their bounds, so the next to open is the first waiting one of some level. The merge
/// walks forward: a backward step or seek is refused, and <see cref="SeekLastAsync"/> ends a walk
/// rather than starting one.
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

    // The slots whose cursor is on an entry, a binary min-heap by key, a tie going to the earlier
    // object, so that the walk's entry is the top's.
    private readonly int[] _heap;
    private int _live;

    // Per level, the place in its order of the first object the walk has not positioned yet.
    private readonly int[] _next;

    // The slots a distinct step moves past the current key, gathered before any of them moves.
    private readonly int[] _moved;

    // The smallest open key, row-encoded, which a single column's bounds are compared with.
    private byte[] _smallest = [];

    // Per object, its part in a selection, rented by the first selection and returned when the walk
    // is disposed: a walk that never selects holds none. It may be longer than the objects.
    private Window[]? _windows;

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
        _heap = new int[slots.Length];
        _moved = new int[slots.Length];
        _next = new int[levels.Length];
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
        Restart();
        Array.Clear(_next);
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
        Restart();
        for (int level = 0; level < _levels.Length; level++)
        {
            _next[level] = _levels[level].Count;
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
        Push(last);
        return Choose();
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
        Restart();
        for (int level = 0; level < _levels.Length; level++)
        {
            // Above level 0 the objects are disjoint on the clustering key, so everything before the
            // last one whose minimum is at or below the sought key holds only smaller keys.
            _next[level] = sought is not null && level > 0 ? LastAtOrBelow(_levels[level], sought) : 0;
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
    /// Positions on the entry of rank <paramref name="rank"/> in key order: a near one by walking
    /// to it from the first, a far one by a selection over the objects' cursors.
    /// </summary>
    /// <remarks>
    /// A walk costs a step per entry before the rank; the selection, which opens the objects a walk
    /// would open, a few rounds of a bound in each of them. Below one entry per object the walk is
    /// the cheaper, whether the objects interleave or not.
    /// </remarks>
    public async ValueTask<bool> SeekRankAsync(long rank, CancellationToken cancellationToken = default)
    {
        if (rank < 0)
        {
            _current = -1;
            return false;
        }

        if (rank >= _slots.Length)
        {
            return await SelectAsync(rank, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// The entry of rank <paramref name="rank"/>, selected over the objects' cursors, each keeping
    /// the window of its own ranks still candidates.
    /// </summary>
    /// <remarks>
    /// On the clustering key the objects take part in the order of their bounds, until they hold
    /// more entries than the rank; the ones left waiting hold only keys at or above their bounds.
    /// Once the entry is found, the waiting objects whose bounds are at or below its key take part
    /// and the selection runs again, which can only find a smaller key, so none is left that could
    /// come before it. An object whose keys all come after the entry is not opened, as a walk
    /// would not open it. On any other column every object takes part.
    /// </remarks>
    internal async ValueTask<bool> SelectAsync(long rank, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _windows ??= ArrayPool<Window>.Shared.Rent(_slots.Length);
        Restart();
        Array.Clear(_next);
        long held = 0;
        for (int next = FirstWaiting(out int level); next >= 0 && (held <= rank || !_bounded); next = FirstWaiting(out level))
        {
            held += await JoinAsync(next, level, cancellationToken).ConfigureAwait(false);
        }

        while (await RoundsAsync(rank, cancellationToken).ConfigureAwait(false))
        {
            await PlaceAsync(cancellationToken).ConfigureAwait(false);
            bool joined = false;
            for (int next = FirstWaiting(out int level); next >= 0 && MayHoldSmallest(next); next = FirstWaiting(out level))
            {
                await JoinAsync(next, level, cancellationToken).ConfigureAwait(false);
                joined = true;
            }

            if (!joined)
            {
                return Choose();
            }
        }

        Restart();
        return false;
    }

    /// <summary>An object takes part in a selection: past its place in its level, opened, and counted.</summary>
    private async ValueTask<long> JoinAsync(int slot, int level, CancellationToken cancellationToken)
    {
        _next[level]++;
        Slot joined = _slots[slot];
        joined.Walked = true;
        KeyCursor cursor = await CursorOfAsync(joined, cancellationToken).ConfigureAwait(false);
        long count = cursor.EntryCount ?? 0;
        _windows![slot].Count = count;
        return count;
    }

    /// <summary>
    /// The rounds of a selection over the objects taking part, which leave each one's bound for the
    /// entry of rank <paramref name="rank"/> in its <see cref="Window.Cut"/>.
    /// </summary>
    /// <remarks>
    /// A round takes a pivot among the candidates and bounds it in every object, in the merge's
    /// order: an earlier object counts the pivot's key's entries as before it, a later one as after
    /// it. Its rank rules out the candidates on its wrong side. The pivot is taken three ways in
    /// turn.
    /// <list type="bullet">
    /// <item>Where the rank would fall in the widest window, were its candidates spread like the
    /// others': over objects whose keys interleave, a few rounds close in on the entry.</item>
    /// <item>Where it would fall were the windows laid end to end in the order of their first keys:
    /// over objects that do not overlap, as a compacted level's, that is the entry itself.</item>
    /// <item>The middle below which half the candidates lie, each object standing on its own middle
    /// and counting for its window: a quarter of them at least are ruled out, whatever the keys,
    /// so the rounds are O(log N).</item>
    /// </list>
    /// </remarks>
    private async ValueTask<bool> RoundsAsync(long rank, CancellationToken cancellationToken)
    {
        Window[] windows = _windows!;
        for (int s = 0; s < _slots.Length; s++)
        {
            windows[s].Low = 0;
            windows[s].High = _slots[s].Walked ? windows[s].Count : 0;
            windows[s].Cut = 0;
        }

        for (int round = 0; ; round++)
        {
            _live = 0;
            long ruledOut = 0;
            long candidates = 0;
            int widest = -1;
            for (int s = 0; s < _slots.Length; s++)
            {
                long width = windows[s].Width;
                ruledOut += windows[s].Low;
                candidates += width;
                widest = width > 0 && (widest < 0 || width > windows[widest].Width) ? s : widest;
            }

            if (widest < 0 || rank < ruledOut || rank >= ruledOut + candidates)
            {
                return false;
            }

            int pivot = widest;
            if (round % 3 == 0)
            {
                long width = windows[widest].Width;
                long offset = Math.Clamp((long)((double)(rank - ruledOut) * width / candidates), 0, width - 1);
                windows[widest].Cut = windows[widest].Low + offset;
                await _slots[widest].Cursor!.SeekRankAsync(windows[widest].Cut, cancellationToken).ConfigureAwait(false);
            }
            else if (round % 3 == 1)
            {
                // The merge's heap orders the windows by their first keys, and gives them back until
                // their widths pass the rank.
                for (int s = 0; s < _slots.Length; s++)
                {
                    if (windows[s].Width > 0
                        && await _slots[s].Cursor!.SeekRankAsync(windows[s].Low, cancellationToken).ConfigureAwait(false))
                    {
                        Push(s);
                    }
                }

                long counted = 0;
                while (_live > 0)
                {
                    pivot = PopTop();
                    if (counted + windows[pivot].Width > rank - ruledOut)
                    {
                        break;
                    }

                    counted += windows[pivot].Width;
                }

                windows[pivot].Cut = windows[pivot].Low + Math.Clamp(rank - ruledOut - counted, 0, windows[pivot].Width - 1);
                await _slots[pivot].Cursor!.SeekRankAsync(windows[pivot].Cut, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // The merge's heap orders the middles, and gives them back smallest first until half
                // the candidates are counted.
                for (int s = 0; s < _slots.Length; s++)
                {
                    if (windows[s].Width > 0)
                    {
                        windows[s].Cut = windows[s].Low + (windows[s].Width >> 1);
                        if (await _slots[s].Cursor!.SeekRankAsync(windows[s].Cut, cancellationToken).ConfigureAwait(false))
                        {
                            Push(s);
                        }
                    }
                }

                long counted = 0;
                while (_live > 0 && 2 * counted < candidates)
                {
                    pivot = PopTop();
                    counted += windows[pivot].Width;
                }
            }

            KeyCursor lender = _slots[pivot].Cursor!;
            long before = 0;
            for (int s = 0; s < _slots.Length; s++)
            {
                if (s != pivot)
                {
                    long cut = windows[s].Low;
                    if (windows[s].Width > 0)
                    {
                        long ranked = await _slots[s].Cursor!.RankOfAsync(lender, upper: s < pivot, cancellationToken).ConfigureAwait(false);
                        cut = Math.Clamp(ranked, windows[s].Low, windows[s].High);
                    }

                    windows[s].Cut = cut;
                }

                before += windows[s].Cut;
            }

            if (before == rank)
            {
                return true;
            }

            for (int s = 0; s < _slots.Length; s++)
            {
                if (before < rank)
                {
                    windows[s].Low = Math.Max(windows[s].Low, windows[s].Cut);
                }
                else
                {
                    windows[s].High = Math.Min(windows[s].High, windows[s].Cut);
                }
            }

            if (before < rank)
            {
                windows[pivot].Low = windows[pivot].Cut + 1;
            }
        }
    }

    /// <summary>
    /// Every object taking part on its bound, in the heap, and the merge resumed from there: a
    /// waiting object opens on its first entry once the walk reaches its bound.
    /// </summary>
    private async ValueTask PlaceAsync(CancellationToken cancellationToken)
    {
        Window[] windows = _windows!;
        _live = 0;
        _first = true;
        for (int s = 0; s < _slots.Length; s++)
        {
            Slot slot = _slots[s];
            slot.Live = slot.Walked
                && windows[s].Cut < windows[s].Count
                && await slot.Cursor!.SeekRankAsync(windows[s].Cut, cancellationToken).ConfigureAwait(false);
            if (slot.Live)
            {
                Push(s);
            }
        }
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

        // Every open cursor on the current key leaves the heap before any of them moves, the first
        // one's key standing for the current key until then.
        int moved = 0;
        while (_live > 0 && (moved == 0 || CompareKeys(_heap[0], _moved[0]) == 0))
        {
            _moved[moved++] = PopTop();
        }

        for (int i = 0; i < moved; i++)
        {
            Slot slot = _slots[_moved[i]];
            slot.Live = await slot.Cursor!.NextKeyAsync(cancellationToken).ConfigureAwait(false);
            if (slot.Live)
            {
                Push(_moved[i]);
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
        if (_windows is not null)
        {
            ArrayPool<Window>.Shared.Return(_windows);
            _windows = null;
        }

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

        // The current entry is the top's: its cursor moves on and sinks to its place, or leaves.
        Slot slot = _slots[_current];
        slot.Live = await slot.Cursor!.NextAsync(cancellationToken).ConfigureAwait(false);
        if (slot.Live)
        {
            SiftDown(0);
        }
        else
        {
            PopTop();
        }

        await ResolveAsync(cancellationToken).ConfigureAwait(false);
        return Choose();
    }

    /// <summary>The key as the bounds encode it: the tuple's row encoding for a composite key.</summary>
    private byte[] Encoded(FilterLiteral key) => _key.IsComposite ? key.BytesValue.ToArray() : _key.Encode([key]);

    /// <summary>Opens and positions every waiting object whose minimum is at or below the smallest
    /// key an open cursor holds, so none that could hold the next key stays closed.</summary>
    private async ValueTask ResolveAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            int next = FirstWaiting(out int nextLevel);
            if (next < 0 || (_bounded && _live > 0 && !MayHoldSmallest(next)))
            {
                return;
            }

            _next[nextLevel]++;
            Slot slot = _slots[next];
            slot.Walked = true;
            KeyCursor cursor = await CursorOfAsync(slot, cancellationToken).ConfigureAwait(false);
            slot.Live = _first
                ? await cursor.SeekFirstAsync(cancellationToken).ConfigureAwait(false)
                : await cursor.SeekAsync(_target, _op, cancellationToken).ConfigureAwait(false);
            if (slot.Live)
            {
                Push(next);
            }
        }
    }

    /// <summary>
    /// The first waiting object of every level, by bound then as the version lists them, and its
    /// level; -1 when none waits. The objects of a level wait in the order of their bounds, so the
    /// first waiting one of each level is the only one of it that can be next.
    /// </summary>
    private int FirstWaiting(out int level)
    {
        int next = -1;
        level = -1;
        for (int l = 0; l < _levels.Length; l++)
        {
            List<int> ordered = _levels[l];
            if (_next[l] < ordered.Count && (next < 0 || Before(ordered[_next[l]], next)))
            {
                next = ordered[_next[l]];
                level = l;
            }
        }

        return next;
    }

    /// <summary>
    /// Whether the object of <paramref name="slot"/> may hold a key at or below the smallest key an
    /// open cursor holds, which the heap's top holds: its bound against that key, encoded as the
    /// bounds are, into a buffer the walk keeps.
    /// </summary>
    private bool MayHoldSmallest(int slot)
    {
        KeyCursor top = _slots[_heap[0]].Cursor!;
        ReadOnlySpan<byte> smallest;
        if (_key.IsComposite)
        {
            smallest = top.KeyBytes;
        }
        else
        {
            // The length first: the encoding may replace the buffer with a larger one.
            int length = _key.EncodeCurrent(top, ref _smallest);
            smallest = _smallest.AsSpan(0, length);
        }

        return _slots[slot].Bound.AsSpan().SequenceCompareTo(smallest) <= 0;
    }

    /// <summary>Whether a waiting object opens before another: by bound, then as the version lists them.</summary>
    private bool Before(int left, int right)
    {
        int order = _slots[left].Bound.AsSpan().SequenceCompareTo(_slots[right].Bound);
        return order < 0 || (order == 0 && left < right);
    }

    /// <summary>The place of the last object of a level whose minimum is at or below <paramref name="sought"/>, or 0.</summary>
    private int LastAtOrBelow(List<int> ordered, byte[] sought)
    {
        int low = 0;
        int high = ordered.Count;
        while (low < high)
        {
            int middle = (int)((uint)(low + high) >> 1);
            if (_slots[ordered[middle]].Bound.AsSpan().SequenceCompareTo(sought) <= 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return Math.Max(low - 1, 0);
    }

    /// <summary>Unpositions every object for a new walk.</summary>
    private void Restart()
    {
        foreach (Slot slot in _slots)
        {
            slot.Live = false;
            slot.Walked = false;
        }

        _live = 0;
        _current = -1;
    }

    /// <summary>
    /// The order of two open cursors' keys: a byte key compared where the cursors lend it, rather
    /// than copied into a literal on every comparison.
    /// </summary>
    private int CompareKeys(int left, int right)
    {
        KeyCursor a = _slots[left].Cursor!;
        KeyCursor b = _slots[right].Cursor!;
        return a.KeyKind == FilterLiteralKind.Bytes
            ? a.KeyBytes.SequenceCompareTo(b.KeyBytes)
            : KeyCursor.Compare(a.Key, b.Key);
    }

    /// <summary>Whether an open cursor comes before another: by key, a tie to the earlier object.</summary>
    private bool Less(int left, int right)
    {
        int order = CompareKeys(left, right);
        return order < 0 || (order == 0 && left < right);
    }

    private void Push(int slot)
    {
        int at = _live++;
        while (at > 0)
        {
            int parent = (at - 1) >> 1;
            if (!Less(slot, _heap[parent]))
            {
                break;
            }

            _heap[at] = _heap[parent];
            at = parent;
        }

        _heap[at] = slot;
    }

    private int PopTop()
    {
        int top = _heap[0];
        _heap[0] = _heap[--_live];
        if (_live > 0)
        {
            SiftDown(0);
        }

        return top;
    }

    private void SiftDown(int at)
    {
        int slot = _heap[at];
        while (true)
        {
            int child = (2 * at) + 1;
            if (child >= _live)
            {
                break;
            }

            if (child + 1 < _live && Less(_heap[child + 1], _heap[child]))
            {
                child++;
            }

            if (!Less(_heap[child], slot))
            {
                break;
            }

            _heap[at] = _heap[child];
            at = child;
        }

        _heap[at] = slot;
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

    /// <summary>The live cursor holding the smallest key, the heap's top; ties go to the earlier
    /// object, so that a walk is a function of the version and not of a race.</summary>
    private bool Choose()
    {
        _current = _live > 0 ? _heap[0] : -1;
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

        /// <summary>Its cursor is on an entry of the current walk, and in the heap.</summary>
        internal bool Live { get; set; }

        /// <summary>It was positioned by the current walk, rather than only opened to rank a key.</summary>
        internal bool Walked { get; set; }
    }

    /// <summary>An object's part in a selection: the window of its ranks still candidates.</summary>
    private struct Window
    {
        /// <summary>The object's entries, once it takes part.</summary>
        internal long Count;

        /// <summary>Its first candidate rank.</summary>
        internal long Low;

        /// <summary>One past its last candidate rank.</summary>
        internal long High;

        /// <summary>Its bound for the round's pivot: its entries before it.</summary>
        internal long Cut;

        /// <summary>Its candidates.</summary>
        internal readonly long Width => High - Low;
    }
}
