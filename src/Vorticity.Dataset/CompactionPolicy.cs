using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Expressions;
using Vorticity.Keys;
using Vorticity.Scanning;

namespace Vorticity.Dataset;

/// <summary>Decides what to compact. The planner reads leaf entries only, never rows.</summary>
/// <remarks>
/// <para>
/// A plan descends the trees rather than reading them. Every page's summary carries the tally of
/// what lies under it: the bytes, the largest object, the most fragments one object carries. A
/// level's bytes are then the sum over its top page; the largest object of a full level is at the
/// end of one descent that follows the largest tally, and the object past a level's pointer at the
/// end of one that follows the keys; the objects a job overlaps, and the objects over their
/// fragments, are found by a walk that skips every subtree whose summary rules it out. A plan reads
/// a level's top page and a path or two below it, and the job's own objects.
/// </para>
/// <para>
/// A tiered job concatenates a run of one level's objects nothing else sits between. The run that
/// starts past the level's pointer ends at the first object of another level past its start, one
/// descent per level; the longest run is a fact of every level's order in full, so a plan that
/// takes it reads every leaf, as does a plan over a level whose top page was written before
/// tallies. Both choose the same job.
/// </para>
/// </remarks>
internal static class CompactionPolicy
{
    /// <summary>
    /// Plans the compaction due on a dataset's current version; the plan's job is null when nothing
    /// is over its bound. Null options take the defaults.
    /// </summary>
    public static ValueTask<CompactionPlan> PlanAsync(
        VortexDataset dataset,
        CompactionOptions? options = null,
        CancellationToken cancellationToken = default) =>
        PlanAsync(dataset, options, 1, cancellationToken);

