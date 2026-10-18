using System;
using System.Collections.Generic;
using Vorticity.Expressions;
using Vorticity.Keys;
using Vorticity.Scanning;

namespace Vorticity.Dataset;

/// <summary>
/// The part of a key-disjoint level a leveled compaction takes over: the bounds of the leading
/// clustering key column over the objects it moves into the level, as their summaries state them.
/// Every object of the level that may hold a key inside it has to be an input of the compaction,
/// whose outputs fill the range. The plan takes those it finds, and the commit looks again, since
/// another compaction may have moved one into the level in between.
/// </summary>
/// <remarks>
/// A summary bound is inexact outwards, a minimum at or below the true one and a maximum at or above
/// it, so a range and a summary that do not meet prove that no key of the object lies in the range.
/// Only that proof excludes an object: one whose summaries say nothing of the key may hold any.
/// </remarks>
internal sealed class LevelRange
{
    private readonly SummaryColumns _leading;
    private readonly FilterLiteral _low;
    private readonly FilterLiteral _high;
    private readonly bool _bounded;
    private readonly bool _lowExcluded;

    private LevelRange(int level, string path, FilterLiteral low, FilterLiteral high, bool bounded, bool lowExcluded = false)
    {
        Level = level;
        Path = path;
        _leading = new SummaryColumns([path]);
        _low = low;
        _high = high;
        _bounded = bounded;
        _lowExcluded = lowExcluded;
    }

    /// <summary>
    /// The part of <paramref name="level"/> strictly above <paramref name="low"/> and up to the largest
    /// maximum of <paramref name="sources"/>: where level 0's exit of a dataset of periods writes the
    /// rows past every fresh object, the last of which ends at <paramref name="low"/>. Unbounded when a
    /// source states no maximum on the key.
    /// </summary>
    internal static LevelRange Above(int level, FilterLiteral low, IReadOnlyList<CompactionInput> sources, ClusteringKey key)
    {
        LevelRange hull = Of(level, sources, key);
        return hull._bounded
            ? new LevelRange(level, hull.Path, low, hull._high, bounded: true, lowExcluded: true)
            : hull;
    }

    /// <summary>The level the range lies in.</summary>
    internal int Level { get; }

    /// <summary>The clustering key's leading column, which the bounds are on.</summary>
    internal string Path { get; }

    /// <summary>
    /// The range <paramref name="sources"/> span once in <paramref name="level"/>: from the smallest
    /// minimum to the largest maximum, the holes between them included, since a merge writes one
    /// sorted sequence across them. Unbounded when one of them states no bound on the key.
    /// </summary>
    internal static LevelRange Of(int level, IReadOnlyList<CompactionInput> sources, ClusteringKey key)
    {
        string path = key.Paths[0];
        FilterLiteral low = default;
        FilterLiteral high = default;
        bool bounded = false;
        foreach (CompactionInput source in sources)
        {
            if (!source.Entry.Summaries.TryGet(path, out ColumnSummary column) || !column.HasMin || !column.HasMax)
            {
                return new LevelRange(level, path, default, default, bounded: false);
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

        return new LevelRange(level, path, low, high, bounded);
    }

    /// <summary>Whether an object with these summaries may hold a key inside the range.</summary>
    internal bool Overlaps(ObjectSummaries summaries) =>
        !_bounded || !summaries.TryGet(Path, out ColumnSummary column) || Meets(column);

    /// <summary>
    /// <see cref="Overlaps(ObjectSummaries)"/> asked of a leaf entry's bytes, its summaries decoded for
    /// the key's column alone.
    /// </summary>
    internal bool Overlaps(ReadOnlyMemory<byte> entry) =>
        !_bounded || Overlaps(ObjectSummaries.Of(ObjectEntry.SummaryOf(entry).Span, _leading));

    /// <summary>
    /// Whether a subtree may hold an object overlapping the range: the test an object's own summary
    /// is put to, asked of the union of the summaries under it, whose bounds hold every object's.
    /// </summary>
    internal bool MayOverlap(InternalEntry node)
    {
        if (!_bounded)
        {
            return true;
        }

        ReadOnlySpan<byte> summaries = ObjectSummaryFold.SummariesOf(node.Summary.Span, out _);
        return summaries.IsEmpty
            || !ObjectSummaries.Of(summaries, _leading).TryGet(Path, out ColumnSummary column)
            || Meets(column);
    }

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
