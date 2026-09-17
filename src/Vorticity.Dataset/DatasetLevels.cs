// The levels of docs/13-dataset.md §5, as a version names them: one tree per level (§4.1), not one
// tree.
//
// WHY LEVELS ARE PART OF THE READ BOUND AND NOT AN OPTIMISATION (§5.1): "without a merge policy,
// the number of objects a lookup touches is the number of appends". §5.2 fixes that with an
// invariant -- at most 8 objects in level 0, key-disjoint objects inside every level above -- so a
// lookup by key touches at most 8 + L objects whatever the dataset holds. This type is the shape
// that invariant is stated over, and `Lag` is the invariant itself, asked rather than assumed:
// §5.1 requires that a violation be REPORTED with its count, never refused, because "a library
// never stalls a writer" (§5.3).
//
// EMPTY LEVELS ARE KEPT IN PLACE, not compacted out of the list. A level's number is its meaning --
// its target size, its place in the lookup bound, what a compaction of the level below writes into
// -- so level 2 stays level 2 when level 1 is emptied by a compaction that consumed all of it.
// The header writes only the occupied ones and the gaps come back as empty trees.
using System;
using System.Collections.Generic;

namespace Vorticity.Dataset;

/// <summary>The trees of one version of a dataset, one per level (§5.2).</summary>
public sealed class DatasetLevels
{
    private static readonly DatasetLevels None = new DatasetLevels([]);
    private readonly DatasetTree[] _levels;

    private DatasetLevels(DatasetTree[] levels) => _levels = levels;

    /// <summary>A dataset with nothing in it.</summary>
    public static DatasetLevels Empty => None;

    /// <summary>How many levels the version names; level numbers run from 0 to this minus one.</summary>
    public int Count => _levels.Length;

    /// <summary>The tree of one level, empty for a level this version does not name.</summary>
    /// <param name="level">The level number.</param>
    /// <returns>Its tree.</returns>
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

    /// <summary>The levels a header names (§4.3's "a sorted batch of changes per level").</summary>
    /// <param name="header">The commit header.</param>
    /// <returns>The trees.</returns>
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
    /// <param name="level">The level number.</param>
    /// <param name="tree">Its new tree.</param>
    /// <returns>A new set of levels.</returns>
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
    /// How far the version is from §5.2's invariant: the objects level 0 holds above the eight it
    /// may hold, and nothing else yet.
    /// </summary>
    /// <param name="ceiling">What level 0 may hold; 8 by §5.2.</param>
    /// <returns>The lag, zero when the invariant holds.</returns>
    /// <remarks>
    /// REPORTED, NEVER REFUSED. §5.3: "a level-0 count above 8 degrades the read bound and is
    /// reported, never refused", because compaction is the user's background job and "a library
    /// never stalls a writer". A dataset that appends faster than it compacts is a dataset with a
    /// worse read bound and a number that says by how much, which is a different thing from a
    /// dataset that stops accepting writes.
    /// </remarks>
    public long LagAtLevelZero(int ceiling = DefaultLevelZeroCeiling)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ceiling);
        return Math.Max(this[0].Entries - ceiling, 0);
    }

    /// <summary>What level 0 may hold before the read bound of §5.2 degrades.</summary>
    public const int DefaultLevelZeroCeiling = 8;

    /// <summary>The levels a header should carry, in order, skipping the empty ones.</summary>
    /// <returns>Each occupied level and its tree.</returns>
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
