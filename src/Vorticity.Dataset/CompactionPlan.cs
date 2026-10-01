using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Vorticity.Dataset;

/// <summary>How a dataset merges its levels.</summary>
public enum CompactionStyle
{
    /// <summary>Leveled when the dataset declares a clustering key, tiered otherwise.</summary>
    Auto = 0,

    /// <summary>
    /// Key-disjoint objects inside every level above 0, so a lookup by key touches at most one
    /// object per level, at the price of rewriting each row about <c>F/2</c> times per level.
    /// </summary>
    Leveled = 1,

    /// <summary>
    /// Size tiers: a level holds up to <c>F</c> objects of its size and they may overlap. Rewrites
    /// each row about once per level, and a lookup is output-sensitive rather than bounded. Refused
    /// for a dataset with a clustering key, whose reads by key take every level above 0 to be
    /// key-disjoint.
    /// </summary>
    Tiered = 2,
}

/// <summary>Which objects a job takes from a level over its size.</summary>
public enum CompactionPick
{
    /// <summary>
    /// <see cref="Largest"/> for a leveled dataset, which writes fewer bytes that way, and
    /// <see cref="RoundRobin"/> for a tiered one, whose plan then descends the trees: a dataset
    /// ordered by arrival keeps each level's objects together, and the two take the same runs.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// The largest object of a leveled level, and the longest run of a tiered one that nothing else
    /// sits between: the most bytes one job moves. A tiered plan reads every leaf to find the run.
    /// </summary>
    Largest = 1,

    /// <summary>
    /// The object past where the level's last job stopped, and back to the first past the end, as
    /// LevelDB takes them: every key is rewritten in turn, wherever the largest objects lie. A tiered
    /// job takes the run that starts there, which a plan finds by descending the trees.
    /// </summary>
    RoundRobin = 2,
}

/// <summary>Why a compaction was planned.</summary>
public enum CompactionTrigger
{
    /// <summary>Nothing is over its bound.</summary>
    None = 0,

    /// <summary>Level 0 holds more objects than its ceiling allows.</summary>
    LevelZeroCeiling = 1,

    /// <summary>A level holds more bytes than its size.</summary>
    LevelSize = 2,

    /// <summary>An object carries more index fragments than the options allow.</summary>
    Fragments = 3,

    /// <summary>
    /// An object's deleted rows reach half of what a delete marks before it rewrites the object
    /// instead: the object is rewritten alone, in its level, without them.
    /// </summary>
    Marks = 4,
}

/// <summary>
/// The sizes and bounds compaction works to. A caller that overrides them owns the read bound that
/// follows. The fan-out, level 1's target and the level cap may also be written in the dataset
/// itself, so that its writers compact it to the same shape, and those win over these.
/// </summary>
public sealed record CompactionOptions
{
    internal const int DefaultFanout = 10;

    internal const long DefaultTargetBytesAtLevelOne = 128L << 10;

    internal const long DefaultMaxObjectBytes = 4L << 20;

    internal const int DefaultMaxFragments = 4;

    private readonly int _maxLevels;

    /// <summary>What level 0 may hold before the read bound degrades; 8 by default.</summary>
    public int LevelZeroCeiling { get; init; } = DatasetLevels.DefaultLevelZeroCeiling;

    /// <summary>The fan-out <c>F</c>, how much bigger each level is than the one below; 10 by default.</summary>
    public int Fanout { get; init; } = DefaultFanout;

    /// <summary>
    /// The size an object written into level 1 targets; 128 KiB by default. Each level above
    /// targets <c>F</c> times the one below, up to <see cref="MaxObjectBytes"/>, and holds <c>F</c>
    /// times as much whatever its objects' size: level 1 holds <c>F</c> of its objects.
    /// </summary>
    /// <remarks>
    /// Small, because level 1 is where level 0 empties when it holds a few small commits: merging
    /// them into a level of gigabytes would rewrite the level for a few rows, where a small one
    /// takes them for a few times their size and passes them on. A load too large for level 1
    /// goes past it, into the first level that holds it.
    /// </remarks>
    public long TargetBytesAtLevelOne { get; init; } = DefaultTargetBytesAtLevelOne;

    /// <summary>
    /// The cap on an output object, so that one stays a reasonable unit of rewrite: what a delete or
    /// an update of one of its rows rewrites, and what a compaction reads at least; 4 MiB by default.
    /// </summary>
    /// <remarks>
    /// Measured on ten million rows, a delete of ten rows cost 40 ms under a cap of 16 MiB and 14 ms
    /// under 4 MiB, rewriting 12 and 3 MB, while a scan of every row took 19 and 23 ms over nine and
    /// thirty-one objects. A dataset that is scanned far more than it is changed may raise it.
    /// </remarks>
    public long MaxObjectBytes { get; init; } = DefaultMaxObjectBytes;

