// What compaction would do, before it does it - docs/13-dataset.md §5.3 and §5.4.
//
// A PLAN IS A VALUE, AND THAT IS THE POINT. §5.3 makes compaction "the user's background job": the
// library never decides on its own to rewrite gigabytes, so the decision has to be inspectable
// before it is taken. A caller reads the plan, sees which level is over its size and what the
// rewrite would cost in bytes, and runs it or does not. It is also how the policy is tested: a
// trigger is a function from a version to a job, and a function can be asserted on without writing
// a single object.
//
// THE TWO NUMBERS OF §5, RECONCILED. §5.3 sizes the OBJECTS a compaction writes ("256 MiB at level
// 1, growing with the level, capped at 4 GiB"); §5.2 sizes the LEVELS ("level i holds up to F^i
// times the size of level 1", and, for tiers, "at most F of them"). One formula satisfies both:
// an object at level i targets `level1 x F^(i-1)`, and a level holds `F` of them. Then level 1
// holds F x level1 = F^1 x level1, level 2 holds F^2 x level1, and the tiered bullet's "at most F
// of them" is the same sentence read from the other side.
using System;
using System.Collections.Generic;

namespace Vorticity.Dataset;

/// <summary>How a dataset merges its levels (§5.4).</summary>
public enum CompactionStyle
{
    /// <summary>Leveled when the dataset declares a clustering key, tiered otherwise (§5.4).</summary>
    Auto = 0,

    /// <summary>
    /// Key-disjoint objects inside every level above 0, so a lookup by key touches at most one
    /// object per level. Rewrites each row about <c>F/2</c> times per level it crosses (§5.4).
    /// </summary>
    Leveled = 1,

    /// <summary>
    /// Size tiers: a level holds up to <c>F</c> objects of its size and they may overlap. Rewrites
    /// each row about once per level, and a lookup is output-sensitive rather than bounded (§5.2).
    /// </summary>
    Tiered = 2,
}

/// <summary>Why a compaction was planned (§5.3's triggers).</summary>
public enum CompactionTrigger
{
    /// <summary>Nothing is over its bound.</summary>
    None = 0,

    /// <summary>Level 0 holds more objects than §5.2 allows.</summary>
    LevelZeroCeiling = 1,

    /// <summary>A level holds more bytes than its size.</summary>
    LevelSize = 2,

    /// <summary>An entry carries more than <c>K</c> index fragments (§6.4).</summary>
    Fragments = 3,
}

/// <summary>The numbers §5 states, as options a dataset can override.</summary>
/// <remarks>
/// <para>
/// Overridable because every one of them is absurd in a test: a level-1 target of 256 MiB would
/// make one object of a hundred appended rows, and no compaction would ever be exercised. They are
/// the same kind of setting as the chunker's in §4.1 — the defaults are the specification's, and a
/// caller that changes them owns the read bound that follows.
/// </para>
/// <para>
/// Two of them are also written in every header, as <see cref="CompactionSettings"/>: the fan-out
/// and level 1's target size. <see cref="From"/> reads them from there, the way the boundary rule
/// reads the chunker's, so that two writers of one dataset compact it to the same shape rather than
/// to whatever each of them was constructed with.
/// </para>
/// </remarks>
public sealed record CompactionOptions
{
    /// <summary>The default fan-out <c>F</c> of §5.2.</summary>
    public const int DefaultFanout = 10;

    /// <summary>The default object size at level 1 (§5.3): 256 MiB.</summary>
    public const long DefaultTargetBytesAtLevelOne = 256L << 20;

    /// <summary>The largest object a compaction writes (§5.3): 4 GiB.</summary>
    public const long DefaultMaxObjectBytes = 4L << 30;

    /// <summary>The runs per index entry an object may carry before it is compacted (§5.2's K).</summary>
    public const int DefaultMaxFragments = 4;

    /// <summary>What level 0 may hold before the read bound degrades (§5.2).</summary>
    public int LevelZeroCeiling { get; init; } = DatasetLevels.DefaultLevelZeroCeiling;

    /// <summary>The fan-out <c>F</c>: how much bigger each level is than the one below (§5.2).</summary>
    public int Fanout { get; init; } = DefaultFanout;

    /// <summary>The size an object written into level 1 targets (§5.3).</summary>
    public long TargetBytesAtLevelOne { get; init; } = DefaultTargetBytesAtLevelOne;

    /// <summary>The cap on an output object, "so that an object stays a reasonable unit of rewrite".</summary>
    public long MaxObjectBytes { get; init; } = DefaultMaxObjectBytes;

    /// <summary>The fragments an entry may carry before §6.4's fragment compaction is due.</summary>
    public int MaxFragments { get; init; } = DefaultMaxFragments;

