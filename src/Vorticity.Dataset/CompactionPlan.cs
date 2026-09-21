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
    /// each row about once per level, and a lookup is output-sensitive rather than bounded.
    /// </summary>
    Tiered = 2,
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
}

/// <summary>
/// The sizes and bounds compaction works to. A caller that overrides them owns the read bound that
/// follows. The fan-out, level 1's target and the level cap may also be written in the dataset
/// itself, so that its writers compact it to the same shape, and those win over these.
/// </summary>
public sealed record CompactionOptions
{
    internal const int DefaultFanout = 10;

    internal const long DefaultTargetBytesAtLevelOne = 256L << 20;

    internal const long DefaultMaxObjectBytes = 4L << 30;

    internal const int DefaultMaxFragments = 4;

    private readonly int _maxLevels;

    /// <summary>What level 0 may hold before the read bound degrades; 8 by default.</summary>
    public int LevelZeroCeiling { get; init; } = DatasetLevels.DefaultLevelZeroCeiling;

    /// <summary>The fan-out <c>F</c>, how much bigger each level is than the one below; 10 by default.</summary>
    public int Fanout { get; init; } = DefaultFanout;

    /// <summary>The size an object written into level 1 targets; 256 MiB by default.</summary>
    public long TargetBytesAtLevelOne { get; init; } = DefaultTargetBytesAtLevelOne;

    /// <summary>The cap on an output object, so that one stays a reasonable unit of rewrite; 4 GiB by default.</summary>
    public long MaxObjectBytes { get; init; } = DefaultMaxObjectBytes;

    /// <summary>The index fragments an object may carry before a fragment compaction is due; 4 by default.</summary>
    public int MaxFragments { get; init; } = DefaultMaxFragments;

    /// <summary>Leveled, tiered, or whichever the clustering key implies.</summary>
    public CompactionStyle Style { get; init; } = CompactionStyle.Auto;

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
    /// What a level may hold before it is over its size. Level 0 is bounded by an object count
    /// instead and is refused here.
    /// </summary>
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

        long target = TargetBytes(level);
        return target >= long.MaxValue / Math.Max(Fanout, 1) ? long.MaxValue : target * Fanout;
    }

    /// <summary>The style this dataset merges under, never <see cref="CompactionStyle.Auto"/>.</summary>
    internal CompactionStyle StyleFor(bool clustered) => Style switch
    {
        CompactionStyle.Leveled => CompactionStyle.Leveled,
        CompactionStyle.Tiered => CompactionStyle.Tiered,
        _ => clustered ? CompactionStyle.Leveled : CompactionStyle.Tiered,
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
    /// The objects carrying more index fragments than <see cref="CompactionOptions.MaxFragments"/>;
    /// compacting those reads index bytes only, and is planned once no compaction that moves rows is due.
    /// </summary>
    public long FragmentedObjects { get; init; }

    /// <summary>Whether a compaction is due.</summary>
    public bool HasWork => Job is not null;
}
