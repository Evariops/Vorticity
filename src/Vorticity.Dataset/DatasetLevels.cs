using System;
using System.Collections.Generic;

namespace Vorticity.Dataset;

/// <summary>
/// The trees of one version of a dataset, one per level. Levels hold the lookup bound: at most
/// eight objects in level 0 and key-disjoint objects in every level above, so a lookup by key
/// touches at most eight plus the level count. Empty levels keep their place, since a level's
/// number is its meaning. A dataset compacted by periods keeps one level above 0 whose objects
/// overlap, its late level (<see cref="PeriodLevels"/>), under a ceiling as level 0 is.
/// </summary>
internal sealed class DatasetLevels
{
    private static readonly DatasetLevels None = new DatasetLevels([], null, byPeriods: false);
    private static readonly DatasetLevels NoneByPeriods = new DatasetLevels([], null, byPeriods: true);
    private readonly DatasetTree[] _levels;

    // Where each level's last job stopped, by level; null while no level records one, which is
    // every version of a dataset compacted by its largest objects.
    private readonly ReadOnlyMemory<byte>[]? _pointers;

    private DatasetLevels(DatasetTree[] levels, ReadOnlyMemory<byte>[]? pointers, bool byPeriods)
    {
        _levels = levels;
        _pointers = pointers;
        ByPeriods = byPeriods;
    }

    /// <summary>A dataset with nothing in it.</summary>
    public static DatasetLevels Empty => None;

    /// <summary>Whether the dataset is compacted by periods, and so keeps a late level.</summary>
    public bool ByPeriods { get; }

    /// <summary>
    /// Whether a level of a dataset with a clustering key holds objects each written in the key's
    /// order, null keys last: every level above 0, which only a merge on the key writes into, since a
    /// plan refuses tiered levels for such a dataset. Level 0 holds what appends wrote, in any order.
    /// Off the key, a level's objects are in no order of it.
    /// </summary>
    public static bool InKeyOrder(int level) => level > 0;

    /// <summary>
    /// Whether no two objects of a level overlap on the clustering key, so that its objects follow
    /// each other in the order of their entries: every level above 0 but a dataset of periods' late
    /// level, whose sorted objects overlap as level 0's do. A read by key opens at most one object of
    /// such a level at a time.
    /// </summary>
    public bool IsDisjoint(int level) => level > 0 && !(ByPeriods && level == PeriodLevels.Late);

    /// <summary>How many levels the version names; level numbers run from 0 to this minus one.</summary>
    public int Count => _levels.Length;

    /// <summary>The tree of one level, empty for a level this version does not name.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is negative.</exception>
    public DatasetTree this[int level]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(level);
            return level < _levels.Length ? _levels[level] : DatasetTree.Empty;
        }
    }

    /// <summary>The data objects every level holds.</summary>
    public long Entries
    {
        get
        {
            long entries = 0;
            foreach (DatasetTree tree in _levels)
            {
                entries += tree.Entries;
            }

            return entries;
        }
    }

    /// <summary>Their rows.</summary>
    public long Rows
    {
        get
        {
            long rows = 0;
            foreach (DatasetTree tree in _levels)
            {
                rows += tree.Rows;
            }

            return rows;
        }
    }

    /// <summary>Whether every level is empty.</summary>
    public bool IsEmpty => Entries == 0;

    /// <summary>The levels a header names.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="header"/> is null.</exception>
    public static DatasetLevels Of(CommitHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        int highest = -1;
        foreach (CommitLevel level in header.Levels)
        {
            if (level.Level > highest)
            {
                highest = level.Level;
            }
        }

        bool byPeriods = header.Lateness is not null;
        if (highest < 0)
        {
            return byPeriods ? NoneByPeriods : None;
        }

        DatasetTree[] trees = new DatasetTree[highest + 1];
        Array.Fill(trees, DatasetTree.Empty);
        ReadOnlyMemory<byte>[]? pointers = null;
        foreach (CommitLevel level in header.Levels)
        {
            if (level.Top.Exists)
            {
                trees[level.Level] = new DatasetTree(level.Top, level.Depth, level.Entries, level.Rows);
                if (!level.Pointer.IsEmpty)
                {
                    pointers ??= new ReadOnlyMemory<byte>[highest + 1];
                    pointers[level.Level] = level.Pointer;
                }
            }
        }

        return new DatasetLevels(trees, pointers, byPeriods);
    }

    /// <summary>These levels with one of them replaced.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is negative.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="tree"/> is null.</exception>
    public DatasetLevels With(int level, DatasetTree tree)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        ArgumentNullException.ThrowIfNull(tree);
        DatasetTree[] trees = new DatasetTree[Math.Max(_levels.Length, level + 1)];
        Array.Fill(trees, DatasetTree.Empty);
        _levels.CopyTo(trees, 0);
        trees[level] = tree;
        return new DatasetLevels(trees, _pointers, ByPeriods);
    }

    /// <summary>
    /// The tree key the level's last job stopped at, which its next job starts past under a round
    /// robin; empty when no job of the level recorded one. A header records it with its level, so a
    /// level that empties forgets it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is negative.</exception>
    public ReadOnlyMemory<byte> PointerOf(int level)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        return _pointers is { } pointers && level < pointers.Length ? pointers[level] : default;
    }

    /// <summary>These levels with where one level's last job stopped moved to <paramref name="key"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is negative.</exception>
    public DatasetLevels WithPointer(int level, ReadOnlyMemory<byte> key)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        ReadOnlyMemory<byte>[] pointers = new ReadOnlyMemory<byte>[Math.Max(_pointers?.Length ?? 0, level + 1)];
        _pointers?.CopyTo(pointers, 0);
        pointers[level] = key;
        return new DatasetLevels(_levels, pointers, ByPeriods);
    }

    /// <summary>
    /// How far the version is from the level-0 invariant: the objects level 0 holds above the
    /// ceiling, zero when the invariant holds. A lag degrades the read bound; it is reported, never
    /// refused, since compaction is the caller's background job and a library never stalls a writer.
    /// </summary>
    public long LagAtLevelZero(int ceiling = DefaultLevelZeroCeiling)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ceiling);
        return Math.Max(this[0].Entries - ceiling, 0);
    }

    /// <summary>What level 0 may hold before the read bound degrades.</summary>
    public const int DefaultLevelZeroCeiling = 8;

    /// <summary>The levels a header should carry, in order, skipping the empty ones.</summary>
    public IEnumerable<(int Level, DatasetTree Tree)> Occupied()
    {
        for (int level = 0; level < _levels.Length; level++)
        {
            if (!_levels[level].IsEmpty)
            {
                yield return (level, _levels[level]);
            }
        }
    }
}
