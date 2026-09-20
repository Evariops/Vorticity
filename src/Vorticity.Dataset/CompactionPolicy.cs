using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Expressions;
using Vorticity.Keys;
using Vorticity.Scan;

namespace Vorticity.Dataset;

/// <summary>Decides what to compact. The planner reads leaf entries only, never rows.</summary>
public static class CompactionPolicy
{
    /// <summary>
    /// Plans the compaction due on a dataset's current version; the plan's job is null when nothing
    /// is over its bound. Null options take the defaults.
    /// </summary>
    public static async ValueTask<CompactionPlan> PlanAsync(
        VortexDataset dataset,
        CompactionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        CompactionOptions settings = (options ?? new CompactionOptions()).From(dataset.Compaction);

        bool clustered = dataset.Key is not null;
        CompactionStyle style = settings.StyleFor(clustered);
        List<List<CompactionInput>> levels = await ReadAsync(dataset, cancellationToken).ConfigureAwait(false);

        long[] objects = new long[levels.Count];
        long[] bytes = new long[levels.Count];
        long fragmented = 0;
        for (int level = 0; level < levels.Count; level++)
        {
            objects[level] = levels[level].Count;
            foreach (CompactionInput input in levels[level])
            {
                bytes[level] += input.Entry.Bytes;
                if (input.Entry.Fragments.Count > settings.MaxFragments)
                {
                    fragmented++;
                }
            }
        }

        CompactionJob? job = Choose(dataset, levels, objects, bytes, settings, style);
        return new CompactionPlan(
            dataset.Version,
            objects,
            bytes,
            dataset.Lag,
            clustered,
            style,
            job,
            fragmented);
    }

    /// <summary>The first trigger that fires, in order of priority.</summary>
    private static CompactionJob? Choose(
        VortexDataset dataset,
        List<List<CompactionInput>> levels,
        long[] objects,
        long[] bytes,
        CompactionOptions settings,
        CompactionStyle style)
    {
        if (levels.Count > 0 && objects[0] > settings.LevelZeroCeiling)
        {
            return Build(dataset, levels, 0, settings, style, CompactionTrigger.LevelZeroCeiling);
        }

        // The lowest level over its size first: compacting it feeds the one above. A level of one
        // object is as compacted as it can be whatever its size says, and that guard is what makes
        // a drain terminate once the object target has saturated. A capped dataset's top level has
        // no size bound at all.
        for (int level = 1; level < levels.Count; level++)
        {
            if (!settings.IsTop(level) && objects[level] > 1 && bytes[level] > settings.CapacityBytes(level))
            {
                return Build(dataset, levels, level, settings, style, CompactionTrigger.LevelSize);
            }
        }

        // Fragments last: this rewrites no row, and a trigger that moves data would drop the
        // object's fragments with the object anyway.
        List<CompactionInput> fragmented = [];
        foreach (List<CompactionInput> level in levels)
        {
            foreach (CompactionInput input in level)
            {
                if (input.Entry.Fragments.Count > settings.MaxFragments)
                {
                    fragmented.Add(input);
                }
            }
        }

        return fragmented.Count == 0
            ? null
            : new CompactionJob(
                fragmented[0].Level, fragmented[0].Level, style, CompactionTrigger.Fragments, fragmented, 0, 0);
    }

    /// <summary>The job that empties a level into the one above it.</summary>
    private static CompactionJob? Build(
        VortexDataset dataset,
        List<List<CompactionInput>> levels,
        int from,
        CompactionOptions settings,
        CompactionStyle style,
        CompactionTrigger trigger)
    {
        int to = from + 1;
        List<CompactionInput> sources = style == CompactionStyle.Leveled
            ? Leveled(levels[from], from)
            : Contiguous(levels, from);
        if (sources.Count == 0)
        {
            return null;
        }

        List<CompactionInput> inputs = [.. sources];
        if (style == CompactionStyle.Leveled && to < levels.Count)
        {
            // The destination objects the outputs would overlap are read too, so that the level
            // stays key-disjoint. The range is the union of the sources, not one source at a time:
            // a merge writes one sorted sequence, so it spans the holes between them as well.
            ClusteringKey key = dataset.Key!;
            (FilterLiteral low, bool hasLow, FilterLiteral high, bool hasHigh) = Range(sources, key);
            foreach (CompactionInput candidate in levels[to])
            {
                if (Overlaps(candidate, key, low, hasLow, high, hasHigh))
                {
                    inputs.Add(candidate);
                }
            }
        }

        return new CompactionJob(
            from, to, style, trigger, inputs, settings.TargetBytes(to), FirstRow(levels, inputs));
    }

    /// <summary>The objects a leveled compaction drains out of the source level.</summary>
    private static List<CompactionInput> Leveled(List<CompactionInput> source, int from)
    {
        // Level 0's objects overlap each other by construction, so all of them go in. A level above
        // is already key-disjoint, and one object of it is a complete compaction on its own -- the
        // largest, because that is the one holding the level over its size.
        if (from == 0 || source.Count <= 1)
        {
            return [.. source];
        }

        CompactionInput largest = source[0];
        foreach (CompactionInput candidate in source)
        {
            if (candidate.Entry.Bytes > largest.Entry.Bytes)
            {
                largest = candidate;
            }
        }

        return [largest];
    }

