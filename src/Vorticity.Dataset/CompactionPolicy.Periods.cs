using System;
using System.Collections.Generic;
using Vorticity.Expressions;
using Vorticity.Scanning;
using Vorticity.Types;

namespace Vorticity.Dataset;

internal static partial class CompactionPolicy
{
    /// <summary>
    /// The first job due on a dataset compacted by periods, on the levels no job ranked before took, in
    /// order of priority: level 0's exit, a seal, the late level's merge, the fresh merges from the
    /// lowest fresh level up, a purge of marks, then fragments.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Level 0's exit reads every object of level 0 and routes each row: past the largest instant a
    /// fresh object or a period holds, to the first fresh level, where it follows every object; any
    /// other row to the late level, so that a late row rewrites neither a fresh object nor a period.
    /// A period whose end the newest instant has passed by the lateness horizon is sealed into one
    /// object of the periods level. The late level merges into itself past level 0's ceiling. A fresh
    /// level whose objects hold <c>F</c> adjacent ones of one open period merges them into the next,
    /// where they follow every object of that level, since the rows past every fresh object only ever
    /// enter the first. Every job writes an object per period, so that no object spans two.
    /// </para>
    /// <para>
    /// <c>F</c> is the options' fan-out until the cost model that weighs the objects a question over the
    /// recent hours reads against the rewrites per row gives it.
    /// </para>
    /// </remarks>
    private static CompactionJob? ChoosePeriods(
        PlannedVersion dataset, List<List<CompactionInput>> levels, CompactionOptions settings, HashSet<int> taken)
    {
        ClusteringKey key = dataset.Key!;
        bool exitDue = Held(levels, 0) > settings.LevelZeroCeiling;
        TimeUnit unit = PeriodOptions.InstantOf(key);
        PeriodCalendar calendar = PeriodCalendar.Of(dataset.Header, unit);
        TimeSpan lateness = dataset.Header.Lateness ?? TimeSpan.Zero;
        string path = key.InstantPath;
        long? after = NewestHeld(levels, path);

        // A period is closed once the newest instant held is past its end by the horizon: the horizon
        // is how late a row may come behind the newest one.
        long horizon = TimeSlicer.Units(lateness, unit);
        bool Closed(long period) =>
            after is { } newest && newest != long.MaxValue && calendar.Next(period) <= newest - horizon;

        if (exitDue && Held(levels, 0) > 0
            && !taken.Contains(0) && !taken.Contains(PeriodLevels.Late) && !taken.Contains(PeriodLevels.FirstFresh))
        {
            List<CompactionInput> sources = levels[0];
            return new CompactionJob(
                0, PeriodLevels.FirstFresh, CompactionStyle.Periods, CompactionTrigger.LevelZeroCeiling, [.. sources], long.MaxValue, 0)
            {
                Range = after is { } newest
                    ? LevelRange.Above(PeriodLevels.FirstFresh, FilterLiteral.From(newest), sources, key)
                    : LevelRange.Of(PeriodLevels.FirstFresh, sources, key),
                Cut = new PeriodCut(calendar, after, Exit: true) { Horizon = horizon },
            };
        }

        bool lateCrowded = Held(levels, PeriodLevels.Late) > settings.LevelZeroCeiling;
        if (!taken.Contains(PeriodLevels.Late) && !taken.Contains(PeriodLevels.Periods)
            && Sealable(levels, path, calendar, Closed, lateCrowded) is { } sealing)
        {
            List<CompactionInput> inputs = Of(levels, sealing, path, calendar);
            return new CompactionJob(
                inputs[0].Level, PeriodLevels.Periods, CompactionStyle.Periods, CompactionTrigger.Seal, inputs, long.MaxValue, 0)
            {
                Range = LevelRange.Of(PeriodLevels.Periods, inputs, key),
                Cut = new PeriodCut(calendar, null, Exit: false, Slices: true) { Newest = after },
            };
        }

        // Merged per period, the late level holds an object per period with late rows: when there are
        // more such periods than its ceiling, only sealing them lowers it.
        if (lateCrowded && !taken.Contains(0) && !taken.Contains(PeriodLevels.Late)
            && Crowded(levels[PeriodLevels.Late], path, calendar) is { Count: > 1 } crowded)
        {
            return new CompactionJob(
                PeriodLevels.Late, PeriodLevels.Late, CompactionStyle.Periods, CompactionTrigger.LateCeiling, crowded, long.MaxValue, 0)
            {
                Cut = new PeriodCut(calendar, null, Exit: false),
            };
        }

        int fanout = Math.Max(settings.Fanout, 2);
        for (int level = PeriodLevels.FirstFresh; level < levels.Count; level++)
        {
            // A closed period's objects are sealed rather than merged up.
            if (taken.Contains(level) || taken.Contains(level + 1)
                || Adjacent(levels[level], fanout, path, calendar, Closed) is not { } run)
            {
                continue;
            }

            // The next level's objects the merge overlaps, none unless a concurrent job left one there,
            // are read too, so that the level stays key-disjoint.
            List<CompactionInput> inputs = [.. run];
            LevelRange range = LevelRange.Of(level + 1, run, key);
            if (level + 1 < levels.Count)
            {
                foreach (CompactionInput candidate in levels[level + 1])
                {
                    if (range.Overlaps(candidate.Entry.Summaries))
                    {
                        inputs.Add(candidate);
                    }
                }
            }

            return new CompactionJob(level, level + 1, CompactionStyle.Periods, CompactionTrigger.FreshMerge, inputs, long.MaxValue, 0)
            {
                Range = range,
                Cut = new PeriodCut(calendar, null, Exit: false),
            };
        }

        if (settings.PurgeMarks && MostMarked(levels, Marking.Of(dataset), taken) is { } marked)
        {
            return Purge(marked, Marking.Of(dataset), settings, CompactionStyle.Periods, 0) with
            {
                Cut = new PeriodCut(calendar, null, Exit: false),
            };
        }

        List<CompactionInput> fragmented = [];
        foreach (List<CompactionInput> level in levels)
        {
            foreach (CompactionInput input in level)
            {
                if (input.Entry.Fragments.Count > settings.MaxFragments && !taken.Contains(input.Level))
                {
                    fragmented.Add(input);
                }
            }
        }

        return fragmented.Count == 0
            ? null
            : new CompactionJob(
                fragmented[0].Level, fragmented[0].Level, CompactionStyle.Periods, CompactionTrigger.Fragments, fragmented, 0, 0);
    }

