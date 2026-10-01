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
/// objects' own cursors merged, the open ones in a heap by their key, so that a step costs the
/// logarithm of the cursors open rather than a pass over every object.
/// </summary>
/// <remarks>
/// <para>
/// On the clustering key an object's cursor opens only once it could hold the next key. Walking
/// up, its tree key is an exact lower bound on its keys. Walking down, the levels above 0 are
/// key-disjoint, so the tree key of the next object of its level is a strict upper bound on them;
/// level 0's objects, which nothing bounds from above, open at the seek. On any other column every
/// object's cursor opens at the first seek. The heap is a min-heap walking up and a max-heap
/// walking down, and a step against its direction re-seeks every object at the current entry, as
/// the core's merge of runs does: the entries are ordered by key, then by the dataset's row, so the
/// entry just past the current one is known whatever the objects' cursors did before.
/// </para>
/// <para>
/// The objects are found in their levels' trees as the walk reaches them: a seek goes down one path
/// of each level, and a step reads a page only when it crosses into one, so that a seek costs the
/// depth of the trees whatever the number of objects. The pages and the objects' entries are parsed
/// once per version and shared by its walks; an object's state in a walk is the cursor's own, made
/// once the walk reaches it.
/// </para>
/// </remarks>
internal sealed partial class DatasetKeyCursor : IKeyWalker
{
    private readonly DatasetSnapshot _version;
    private readonly ClusteringKey _key;
    private readonly bool _bounded;
    private readonly bool _distinct;
    private readonly bool _indexes;

    // Per level, where the walk stands among its objects; per leaf the walk read, the state of its
    // objects, each made once the walk reaches it; and the objects reached, whose state a new walk
    // resets.
    private readonly LevelWalk[] _levels;
    private readonly Dictionary<ParsedPage, Slot?[]> _leaves = [];
    private readonly List<Slot> _reached = [];

    // The objects whose cursor is on an entry, a binary heap by key in the walk's direction, a tie
    // going to the earlier object walking up and to the later one walking down, so that the
    // walk's entry is the top's.
    private Slot[] _heap = [];
    private int _live;

    // +1 while the walk goes up and the heap is a min-heap, -1 while it goes down and is a max-heap.
    private int _direction = 1;

    // The objects a distinct step moves past the current key, gathered before any of them moves: at
    // most the heap, whose length it keeps.
    private Slot[] _moved = [];

    // The top's key, row-encoded, which the bounds of the objects still waiting are compared with.
    private byte[] _topKey = [];

    // How the walk positions an object it opens: on its first or last entry, or relative to the
    // target, the objects before the pivot by one operator and the others by another. The pivot is
    // the object a step against the heap's direction starts from; it steps rather than seeks.
    private Anchor _anchor;
    private FilterLiteral _target;
    private SeekOp _beforePivot;
    private SeekOp _fromPivot;
    private Slot? _pivot;
    private Slot? _current;
    private bool _disposed;

    private DatasetKeyCursor(DatasetSnapshot version, ClusteringKey key, bool bounded, bool distinct, bool indexes)
    {
        _version = version;
        _key = key;
        _bounded = bounded;
        _distinct = distinct;
        _indexes = indexes;
        _levels = new LevelWalk[version.Levels.Count];
        for (int level = 0; level < _levels.Length; level++)
        {
            _levels[level] = new LevelWalk(this, level, version.Levels[level]);
        }
    }

    /// <summary>Where the objects a walk opens are positioned.</summary>
    private enum Anchor : byte
    {
        /// <summary>On their first entry.</summary>
        First,

        /// <summary>On their last entry.</summary>
        Last,

        /// <summary>Relative to the walk's target key.</summary>
        Target,
    }

    /// <summary>
    /// How many objects' cursors the walk has opened. A seek on the clustering key opens at most
    /// level 0's objects and one per level above it; a walk opens the rest as it reaches them.
    /// </summary>
    public int Cursors { get; private set; }

    /// <summary>Whether the cursor is positioned on an entry.</summary>
    public bool IsValid => !_disposed && _current is not null;

    /// <summary>Whether the entries carry rows, which every object's cursor does: the merge opens them over sources with rows.</summary>
    public bool HasRows => _current is null || _current.Cursor!.HasRows;

    /// <summary>The current entry's key.</summary>
    /// <exception cref="InvalidOperationException">The cursor is not positioned.</exception>
    public FilterLiteral Key => Positioned().Cursor!.Key;

    /// <summary>The current entry's key as bytes, empty for a key whose values are not bytes.</summary>
    /// <exception cref="InvalidOperationException">The cursor is not positioned.</exception>
    public ReadOnlySpan<byte> KeyBytes => Positioned().Cursor!.KeyBytes;

