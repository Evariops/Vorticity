// The ordered walk across a dataset's objects - docs/13-dataset.md §6.6: "a k-way merge of the
// level-0 objects, through their runs, and of the levels", at a cost of "≤ 8 + L cursors, bounded".
//
// THE COUNT IS THE CLAIM, so `Cursors` reports it and a test reads it -- and it is ONE PER OBJECT,
// of every level. §6.6's "≤ 8 + L" needs what `KeyOrderedMerge` does for `InKeyOrder` since step
// 39d: an object opened only once it could hold the next key, so that a key-disjoint level costs one
// cursor. A cursor adds the SEEK, which would have to reach, in each level above 0, the one object
// that can hold the sought key rather than open every object below it. Not done, and surfaced
// rather than asserted: this number is what that work will be judged by.
//
// A LINEAR SCAN, NOT A HEAP, and deliberately. Choosing the smallest of k keys costs k comparisons
// here and log k with a heap, and k is bounded by a small constant; a heap would add the bookkeeping
// of re-sifting a cursor that advanced, for a saving that starts to matter somewhere past thirty
// cursors, which §5's invariant says never happens. The comparator is `KeyCursor.Compare`, the
// library's own total order, so a merge cannot disagree with the runs it merges.
//
// AN OBJECT WITHOUT A KEY SOURCE IS A REFUSAL, not a skip. A dataset may hold a file it did not
// write (§3's import) with no run on the clustering key and no sorted column to stand in; a walk
// that quietly left its rows out would answer a question nobody asked. So the open fails, names the
// object, and says what would fix it -- and every other reader of the dataset is unaffected, because
// a scan reads objects rather than keys.
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
    private readonly ObjectLease[] _leases;
    private readonly KeyCursor[] _cursors;
    private readonly ObjectEntry[] _objects;
    private readonly bool[] _live;
    private int _current = -1;
    private bool _disposed;

    private DatasetKeyCursor(ObjectLease[] leases, KeyCursor[] cursors, ObjectEntry[] objects)
    {
        _leases = leases;
        _cursors = cursors;
        _objects = objects;
        _live = new bool[cursors.Length];
    }

    /// <summary>How many cursors the walk holds open — §6.6's bounded number.</summary>
    public int Cursors => _cursors.Length;

    /// <summary>Whether the cursor is positioned on an entry.</summary>
    public bool IsValid => !_disposed && _current >= 0;

    /// <summary>The current entry's key.</summary>
    /// <exception cref="InvalidOperationException">The cursor is not positioned.</exception>
    public FilterLiteral Key => _cursors[Positioned()].Key;

    /// <summary>The current entry's key as bytes, empty for a key whose values are not bytes.</summary>
    /// <exception cref="InvalidOperationException">The cursor is not positioned.</exception>
    public ReadOnlySpan<byte> KeyBytes => _cursors[Positioned()].KeyBytes;

    /// <summary>The current entry's row <em>within its own object</em>.</summary>
    /// <exception cref="InvalidOperationException">The cursor is not positioned.</exception>
    public long Row => _cursors[Positioned()].Row;

    /// <summary>The object the current entry lives in.</summary>
    /// <exception cref="InvalidOperationException">The cursor is not positioned.</exception>
    public ObjectEntry Object => _objects[Positioned()];

    /// <summary>Opens one cursor per object of <paramref name="dataset"/>'s current version.</summary>
    /// <param name="dataset">The dataset, which must declare a clustering key (§4.1).</param>
    /// <param name="cancellationToken">Cancels the opens.</param>
    /// <returns>The cursor, unpositioned; seek it first.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="dataset"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The dataset declares no clustering key.</exception>
    /// <exception cref="VortexUnsupportedException">An object has no source for the key.</exception>
    public static async ValueTask<DatasetKeyCursor> OpenAsync(
        VortexDataset dataset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ClusteringKey key = dataset.Key ?? throw new InvalidOperationException(
            "A key-ordered walk needs a clustering key; this dataset is ordered by row position " +
            "(13 §4.1), whose order a scan already delivers.");

        List<ObjectLease> leases = [];
        List<KeyCursor> cursors = [];
        List<ObjectEntry> objects = [];
        try
        {
            await foreach (PositionedObject held in dataset
                .WalkAsync(null, 0, long.MaxValue, null, cancellationToken).ConfigureAwait(false))
            {
                ObjectLease lease = await dataset
                    .RentAsync(held.Entry.Key, cancellationToken).ConfigureAwait(false);
                leases.Add(lease);
                KeyCursor? cursor = await key.TryOpenAsync(lease.File, cancellationToken).ConfigureAwait(false);
                if (cursor is null)
                {
                    throw new VortexUnsupportedException(
                        held.Entry.Key,
                        "clustering key",
                        "The object has no run on the clustering key and no sorted column to stand " +
                        "in, so a key-ordered walk would leave its rows out. Rewrite it through the " +
                        "dataset, or index it (13 §6.1's mandatory run).");
                }

                cursors.Add(cursor);
                objects.Add(held.Entry);
            }
        }
        catch
        {
            await ReleaseAsync([.. leases], [.. cursors]).ConfigureAwait(false);
            throw;
        }

        return new DatasetKeyCursor([.. leases], [.. cursors], [.. objects]);
    }

    /// <summary>Positions on the smallest key of the dataset.</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>Whether any object holds an entry.</returns>
    public async ValueTask<bool> SeekFirstAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        for (int i = 0; i < _cursors.Length; i++)
        {
            _live[i] = await _cursors[i].SeekFirstAsync(cancellationToken).ConfigureAwait(false);
        }

        return Choose();
    }

    /// <summary>Positions relative to <paramref name="key"/>, as every object's cursor would.</summary>
    /// <param name="key">The sought key, in the key's domain.</param>
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
        SeekOp each = op == SeekOp.Exact ? SeekOp.AtOrAfter : op;
        for (int i = 0; i < _cursors.Length; i++)
        {
            _live[i] = await _cursors[i].SeekAsync(key, each, cancellationToken).ConfigureAwait(false);
        }

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
        _live[_current] = await _cursors[_current].NextAsync(cancellationToken).ConfigureAwait(false);
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
        await ReleaseAsync(_leases, _cursors).ConfigureAwait(false);
    }

    /// <summary>The live cursor holding the smallest key; ties go to the earlier object.</summary>
    /// <remarks>
    /// The tie-break is the tree's own order, so two objects holding the same key are walked in the
    /// order the dataset holds them and a walk is a function of the version, not of a race.
    /// </remarks>
    private bool Choose()
    {
        _current = -1;
        for (int i = 0; i < _cursors.Length; i++)
        {
            if (!_live[i])
            {
                continue;
            }

            if (_current < 0 || KeyCursor.Compare(_cursors[i].Key, _cursors[_current].Key) < 0)
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

    private static async ValueTask ReleaseAsync(
        IReadOnlyList<ObjectLease> leases, IReadOnlyList<KeyCursor> cursors)
    {
        foreach (KeyCursor cursor in cursors)
        {
            await cursor.DisposeAsync().ConfigureAwait(false);
        }

        foreach (ObjectLease lease in leases)
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
    }
}