    /// <summary>The index fragments an object may carry before a fragment compaction is due; 4 by default.</summary>
    public int MaxFragments { get; init; } = DefaultMaxFragments;

    /// <summary>
    /// Leveled, tiered, or whichever the clustering key implies. A plan refuses tiered for a dataset
    /// with a clustering key, which compacts leveled only.
    /// </summary>
    public CompactionStyle Style { get; init; } = CompactionStyle.Auto;

    /// <summary>Which objects a job takes from a level over its size, or whichever the style implies.</summary>
    public CompactionPick Pick { get; init; } = CompactionPick.Auto;

    /// <summary>Whether an object whose marks reach half a delete's bounds is rewritten without them; true by default.</summary>
    internal bool PurgeMarks { get; init; } = true;

    /// <summary>How many levels the dataset keeps, level 0 included; 0, the default, for no cap.</summary>
    /// <remarks>
    /// The top level is unbounded: compacting it into itself would rewrite key-disjoint objects
    /// into the same objects with no dead rows to reclaim, so the level below compacts into the top
    /// and the top only grows. A cap of 1 leaves level 0 nowhere to go and is refused.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative, or 1.</exception>
    public int MaxLevels
    {
        get => _maxLevels;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            if (value == 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value), value, "A dataset of one level has nowhere to compact level 0 into.");
            }

            _maxLevels = value;
        }
    }

    /// <summary>Whether a level is the top the cap allows, so that nothing compacts out of it.</summary>
    internal bool IsTop(int level) => _maxLevels > 0 && level >= _maxLevels - 1;

    /// <summary>
    /// The size an object written into a level targets, never above <see cref="MaxObjectBytes"/>.
    /// Levels 0 and 1 share the level-1 size.
    /// </summary>
    internal long TargetBytes(int level)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        long target = TargetBytesAtLevelOne;
        for (int i = 1; i < level; i++)
        {
            if (target >= MaxObjectBytes / Math.Max(Fanout, 1))
            {
                return MaxObjectBytes;
            }

            target *= Fanout;
        }

        return Math.Min(target, MaxObjectBytes);
    }

    /// <summary>
    /// What a level may hold before it is over its size: <c>F</c> times the level below, level 1
    /// holding <c>F</c> of its objects, whatever the cap on an object says. Level 0 is bounded by an
    /// object count instead and is refused here.
    /// </summary>
    /// <remarks>
    /// The capacity does not stop at <c>F</c> capped objects: a level would then hold no more than
    /// the one below it, and a dataset would need a level for every <c>F</c> such objects rather
    /// than one for every factor of <c>F</c>.
    /// </remarks>
    internal long CapacityBytes(int level)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        if (level == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(level),
                level,
                "Level 0 is bounded by its object count, not by its bytes; see LevelZeroCeiling.");
        }

        long fanout = Math.Max(Fanout, 1);
        long capacity = TargetBytesAtLevelOne;
        for (int i = 0; i < level; i++)
        {
            if (capacity >= long.MaxValue / fanout)
            {
                return long.MaxValue;
            }

            capacity *= fanout;
        }

        return capacity;
    }

    /// <summary>
    /// The level a leveled compaction of level 0 writes into: the first above it whose capacity
    /// holds what level 0 holds, never past the top a cap allows.
    /// </summary>
    /// <remarks>
    /// A few small commits then merge into a level a few times their size, and a load go past the
    /// levels it would only overflow, rather than being rewritten once per level on its way up.
    /// Nothing orders the levels among themselves but their sizes: rows in any level are rows of
    /// the dataset, and each level above 0 only has to stay key-disjoint.
    /// </remarks>
    internal int LevelZeroDestination(long bytes)
    {
        int level = 1;
        while (!IsTop(level) && Fanout > 1 && CapacityBytes(level) < bytes)
        {
            level++;
        }

        return level;
    }

    /// <summary>The pick a plan under <paramref name="style"/> works to, never <see cref="CompactionPick.Auto"/>.</summary>
    internal CompactionPick PickFor(CompactionStyle style) => Pick switch
    {
        CompactionPick.Largest => CompactionPick.Largest,
        CompactionPick.RoundRobin => CompactionPick.RoundRobin,
        _ => style == CompactionStyle.Tiered ? CompactionPick.RoundRobin : CompactionPick.Largest,
    };

    /// <summary>
    /// The style this dataset merges under, never <see cref="CompactionStyle.Auto"/>: leveled with a
    /// clustering key, whatever was asked, since a plan refuses tiered there first.
    /// </summary>
    internal CompactionStyle StyleFor(bool clustered) => Style switch
    {
        _ when clustered => CompactionStyle.Leveled,
        CompactionStyle.Leveled => CompactionStyle.Leveled,
        _ => CompactionStyle.Tiered,
    };

    /// <summary>
    /// These options with whatever the dataset's own header states; a zero field there means
    /// unstated and leaves this value alone.
    /// </summary>
    internal CompactionOptions From(CompactionSettings stored) => this with
    {
        Fanout = stored.Fanout > 0 ? stored.Fanout : Fanout,
        TargetBytesAtLevelOne = stored.LevelTargetBytes > 0 ? stored.LevelTargetBytes : TargetBytesAtLevelOne,
        MaxLevels = stored.Levels > 0 ? stored.Levels : MaxLevels,
    };
}