    /// <summary>Leveled, tiered, or whichever the clustering key implies (§5.4).</summary>
    public CompactionStyle Style { get; init; } = CompactionStyle.Auto;

    /// <summary>The size an object written into <paramref name="level"/> targets.</summary>
    /// <param name="level">The destination level; 0 and 1 share the level-1 size.</param>
    /// <returns>The target, never above <see cref="MaxObjectBytes"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is negative.</exception>
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

    /// <summary>What <paramref name="level"/> may hold before it is over its size (§5.2).</summary>
    /// <param name="level">The level; level 0 is bounded by a count, not by bytes.</param>
    /// <returns>Its capacity in bytes.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is not above zero.</exception>
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

    /// <summary>The style this dataset actually merges under (§5.4's default).</summary>
    /// <param name="clustered">Whether the dataset declares a clustering key.</param>
    /// <returns>Leveled or tiered, never <see cref="CompactionStyle.Auto"/>.</returns>
    public CompactionStyle StyleFor(bool clustered) => Style switch
    {
        CompactionStyle.Leveled => CompactionStyle.Leveled,
        CompactionStyle.Tiered => CompactionStyle.Tiered,
        _ => clustered ? CompactionStyle.Leveled : CompactionStyle.Tiered,
    };

    /// <summary>These options with whatever the dataset's own header states (§4.1).</summary>
    /// <param name="stored">The header's settings; a zero field means "unstated".</param>
    /// <returns>The options a writer of that dataset compacts under.</returns>
    /// <remarks>
    /// <c>Levels</c> is deliberately not read: it caps how many levels a dataset keeps, and what a
    /// planner should do at the cap — stop, or merge the top level into itself — is a retention
    /// question (§10), not a trigger. Reading it here and doing nothing with it would be worse than
    /// leaving it to the step that answers it.
    /// </remarks>
    public CompactionOptions From(CompactionSettings stored) => this with
    {
        Fanout = stored.Fanout > 0 ? stored.Fanout : Fanout,
        TargetBytesAtLevelOne = stored.LevelTargetBytes > 0 ? stored.LevelTargetBytes : TargetBytesAtLevelOne,
    };
}

/// <summary>One object a compaction reads.</summary>
/// <param name="Level">Where it sits (§5.2).</param>
/// <param name="Key">Its key in that level's tree, which the replacement removes.</param>
/// <param name="Entry">Its leaf entry.</param>
public readonly record struct CompactionInput(int Level, ReadOnlyMemory<byte> Key, ObjectEntry Entry);

/// <summary>One compaction: what it reads, where it writes, and why (§5.3).</summary>
/// <param name="FromLevel">The level the trigger fired on.</param>
/// <param name="ToLevel">Where the outputs go.</param>
/// <param name="Style">Leveled (a merge) or tiered (a concatenation).</param>
/// <param name="Trigger">What made it due.</param>
/// <param name="Inputs">The objects it reads, the source level's first.</param>
/// <param name="TargetBytes">The size each output targets.</param>
/// <param name="FirstRow">
/// Where the inputs' rows start in the dataset, which an unclustered output's leaf key is derived
/// from (§4.1: "ordered by first row position").
/// </param>
public sealed record CompactionJob(
    int FromLevel,
    int ToLevel,
    CompactionStyle Style,
    CompactionTrigger Trigger,
    IReadOnlyList<CompactionInput> Inputs,
    long TargetBytes,
    long FirstRow)
{
    /// <summary>The rows it rewrites.</summary>
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

    /// <summary>The bytes it reads, which are also about the bytes it writes (§5.4's price).</summary>
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

/// <summary>What compaction is due on one version, and what it would cost (§5.3).</summary>
/// <param name="Version">The version it was planned against.</param>
/// <param name="ObjectsByLevel">How many objects each level holds, level 0 first.</param>
/// <param name="BytesByLevel">Their bytes, per level.</param>
/// <param name="Lag">The objects level 0 holds above its ceiling (§5.1).</param>
/// <param name="IsClustered">Whether a clustering key is declared.</param>
/// <param name="Style">Leveled or tiered, as §5.4 decides it for this dataset.</param>
/// <param name="Job">The compaction to run, or null when nothing is over its bound.</param>
/// <param name="FragmentedObjects">
/// Objects carrying more than <see cref="CompactionOptions.MaxFragments"/> index fragments. §5.3
/// names that trigger next to the other two, but it is a fragment compaction (§6.4) that reads
/// index bytes only: counted here, executed with the fragments of step 42.
/// </param>
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
    /// <summary>Whether anything is due.</summary>
    public bool HasWork => Job is not null;
}
