using System;
using System.Collections.Generic;

namespace Vorticity.Dataset;

/// <summary>
/// The trees of one version of a dataset, one per level. Levels hold the lookup bound: at most
/// eight objects in level 0 and key-disjoint objects in every level above, so a lookup by key
/// touches at most eight plus the level count. Empty levels keep their place, since a level's
/// number is its meaning.
/// </summary>
internal sealed class DatasetLevels
{
    private static readonly DatasetLevels None = new DatasetLevels([]);
    private readonly DatasetTree[] _levels;

    private DatasetLevels(DatasetTree[] levels) => _levels = levels;

    /// <summary>A dataset with nothing in it.</summary>
    public static DatasetLevels Empty => None;

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

        if (highest < 0)
        {
            return None;
        }

        DatasetTree[] trees = new DatasetTree[highest + 1];
        Array.Fill(trees, DatasetTree.Empty);
        foreach (CommitLevel level in header.Levels)
        {
            if (level.Top.Exists)
            {
                trees[level.Level] = new DatasetTree(level.Top, level.Depth, level.Entries, level.Rows);
            }
        }

        return new DatasetLevels(trees);
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
        return new DatasetLevels(trees);
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
