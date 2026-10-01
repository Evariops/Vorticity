using System;
using System.Collections.Generic;

namespace Vorticity.Dataset;

/// <summary>
/// The objects of one version as a walk in key order ranges over them: each one's entry, where its
/// rows start, its level, and the smallest key its tree key orders it by; and per level, the objects
/// in that level's order. Immutable, and shared by every walk of the version.
/// </summary>
/// <remarks>
/// The bounds lie end to end in one array, so the version's objects cost a few arrays whatever
/// their number, rather than an array each. A level's objects come in its tree's order, which is
/// the order of their bounds, ties by uid: the order a walk opens them in.
/// </remarks>
internal sealed class KeyedObjects
{
    private readonly ObjectEntry[] _entries;
    private readonly long[] _firstRows;
    private readonly int[] _levels;
    private readonly byte[] _bounds;
    private readonly int[] _boundStarts;
    private readonly int[][] _order;

    private KeyedObjects(ObjectEntry[] entries, long[] firstRows, int[] levels, byte[] bounds, int[] boundStarts, int[][] order)
    {
        _entries = entries;
        _firstRows = firstRows;
        _levels = levels;
        _bounds = bounds;
        _boundStarts = boundStarts;
        _order = order;
    }

    /// <summary>How many objects the version holds.</summary>
    internal int Count => _entries.Length;

    /// <summary>How many levels hold them, empty ones included up to the highest.</summary>
    internal int LevelCount => _order.Length;

    /// <summary>The entry of object <paramref name="index"/>.</summary>
    internal ObjectEntry Entry(int index) => _entries[index];

    /// <summary>Where object <paramref name="index"/>'s rows start among the dataset's.</summary>
    internal long FirstRow(int index) => _firstRows[index];

    /// <summary>The level whose tree holds object <paramref name="index"/>.</summary>
    internal int Level(int index) => _levels[index];

    /// <summary>The encoded smallest key of object <paramref name="index"/>, which its tree key orders it by.</summary>
    internal ReadOnlySpan<byte> Bound(int index) => _bounds.AsSpan(_boundStarts[index], _boundStarts[index + 1] - _boundStarts[index]);

    /// <summary>The objects of <paramref name="level"/>, in its tree's order.</summary>
    internal int[] Order(int level) => _order[level];

    /// <summary>Gathers the objects of a version, as its walk lists them.</summary>
    internal sealed class Builder
    {
        private readonly List<ObjectEntry> _entries = [];
        private readonly List<long> _firstRows = [];
        private readonly List<int> _levels = [];
        private readonly List<byte> _bounds = [];
        private readonly List<int> _boundStarts = [0];
        private readonly List<List<int>> _order = [];

        internal void Add(ObjectEntry entry, long firstRow, int level, ReadOnlySpan<byte> bound)
        {
            while (_order.Count <= level)
            {
                _order.Add([]);
            }

            _order[level].Add(_entries.Count);
            _entries.Add(entry);
            _firstRows.Add(firstRow);
            _levels.Add(level);
            _bounds.AddRange(bound);
            _boundStarts.Add(_bounds.Count);
        }

        internal KeyedObjects Build()
        {
            int[][] order = new int[_order.Count][];
            for (int level = 0; level < order.Length; level++)
            {
                order[level] = [.. _order[level]];
            }

            return new KeyedObjects([.. _entries], [.. _firstRows], [.. _levels], [.. _bounds], [.. _boundStarts], order);
        }
    }
}
