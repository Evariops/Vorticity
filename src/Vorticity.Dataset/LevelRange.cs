using System;
using System.Collections.Generic;
using Vorticity.Expressions;
using Vorticity.Keys;
using Vorticity.Scanning;

namespace Vorticity.Dataset;

/// <summary>
/// The part of a key-disjoint level a leveled compaction takes over: the bounds of the clustering key over the objects it
/// moves into the level, as their entries state them. Every object of the level that may hold a key inside it has to be an
/// input of the compaction, whose outputs fill the range. The plan takes those it finds, and the commit looks again, since
/// another compaction may have moved one into the level in between.
/// </summary>
/// <remarks>
/// <para>
/// The bounds are the objects' keys when every entry states its largest (<see cref="ObjectEntry.LastKey"/>): from the
/// smallest first key, which a tree key leads with, to the largest last key, row-encoded, for a key of any type. Otherwise
/// they are the leading column's, as the summaries state them, which bound a number and not a text: a text key then has
/// no range, and every object of the level meets it.
/// </para>
/// <para>
/// A summary bound is inexact outwards, a minimum at or below the true one and a maximum at or above
/// it, so a range and a summary that do not meet prove that no key of the object lies in the range.
/// Only that proof excludes an object: one whose summaries say nothing of the key may hold any. An object whose entry
/// states no largest key is judged by its summaries.
/// </para>
/// </remarks>
internal sealed class LevelRange
{
    private readonly SummaryColumns _leading;
    private readonly FilterLiteral _low;
    private readonly FilterLiteral _high;
    private readonly bool _bounded;
    private readonly bool _lowExcluded;

    // The smallest first key and the largest last key of the sources, row-encoded; null when one states no last key.
    private readonly byte[]? _lowKey;
    private readonly byte[]? _highKey;

    private LevelRange(
        int level, string path, FilterLiteral low, FilterLiteral high, bool bounded, bool lowExcluded = false, byte[]? lowKey = null, byte[]? highKey = null)
    {
        Level = level;
        Path = path;
        _leading = new SummaryColumns([path]);
        _low = low;
        _high = high;
        _bounded = bounded;
        _lowExcluded = lowExcluded;
        _lowKey = lowKey;
        _highKey = highKey;
    }

    /// <summary>
    /// The part of <paramref name="level"/> strictly above <paramref name="low"/> and up to the largest
    /// maximum of <paramref name="sources"/>: where level 0's exit of a dataset of periods writes the
    /// rows past every fresh object, the last of which ends at <paramref name="low"/>. Unbounded when a
    /// source states no maximum on the key. Judged by the summaries alone.
    /// </summary>
    internal static LevelRange Above(int level, FilterLiteral low, IReadOnlyList<CompactionInput> sources, ClusteringKey key)
    {
        LevelRange hull = Of(level, sources, key);
        return hull._bounded
            ? new LevelRange(level, hull.Path, low, hull._high, bounded: true, lowExcluded: true)
            : new LevelRange(level, hull.Path, default, default, bounded: false);
    }

    /// <summary>The level the range lies in.</summary>
    internal int Level { get; }

    /// <summary>The column the summaries' bounds are on: the clustering key's first.</summary>
    internal string Path { get; }

    /// <summary>
    /// The range <paramref name="sources"/> span once in <paramref name="level"/>: from the smallest
    /// first key to the largest last, or minimum to maximum, the holes between them included, since a merge writes one
    /// sorted sequence across them. Unbounded when one of them states no bound on the key.
    /// </summary>
    internal static LevelRange Of(int level, IReadOnlyList<CompactionInput> sources, ClusteringKey key)
    {
        (byte[]? lowKey, byte[]? highKey) = KeysOf(sources);
        string path = key.InstantPath;
        FilterLiteral low = default;
        FilterLiteral high = default;
        bool bounded = false;
        foreach (CompactionInput source in sources)
        {
            if (!source.Entry.Summaries.TryGet(path, out ColumnSummary column) || !column.HasMin || !column.HasMax)
            {
                return new LevelRange(level, path, default, default, bounded: false, lowKey: lowKey, highKey: highKey);
            }

            if (!bounded || KeyCursor.Compare(column.Min, low) < 0)
            {
                low = column.Min;
            }

            if (!bounded || KeyCursor.Compare(column.Max, high) > 0)
            {
                high = column.Max;
            }

            bounded = true;
        }

        return new LevelRange(level, path, low, high, bounded, lowKey: lowKey, highKey: highKey);
    }

