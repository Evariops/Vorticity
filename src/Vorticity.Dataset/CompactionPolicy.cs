// When to compact - docs/13-dataset.md §5.3: "Triggers: level 0 above 8 objects; a level above its
// size; an entry above K fragments".
//
// THE PLANNER READS ENTRIES, NEVER ROWS, and that is the whole cost of asking. It walks the leaves
// of every occupied level -- the objects, with their summaries -- so under §5.2's invariant it
// reads at most `8 + F x L` of them. A lagging dataset costs more, which is the same
// output-sensitive number §5.2 already states for a lookup; a planner that hid it would be lying
// about the thing it exists to report.
//
// WHY THE OVERLAP IS COMPUTED AGAINST THE UNION and not per object. A leveled compaction takes the
// objects of level `i + 1` that overlap the inputs of level `i`. Testing each source object
// separately looks cheaper and is wrong: source objects [1,5] and [90,100] leave a hole, the
// outputs span [1,100] because a merge writes one sorted sequence, and a level-(i+1) object at
// [20,30] that was not taken as an input now overlaps an output. The invariant of §5.2 -- disjoint
// ranges inside a level -- would be broken by a compaction whose job is to hold it.
//
// AND WHY A TIERED JOB IS A CONTIGUOUS RUN. Without a clustering key an object's leaf key is its
// first row position (§4.1), so the dataset's row order IS the tree's order. A concatenation of
// objects that are not adjacent in that order would move rows past objects it did not read -- the
// scan would answer the same rows in a different sequence, and §14 compares sequences. So the
// planner takes the longest run of source-level objects that nothing else sits between.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Expressions;
using Vorticity.Keys;
using Vorticity.Scan;

namespace Vorticity.Dataset;

/// <summary>Decides what to compact (§5.3).</summary>
public static class CompactionPolicy
{
    /// <summary>Plans the compaction that is due on a dataset's current version.</summary>
    /// <param name="dataset">The dataset.</param>
    /// <param name="options">The numbers of §5, or null for the specification's.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The plan, whose job is null when nothing is over its bound.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="dataset"/> is null.</exception>
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

    /// <summary>The first trigger that fires, in §5.3's order.</summary>
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

        // The lowest level over its size first: compacting it feeds the one above, and doing the
        // top one first would only have to be redone.
        //
        // A LEVEL OF ONE OBJECT IS AS COMPACTED AS IT CAN BE, whatever its size says, and the guard
        // is what makes a drain terminate. The object target saturates at `MaxObjectBytes`, so a
        // level's capacity stops growing at the top; without this, a dataset past that point would
        // move its last object up a new level on every call, for ever, and each move would look
        // like progress.
        for (int level = 1; level < levels.Count; level++)
        {
            if (objects[level] > 1 && bytes[level] > settings.CapacityBytes(level))
            {
                return Build(dataset, levels, level, settings, style, CompactionTrigger.LevelSize);
            }
        }

        return null;
    }

    /// <summary>The job that empties <paramref name="from"/> into the level above it.</summary>
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
            // §5.3's second half of a leveled compaction: the destination objects the outputs would
            // otherwise overlap are read too, so that the level stays key-disjoint.
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

    /// <summary>Every object of the source level: a leveled compaction drains it (§5.2).</summary>
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

    /// <summary>The longest run of source-level objects nothing else sits between.</summary>
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

    /// <summary>Every level's objects, merged into the one order a scan reads them in (§5.2).</summary>
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
                // union does too: an inexact bound is still a conservative bound (08 §1).
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

        // Only a positive proof of disjointness excludes the object (08 §1): a missing bound proves
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
