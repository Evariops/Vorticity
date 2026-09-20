using System;
using System.Collections.Generic;

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

    /// <summary>An entry carries more index fragments than the options allow.</summary>
    Fragments = 3,
}

/// <summary>
/// The sizes and bounds compaction works to. A caller that overrides them owns the read bound that
/// follows. The fan-out and level 1's target are also written in every header, and
/// <see cref="From"/> reads them back from there, so that two writers of one dataset compact it to
/// the same shape.
/// </summary>
public sealed record CompactionOptions
{
    public const int DefaultFanout = 10;

    public const long DefaultTargetBytesAtLevelOne = 256L << 20;

    public const long DefaultMaxObjectBytes = 4L << 30;

    public const int DefaultMaxFragments = 4;

    /// <summary>What level 0 may hold before the read bound degrades.</summary>
    public int LevelZeroCeiling { get; init; } = DatasetLevels.DefaultLevelZeroCeiling;

    /// <summary>The fan-out <c>F</c>: how much bigger each level is than the one below.</summary>
    public int Fanout { get; init; } = DefaultFanout;

    /// <summary>The size an object written into level 1 targets.</summary>
    public long TargetBytesAtLevelOne { get; init; } = DefaultTargetBytesAtLevelOne;

    /// <summary>The cap on an output object, so that one stays a reasonable unit of rewrite.</summary>
    public long MaxObjectBytes { get; init; } = DefaultMaxObjectBytes;

    /// <summary>The fragments an entry may carry before a fragment compaction is due.</summary>
    public int MaxFragments { get; init; } = DefaultMaxFragments;

    /// <summary>Leveled, tiered, or whichever the clustering key implies.</summary>
    public CompactionStyle Style { get; init; } = CompactionStyle.Auto;

    /// <summary>
    /// How many levels the dataset keeps, level 0 included; 0, the default, for no cap. Taken from
    /// the header's <see cref="CompactionSettings.Levels"/> when it states one.
    /// </summary>
    /// <remarks>
    /// The top level is unbounded: compacting it into itself would rewrite key-disjoint objects
    /// into the same objects with no dead rows to reclaim, so the level below compacts into the top
    /// and the top only grows. A cap of 1 leaves level 0 nowhere to go and is refused.
    /// </remarks>
    public int MaxLevels
    {
        get => _maxLevels;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            if (value == 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value), value, "A dataset of one level has nowhere to compact level 0 into (13 §5.2).");
            }

            _maxLevels = value;
        }
    }

    private readonly int _maxLevels;

    /// <summary>Whether a level is the top the cap allows, so that nothing compacts out of it.</summary>
    public bool IsTop(int level) => _maxLevels > 0 && level >= _maxLevels - 1;

    /// <summary>
    /// The size an object written into a level targets, never above <see cref="MaxObjectBytes"/>.
    /// Levels 0 and 1 share the level-1 size.
    /// </summary>
    public long TargetBytes(int level)
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
    public long CapacityBytes(int level)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        if (level == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(level),
                level,
                "Level 0 is bounded by its object count, not by its bytes (13 §5.2); see LevelZeroCeiling.");
        }

        long target = TargetBytes(level);
        return target >= long.MaxValue / Math.Max(Fanout, 1) ? long.MaxValue : target * Fanout;
    }

    /// <summary>The style this dataset merges under, never <see cref="CompactionStyle.Auto"/>.</summary>
    public CompactionStyle StyleFor(bool clustered) => Style switch
    {
        CompactionStyle.Leveled => CompactionStyle.Leveled,
        CompactionStyle.Tiered => CompactionStyle.Tiered,
        _ => clustered ? CompactionStyle.Leveled : CompactionStyle.Tiered,
    };

    /// <summary>
    /// These options with whatever the dataset's own header states; a zero field there means
    /// unstated and leaves this value alone.
    /// </summary>
    public CompactionOptions From(CompactionSettings stored) => this with
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
public readonly record struct CompactionInput(int Level, ReadOnlyMemory<byte> Key, ObjectEntry Entry);

/// <summary>
/// One compaction: what it reads, where it writes, and why. <c>Inputs</c> lists the source level's
/// objects first, and <c>FirstRow</c> is where their rows start in the dataset, which is what an
/// unclustered output's leaf key is derived from.
/// </summary>
public sealed record CompactionJob(
    int FromLevel,
    int ToLevel,
    CompactionStyle Style,
    CompactionTrigger Trigger,
    IReadOnlyList<CompactionInput> Inputs,
    long TargetBytes,
    long FirstRow)
{
    public long Rows
    {
        get
        {
            long rows = 0;
            foreach (CompactionInput input in Inputs)
            {
                rows += input.Entry.Rows;
            }

            return rows;
        }
    }

    /// <summary>The bytes it reads, which are about the bytes it will write.</summary>
    public long Bytes
    {
        get
        {
            long bytes = 0;
            foreach (CompactionInput input in Inputs)
            {
                bytes += input.Entry.Bytes;
            }

            return bytes;
        }
    }
}

/// <summary>
/// What compaction is due on one version, and what it would cost; the plan is a value so that a
/// caller can inspect the rewrite before deciding to run it. <c>Lag</c> is what level 0 holds above
/// its ceiling, and <c>Job</c> is null when nothing is over its bound. <c>FragmentedObjects</c>
/// counts objects carrying more than <see cref="CompactionOptions.MaxFragments"/> index fragments;
/// compacting those reads index bytes only and is planned once no trigger that moves data is due.
/// </summary>
public sealed record CompactionPlan(
    ulong Version,
    IReadOnlyList<long> ObjectsByLevel,
    IReadOnlyList<long> BytesByLevel,
    long Lag,
    bool IsClustered,
    CompactionStyle Style,
    CompactionJob? Job,
    long FragmentedObjects)
{
    public bool HasWork => Job is not null;
}
