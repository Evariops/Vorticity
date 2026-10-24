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
        if (key.PartitionColumns == 0)
        {
            return ChoosePeriodsOf(dataset, levels, settings, taken, exitDue);
        }

        // Each partition grows its own periods, and no job takes objects of two. Past level 0's ceiling, which
        // holds over the whole level, the partition with the most objects in it exits first.
        List<List<List<CompactionInput>>> partitions = Partitioned(levels, key);
        int loaded = 0;
        for (int p = 1; p < partitions.Count; p++)
        {
            loaded = Held(partitions[p], 0) > Held(partitions[loaded], 0) ? p : loaded;
        }

        // The calendar is the dataset's: a seal weighs it on every partition's objects of its period, and a change it asks
        // for takes effect past the newest instant of any partition.
        long? newest = null;
        foreach (List<List<CompactionInput>> partition in partitions)
        {
            if (NewestHeld(partition, key.InstantPath) is { } held)
            {
                newest = Math.Max(newest ?? long.MinValue, held);
            }
        }

        PeriodCalendar calendar = PeriodCalendar.Of(dataset.Header, PeriodOptions.InstantOf(key));
        for (int i = 0; i < partitions.Count; i++)
        {
            int p = i == 0 ? loaded : i <= loaded ? i - 1 : i;
            if (ChoosePeriodsOf(dataset, partitions[p], settings, taken, exitDue && p == loaded, newest, period => PeersOf(partitions, p, period, key.InstantPath, calendar)) is { } job)
            {
                return job;
            }
        }

        return null;
    }

    /// <summary>
    /// The largest object the partitions but <paramref name="sealing"/> sealed of the period from <paramref name="period"/>, its
    /// bytes and rows, (0, 0) for none; null while one of them still holds rows of the period below the level of periods.
    /// </summary>
    private static (long Bytes, long Rows)? PeersOf(
        List<List<List<CompactionInput>>> partitions, int sealing, long period, string path, PeriodCalendar calendar)
    {
        long end = calendar.Next(period);
        (long Bytes, long Rows) largest = (0, 0);
        for (int p = 0; p < partitions.Count; p++)
        {
            if (p == sealing)
            {
                continue;
            }

            List<List<CompactionInput>> levels = partitions[p];
            for (int level = 0; level < levels.Count; level++)
            {
                foreach (CompactionInput input in levels[level])
                {
                    if (!MayHold(input.Entry, path, period, end))
                    {
                        continue;
                    }

                    if (level != PeriodLevels.Periods)
                    {
                        return null;
                    }

                    // A joined object, of a partition whose periods are small, holds several: it weighs none.
                    if (PeriodOf(input.Entry, path, calendar) is null)
                    {
                        continue;
                    }

                    if (input.Entry.Bytes > largest.Bytes)
                    {
                        largest = (input.Entry.Bytes, input.Entry.Rows);
                    }
                }
            }
        }

        return largest;
    }

    /// <summary>Whether an object may hold rows of <c>[start, end)</c>: its instants meet it, or its summaries do not bound them.</summary>
    private static bool MayHold(ObjectEntry entry, string path, long start, long end) =>
        !entry.Summaries.TryGet(path, out ColumnSummary column) || !column.HasMin || !column.HasMax
        || column.Min.Kind != FilterLiteralKind.Signed || column.Max.Kind != FilterLiteralKind.Signed
        || (column.Min.SignedValue < end && column.Max.SignedValue >= start);

    /// <summary>
    /// The levels of each partition, in the order of the partitions' values: every object of one value of the
    /// partition columns, from its summaries. Objects whose summaries do not say one value, none a split append
    /// writes, are a group of their own, first.
    /// </summary>
    private static List<List<List<CompactionInput>>> Partitioned(List<List<CompactionInput>> levels, ClusteringKey key)
    {
        SortedDictionary<byte[], List<List<CompactionInput>>> partitions = new SortedDictionary<byte[], List<List<CompactionInput>>>(ByteOrder.Instance);
        FilterLiteral[] values = new FilterLiteral[key.PartitionColumns];
        for (int level = 0; level < levels.Count; level++)
        {
            foreach (CompactionInput input in levels[level])
            {
                byte[] partition = [];
                bool one = true;
                for (int i = 0; i < values.Length && one; i++)
                {
                    one = input.Entry.Summaries.TryGet(key.PartitionPaths[i], out ColumnSummary column)
                        && column.HasMin && column.HasMax && column.Min.Equals(column.Max);
                    values[i] = one ? column.Min : default;
                }

                if (one)
                {
                    partition = key.Encode(values);
                }

                if (!partitions.TryGetValue(partition, out List<List<CompactionInput>>? of))
                {
                    of = new List<List<CompactionInput>>(levels.Count);
                    for (int l = 0; l < levels.Count; l++)
                    {
                        of.Add([]);
                    }

                    partitions[partition] = of;
                }

                of[level].Add(input);
            }
        }

        return [.. partitions.Values];
    }

    /// <summary>The values of the partition columns the objects hold rows of, from the objects whose summaries say one each.</summary>
    private static int PartitionsOf(List<List<CompactionInput>> levels, ClusteringKey key)
    {
        HashSet<string> partitions = new HashSet<string>(StringComparer.Ordinal);
        foreach (List<CompactionInput> level in levels)
        {
            foreach (CompactionInput input in level)
            {
                if (key.PartitionOf(input.Entry.Summaries) is { } values)
                {
                    partitions.Add(Convert.ToHexString(key.Encode(values)));
                }
            }
        }

        return partitions.Count;
    }

    /// <summary>Byte strings in ordinal order: the order of encoded keys.</summary>
    private sealed class ByteOrder : IComparer<byte[]>
    {
        internal static readonly ByteOrder Instance = new ByteOrder();

        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    }

    /// <summary>
    /// <see cref="ChoosePeriods"/> over the levels of one partition, or of the whole dataset without partition
    /// columns; <paramref name="exitDue"/> says whether level 0's exit is due for these objects. With partitions,
    /// <paramref name="newestOfAll"/> is the newest instant of any and <paramref name="peers"/> what the others sealed of a
    /// period (<see cref="PeriodCut.Peers"/>), which a seal weighs the dataset's calendar on.
    /// </summary>
    private static CompactionJob? ChoosePeriodsOf(
        PlannedVersion dataset,
        List<List<CompactionInput>> levels,
        CompactionOptions settings,
        HashSet<int> taken,
        bool exitDue,
        long? newestOfAll = null,
        Func<long, (long Bytes, long Rows)?>? peers = null)
    {
        ClusteringKey key = dataset.Key!;
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
            // The late rows of a period a joined object holds are folded into the whole object, which weighs no length: it
            // holds several periods.
            (List<CompactionInput> inputs, (long Start, long End)? joined) = Of(levels, sealing, path, calendar);
            return new CompactionJob(
                inputs[0].Level, PeriodLevels.Periods, CompactionStyle.Periods, CompactionTrigger.Seal, inputs, long.MaxValue, 0)
            {
                Range = LevelRange.Of(PeriodLevels.Periods, inputs, key),
                Cut = joined is { } span
                    ? new PeriodCut(calendar, null, Exit: false, Slices: true) { Span = span }
                    : new PeriodCut(calendar, null, Exit: false, Slices: true) { Newest = newestOfAll ?? after, Peers = peers is null ? (0, 0) : peers(sealing) },
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

        // F adjacent small objects of the partition's periods join into one that spans their periods, each under an F-th
        // of the joined size, so that a joined object stays under it, under the dataset's object cap, and under what a
        // purge rewrites within half the erasure delay.
        double purged = dataset.ErasureDelay is { } delay ? PeriodLengths.RewriteBytesPerSecond * delay.TotalSeconds / 2 : double.PositiveInfinity;
        long capped = Math.Min(settings.JoinedPeriodBytes, dataset.Options.MaxObjectBytes);
        long most = purged >= capped ? capped : (long)purged;
        if (settings.JoinedPeriodBytes > 0 && !taken.Contains(PeriodLevels.Periods)
            && Joinable(levels, fanout, settings.JoinedPeriodBytes / fanout, most, key, calendar) is { } joining)
        {
            return new CompactionJob(
                PeriodLevels.Periods, PeriodLevels.Periods, CompactionStyle.Periods, CompactionTrigger.Join, joining, long.MaxValue, 0)
            {
                Range = LevelRange.Of(PeriodLevels.Periods, joining, key),
                Cut = new PeriodCut(calendar, null, Exit: false, Slices: true)
                {
                    Span = (SpanOf(joining[0].Entry, path, calendar)!.Value.Start, SpanOf(joining[^1].Entry, path, calendar)!.Value.End),
                },
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
        // The periods the periods level holds, a joined object's every one.
        List<(long Start, long End)> done = [];
        if (PeriodLevels.Periods < levels.Count)
        {
            foreach (CompactionInput input in levels[PeriodLevels.Periods])
            {
                if (SpanOf(input.Entry, path, calendar) is { } span)
                {
                    done.Add(span);
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
                    && (level != PeriodLevels.Late || lateCrowded || !done.Exists(span => span.Start <= period && period < span.End))
                    && (oldest is null || period < oldest))
                {
                    oldest = period;
                }
            }
        }

        return oldest;
    }

    /// <summary>
    /// Every object of <paramref name="period"/> in the fresh levels, the late level and the periods level, in that order;
    /// and when the object of the periods level that holds the period joins several, their span, every object of whose
    /// periods comes too: the seal rewrites the whole object.
    /// </summary>
    private static (List<CompactionInput> Inputs, (long Start, long End)? Joined) Of(
        List<List<CompactionInput>> levels, long period, string path, PeriodCalendar calendar)
    {
        (long Start, long End)? joined = null;
        if (PeriodLevels.Periods < levels.Count)
        {
            foreach (CompactionInput input in levels[PeriodLevels.Periods])
            {
                if (SpanOf(input.Entry, path, calendar) is { } span && span.Start <= period && period < span.End
                    && calendar.Next(span.Start) < span.End)
                {
                    joined = span;
                    break;
                }
            }
        }

        (long start, long end) = joined ?? (period, calendar.Next(period));
        List<CompactionInput> inputs = [];
        void Add(int level)
        {
            if (level >= levels.Count)
            {
                return;
            }

            foreach (CompactionInput input in levels[level])
            {
                if (level == PeriodLevels.Periods
                        ? SpanOf(input.Entry, path, calendar) is { } span && span.Start >= start && span.End <= end
                        : PeriodOf(input.Entry, path, calendar) is { } of && of >= start && of < end)
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
        return (inputs, joined);
    }

    /// <summary>
    /// The first <paramref name="fanout"/> adjacent objects of the periods level each under <paramref name="small"/> bytes,
    /// in key order, of one tier, of one month of the calendar and together under <paramref name="most"/> bytes, each of
    /// one value of the partition columns when the dataset has some; null when no run holds that many. A larger object,
    /// one whose summaries do not place it, and the first of another tier or another month start a run again.
    /// </summary>
    /// <remarks>
    /// A tier counts an object's rows in powers of the fan-out, so that joins grow objects <paramref name="fanout"/>-fold
    /// each time, a row rewritten once a tier, rather than one object taking its neighbours in one by one, its rows
    /// rewritten at each. A joined object never spans two months, so that an expiry keeps its rows at most a month past
    /// the cutoff, as it would a partition whose periods last a month; <paramref name="most"/> keeps it under the
    /// dataset's object cap and a size a purge rewrites within half its erasure delay, as the length of a period does.
    /// </remarks>
    private static List<CompactionInput>? Joinable(
        List<List<CompactionInput>> levels, int fanout, long small, long most, ClusteringKey key, PeriodCalendar calendar)
    {
        if (PeriodLevels.Periods >= levels.Count)
        {
            return null;
        }

        List<CompactionInput> run = [];
        (long Month, int Tier) of = (long.MinValue, -1);
        long bytes = 0;
        foreach (CompactionInput input in levels[PeriodLevels.Periods])
        {
            if (input.Entry.Bytes >= small || SpanOf(input.Entry, key.InstantPath, calendar) is not { } span
                || (key.PartitionColumns > 0 && key.PartitionOf(input.Entry.Summaries) is null)
                || TimeSlicer.Floor(span.End - 1, calendar.Unit, SliceWidth.Month) != TimeSlicer.Floor(span.Start, calendar.Unit, SliceWidth.Month))
            {
                run.Clear();
                of = (long.MinValue, -1);
                continue;
            }

            (long Month, int Tier) place = (TimeSlicer.Floor(span.Start, calendar.Unit, SliceWidth.Month), TierOf(input.Entry.Rows, fanout));
            if (place != of || bytes + input.Entry.Bytes > most)
            {
                run.Clear();
                of = place;
                bytes = 0;
            }

            run.Add(input);
            bytes += input.Entry.Bytes;
            if (run.Count == fanout)
            {
                return run;
            }
        }

        return null;
    }

    /// <summary>The tier of an object of <paramref name="rows"/> rows: how many times the fan-out goes into them.</summary>
    private static int TierOf(long rows, int fanout)
    {
        int tier = 0;
        for (long left = rows; left >= fanout; left /= fanout)
        {
            tier++;
        }

        return tier;
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

    /// <summary>
    /// The periods an object's instants lie in, from the start of the first to the end of the last, or null when its
    /// summaries do not say so: one period, but for an object a join wrote.
    /// </summary>
    private static (long Start, long End)? SpanOf(ObjectEntry entry, string path, PeriodCalendar calendar)
    {
        if (!entry.Summaries.TryGet(path, out ColumnSummary column) || !column.HasMin || !column.HasMax
            || column.Min.Kind != FilterLiteralKind.Signed || column.Max.Kind != FilterLiteralKind.Signed)
        {
            return null;
        }

        return (calendar.Floor(column.Min.SignedValue), calendar.Next(calendar.Floor(column.Max.SignedValue)));
    }
}