    /// <summary>
    /// Plans up to <paramref name="jobs"/> compactions due on a dataset's current version, most
    /// urgent first, no two of which read or write one level: the first is the plan's job, and loops
    /// sharing the dataset spread over the rest, since jobs on distinct levels never take one input.
    /// </summary>
    internal static async ValueTask<CompactionPlan> PlanAsync(
        VortexDataset dataset,
        CompactionOptions? options,
        int jobs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(jobs);
        CompactionOptions settings = SettingsFor(dataset, options);
        CompactionStyle style = settings.StyleFor(dataset.Key is not null);
        bool descends = style == CompactionStyle.Leveled ? dataset.Key is not null : settings.PickFor(style) == CompactionPick.RoundRobin;
        if (descends && await TalliesAsync(dataset, cancellationToken).ConfigureAwait(false) is { } tallies)
        {
            return await DescendAsync(dataset, settings, style, tallies, jobs, cancellationToken).ConfigureAwait(false);
        }

        return await ReadEveryLeafAsync(dataset, settings, style, jobs, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The plan read from every leaf of every level: the tiered plan, the plan of a level written
    /// before tallies, and the one a descent is held to.
    /// </summary>
    internal static async ValueTask<CompactionPlan> PlanByReadingEveryLeafAsync(
        VortexDataset dataset, CompactionOptions? options, CancellationToken cancellationToken, int jobs = 1)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        CompactionOptions settings = SettingsFor(dataset, options);
        return await ReadEveryLeafAsync(dataset, settings, settings.StyleFor(dataset.Key is not null), jobs, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The options a plan works to: the caller's, under whatever the dataset's header states, and on a
    /// store that locks what it keeps no purge, which would add bytes for the lock's term and free none.
    /// </summary>
    private static CompactionOptions SettingsFor(VortexDataset dataset, CompactionOptions? options)
    {
        CompactionOptions settings = (options ?? new CompactionOptions()).From(dataset.Compaction);
        return dataset.LockedStore ? settings with { PurgeMarks = false } : settings;
    }

    private static async ValueTask<CompactionPlan> ReadEveryLeafAsync(
        VortexDataset dataset, CompactionOptions settings, CompactionStyle style, int count, CancellationToken cancellationToken)
    {
        bool clustered = dataset.Key is not null;
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

        List<CompactionJob> jobs = [];
        HashSet<int> taken = [];
        while (jobs.Count < count && Choose(dataset, levels, objects, bytes, settings, style, taken) is { } job)
        {
            Take(job, taken);
            jobs.Add(job);
        }

        return new CompactionPlan
        {
            Version = dataset.Version,
            ObjectsByLevel = [.. objects],
            BytesByLevel = [.. bytes],
            Lag = dataset.Lag,
            IsClustered = clustered,
            Style = style,
            Job = jobs.Count > 0 ? jobs[0] : null,
            Jobs = [.. jobs],
            FragmentedObjects = fragmented,
        };
    }

    /// <summary>Marks every level a job reads or writes as taken, so that no job ranked after it touches one.</summary>
    private static void Take(CompactionJob job, HashSet<int> taken)
    {
        taken.Add(job.FromLevel);
        taken.Add(job.ToLevel);
        foreach (CompactionInput input in job.Inputs)
        {
            taken.Add(input.Level);
        }
    }

    /// <summary>The first trigger that fires on the levels no job ranked before took, in order of priority.</summary>
    private static CompactionJob? Choose(
        VortexDataset dataset,
        List<List<CompactionInput>> levels,
        long[] objects,
        long[] bytes,
        CompactionOptions settings,
        CompactionStyle style,
        HashSet<int> taken)
    {
        if (levels.Count > 0 && objects[0] > settings.LevelZeroCeiling && !taken.Contains(0)
            && Build(dataset, levels, 0, settings, style, CompactionTrigger.LevelZeroCeiling) is { } drained
            && !taken.Contains(drained.ToLevel))
        {
            return drained;
        }

        // The lowest level over its size first: compacting it feeds the one above. A level of one
        // object is as compacted as it can be whatever its size says, and that guard is what makes
        // a drain terminate once the object target has saturated. A capped dataset's top level has
        // no size bound at all.
        for (int level = 1; level < levels.Count; level++)
        {
            if (!taken.Contains(level) && !taken.Contains(level + 1)
                && !settings.IsTop(level) && objects[level] > 1 && bytes[level] > settings.CapacityBytes(level)
                && Build(dataset, levels, level, settings, style, CompactionTrigger.LevelSize) is { } grown)
            {
                return grown;
            }
        }

        // An object whose marks are due next: rewriting it alone costs its bytes, so the one with the
        // most marked goes first.
        if (settings.PurgeMarks && MostMarked(levels, Marking.Of(dataset.Options), taken) is { } marked)
        {
            return Purge(marked, settings, style, FirstRow(dataset, marked));
        }

        // Fragments last: this rewrites no row, and a trigger that moves data would drop the
        // object's fragments with the object anyway.
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
                fragmented[0].Level, fragmented[0].Level, style, CompactionTrigger.Fragments, fragmented, 0, 0);
    }

    /// <summary>
    /// The job that empties a level into one above it: the next, or for level 0 under the leveled
    /// style, the first whose capacity holds it.
    /// </summary>
    private static CompactionJob? Build(
        VortexDataset dataset,
        List<List<CompactionInput>> levels,
        int from,
        CompactionOptions settings,
        CompactionStyle style,
        CompactionTrigger trigger)
    {
        CompactionPick pick = settings.PickFor(style);
        ReadOnlySpan<byte> pointer = dataset.Levels.PointerOf(from).Span;
        List<CompactionInput> sources = style == CompactionStyle.Leveled
            ? Leveled(levels[from], from, pick, pointer)
            : Contiguous(levels, from, pick, pointer);
        if (sources.Count == 0)
        {
            return null;
        }

        int to = from + 1;
        if (style == CompactionStyle.Leveled && from == 0)
        {
            long bytes = 0;
            foreach (CompactionInput source in sources)
            {
                bytes += source.Entry.Bytes;
            }

            to = settings.LevelZeroDestination(bytes);
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

        // Under a round robin the level records where the job stopped, unless the job takes the
        // whole of level 0, as a leveled one does.
        bool stops = pick == CompactionPick.RoundRobin && (style == CompactionStyle.Tiered || from > 0);
        return new CompactionJob(from, to, style, trigger, inputs, settings.TargetBytes(to), FirstRow(dataset, inputs[0]))
        {
            Stop = stops ? sources[^1].Key : default,
        };
    }

    /// <summary>The objects a leveled compaction drains out of the source level.</summary>
    private static List<CompactionInput> Leveled(List<CompactionInput> source, int from, CompactionPick pick, ReadOnlySpan<byte> pointer)
    {
        // Level 0's objects overlap each other by construction, so all of them go in. A level above
        // is already key-disjoint, and one object of it is a complete compaction on its own: the
        // largest, the one holding the level over its size the most, or the next in a round robin.
        if (from == 0 || source.Count <= 1)
        {
            return [.. source];
        }

        if (pick == CompactionPick.RoundRobin)
        {
            return [Next(source, pointer)];
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

    /// <summary>The first object of a level past <paramref name="pointer"/>, or its first when none is.</summary>
    private static CompactionInput Next(List<CompactionInput> level, ReadOnlySpan<byte> pointer)
    {
        foreach (CompactionInput input in level)
        {
            if (TreePage.Compare(input.Key.Span, pointer) > 0)
            {
                return input;
            }
        }

        return level[0];
    }

    /// <summary>
    /// A run of source-level objects nothing else sits between: the longest, or the one that starts
    /// with the object past the level's pointer. A tiered job concatenates its inputs, so taking
    /// objects that are not adjacent in the tree's order would move rows past objects it did not read
    /// and change the sequence a scan answers.
    /// </summary>
    private static List<CompactionInput> Contiguous(
        List<List<CompactionInput>> levels, int from, CompactionPick pick, ReadOnlySpan<byte> pointer)
    {
        if (pick == CompactionPick.RoundRobin)
        {
            return RunFrom(levels, from, Next(levels[from], pointer).Key);
        }

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

    /// <summary>The run that starts with the source-level object at <paramref name="start"/>, up to the first object of another level.</summary>
    private static List<CompactionInput> RunFrom(List<List<CompactionInput>> levels, int from, ReadOnlyMemory<byte> start)
    {
        List<CompactionInput> run = [];
        foreach ((int level, CompactionInput input) in InOrder(levels))
        {
            if (run.Count == 0)
            {
                if (level == from && TreePage.Compare(input.Key.Span, start.Span) == 0)
                {
                    run.Add(input);
                }

                continue;
            }

            if (level != from)
            {
                break;
            }

            run.Add(input);
        }

        return run;
    }

    /// <summary>
    /// Where the outputs of a dataset ordered by arrival are keyed from: the position of a job's first
    /// input, the first of its run in the tree's order, which they take over, so that a removal before
    /// them leaves them where the inputs were. A clustered dataset keys an output by its smallest key,
    /// and reads none.
    /// </summary>
    private static long FirstRow(VortexDataset dataset, CompactionInput first) =>
        dataset.Key is null ? VortexDataset.PositionAt(first.Key.Span) : 0;

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

        return !candidate.Entry.Summaries.TryGet(key.Paths[0], out ColumnSummary column) || Overlaps(column, low, high);
    }

    /// <summary>
    /// Whether a column's bounds may meet <c>[low, high]</c>. Only a positive proof of disjointness
    /// excludes: a missing bound proves nothing, so it overlaps.
    /// </summary>
    private static bool Overlaps(ColumnSummary column, FilterLiteral low, FilterLiteral high) =>
        !(column.HasMin && KeyCursor.Compare(column.Min, high) > 0)
        && (!column.HasMax || KeyCursor.Compare(column.Max, low) >= 0);

    /// <summary>
    /// The plan by descent: the same job the full read chooses, found from the levels' top pages, one
    /// descent to the object a full level gives up, one per level to where a tiered run ends, and
    /// walks that skip what the summaries rule out.
    /// </summary>
    private static async ValueTask<CompactionPlan> DescendAsync(
        VortexDataset dataset, CompactionOptions settings, CompactionStyle style, ObjectTally[] tallies, int count, CancellationToken cancellationToken)
    {
        int levels = dataset.Levels.Count;
        long[] objects = new long[levels];
        long[] bytes = new long[levels];
        for (int level = 0; level < levels; level++)
        {
            objects[level] = dataset.Levels[level].Entries;
            bytes[level] = tallies[level].Bytes;
        }

        List<CompactionInput> fragmented = await FragmentedAsync(dataset, tallies, settings.MaxFragments, cancellationToken)
            .ConfigureAwait(false);
        List<CompactionJob> jobs = [];
        HashSet<int> taken = [];
        while (jobs.Count < count
            && await ChooseAsync(dataset, settings, style, tallies, objects, bytes, fragmented, taken, cancellationToken).ConfigureAwait(false) is { } job)
        {
            Take(job, taken);
            jobs.Add(job);
        }

        return new CompactionPlan
        {
            Version = dataset.Version,
            ObjectsByLevel = [.. objects],
            BytesByLevel = [.. bytes],
            Lag = dataset.Lag,
            IsClustered = dataset.Key is not null,
            Style = style,
            Job = jobs.Count > 0 ? jobs[0] : null,
            Jobs = [.. jobs],
            FragmentedObjects = fragmented.Count,
        };
    }

    /// <summary>
    /// <see cref="Choose"/> by descent: the first trigger that fires on the levels no job ranked
    /// before took, found without reading a level's leaves.
    /// </summary>
    private static async ValueTask<CompactionJob?> ChooseAsync(
        VortexDataset dataset,
        CompactionOptions settings,
        CompactionStyle style,
        ObjectTally[] tallies,
        long[] objects,
        long[] bytes,
        List<CompactionInput> fragmented,
        HashSet<int> taken,
        CancellationToken cancellationToken)
    {
        int levels = objects.Length;
        bool leveled = style == CompactionStyle.Leveled;
        if (levels > 0 && objects[0] > settings.LevelZeroCeiling && !taken.Contains(0)
            && (leveled ? settings.LevelZeroDestination(bytes[0]) : 1) is var destination && !taken.Contains(destination))
        {
            if (!leveled)
            {
                return Tiered(dataset, await RunAsync(dataset, 0, cancellationToken).ConfigureAwait(false), 0, settings, CompactionTrigger.LevelZeroCeiling);
            }

            List<CompactionInput> sources = [];
            await foreach (TreeEntry entry in dataset.Levels[0].EnumerateAsync(dataset.Pages, cancellationToken).ConfigureAwait(false))
            {
                sources.Add(new CompactionInput(0, entry.Key, ObjectEntry.FromBytes(entry.Value)));
            }

            return await LeveledAsync(dataset, sources, 0, destination, settings, CompactionTrigger.LevelZeroCeiling, default, cancellationToken)
                .ConfigureAwait(false);
        }

        for (int level = 1; level < levels; level++)
        {
            if (!taken.Contains(level) && !taken.Contains(level + 1)
                && !settings.IsTop(level) && objects[level] > 1 && bytes[level] > settings.CapacityBytes(level))
            {
                if (!leveled)
                {
                    return Tiered(dataset, await RunAsync(dataset, level, cancellationToken).ConfigureAwait(false), level, settings, CompactionTrigger.LevelSize);
                }

                bool roundRobin = settings.PickFor(style) == CompactionPick.RoundRobin;
                CompactionInput source = roundRobin
                    ? await NextAsync(dataset, level, cancellationToken).ConfigureAwait(false)
                    : await LargestAsync(dataset, level, cancellationToken).ConfigureAwait(false);
                return await LeveledAsync(
                        dataset, [source], level, level + 1, settings, CompactionTrigger.LevelSize, roundRobin ? source.Key : default, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (settings.PurgeMarks
            && await MostMarkedAsync(dataset, tallies, Marking.Of(dataset.Options), taken, cancellationToken).ConfigureAwait(false) is { } marked)
        {
            return Purge(marked, settings, style, FirstRow(dataset, marked));
        }

        List<CompactionInput> untaken = fragmented.FindAll(input => !taken.Contains(input.Level));
        return untaken.Count == 0
            ? null
            : new CompactionJob(untaken[0].Level, untaken[0].Level, style, CompactionTrigger.Fragments, untaken, 0, 0);
    }

    /// <summary>The tiered job that concatenates <paramref name="run"/> into the level above, and records where it stopped.</summary>
    private static CompactionJob Tiered(
        VortexDataset dataset, List<CompactionInput> run, int from, CompactionOptions settings, CompactionTrigger trigger) =>
        new CompactionJob(from, from + 1, CompactionStyle.Tiered, trigger, run, settings.TargetBytes(from + 1), FirstRow(dataset, run[0]))
        {
            Stop = run[^1].Key,
        };

    /// <summary>
    /// <see cref="RunFrom"/> by descent: the run starts with the object past the level's pointer and
    /// ends before the first object another level holds past that start, which one descent per level
    /// finds, the levels side by side; a walk of the source level between the two reads the run's own
    /// leaves.
    /// </summary>
    private static async ValueTask<List<CompactionInput>> RunAsync(VortexDataset dataset, int from, CancellationToken cancellationToken)
    {
        DatasetLevels levels = dataset.Levels;
        TreeEntry first = await levels[from]
            .NextAsync(levels.PointerOf(from), wrap: true, dataset.Pages, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("A level over its bound holds an object.");
        ReadOnlyMemory<byte> start = first.Key;
        Task<TreeEntry?>[] nexts = new Task<TreeEntry?>[levels.Count];
        for (int level = 0; level < levels.Count; level++)
        {
            nexts[level] = level == from
                ? Task.FromResult<TreeEntry?>(null)
                : levels[level].NextAsync(start, wrap: false, dataset.Pages, cancellationToken).AsTask();
        }

        await Task.WhenAll(nexts).ConfigureAwait(false);
        ReadOnlyMemory<byte> stop = default;
        bool bounded = false;
        foreach (Task<TreeEntry?> found in nexts)
        {
            if (await found.ConfigureAwait(false) is { } next && (!bounded || TreePage.Compare(next.Key.Span, stop.Span) < 0))
            {
                stop = next.Key;
                bounded = true;
            }
        }

        List<CompactionInput> run = [];
        await foreach (PositionedEntry held in levels[from]
            .WalkAsync(
                dataset.Pages,
                0,
                long.MaxValue,
                node => TreePage.Compare(node.MaxKey.Span, start.Span) >= 0 && (!bounded || TreePage.Compare(node.MinKey.Span, stop.Span) < 0),
                cancellationToken)
            .ConfigureAwait(false))
        {
            if (bounded && TreePage.Compare(held.Entry.Key.Span, stop.Span) >= 0)
            {
                break;
            }

            if (TreePage.Compare(held.Entry.Key.Span, start.Span) >= 0)
            {
                run.Add(new CompactionInput(from, held.Entry.Key, ObjectEntry.FromBytes(held.Entry.Value)));
            }
        }

        return run;
    }

    /// <summary>
    /// The object a round robin takes next from a level: the first past its pointer, or its first,
    /// at the end of one descent.
    /// </summary>
    private static async ValueTask<CompactionInput> NextAsync(VortexDataset dataset, int level, CancellationToken cancellationToken)
    {
        TreeEntry next = await dataset.Levels[level]
            .NextAsync(dataset.Levels.PointerOf(level), wrap: true, dataset.Pages, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("A level over its bound holds an object.");
        return new CompactionInput(level, next.Key, ObjectEntry.FromBytes(next.Value));
    }

    /// <summary>
    /// The job that merges <paramref name="sources"/> into level <paramref name="to"/>, with the
    /// objects there the union of their key range overlaps, found by a walk that skips every
    /// subtree whose summary puts it outside the range. A clustered dataset's outputs are keyed by
    /// their smallest key, so the job's first row is not read.
    /// </summary>
    private static async ValueTask<CompactionJob> LeveledAsync(
        VortexDataset dataset,
        List<CompactionInput> sources,
        int from,
        int to,
        CompactionOptions settings,
        CompactionTrigger trigger,
        ReadOnlyMemory<byte> stop,
        CancellationToken cancellationToken)
    {
        List<CompactionInput> inputs = [.. sources];
        if (to < dataset.Levels.Count)
        {
            ClusteringKey key = dataset.Key!;
            (FilterLiteral low, bool hasLow, FilterLiteral high, bool hasHigh) = Range(sources, key);
            await foreach (PositionedEntry held in dataset.Levels[to]
                .WalkAsync(dataset.Pages, 0, long.MaxValue, node => MayOverlap(node, key, low, hasLow, high, hasHigh), cancellationToken)
                .ConfigureAwait(false))
            {
                CompactionInput candidate = new CompactionInput(to, held.Entry.Key, ObjectEntry.FromBytes(held.Entry.Value));
                if (Overlaps(candidate, key, low, hasLow, high, hasHigh))
                {
                    inputs.Add(candidate);
                }
            }
        }

        return new CompactionJob(from, to, CompactionStyle.Leveled, trigger, inputs, settings.TargetBytes(to), 0) { Stop = stop };
    }

    /// <summary>
    /// The largest object of a level, the first of equals in key order as a full read would find
    /// it: at each page, the first entry whose tally holds the largest object below.
    /// </summary>
    private static async ValueTask<CompactionInput> LargestAsync(VortexDataset dataset, int level, CancellationToken cancellationToken)
    {
        DatasetTree tree = dataset.Levels[level];
        PageReference reference = tree.Root;
        for (int depth = tree.Depth; depth > 1; depth--)
        {
            IReadOnlyList<InternalEntry> page = TreePage.ReadInternal(
                await dataset.Pages.ReadPageAsync(reference, cancellationToken).ConfigureAwait(false));
            int best = 0;
            long largest = -1;
            for (int i = 0; i < page.Count; i++)
            {
                long below = TallyOf(page[i]).Largest;
                if (below > largest)
                {
                    largest = below;
                    best = i;
                }
            }

            reference = page[best].Child;
        }

        IReadOnlyList<TreeEntry> leaf = TreePage.ReadLeaf(
            await dataset.Pages.ReadPageAsync(reference, cancellationToken).ConfigureAwait(false));
        TreeEntry chosen = leaf[0];
        long most = -1;
        foreach (TreeEntry entry in leaf)
        {
            long held = ObjectTally.Of(entry.Value.Span).Bytes;
            if (held > most)
            {
                most = held;
                chosen = entry;
            }
        }

        return new CompactionInput(level, chosen.Key, ObjectEntry.FromBytes(chosen.Value));
    }

    /// <summary>
    /// Every object carrying more than <paramref name="most"/> index fragments, level by level in key
    /// order, found by walks that enter only the subtrees whose tally holds one.
    /// </summary>
    private static async ValueTask<List<CompactionInput>> FragmentedAsync(
        VortexDataset dataset, ObjectTally[] tallies, int most, CancellationToken cancellationToken)
    {
        List<CompactionInput> found = [];
        for (int level = 0; level < tallies.Length; level++)
        {
            if (tallies[level].MostFragments <= most)
            {
                continue;
            }

            await foreach (PositionedEntry held in dataset.Levels[level]
                .WalkAsync(dataset.Pages, 0, long.MaxValue, node => TallyOf(node).MostFragments > most, cancellationToken)
                .ConfigureAwait(false))
            {
                ObjectEntry entry = ObjectEntry.FromBytes(held.Entry.Value);
                if (entry.Fragments.Count > most)
                {
                    found.Add(new CompactionInput(level, held.Entry.Key, entry));
                }
            }
        }

        return found;
    }

    /// <summary>
    /// The job that rewrites one object alone, in its own level, without the rows marked in it: its
    /// keys lie inside its old range, so a level stays key-disjoint, and it is written as one object
    /// whatever its level's target, so that a purge of level 0 does not put it over its ceiling.
    /// </summary>
    private static CompactionJob Purge(CompactionInput marked, CompactionOptions settings, CompactionStyle style, long firstRow) =>
        new CompactionJob(
            marked.Level,
            marked.Level,
            style,
            CompactionTrigger.Marks,
            [marked],
            Math.Max(settings.TargetBytes(marked.Level), marked.Entry.Bytes),
            firstRow);

    /// <summary>
    /// The object whose marks are the most due outside the levels <paramref name="taken"/>, the first
    /// of equals in level and key order; null when none is due.
    /// </summary>
    private static CompactionInput? MostMarked(List<List<CompactionInput>> levels, Marking marking, HashSet<int> taken)
    {
        CompactionInput? best = null;
        double most = 0;
        for (int at = 0; at < levels.Count; at++)
        {
            if (taken.Contains(at))
            {
                continue;
            }

            foreach (CompactionInput input in levels[at])
            {
                double load = marking.Load(input.Entry);
                if (load >= 1 && load > most)
                {
                    most = load;
                    best = input;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// <see cref="MostMarked(List{List{CompactionInput}}, Marking, HashSet{int})"/> by walks that enter
    /// only the subtrees whose tally says an object under them may be due.
    /// </summary>
    private static async ValueTask<CompactionInput?> MostMarkedAsync(
        VortexDataset dataset, ObjectTally[] tallies, Marking marking, HashSet<int> taken, CancellationToken cancellationToken)
    {
        CompactionInput? best = null;
        double most = 0;
        for (int level = 0; level < tallies.Length; level++)
        {
            if (taken.Contains(level) || !marking.MayBeDue(tallies[level]))
            {
                continue;
            }

            await foreach (PositionedEntry held in dataset.Levels[level]
                .WalkAsync(dataset.Pages, 0, long.MaxValue, node => marking.MayBeDue(TallyOf(node)), cancellationToken)
                .ConfigureAwait(false))
            {
                ObjectEntry entry = ObjectEntry.FromBytes(held.Entry.Value);
                double load = marking.Load(entry);
                if (load >= 1 && load > most)
                {
                    most = load;
                    best = new CompactionInput(level, held.Entry.Key, entry);
                }
            }
        }

        return best;
    }

    /// <summary>
    /// When an object's marks are due for a purge: at half the share of its rows, or half the bytes of
    /// vector, past which a delete rewrites the object itself.
    /// </summary>
    /// <param name="Share">The divisor of a delete's share, <see cref="DatasetOptions.MarkedShare"/>.</param>
    /// <param name="VectorBytes">A delete's bound on a vector, <see cref="DatasetOptions.MarkedVectorBytes"/>.</param>
    private readonly record struct Marking(int Share, int VectorBytes)
    {
        public static Marking Of(DatasetOptions options) =>
            new Marking(Math.Max(options.MarkedShare, 1), Math.Max(options.MarkedVectorBytes, 1));

        /// <summary>
        /// How due an object's marks are: its share of marked rows, or its vector's bytes, over half of
        /// a delete's bound, whichever is further; due from 1.
        /// </summary>
        public double Load(ObjectEntry entry) => !entry.HasDeletions
            ? 0
            : Math.Max(
                (double)entry.DeletedRows * 2 * Share / entry.PhysicalRows,
                (double)entry.VectorBytes * 2 / VectorBytes);

        /// <summary>
        /// Whether an object under a tally may be due: a share rounded down past the due share rounded
        /// down, or a vector at half the bound. A tally never rules out an object that is due.
        /// </summary>
        public bool MayBeDue(ObjectTally tally) =>
            tally.MostMarked >= ObjectTally.Whole / (2L * Share) || tally.LargestVector * 2 >= VectorBytes;
    }

    /// <summary>
    /// Every level's tally, from its top page alone; null when a level's top page holds a subtree
    /// written before tallies, whose leaves the plan then reads. A page's tally is known only when
    /// every page below it carries one, so a top page whose entries all have one vouches for its tree.
    /// </summary>
    private static async ValueTask<ObjectTally[]?> TalliesAsync(VortexDataset dataset, CancellationToken cancellationToken)
    {
        ObjectTally[] tallies = new ObjectTally[dataset.Levels.Count];
        for (int level = 0; level < tallies.Length; level++)
        {
            DatasetTree tree = dataset.Levels[level];
            if (tree.IsEmpty)
            {
                continue;
            }

            ReadOnlyMemory<byte> top = await dataset.Pages.ReadPageAsync(tree.Root, cancellationToken).ConfigureAwait(false);
            ObjectTally? tally = null;
            if (tree.Depth == 1)
            {
                foreach (TreeEntry entry in TreePage.ReadLeaf(top))
                {
                    ObjectTally one = ObjectTally.Of(entry.Value.Span);
                    tally = tally is { } sum ? sum.With(one) : one;
                }
            }
            else
            {
                foreach (InternalEntry child in TreePage.ReadInternal(top))
                {
                    ObjectSummaryFold.SummariesOf(child.Summary.Span, out ObjectTally? below);
                    if (below is not { } known)
                    {
                        return null;
                    }

                    tally = tally is { } sum ? sum.With(known) : known;
                }
            }

            tallies[level] = tally ?? default;
        }

        return tallies;
    }

    /// <summary>The tally a page's summary carries, which every page of a tree its top page vouches for has.</summary>
    private static ObjectTally TallyOf(InternalEntry node)
    {
        ObjectSummaryFold.SummariesOf(node.Summary.Span, out ObjectTally? tally);
        return tally ?? throw new CommitFormatException("A page under a tallied page carries no tally.");
    }

    /// <summary>
    /// Whether a subtree may hold an object overlapping <c>[low, high]</c>: the test an object's own
    /// summary is put to, asked of the union of its summaries, whose bounds hold every object's under it.
    /// </summary>
    private static bool MayOverlap(
        InternalEntry node, ClusteringKey key, FilterLiteral low, bool hasLow, FilterLiteral high, bool hasHigh)
    {
        if (!hasLow || !hasHigh)
        {
            return true;
        }

        ReadOnlySpan<byte> summaries = ObjectSummaryFold.SummariesOf(node.Summary.Span, out _);
        if (summaries.IsEmpty || !ObjectSummaries.FromBytes(summaries).TryGet(key.Paths[0], out ColumnSummary column))
        {
            return true;
        }

        return Overlaps(column, low, high);
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
                entries.Add(new CompactionInput(level, entry.Key, ObjectEntry.FromBytes(entry.Value)));
            }

            levels.Add(entries);
        }

        return levels;
    }
}