    /// <summary>The objects a level holds, none for a level past the version's.</summary>
    private static int Held(List<List<CompactionInput>> levels, int level) => level < levels.Count ? levels[level].Count : 0;

    /// <summary>
    /// The largest instant the fresh levels and the periods level hold, from the last object of each,
    /// which holds the level's largest key since the level is key-disjoint; null when they hold none.
    /// A level whose last object states no maximum holds any instant, and makes every row of the exit
    /// late, which is safe.
    /// </summary>
    private static long? NewestHeld(List<List<CompactionInput>> levels, string path)
    {
        long? newest = null;
        for (int level = PeriodLevels.Periods; level < levels.Count; level++)
        {
            if (levels[level].Count == 0)
            {
                continue;
            }

            if (!levels[level][^1].Entry.Summaries.TryGet(path, out ColumnSummary column)
                || !column.HasMax || column.Max.Kind != FilterLiteralKind.Signed)
            {
                return long.MaxValue;
            }

            newest = Math.Max(newest ?? long.MinValue, column.Max.SignedValue);
        }

        return newest;
    }

    /// <summary>
    /// The first <paramref name="fanout"/> adjacent objects of one open period in a fresh level, in key
    /// order; null when no open period holds that many. An object whose summaries do not place it in
    /// one period, or that lies in a closed one, breaks a run.
    /// </summary>
    private static List<CompactionInput>? Adjacent(
        List<CompactionInput> level, int fanout, string path, PeriodCalendar calendar, Func<long, bool> closed)
    {
        List<CompactionInput> run = [];
        long period = long.MinValue;
        foreach (CompactionInput input in level)
        {
            long? of = PeriodOf(input.Entry, path, calendar);
            if (of is null || of != period)
            {
                run.Clear();
                period = of ?? long.MinValue;
            }

            if (of is not { } open || closed(open))
            {
                continue;
            }

            run.Add(input);
            if (run.Count == fanout)
            {
                return run;
            }
        }

        return null;
    }

