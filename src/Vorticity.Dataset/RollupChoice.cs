using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Scanning;

namespace Vorticity.Dataset;

/// <summary>
/// The rollups (<see cref="RollupEntry"/>) an aggregation takes in place of the objects they cover: of the highest rank
/// first, those the counts answer for as they answer for an object (<see cref="CountsAnswer.FieldOf(RollupEntry)"/>), its
/// union of summaries proving the filter and the keys, none covering another; under a rollup that does not answer, those
/// of the rank below; under them, the objects. Each is taken with the column whose totals answer.
/// </summary>
/// <remarks>
/// A rollup's rows are its objects', which the scan must cover whole and which no other part of it may read: only a scan of
/// the whole version takes rollups, and a source that would cut it between lanes reads it on one
/// (<see cref="DatasetScanSource.PiecesAsync"/>).
/// </remarks>
internal sealed class RollupChoice
{
    // The rollups taken, with their columns, by their levels and their first tree keys.
    private readonly List<(RollupEntry Rollup, int Field)> _chosen;

    private RollupChoice(List<(RollupEntry Rollup, int Field)> chosen) => _chosen = chosen;

    /// <summary>The rollups taken, each with the column whose totals answer, by level and first tree key.</summary>
    internal IReadOnlyList<(RollupEntry Rollup, int Field)> Chosen => _chosen;

    /// <summary>
    /// The rollups <paramref name="answer"/> takes over <paramref name="version"/>, those <paramref name="pruner"/> refutes
    /// left to the walk, which finds no row in them; null when it takes none.
    /// </summary>
    internal static async ValueTask<RollupChoice?> OfAsync(
        DatasetSnapshot version, CountsAnswer answer, SummaryPruner? pruner, CancellationToken cancellationToken)
    {
        if (version.Levels.Rollups.IsEmpty)
        {
            return null;
        }

        List<RollupEntry> candidates = [];
        await foreach (TreeEntry entry in version.Levels.Rollups.EnumerateAsync(version.Pages, cancellationToken).ConfigureAwait(false))
        {
            RollupEntry rollup = RollupEntry.FromBytes(entry.Key.Span, entry.Value.Span);
            if (pruner is null || pruner.MayMatch(rollup.Summaries.Columns, rollup.Rows))
            {
                candidates.Add(rollup);
            }
        }

        // The highest rank first, then each level's rollups in the order of their keys.
        candidates.Sort(static (left, right) =>
            left.Rank != right.Rank ? right.Rank.CompareTo(left.Rank)
            : left.Level != right.Level ? left.Level.CompareTo(right.Level)
            : left.FirstKey.Span.SequenceCompareTo(right.FirstKey.Span));
        List<(RollupEntry Rollup, int Field)> chosen = [];
        foreach (RollupEntry rollup in candidates)
        {
            if (!Overlaps(chosen, rollup) && answer.FieldOf(rollup) is int field)
            {
                chosen.Add((rollup, field));
            }
        }

        if (chosen.Count == 0)
        {
            return null;
        }

        chosen.Sort(static (left, right) =>
            left.Rollup.Level != right.Rollup.Level ? left.Rollup.Level.CompareTo(right.Rollup.Level)
            : left.Rollup.FirstKey.Span.SequenceCompareTo(right.Rollup.FirstKey.Span));
        return new RollupChoice(chosen);
    }

    /// <summary>The rollup taken that covers the object <paramref name="held"/>, by its place among <see cref="Chosen"/>, or -1.</summary>
    internal int CoverOf(in PositionedObject held)
    {
        int found = Last(held.Level, held.TreeKey.Span);
        return found >= 0 && _chosen[found].Rollup.Covers(held.TreeKey.Span) ? found : -1;
    }

    /// <summary>Whether a rollup taken covers the whole subtree <paramref name="node"/> of level <paramref name="level"/>: its smallest key and its largest.</summary>
    internal bool Covers(int level, InternalEntry node)
    {
        int found = Last(level, node.MinKey.Span);
        return found >= 0 && _chosen[found].Rollup.Covers(node.MinKey.Span) && _chosen[found].Rollup.Covers(node.MaxKey.Span);
    }

    /// <summary>The last rollup taken of level <paramref name="level"/> whose first key is at or before <paramref name="key"/>, or -1.</summary>
    private int Last(int level, ReadOnlySpan<byte> key)
    {
        int low = 0;
        int high = _chosen.Count - 1;
        int found = -1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            RollupEntry rollup = _chosen[middle].Rollup;
            int order = rollup.Level != level ? rollup.Level.CompareTo(level) : rollup.FirstKey.Span.SequenceCompareTo(key);
            if (order <= 0)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return found >= 0 && _chosen[found].Rollup.Level == level ? found : -1;
    }

    /// <summary>Whether <paramref name="rollup"/> covers a key of its level a rollup of <paramref name="chosen"/> covers.</summary>
    private static bool Overlaps(List<(RollupEntry Rollup, int Field)> chosen, RollupEntry rollup)
    {
        foreach ((RollupEntry taken, int _) in chosen)
        {
            if (taken.Level == rollup.Level
                && taken.FirstKey.Span.SequenceCompareTo(rollup.LastKey.Span) <= 0
                && rollup.FirstKey.Span.SequenceCompareTo(taken.LastKey.Span) <= 0)
            {
                return true;
            }
        }

        return false;
    }
}