    /// <summary>The current entry's row among the dataset's, counted as a scan of the version counts them.</summary>
    /// <exception cref="InvalidOperationException">The cursor is not positioned.</exception>
    public long Row
    {
        get
        {
            Slot slot = Positioned();
            return slot.FirstRow + slot.Entry.Deletions.Logical(slot.Cursor!.Row);
        }
    }

    /// <summary>The object the current entry lives in.</summary>
    /// <exception cref="InvalidOperationException">The cursor is not positioned.</exception>
    public ObjectEntry Object => Positioned().Entry;

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
        DatasetSnapshot version = dataset.Snapshot;
        ClusteringKey key = version.Schema.Key ?? throw new InvalidOperationException(
            "A key-ordered walk needs a clustering key; this dataset is ordered by row position, " +
            "whose order a scan already delivers.");
        return OpenAsync(version, key, bounded: true, distinct: false, indexes: true, cancellationToken);
    }

    /// <summary>
    /// Prepares a walk of one column over the objects of <paramref name="version"/>: the clustering
    /// key's own walk when the column is that key, a walk that opens every object otherwise.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="path"/> names no column of the dataset.</exception>
    internal static ValueTask<DatasetKeyCursor> OpenAsync(
        VortexDataset dataset, DatasetSnapshot version, string path, bool distinct, bool indexes, CancellationToken cancellationToken)
    {
        ClusteringKey? key = version.Schema.Key;
        bool clustering = key is { IsComposite: false } && string.Equals(key.Paths[0], path, StringComparison.Ordinal);
        ClusteringKey walked = clustering ? key! : ClusteringKey.For([path], version.Schema.DType)!;
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
            KeyPlan? plan = null;
            ObjectLease lease = await version.RentAsync(held.Entry, cancellationToken).ConfigureAwait(false);
            await using (lease.ConfigureAwait(false))
            {
                // An object of an earlier schema holds the column under the name it had then, and
                // one that lacks the column holds only nulls, which are no entries.
                string? named = version.Schema.ColumnsOf(lease.File.DType, held.Entry.Key) is { } columns
                    ? columns.SourcePath(path)
                    : path;
                if (named is not null)
                {
                    KeyCursorBuilder keys = lease.File.Keys(named);
                    plan = await (indexes ? keys : keys.WithSource(KeySourceKind.SortedColumn))
                        .ExplainAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            if (plan is null)
            {
                continue;
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
        await BeginAsync(1, Anchor.First, null, inclusive: true, cancellationToken).ConfigureAwait(false);
        await ResolveAsync(cancellationToken).ConfigureAwait(false);
        return Choose();
    }

    /// <summary>
    /// Positions on the largest key's last entry, walking down from it; on a distinct walk, on that
    /// key's first entry.
    /// </summary>
    public async ValueTask<bool> SeekLastAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await BeginAsync(-1, Anchor.Last, null, inclusive: true, cancellationToken).ConfigureAwait(false);
        await ResolveAsync(cancellationToken).ConfigureAwait(false);
        return Choose() && (!_distinct || await FirstOfKeyAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Positions relative to a key, given in the key's domain: the row encoding of the tuple for a
    /// composite key. <see cref="SeekOp.AtOrBefore"/> and <see cref="SeekOp.Before"/> walk down from
    /// where they land, and on a distinct walk land on that key's first entry.
    /// </summary>
    public async ValueTask<bool> SeekAsync(
        FilterLiteral key, SeekOp op = SeekOp.AtOrAfter, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (op is SeekOp.AtOrBefore or SeekOp.Before)
        {
            return await SeekDownAsync(key, op, cancellationToken).ConfigureAwait(false)
                && (!_distinct || await FirstOfKeyAsync(cancellationToken).ConfigureAwait(false));
        }

        // Every cursor goes to the lower bound even for an exact seek, exactness being decided on
        // the winner afterwards: seeking each one exactly would invalidate the cursors whose object
        // does not hold the key, and truncate their rows out of the rest of the merge.
        if (!await SeekUpAsync(key, op == SeekOp.Exact ? SeekOp.AtOrAfter : op, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        if (op == SeekOp.Exact && KeyCursor.Compare(Key, key) != 0)
        {
            _current = null;
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
            _current = null;
            return false;
        }

        if (rank >= _version.Levels.Entries)
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
    /// would not open it. On any other column every object takes part. The walk goes up from the
    /// entry it lands on.
    /// </remarks>
    internal async ValueTask<bool> SelectAsync(long rank, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await BeginAsync(1, Anchor.First, null, inclusive: true, cancellationToken).ConfigureAwait(false);
        long held = 0;
        for (LevelWalk? level = Waiting(); level is not null && (held <= rank || !_bounded); level = Waiting())
        {
            held += await JoinAsync(level, cancellationToken).ConfigureAwait(false);
        }

        while (await RoundsAsync(rank, cancellationToken).ConfigureAwait(false))
        {
            await PlaceAsync(cancellationToken).ConfigureAwait(false);
            bool joined = false;
            for (LevelWalk? level = Waiting(); level is not null && MayHoldNext(level); level = Waiting())
            {
                await JoinAsync(level, cancellationToken).ConfigureAwait(false);
                joined = true;
            }

            if (!joined)
            {
                if (_live > 0 && _heap[0].FirstRow < 0)
                {
                    _heap[0].FirstRow = await FirstRowAsync(_heap[0], cancellationToken).ConfigureAwait(false);
                }

                return Choose();
            }
        }

        Restart();
        return false;
    }

    /// <summary>An object takes part in a selection: the next of its level, opened, and counted.</summary>
    private async ValueTask<long> JoinAsync(LevelWalk level, CancellationToken cancellationToken)
    {
        Slot joined = await level.TakeNextAsync(cancellationToken).ConfigureAwait(false);
        joined.Walked = true;
        KeyCursor? cursor = await CursorOfAsync(joined, cancellationToken).ConfigureAwait(false);
        joined.Count = cursor?.EntryCount ?? 0;
        return joined.Count;
    }

    /// <summary>
    /// The rounds of a selection over the objects taking part, which leave each one's bound for the
    /// entry of rank <paramref name="rank"/> in its <see cref="Slot.Cut"/>.
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
        List<Slot> slots = _reached;
        for (int s = 0; s < slots.Count; s++)
        {
            Slot slot = slots[s];
            slot.Low = 0;
            slot.High = slot.Walked ? slot.Count : 0;
            slot.Cut = 0;
        }

        for (int round = 0; ; round++)
        {
            _live = 0;
            long ruledOut = 0;
            long candidates = 0;
            Slot? widest = null;
            for (int s = 0; s < slots.Count; s++)
            {
                Slot slot = slots[s];
                long width = slot.Width;
                ruledOut += slot.Low;
                candidates += width;
                widest = width > 0 && (widest is null || width > widest.Width) ? slot : widest;
            }

            if (widest is null || rank < ruledOut || rank >= ruledOut + candidates)
            {
                return false;
            }

            Slot pivot = widest;
            if (round % 3 == 0)
            {
                long width = widest.Width;
                long offset = Math.Clamp((long)((double)(rank - ruledOut) * width / candidates), 0, width - 1);
                widest.Cut = widest.Low + offset;
                await widest.Cursor!.SeekRankAsync(widest.Cut, cancellationToken).ConfigureAwait(false);
            }
            else if (round % 3 == 1)
            {
                // The merge's heap orders the windows by their first keys, and gives them back until
                // their widths pass the rank.
                for (int s = 0; s < slots.Count; s++)
                {
                    Slot slot = slots[s];
                    if (slot.Width > 0
                        && await slot.Cursor!.SeekRankAsync(slot.Low, cancellationToken).ConfigureAwait(false))
                    {
                        Push(slot);
                    }
                }

                long counted = 0;
                while (_live > 0)
                {
                    pivot = PopTop();
                    if (counted + pivot.Width > rank - ruledOut)
                    {
                        break;
                    }

                    counted += pivot.Width;
                }

                pivot.Cut = pivot.Low + Math.Clamp(rank - ruledOut - counted, 0, pivot.Width - 1);
                await pivot.Cursor!.SeekRankAsync(pivot.Cut, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // The merge's heap orders the middles, and gives them back smallest first until half
                // the candidates are counted.
                for (int s = 0; s < slots.Count; s++)
                {
                    Slot slot = slots[s];
                    if (slot.Width > 0)
                    {
                        slot.Cut = slot.Low + (slot.Width >> 1);
                        if (await slot.Cursor!.SeekRankAsync(slot.Cut, cancellationToken).ConfigureAwait(false))
                        {
                            Push(slot);
                        }
                    }
                }

                long counted = 0;
                while (_live > 0 && 2 * counted < candidates)
                {
                    pivot = PopTop();
                    counted += pivot.Width;
                }
            }

            KeyCursor lender = pivot.Cursor!;
            long before = 0;
            for (int s = 0; s < slots.Count; s++)
            {
                Slot slot = slots[s];
                if (slot != pivot)
                {
                    long cut = slot.Low;
                    if (slot.Width > 0)
                    {
                        long ranked = await slot.Cursor!.RankOfAsync(lender, upper: Order(slot, pivot) < 0, cancellationToken).ConfigureAwait(false);
                        cut = Math.Clamp(ranked, slot.Low, slot.High);
                    }

                    slot.Cut = cut;
                }

                before += slot.Cut;
            }

            if (before == rank)
            {
                return true;
            }

            for (int s = 0; s < slots.Count; s++)
            {
                Slot slot = slots[s];
                if (before < rank)
                {
                    slot.Low = Math.Max(slot.Low, slot.Cut);
                }
                else
                {
                    slot.High = Math.Min(slot.High, slot.Cut);
                }
            }

            if (before < rank)
            {
                pivot.Low = pivot.Cut + 1;
            }
        }
    }

    /// <summary>
    /// Every object taking part on its bound, in the heap, and the merge resumed from there: a
    /// waiting object opens on its first entry once the walk reaches its bound.
    /// </summary>
    private async ValueTask PlaceAsync(CancellationToken cancellationToken)
    {
        _live = 0;
        _anchor = Anchor.First;
        foreach (Slot slot in _reached)
        {
            slot.Live = slot.Walked
                && slot.Cut < slot.Count
                && await slot.Cursor!.SeekRankAsync(slot.Cut, cancellationToken).ConfigureAwait(false);
            if (slot.Live)
            {
                Push(slot);
            }
        }
    }

    /// <summary>Moves to the next entry in key order; on a distinct walk, to the next key.</summary>
    public ValueTask<bool> NextAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_current is null)
        {
            return new ValueTask<bool>(false);
        }

        if (_distinct)
        {
            return NextKeyAsync(cancellationToken);
        }

        return _direction > 0 ? StepAsync(cancellationToken) : TurnAsync(cancellationToken);
    }

    /// <summary>
    /// Moves to the previous entry in key order; on a distinct walk, to the previous key's first
    /// entry.
    /// </summary>
    public ValueTask<bool> PrevAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_current is null)
        {
            return new ValueTask<bool>(false);
        }

        if (_distinct)
        {
            return PrevDistinctAsync(cancellationToken);
        }

        return _direction < 0 ? StepAsync(cancellationToken) : TurnAsync(cancellationToken);
    }

    /// <summary>
    /// Moves to the first entry of the next distinct key: walking up, every object positioned on
    /// the current key steps past it, and the others stay where they are; walking down, the walk
    /// turns and seeks past it.
    /// </summary>
    public ValueTask<bool> NextKeyAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_current is null)
        {
            return new ValueTask<bool>(false);
        }

        return _direction > 0 ? PastKeyAsync(cancellationToken) : SeekUpAsync(Key, SeekOp.After, cancellationToken);
    }

    /// <summary>
    /// Moves to the last entry of the previous distinct key: walking down, every object positioned
    /// on the current key steps past it; walking up, the walk turns and seeks before it.
    /// </summary>
    public ValueTask<bool> PrevKeyAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_current is null)
        {
            return new ValueTask<bool>(false);
        }

        return _direction < 0 ? PastKeyAsync(cancellationToken) : SeekDownAsync(Key, SeekOp.Before, cancellationToken);
    }

    /// <summary>
    /// How many entries across the objects have a key below <paramref name="key"/>; an object whose
    /// lower bound is at or above it is counted as none without being opened.
    /// </summary>
    public async ValueTask<long> RankAsync(FilterLiteral key, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[]? sought = _bounded ? Encoded(key) : null;
        long rank = 0;
        foreach (LevelWalk level in _levels)
        {
            if (await level.RootAsync(cancellationToken).ConfigureAwait(false) is { } root)
            {
                rank += await RankUnderAsync(root, level.Number, key, sought, cancellationToken).ConfigureAwait(false);
            }
        }

        return rank;
    }

    /// <summary>
    /// How many entries across the objects share the current key. An object positioned on it counts
    /// its own. Walking up, one that has stepped past it counts the entries between it and its
    /// position, which are all that key; walking down, one that is not on it counts the entries at
    /// or below the key that are not below it. One the walk has not positioned holds none, since it
    /// would have opened before the walk reached a key it could hold.
    /// </summary>
    /// <exception cref="InvalidOperationException">The cursor is not positioned.</exception>
    public async ValueTask<long> KeyCountAsync(CancellationToken cancellationToken = default)
    {
        FilterLiteral key = Key;
        long count = 0;
        foreach (Slot slot in _reached)
        {
            if (slot.Cursor is not { } cursor || !slot.Walked)
            {
                continue;
            }

            if (slot.Live && KeyCursor.Compare(cursor.Key, key) == 0)
            {
                count += await cursor.KeyCountAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (_direction < 0)
            {
                count += await cursor.UpperRankAsync(key, cancellationToken).ConfigureAwait(false)
                    - await cursor.RankAsync(key, cancellationToken).ConfigureAwait(false);
            }
            else if (slot.Live)
            {
                count += KeyCursor.Compare(cursor.Key, key) > 0
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
        _current = null;
        foreach (Slot slot in _reached)
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
    /// A walk over the version's objects, which reads nothing of its trees until a seek: each level's
    /// pages are read as the walk first needs them.
    /// </summary>
    private static ValueTask<DatasetKeyCursor> OpenAsync(
        DatasetSnapshot version, ClusteringKey key, bool bounded, bool distinct, bool indexes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<DatasetKeyCursor>(new DatasetKeyCursor(version, key, bounded, distinct, indexes));
    }

    /// <summary>
    /// The entries below <paramref name="key"/> of every object under <paramref name="page"/> whose
    /// bound is below <paramref name="sought"/>, all of them when there is none: a level's objects
    /// come in the order of their bounds, so the first at or past it ends the count.
    /// </summary>
    private async ValueTask<long> RankUnderAsync(ParsedPage page, int level, FilterLiteral key, byte[]? sought, CancellationToken cancellationToken)
    {
        Slot?[]? slots = page.IsLeaf ? SlotsOf(page) : null;
        long rank = 0;
        for (int i = 0; i < page.Count; i++)
        {
            if (sought is not null && VortexDataset.OrderOf(page.MinKey(i)).Span.SequenceCompareTo(sought) >= 0)
            {
                break;
            }

            if (slots is null)
            {
                ParsedPage child = await page.ChildAsync(i, _version.Pages, cancellationToken).ConfigureAwait(false);
                rank += await RankUnderAsync(child, level, key, sought, cancellationToken).ConfigureAwait(false);
            }
            else if (await CursorOfAsync(SlotOf(page, slots, i, level), cancellationToken).ConfigureAwait(false) is { } cursor)
            {
                rank += await cursor.RankAsync(key, cancellationToken).ConfigureAwait(false);
            }
        }

        return rank;
    }

    /// <summary>The walk's state of a leaf's objects: an array made the first time the walk reads the leaf.</summary>
    private Slot?[] SlotsOf(ParsedPage leaf)
    {
        if (!_leaves.TryGetValue(leaf, out Slot?[]? slots))
        {
            slots = new Slot?[leaf.Count];
            _leaves.Add(leaf, slots);
        }

        return slots;
    }

    /// <summary>The walk's state of entry <paramref name="index"/> of a leaf of <paramref name="level"/>, made the first time the walk reaches it.</summary>
    private Slot SlotOf(ParsedPage leaf, Slot?[] slots, int index, int level)
    {
        if (slots[index] is { } slot)
        {
            return slot;
        }

        slot = new Slot(leaf.ObjectAt(index), leaf.Key(index), level);
        slots[index] = slot;
        _reached.Add(slot);
        return slot;
    }

    /// <summary>The encoded minimum of the object past the place of <paramref name="level"/>; empty off the clustering key, where none is known.</summary>
    private ReadOnlySpan<byte> Bound(LevelWalk level) => _bounded ? level.NextBound : default;

    /// <summary>
    /// One step of the merge in the heap's direction: the object that was chosen moves on, the
    /// others keep their entry.
    /// </summary>
    private async ValueTask<bool> StepAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_current is not { } slot)
        {
            return false;
        }

        // The current entry is the top's: its cursor moves on and sinks to its place, or leaves.
        slot.Live = _direction > 0
            ? await slot.Cursor!.NextAsync(cancellationToken).ConfigureAwait(false)
            : await slot.Cursor!.PrevAsync(cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Steps against the heap's direction: every object is sought again just past the current
    /// entry, the other way. Entries are ordered by key, then by object, so an earlier object's
    /// entries of the current key lie below it and a later one's above it, and the current object
    /// steps from where it is.
    /// </summary>
    private async ValueTask<bool> TurnAsync(CancellationToken cancellationToken)
    {
        FilterLiteral key = Key;
        _pivot = _current;
        _target = key;
        byte[]? sought = _bounded ? Encoded(key) : null;
        if (_direction > 0)
        {
            _beforePivot = SeekOp.AtOrBefore;
            _fromPivot = SeekOp.Before;
            await BeginAsync(-1, Anchor.Target, sought, inclusive: true, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _beforePivot = SeekOp.After;
            _fromPivot = SeekOp.AtOrAfter;
            await BeginAsync(1, Anchor.Target, sought, inclusive: true, cancellationToken).ConfigureAwait(false);
        }

        await ResolveAsync(cancellationToken).ConfigureAwait(false);
        return Choose();
    }

    /// <summary>Positions every object at or after <paramref name="key"/> by <paramref name="op"/>, walking up.</summary>
    private async ValueTask<bool> SeekUpAsync(FilterLiteral key, SeekOp op, CancellationToken cancellationToken)
    {
        _target = key;
        _fromPivot = op;
        _pivot = null;
        await BeginAsync(1, Anchor.Target, _bounded ? Encoded(key) : null, inclusive: true, cancellationToken).ConfigureAwait(false);
        await ResolveAsync(cancellationToken).ConfigureAwait(false);
        return Choose();
    }

    /// <summary>Positions every object at or before <paramref name="key"/> by <paramref name="op"/>, walking down.</summary>
    private async ValueTask<bool> SeekDownAsync(FilterLiteral key, SeekOp op, CancellationToken cancellationToken)
    {
        _target = key;
        _fromPivot = op;
        _pivot = null;
        await BeginAsync(-1, Anchor.Target, _bounded ? Encoded(key) : null, inclusive: op != SeekOp.Before, cancellationToken).ConfigureAwait(false);
        await ResolveAsync(cancellationToken).ConfigureAwait(false);
        return Choose();
    }

    /// <summary>A distinct walk's step down: the previous key's last entry, then that key's first.</summary>
    private async ValueTask<bool> PrevDistinctAsync(CancellationToken cancellationToken) =>
        await PrevKeyAsync(cancellationToken).ConfigureAwait(false)
        && await FirstOfKeyAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// A distinct walk's landing below a key, moved to that key's first entry: a walk down lands on
    /// a key's last entry, and a distinct entry is the key's first.
    /// </summary>
    private ValueTask<bool> FirstOfKeyAsync(CancellationToken cancellationToken) =>
        SeekUpAsync(Key, SeekOp.AtOrAfter, cancellationToken);

    /// <summary>
    /// Moves every object on the current key past it in the heap's direction: each leaves the heap
    /// before any of them moves, the first one's key standing for the current key until then, and
    /// the others stay where they are.
    /// </summary>
    private async ValueTask<bool> PastKeyAsync(CancellationToken cancellationToken)
    {
        int moved = 0;
        while (_live > 0 && (moved == 0 || CompareKeys(_heap[0], _moved[0]) == 0))
        {
            _moved[moved++] = PopTop();
        }

        for (int i = 0; i < moved; i++)
        {
            Slot slot = _moved[i];
            slot.Live = _direction > 0
                ? await slot.Cursor!.NextKeyAsync(cancellationToken).ConfigureAwait(false)
                : await slot.Cursor!.PrevKeyAsync(cancellationToken).ConfigureAwait(false);
            if (slot.Live)
            {
                Push(slot);
            }
        }

        await ResolveAsync(cancellationToken).ConfigureAwait(false);
        return Choose();
    }

    /// <summary>The key as the bounds encode it: the tuple's row encoding for a composite key.</summary>
    private byte[] Encoded(FilterLiteral key) => _key.IsComposite ? key.BytesValue.ToArray() : _key.Encode([key]);

    /// <summary>
    /// Starts a walk in <paramref name="direction"/>: every object unpositioned, and in every level
    /// the objects that may hold an entry on the walk's side of <paramref name="sought"/> waiting,
    /// all of them when there is no key to seek.
    /// </summary>
    /// <param name="direction">+1 to walk up, -1 to walk down.</param>
    /// <param name="anchor">Where the objects the walk opens are positioned.</param>
    /// <param name="sought">The encoded key the walk starts from, or null.</param>
    /// <param name="inclusive">Walking down, whether an object whose minimum is the key itself may hold an entry the walk wants.</param>
    /// <param name="cancellationToken">Cancels the reads of the levels' pages.</param>
    private async ValueTask BeginAsync(int direction, Anchor anchor, byte[]? sought, bool inclusive, CancellationToken cancellationToken)
    {
        Restart();
        _direction = direction;
        _anchor = anchor;
        foreach (LevelWalk level in _levels)
        {
            if (await level.RootAsync(cancellationToken).ConfigureAwait(false) is not { } root)
            {
                continue;
            }

            if (direction > 0 && sought is not null && level.InKeyOrder)
            {
                // Above level 0 the objects are disjoint on the clustering key, so everything before
                // the last one whose minimum is at or below the sought key holds only smaller keys.
                await level.SeekAsync(root, sought, inclusive: true, before: true, cancellationToken).ConfigureAwait(false);
            }
            else if (direction > 0)
            {
                level.Start(root);
            }
            else if (sought is not null)
            {
                // An object whose minimum lies past the sought key holds nothing at or before it,
                // whatever its level.
                await level.SeekAsync(root, sought, inclusive, before: false, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                level.End(root);
            }
        }
    }

    /// <summary>
    /// Opens and positions every waiting object that may hold the walk's next key: walking up, one
    /// whose minimum is at or below the smallest key an open cursor holds; walking down, one whose
    /// limit lies above the largest. None that could hold the next key stays closed. The object of
    /// the entry the walk takes next has its first row counted, the first time it is the one.
    /// </summary>
    private async ValueTask ResolveAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            LevelWalk? level = Waiting();
            if (level is null || (_bounded && _live > 0 && !MayHoldNext(level)))
            {
                break;
            }

            Slot slot = _direction > 0
                ? await level.TakeNextAsync(cancellationToken).ConfigureAwait(false)
                : await level.TakePreviousAsync(cancellationToken).ConfigureAwait(false);
            slot.Walked = true;
            slot.Live = await CursorOfAsync(slot, cancellationToken).ConfigureAwait(false) is { } cursor
                && await PositionAsync(slot, cursor, cancellationToken).ConfigureAwait(false);
            if (slot.Live)
            {
                Push(slot);
            }
        }

        if (_live > 0 && _heap[0].FirstRow < 0)
        {
            _heap[0].FirstRow = await FirstRowAsync(_heap[0], cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Positions an object's cursor as the walk's anchor says.</summary>
    private ValueTask<bool> PositionAsync(Slot slot, KeyCursor cursor, CancellationToken cancellationToken) => _anchor switch
    {
        Anchor.First => cursor.SeekFirstAsync(cancellationToken),
        Anchor.Last => cursor.SeekLastAsync(cancellationToken),
        _ when slot == _pivot => _direction > 0 ? cursor.NextAsync(cancellationToken) : cursor.PrevAsync(cancellationToken),
        _ => cursor.SeekAsync(_target, _pivot is not null && Order(slot, _pivot) < 0 ? _beforePivot : _fromPivot, cancellationToken),
    };

    /// <summary>
    /// The level whose object the walk would open next; null when none waits. A level's objects wait
    /// in the order of their bounds, so walking up the next is the first waiting one of some level,
    /// by bound then by the version's order; walking down, the last waiting one of the level whose
    /// limit is the highest, level 0's, which nothing bounds, first.
    /// </summary>
    private LevelWalk? Waiting()
    {
        LevelWalk? next = null;
        foreach (LevelWalk level in _levels)
        {
            if (_direction > 0)
            {
                // A tree key is the bound, then a uid: their order is that of the bounds, then the
                // version's own.
                if (level.HasNext && (next is null || TreePage.Compare(level.NextKey, next.NextKey) < 0))
                {
                    next = level;
                }
            }
            else if (level.HasPrevious && (next is null || Higher(level, next)))
            {
                next = level;
            }
        }

        return next;
    }

    /// <summary>
    /// Whether the next object of <paramref name="level"/> may hold the walk's next key, against the
    /// key the heap's top holds, encoded as the bounds are into a buffer the walk keeps. Walking up,
    /// its bound must be at or below that key; walking down, its limit must be above it.
    /// </summary>
    private bool MayHoldNext(LevelWalk level)
    {
        if (_direction < 0 && !HasLimit(level))
        {
            return true;
        }

        KeyCursor top = _heap[0].Cursor!;
        ReadOnlySpan<byte> key;
        if (_key.IsComposite)
        {
            key = top.KeyBytes;
        }
        else
        {
            // The length first: the encoding may replace the buffer with a larger one.
            int length = _key.EncodeCurrent(top, ref _topKey);
            key = _topKey.AsSpan(0, length);
        }

        return _direction > 0
            ? Bound(level).SequenceCompareTo(key) <= 0
            : key.SequenceCompareTo(Bound(level)) < 0;
    }

    /// <summary>
    /// Walking down, whether the object past the place of <paramref name="level"/> bounds the keys
    /// of the next one to open, strictly from above: the levels above 0 are key-disjoint. Nothing
    /// bounds them in level 0, past a level's last object, and off the clustering key.
    /// </summary>
    private bool HasLimit(LevelWalk level) => _bounded && level.InKeyOrder && level.HasNext;

    /// <summary>Whether level <paramref name="left"/>'s next object has a higher limit than level <paramref name="right"/>'s.</summary>
    private bool Higher(LevelWalk left, LevelWalk right) =>
        HasLimit(right) && (!HasLimit(left) || Bound(left).SequenceCompareTo(Bound(right)) > 0);

    /// <summary>Unpositions every object the walk reached for a new walk; the others it never positioned.</summary>
    private void Restart()
    {
        foreach (Slot slot in _reached)
        {
            slot.Live = false;
            slot.Walked = false;
        }

        _live = 0;
        _current = null;
    }

    /// <summary>
    /// The order of two open cursors' keys: a byte key compared where the cursors lend it, rather
    /// than copied into a literal on every comparison.
    /// </summary>
    private static int CompareKeys(Slot left, Slot right)
    {
        KeyCursor a = left.Cursor!;
        KeyCursor b = right.Cursor!;
        return a.KeyKind == FilterLiteralKind.Bytes
            ? a.KeyBytes.SequenceCompareTo(b.KeyBytes)
            : KeyCursor.Compare(a.Key, b.Key);
    }

    /// <summary>
    /// The order of two objects among the version's, which is the order of their rows: that of their
    /// tree keys, which no two objects share.
    /// </summary>
    private static int Order(Slot left, Slot right) => TreePage.Compare(left.TreeKey.Span, right.TreeKey.Span);

    /// <summary>Whether an open cursor comes before another in the walk's direction: by key, then by object.</summary>
    private bool Less(Slot left, Slot right)
    {
        int order = CompareKeys(left, right);
        if (order == 0)
        {
            order = Order(left, right);
        }

        return _direction > 0 ? order < 0 : order > 0;
    }

    private void Push(Slot slot)
    {
        if (_live == _heap.Length)
        {
            Array.Resize(ref _heap, Math.Max(8, _heap.Length * 2));
            Array.Resize(ref _moved, _heap.Length);
        }

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

    private Slot PopTop()
    {
        Slot top = _heap[0];
        _heap[0] = _heap[--_live];
        if (_live > 0)
        {
            SiftDown(0);
        }

        return top;
    }

    private void SiftDown(int at)
    {
        Slot slot = _heap[at];
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

    /// <summary>
    /// The object's cursor, opened on first use and kept for the life of the walk; null for an object
    /// of an earlier schema that lacks the column, whose rows are all null there and so no entries.
    /// An object with no key source is refused rather than skipped, since skipping it would
    /// silently leave its rows out of the walk.
    /// </summary>
    private async ValueTask<KeyCursor?> CursorOfAsync(Slot slot, CancellationToken cancellationToken)
    {
        if (slot.Cursor is { } open)
        {
            return open;
        }

        if (slot.Empty)
        {
            return null;
        }

        slot.Lease = await _version.RentAsync(slot.Entry, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> paths = _key.Paths;
        if (_version.Schema.ColumnsOf(slot.Lease.File.DType, slot.Entry.Key) is { } columns)
        {
            if (columns.SourcePaths(paths) is not { } named)
            {
                slot.Empty = true;
                await slot.Lease.DisposeAsync().ConfigureAwait(false);
                slot.Lease = null;
                return null;
            }

            paths = named;
        }

        // An object above level 0 is a merge's, its rows in the clustering key's order with null keys
        // last, so on that key an entry's rank is its row and a deleted row's rank needs no read.
        slot.Cursor = await _key.TryOpenAsync(
                slot.Lease.File, paths, _indexes, slot.Entry.Deletions, ranksAreRows: _bounded && DatasetLevels.InKeyOrder(slot.Level), cancellationToken)
            .ConfigureAwait(false)
            ?? throw new VortexUnsupportedException(
                slot.Entry.Key,
                ComponentKind.Index,
                $"The object has no run on '{string.Join(", ", _key.Paths)}' and no sorted column to stand in, so a " +
                "key-ordered walk would leave its rows out. Rewrite it through the dataset, or index it.");
        Cursors++;
        return slot.Cursor;
    }

    /// <summary>
    /// Takes the live cursor whose key comes first in the walk's direction, the heap's top, as the
    /// walk's entry, its object's first row counted when the walk resolved; ties go by object, so
    /// that a walk is a function of the version and not of a race.
    /// </summary>
    private bool Choose()
    {
        _current = _live > 0 ? _heap[0] : null;
        return _current is not null;
    }

    /// <summary>
    /// Where an object's rows start among the dataset's: past the rows of every object of every level
    /// whose tree key is below its own, which is the order a scan of the version reads them in.
    /// </summary>
    private async ValueTask<long> FirstRowAsync(Slot slot, CancellationToken cancellationToken)
    {
        long rows = 0;
        foreach (LevelWalk level in _levels)
        {
            rows += await level.RowsBeforeAsync(slot.TreeKey, cancellationToken).ConfigureAwait(false);
        }

        return rows;
    }

    private Slot Positioned()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _current ?? throw new InvalidOperationException("The cursor is not positioned on an entry.");
    }

    /// <summary>The walk's state of one object of the version, made once the walk reaches it.</summary>
    private sealed class Slot(ObjectEntry entry, ReadOnlyMemory<byte> treeKey, int level)
    {
        internal ObjectEntry Entry { get; } = entry;

        /// <summary>The key its level's tree orders it by: its bound, then its uid.</summary>
        internal ReadOnlyMemory<byte> TreeKey { get; } = treeKey;

        /// <summary>The level whose tree holds the object.</summary>
        internal int Level { get; } = level;

        /// <summary>Where the object's rows start among the dataset's; negative until the walk first takes one of its entries.</summary>
        internal long FirstRow { get; set; } = -1;

        internal ObjectLease? Lease { get; set; }

        internal KeyCursor? Cursor { get; set; }

        /// <summary>Its cursor is on an entry of the current walk, and in the heap.</summary>
        internal bool Live { get; set; }

        /// <summary>It was positioned by the current walk, rather than only opened to rank a key.</summary>
        internal bool Walked { get; set; }

        /// <summary>The object lacks the column, having been written under an earlier schema: it holds no entry.</summary>
        internal bool Empty { get; set; }

        /// <summary>Its entries, once it takes part in a selection.</summary>
        internal long Count { get; set; }

        /// <summary>Its first rank still a candidate in a selection.</summary>
        internal long Low { get; set; }

        /// <summary>One past its last rank still a candidate.</summary>
        internal long High { get; set; }

        /// <summary>Its bound for the round's pivot: its entries before it.</summary>
        internal long Cut { get; set; }

        /// <summary>Its candidates.</summary>
        internal long Width => High - Low;
    }
}