    /// <summary>
    /// The oldest closed period a seal is due on: one that fresh objects hold, or late rows and no
    /// object of the periods level yet, or, when the late level is crowded, late rows at all, which a
    /// second seal folds into its object. Null when none is.
    /// </summary>
    private static long? Sealable(
        List<List<CompactionInput>> levels, string path, PeriodCalendar calendar, Func<long, bool> closed, bool lateCrowded)
    {
        // The periods the periods level holds.
        HashSet<long> done = [];
        if (PeriodLevels.Periods < levels.Count)
        {
            foreach (CompactionInput input in levels[PeriodLevels.Periods])
            {
                if (PeriodOf(input.Entry, path, calendar) is { } period)
                {
                    done.Add(period);
                }
            }
        }

        long? oldest = null;
        for (int level = PeriodLevels.Late; level < levels.Count; level++)
        {
            if (level == PeriodLevels.Periods)
            {
                continue;
            }

            foreach (CompactionInput input in levels[level])
            {
                if (PeriodOf(input.Entry, path, calendar) is { } period && closed(period)
                    && (level != PeriodLevels.Late || lateCrowded || !done.Contains(period))
                    && (oldest is null || period < oldest))
                {
                    oldest = period;
                }
            }
        }

        return oldest;
    }

    /// <summary>Every object of <paramref name="period"/> in the fresh levels, the late level and the periods level, in that order.</summary>
    private static List<CompactionInput> Of(List<List<CompactionInput>> levels, long period, string path, PeriodCalendar calendar)
    {
        List<CompactionInput> inputs = [];
        void Add(int level)
        {
            if (level >= levels.Count)
            {
                return;
            }

            foreach (CompactionInput input in levels[level])
            {
                if (PeriodOf(input.Entry, path, calendar) == period)
                {
                    inputs.Add(input);
                }
            }
        }

        for (int level = PeriodLevels.FirstFresh; level < levels.Count; level++)
        {
            Add(level);
        }

        Add(PeriodLevels.Late);
        Add(PeriodLevels.Periods);
        return inputs;
    }

    /// <summary>
    /// The objects of the late level whose period holds another, in level order: what a merge per
    /// period turns into fewer objects. The others are left where they are, which a merge would only
    /// rewrite.
    /// </summary>
    private static List<CompactionInput> Crowded(List<CompactionInput> level, string path, PeriodCalendar calendar)
    {
        Dictionary<long, int> held = [];
        foreach (CompactionInput input in level)
        {
            if (PeriodOf(input.Entry, path, calendar) is { } period)
            {
                held[period] = held.GetValueOrDefault(period) + 1;
            }
        }

        List<CompactionInput> crowded = [];
        foreach (CompactionInput input in level)
        {
            if (PeriodOf(input.Entry, path, calendar) is { } period && held[period] > 1)
            {
                crowded.Add(input);
            }
        }

        return crowded;
    }

    /// <summary>The start of the one period an object's instants lie in, or null when its summaries do not say so.</summary>
    private static long? PeriodOf(ObjectEntry entry, string path, PeriodCalendar calendar)
    {
        if (!entry.Summaries.TryGet(path, out ColumnSummary column) || !column.HasMin || !column.HasMax
            || column.Min.Kind != FilterLiteralKind.Signed || column.Max.Kind != FilterLiteralKind.Signed)
        {
            return null;
        }

        long start = calendar.Floor(column.Min.SignedValue);
        return calendar.Floor(column.Max.SignedValue) == start ? start : null;
    }
}