    /// <summary>The smallest first key and the largest last key of <paramref name="sources"/>; nulls when one states no last key.</summary>
    private static (byte[]? Low, byte[]? High) KeysOf(IReadOnlyList<CompactionInput> sources)
    {
        ReadOnlySpan<byte> low = default;
        ReadOnlySpan<byte> high = default;
        for (int i = 0; i < sources.Count; i++)
        {
            CompactionInput source = sources[i];
            ReadOnlySpan<byte> last = source.Entry.LastKey.Span;
            if (last.IsEmpty)
            {
                return (null, null);
            }

            ReadOnlySpan<byte> first = FirstKey(source.Key.Span);
            if (i == 0 || first.SequenceCompareTo(low) < 0)
            {
                low = first;
            }

            if (i == 0 || last.SequenceCompareTo(high) > 0)
            {
                high = last;
            }
        }

        return sources.Count == 0 ? (null, null) : (low.ToArray(), high.ToArray());
    }

    /// <summary>The smallest key of the object a tree key names: the tree key without the uid that ends it.</summary>
    private static ReadOnlySpan<byte> FirstKey(ReadOnlySpan<byte> treeKey) => treeKey[..^VortexDataset.UidBytes];

    /// <summary>
    /// Whether the object under <paramref name="treeKey"/> with <paramref name="entry"/> may hold a key inside the range:
    /// by its keys when both state theirs, by its summaries otherwise.
    /// </summary>
    internal bool Overlaps(ReadOnlySpan<byte> treeKey, ObjectEntry entry) =>
        _lowKey is not null && !entry.LastKey.IsEmpty ? KeysMeet(treeKey, entry.LastKey.Span) : Overlaps(entry.Summaries);

    /// <summary>
    /// <see cref="Overlaps(ReadOnlySpan{byte}, ObjectEntry)"/> asked of a leaf entry's bytes, its largest key or its
    /// summaries for the key's column alone decoded.
    /// </summary>
    internal bool Overlaps(ReadOnlySpan<byte> treeKey, ReadOnlyMemory<byte> entry)
    {
        if (_lowKey is not null && ObjectEntry.LastKeyOf(entry) is { IsEmpty: false } last)
        {
            return KeysMeet(treeKey, last.Span);
        }

        return !_bounded || Overlaps(ObjectSummaries.Of(ObjectEntry.SummaryOf(entry).Span, _leading));
    }

    /// <summary>Whether an object with these summaries may hold a key inside the range.</summary>
    internal bool Overlaps(ObjectSummaries summaries) =>
        !_bounded || !summaries.TryGet(Path, out ColumnSummary column) || Meets(column);

    /// <summary>
    /// <see cref="Overlaps(ObjectSummaries)"/> asked of a leaf entry's bytes, its summaries decoded for
    /// the key's column alone: the range of its summaries, whatever the keys say.
    /// </summary>
    internal bool Overlaps(ReadOnlyMemory<byte> entry) =>
        !_bounded || Overlaps(ObjectSummaries.Of(ObjectEntry.SummaryOf(entry).Span, _leading));

    /// <summary>
    /// Whether a subtree may hold an object overlapping the range: the test an object's own summary
    /// is put to, asked of the union of the summaries under it, whose bounds hold every object's; and, by the keys, none
    /// whose smallest key, which orders it, lies past the range.
    /// </summary>
    internal bool MayOverlap(InternalEntry node)
    {
        if (_highKey is not null && node.MinKey.Length > VortexDataset.UidBytes && FirstKey(node.MinKey.Span).SequenceCompareTo(_highKey) > 0)
        {
            return false;
        }

        if (!_bounded)
        {
            return true;
        }

        ReadOnlySpan<byte> summaries = ObjectSummaryFold.SummariesOf(node.Summary.Span, out _);
        if (summaries.IsEmpty)
        {
            return true;
        }

        ObjectSummaries held = ObjectSummaries.Of(summaries, _leading);
        return !held.TryGet(Path, out ColumnSummary column) || Meets(column);
    }

    /// <summary>Whether the keys from <paramref name="treeKey"/>'s first to <paramref name="last"/> meet the range's.</summary>
    private bool KeysMeet(ReadOnlySpan<byte> treeKey, ReadOnlySpan<byte> last) =>
        last.SequenceCompareTo(_lowKey) >= 0 && FirstKey(treeKey).SequenceCompareTo(_highKey) <= 0;

    /// <summary>Whether a column's bounds may meet the range; a missing bound proves nothing, so it meets.</summary>
    private bool Meets(ColumnSummary column)
    {
        if (column.HasMin && KeyCursor.Compare(column.Min, _high) > 0)
        {
            return false;
        }

        if (!column.HasMax)
        {
            return true;
        }

        int order = KeyCursor.Compare(column.Max, _low);
        return order > 0 || (order == 0 && !_lowExcluded);
    }
}