/// <summary>
/// One object a compaction reads. <c>Key</c> is its key in that level's tree, which the replacement
/// removes.
/// </summary>
internal readonly record struct CompactionInput(int Level, ReadOnlyMemory<byte> Key, ObjectEntry Entry);

/// <summary>One compaction: what it reads, where it writes, and why.</summary>
public sealed record CompactionJob
{
    internal CompactionJob(
        int fromLevel,
        int toLevel,
        CompactionStyle style,
        CompactionTrigger trigger,
        IReadOnlyList<CompactionInput> inputs,
        long targetBytes,
        long firstRow)
    {
        FromLevel = fromLevel;
        ToLevel = toLevel;
        Style = style;
        Trigger = trigger;
        Inputs = inputs;
        TargetBytes = targetBytes;
        FirstRow = firstRow;
        ImmutableArray<string>.Builder objects = ImmutableArray.CreateBuilder<string>(inputs.Count);
        foreach (CompactionInput input in inputs)
        {
            objects.Add(input.Entry.Key);
            Rows += input.Entry.Rows;
            Bytes += input.Entry.Bytes;
        }

        Objects = objects.MoveToImmutable();
    }

    /// <summary>The level it empties.</summary>
    public int FromLevel { get; init; }

    /// <summary>The level it writes into; the same level for a fragment compaction.</summary>
    public int ToLevel { get; init; }

    /// <summary>Whether it merges on the clustering key or concatenates.</summary>
    public CompactionStyle Style { get; init; }

    /// <summary>Why it is due.</summary>
    public CompactionTrigger Trigger { get; init; }

    /// <summary>The size each object it writes targets; 0 for a fragment compaction, which rewrites no row.</summary>
    public long TargetBytes { get; init; }

    /// <summary>The keys of the objects it reads, the source level's first.</summary>
    public ImmutableArray<string> Objects { get; init; }

    /// <summary>The rows it reads, which are the rows it writes.</summary>
    public long Rows { get; init; }

    /// <summary>The bytes it reads, which are about the bytes it will write.</summary>
    public long Bytes { get; init; }

    /// <summary>The objects it reads, each with its level and tree key, the source level's first.</summary>
    internal IReadOnlyList<CompactionInput> Inputs { get; init; }

    /// <summary>
    /// Where the outputs of a compaction of a dataset ordered by arrival are keyed from: the first
    /// input's own position, so that they take its place.
    /// </summary>
    internal long FirstRow { get; init; }

    /// <summary>
    /// The tree key of the last object the job takes from its source level, which that level's next
    /// job starts past under a round robin; empty for a job that moves no pointer.
    /// </summary>
    internal ReadOnlyMemory<byte> Stop { get; init; }
}

/// <summary>
/// What compaction is due on one version, and what it would cost: a value a caller inspects before
/// deciding to run the rewrite.
/// </summary>
public sealed record CompactionPlan
{
    /// <summary>The version planned.</summary>
    public ulong Version { get; init; }

    /// <summary>How many objects each level holds, level 0 first.</summary>
    public ImmutableArray<long> ObjectsByLevel { get; init; }

    /// <summary>How many bytes each level holds, level 0 first.</summary>
    public ImmutableArray<long> BytesByLevel { get; init; }

    /// <summary>What level 0 holds above its ceiling: the extra objects every lookup touches until compaction catches up.</summary>
    public long Lag { get; init; }

    /// <summary>Whether the dataset declares a clustering key.</summary>
    public bool IsClustered { get; init; }

    /// <summary>The style the dataset merges under.</summary>
    public CompactionStyle Style { get; init; }

    /// <summary>The compaction due, or null when nothing is over its bound.</summary>
    public CompactionJob? Job { get; init; }

    /// <summary>
    /// The compactions due that the plan was asked to rank, most urgent first, no two of which read or
    /// write one level; <see cref="Job"/> is the first.
    /// </summary>
    internal ImmutableArray<CompactionJob> Jobs { get; init; } = [];

    /// <summary>
    /// The objects carrying more index fragments than <see cref="CompactionOptions.MaxFragments"/>;
    /// compacting those reads index bytes only, and is planned once no compaction that moves rows is due.
    /// </summary>
    public long FragmentedObjects { get; init; }

    /// <summary>Whether a compaction is due.</summary>
    public bool HasWork => Job is not null;
}
