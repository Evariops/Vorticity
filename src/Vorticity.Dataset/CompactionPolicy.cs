using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

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
        PlannedVersion version = new PlannedVersion(dataset);
        CompactionOptions settings = SettingsFor(version, options);
        CompactionStyle style = settings.StyleFor(version.Key is not null);
        bool descends = style == CompactionStyle.Leveled ? version.Key is not null : settings.PickFor(style) == CompactionPick.RoundRobin;
        if (descends && await TalliesAsync(version, cancellationToken).ConfigureAwait(false) is { } tallies)
        {
            return await DescendAsync(version, settings, style, tallies, jobs, cancellationToken).ConfigureAwait(false);
        }

        return await ReadEveryLeafAsync(version, settings, style, jobs, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The plan read from every leaf of every level: the tiered plan, the plan of a level written
    /// before tallies, and the one a descent is held to.
    /// </summary>
    internal static async ValueTask<CompactionPlan> PlanByReadingEveryLeafAsync(
        VortexDataset dataset, CompactionOptions? options, CancellationToken cancellationToken, int jobs = 1)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        PlannedVersion version = new PlannedVersion(dataset);
        CompactionOptions settings = SettingsFor(version, options);
        return await ReadEveryLeafAsync(version, settings, settings.StyleFor(version.Key is not null), jobs, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The version a plan reads, taken from the handle once. Loops that share a handle commit and
    /// refresh it while another plans, between two pages that one reads: a plan that asked the
    /// handle at every step would count the levels of one version and the tallies of another.
    /// </summary>
    private readonly struct PlannedVersion(VortexDataset dataset)
    {
        private readonly DatasetSnapshot _snapshot = dataset.Snapshot;

        /// <summary>The version.</summary>
        internal ulong Version => _snapshot.Version;

        /// <summary>Its levels, where level 0 is the one an append lands in.</summary>
        internal DatasetLevels Levels => _snapshot.Levels;

        /// <summary>Where its tree pages are read from.</summary>
        internal IPageSource Pages => _snapshot.Pages;

        /// <summary>The objects its level 0 holds above its ceiling.</summary>
        internal long Lag => _snapshot.Levels.LagAtLevelZero();

        /// <summary>Its clustering key, null when objects are kept in the order they arrived.</summary>
        internal ClusteringKey? Key => _snapshot.Schema.Key;

        /// <summary>The compaction settings its header carries.</summary>
        internal CompactionSettings Compaction => _snapshot.Header.Compaction;

        /// <summary>Whether its header says the store locks what it keeps.</summary>
        internal bool LockedStore => _snapshot.Header.LockedStore;

        /// <summary>The erasure delay its header carries; null for none.</summary>
        internal TimeSpan? ErasureDelay => _snapshot.Header.ErasureDelay;

        /// <summary>The handle's options, which no version changes.</summary>
        internal DatasetOptions Options { get; } = dataset.Options;
    }

    /// <summary>
    /// The options a plan works to: the caller's, under whatever the dataset's header states, and on a
    /// store that locks what it keeps no purge, which would add bytes for the lock's term and free none.
    /// </summary>
    /// <exception cref="ArgumentException">The options ask for tiered levels on a dataset with a clustering key.</exception>
    private static CompactionOptions SettingsFor(PlannedVersion dataset, CompactionOptions? options)
    {
        if (options is { Style: CompactionStyle.Tiered } && dataset.Key is not null)
        {
            // The style is not written in the dataset, so two writers could compact it under two;
            // refusing the one that breaks the key's invariant keeps every level above 0 a merge's.
            throw new ArgumentException(
                "A dataset with a clustering key compacts leveled: its key cursor, its deletes and its ranks take every level " +
                "above 0 to hold sorted objects whose keys do not overlap, and tiered levels hold objects that do.",
                nameof(options));
        }

        CompactionOptions settings = (options ?? new CompactionOptions()).From(dataset.Compaction);
        return dataset.LockedStore ? settings with { PurgeMarks = false } : settings;
    }

    private static async ValueTask<CompactionPlan> ReadEveryLeafAsync(
        PlannedVersion dataset, CompactionOptions settings, CompactionStyle style, int count, CancellationToken cancellationToken)
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
        PlannedVersion dataset,
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
        if (settings.PurgeMarks && MostMarked(levels, Marking.Of(dataset), taken) is { } marked)
        {
            return Purge(marked, Marking.Of(dataset), settings, style, FirstRow(dataset, marked));
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
        PlannedVersion dataset,
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
        LevelRange? range = style == CompactionStyle.Leveled && dataset.Key is { } key ? LevelRange.Of(to, sources, key) : null;
        if (range is not null && to < levels.Count)
        {
            // The destination objects the outputs would overlap are read too, so that the level
            // stays key-disjoint.
            foreach (CompactionInput candidate in levels[to])
            {
                if (range.Overlaps(candidate.Entry.Summaries))
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
            Range = range,
            Moves = range is not null && Moves(from, to, inputs, settings),
        };
    }

    /// <summary>
    /// Whether a leveled job may move its one object into the level above whole: an object of a level
    /// above 0, which a merge wrote sorted on the key, that overlaps nothing in the level it goes to,
    /// with no marks or fragments to fold, and no larger than what an object written there targets,
    /// so that a merge of it alone would write it again as one object of the same bounds. Whether it
    /// holds the current schema is known once it is opened, and one that does not is rewritten into it.
    /// </summary>
    private static bool Moves(int from, int to, List<CompactionInput> inputs, CompactionOptions settings) =>
        settings.TrivialMoves && from > 0 && to == from + 1 && inputs.Count == 1
        && inputs[0].Entry is { HasDeletions: false } entry && entry.Fragments.Count == 0
        && entry.Bytes <= settings.TargetBytes(to);

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
    private static long FirstRow(PlannedVersion dataset, CompactionInput first) =>
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

    /// <summary>
    /// The plan by descent: the same job the full read chooses, found from the levels' top pages, one
    /// descent to the object a full level gives up, one per level to where a tiered run ends, and
    /// walks that skip what the summaries rule out.
    /// </summary>
    private static async ValueTask<CompactionPlan> DescendAsync(
        PlannedVersion dataset, CompactionOptions settings, CompactionStyle style, ObjectTally[] tallies, int count, CancellationToken cancellationToken)
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
        PlannedVersion dataset,
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
            && await MostMarkedAsync(dataset, tallies, Marking.Of(dataset), taken, cancellationToken).ConfigureAwait(false) is { } marked)
        {
            return Purge(marked, Marking.Of(dataset), settings, style, FirstRow(dataset, marked));
        }

        List<CompactionInput> untaken = fragmented.FindAll(input => !taken.Contains(input.Level));
        return untaken.Count == 0
            ? null
            : new CompactionJob(untaken[0].Level, untaken[0].Level, style, CompactionTrigger.Fragments, untaken, 0, 0);
    }

    /// <summary>The tiered job that concatenates <paramref name="run"/> into the level above, and records where it stopped.</summary>
    private static CompactionJob Tiered(
        PlannedVersion dataset, List<CompactionInput> run, int from, CompactionOptions settings, CompactionTrigger trigger) =>
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
    private static async ValueTask<List<CompactionInput>> RunAsync(PlannedVersion dataset, int from, CancellationToken cancellationToken)
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
    private static async ValueTask<CompactionInput> NextAsync(PlannedVersion dataset, int level, CancellationToken cancellationToken)
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
        PlannedVersion dataset,
        List<CompactionInput> sources,
        int from,
        int to,
        CompactionOptions settings,
        CompactionTrigger trigger,
        ReadOnlyMemory<byte> stop,
        CancellationToken cancellationToken)
    {
        List<CompactionInput> inputs = [.. sources];
        LevelRange range = LevelRange.Of(to, sources, dataset.Key!);
        if (to < dataset.Levels.Count)
        {
            await foreach (PositionedEntry held in dataset.Levels[to]
                .WalkAsync(dataset.Pages, 0, long.MaxValue, range.MayOverlap, cancellationToken)
                .ConfigureAwait(false))
            {
                if (range.Overlaps(held.Entry.Value))
                {
                    inputs.Add(new CompactionInput(to, held.Entry.Key, ObjectEntry.FromBytes(held.Entry.Value)));
                }
            }
        }

        return new CompactionJob(from, to, CompactionStyle.Leveled, trigger, inputs, settings.TargetBytes(to), 0)
        {
            Stop = stop,
            Range = range,
            Moves = Moves(from, to, inputs, settings),
        };
    }

    /// <summary>
    /// The largest object of a level, the first of equals in key order as a full read would find
    /// it: at each page, the first entry whose tally holds the largest object below.
    /// </summary>
    private static async ValueTask<CompactionInput> LargestAsync(PlannedVersion dataset, int level, CancellationToken cancellationToken)
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
        PlannedVersion dataset, ObjectTally[] tallies, int most, CancellationToken cancellationToken)
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
    private static CompactionJob Purge(CompactionInput marked, Marking marking, CompactionOptions settings, CompactionStyle style, long firstRow) =>
        new CompactionJob(
            marked.Level,
            marked.Level,
            style,
            marking.Erases(marked.Entry) ? CompactionTrigger.Erasure : CompactionTrigger.Marks,
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
        PlannedVersion dataset, ObjectTally[] tallies, Marking marking, HashSet<int> taken, CancellationToken cancellationToken)
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
    /// vector, past which a delete rewrites the object itself. Under an erasure delay, at half the
    /// delay since its oldest mark instead of by its vector's bytes, so that the purge has the other
    /// half to run and the vector alone triggers none.
    /// </summary>
    /// <param name="Share">The divisor of a delete's share, <see cref="DatasetOptions.MarkedShare"/>.</param>
    /// <param name="Options">The handle's options, whose bound on a vector grows with the object.</param>
    /// <param name="ErasureSeconds">The erasure delay in seconds; 0 for none.</param>
    /// <param name="NowSeconds">The time the plan is made, in seconds since the Unix epoch.</param>
    private readonly record struct Marking(int Share, DatasetOptions Options, long ErasureSeconds, long NowSeconds)
    {
        public static Marking Of(PlannedVersion dataset) =>
            new Marking(
                Math.Max(dataset.Options.MarkedShare, 1),
                dataset.Options,
                dataset.ErasureDelay is { } delay ? Math.Max((long)delay.TotalSeconds, 1) : 0,
                dataset.Options.TimeProvider.GetUtcNow().ToUnixTimeSeconds());

        /// <summary>
        /// How due an object's marks are: its share of marked rows, or its vector's bytes, over half of
        /// a delete's bound, or under an erasure delay the age of its oldest mark over half the delay,
        /// whichever is further; due from 1. A mark of no known age is due.
        /// </summary>
        public double Load(ObjectEntry entry)
        {
            if (!entry.HasDeletions)
            {
                return 0;
            }

            double share = (double)entry.DeletedRows * 2 * Share / entry.PhysicalRows;
            if (ErasureSeconds > 0)
            {
                double age = entry.MarkedAt > 0 ? (double)(NowSeconds - entry.MarkedAt) * 2 / ErasureSeconds : 1;
                return Math.Max(share, age);
            }

            return Math.Max(share, (double)entry.VectorBytes * 2 / Options.MarkedVectorBytesOf(entry.Bytes));
        }

        /// <summary>
        /// Whether the purge <see cref="Load"/> calls due erases rows a delay owes: the age of the
        /// marks, and not their share, made it due.
        /// </summary>
        public bool Erases(ObjectEntry entry) =>
            ErasureSeconds > 0 && entry.HasDeletions
            && (double)entry.DeletedRows * 2 * Share / entry.PhysicalRows < 1;

        /// <summary>
        /// Whether an object under a tally may be due: a share rounded down past the due share rounded
        /// down, or a vector at half the least bound; under an erasure delay, any mark. A tally never
        /// rules out an object that is due.
        /// </summary>
        public bool MayBeDue(ObjectTally tally) =>
            ErasureSeconds > 0
                ? tally.DeletedRows > 0
                : tally.MostMarked >= ObjectTally.Whole / (2L * Share) || tally.LargestVector * 2 >= Math.Max(Options.MarkedVectorBytes, 1);
    }

    /// <summary>
    /// Every level's tally, from its top page alone; null when a level's top page holds a subtree
    /// written before tallies, whose leaves the plan then reads. A page's tally is known only when
    /// every page below it carries one, so a top page whose entries all have one vouches for its tree.
    /// </summary>
    private static async ValueTask<ObjectTally[]?> TalliesAsync(PlannedVersion dataset, CancellationToken cancellationToken)
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

    /// <summary>Every level's leaves, in key order, with the key each one sits at.</summary>
    private static async ValueTask<List<List<CompactionInput>>> ReadAsync(
        PlannedVersion dataset, CancellationToken cancellationToken)
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