    /// <summary>
    /// The longest run of source-level objects nothing else sits between. A tiered job concatenates
    /// its inputs, so taking objects that are not adjacent in the tree's order would move rows past
    /// objects it did not read and change the sequence a scan answers.
    /// </summary>
    private static List<CompactionInput> Contiguous(List<List<CompactionInput>> levels, int from)
    {
        List<CompactionInput> best = [];
        List<CompactionInput> run = [];
        foreach ((int level, CompactionInput input) in InOrder(levels))
        {
            if (level == from)
            {
                run.Add(input);
                continue;
            }

            if (run.Count > best.Count)
            {
                best = run;
            }

            run = [];
        }

        return run.Count > best.Count ? run : best;
    }

    /// <summary>Where the first of the inputs' rows sits in the dataset, in the tree's own order.</summary>
    private static long FirstRow(List<List<CompactionInput>> levels, List<CompactionInput> inputs)
    {
        HashSet<string> taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (CompactionInput input in inputs)
        {
            taken.Add(input.Entry.Key);
        }

        long row = 0;
        foreach ((int _, CompactionInput input) in InOrder(levels))
        {
            if (taken.Contains(input.Entry.Key))
            {
                return row;
            }

            row += input.Entry.Rows;
        }

        return row;
    }

    /// <summary>Every level's objects, merged into the one order a scan reads them in.</summary>
    private static IEnumerable<(int Level, CompactionInput Input)> InOrder(List<List<CompactionInput>> levels)
    {
        int[] at = new int[levels.Count];
        while (true)
        {
            int smallest = -1;
            for (int level = 0; level < levels.Count; level++)
            {
                if (at[level] < levels[level].Count
                    && (smallest < 0
                        || TreePage.Compare(
                            levels[level][at[level]].Key.Span, levels[smallest][at[smallest]].Key.Span) < 0))
                {
                    smallest = level;
                }
            }

            if (smallest < 0)
            {
                yield break;
            }

            yield return (smallest, levels[smallest][at[smallest]++]);
        }
    }

    /// <summary>The key range the inputs span, as their summaries state it.</summary>
    private static (FilterLiteral Low, bool HasLow, FilterLiteral High, bool HasHigh) Range(
        List<CompactionInput> inputs, ClusteringKey key)
    {
        string path = key.Paths[0];
        FilterLiteral low = default;
        FilterLiteral high = default;
        bool hasLow = false;
        bool hasHigh = false;
        foreach (CompactionInput input in inputs)
        {
            if (!input.Entry.Summaries.TryGet(path, out ColumnSummary column))
            {
                // An object that says nothing about its key spans everything it might hold, so the
                // union does too.
                return (default, false, default, false);
            }

            if (!column.HasMin || !column.HasMax)
            {
                return (default, false, default, false);
            }

            if (!hasLow || KeyCursor.Compare(column.Min, low) < 0)
            {
                low = column.Min;
                hasLow = true;
            }

            if (!hasHigh || KeyCursor.Compare(column.Max, high) > 0)
            {
                high = column.Max;
                hasHigh = true;
            }
        }

        return (low, hasLow, high, hasHigh);
    }

    /// <summary>Whether a destination object may hold a key inside <c>[low, high]</c>.</summary>
    private static bool Overlaps(
        CompactionInput candidate,
        ClusteringKey key,
        FilterLiteral low,
        bool hasLow,
        FilterLiteral high,
        bool hasHigh)
    {
        if (!hasLow || !hasHigh)
        {
            return true;
        }

        if (!candidate.Entry.Summaries.TryGet(key.Paths[0], out ColumnSummary column))
        {
            return true;
        }

        // Only a positive proof of disjointness excludes the object: a missing bound proves
        // nothing, so it overlaps.
        if (column.HasMin && KeyCursor.Compare(column.Min, high) > 0)
        {
            return false;
        }

        return !column.HasMax || KeyCursor.Compare(column.Max, low) >= 0;
    }

    /// <summary>Every level's leaves, in key order, with the key each one sits at.</summary>
    private static async ValueTask<List<List<CompactionInput>>> ReadAsync(
        VortexDataset dataset, CancellationToken cancellationToken)
    {
        List<List<CompactionInput>> levels = [];
        for (int level = 0; level < dataset.Levels.Count; level++)
        {
            List<CompactionInput> entries = [];
            await foreach (TreeEntry entry in dataset.Levels[level]
                .EnumerateAsync(dataset.Pages, cancellationToken).ConfigureAwait(false))
            {
                entries.Add(new CompactionInput(level, entry.Key, ObjectEntry.FromBytes(entry.Value.Span)));
            }

            levels.Add(entries);
        }

        return levels;
    }
}
