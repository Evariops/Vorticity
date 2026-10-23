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

    /// <summary>
    /// By periods of time, for a dataset that declares them (<see cref="DatasetOptions.Periods"/>), and
    /// only for one: level 0's rows past every fresh object go to the first fresh level and the others
    /// to a late level, <c>F</c> adjacent objects of one period in a fresh level merge into the next,
    /// and the late level merges into itself past level 0's ceiling. A lookup by key opens at most
    /// level 0, the late level and one object per other level.
    /// </summary>
    Periods = 3,
}

/// <summary>Which objects a job takes from a level over its size.</summary>
public enum CompactionPick
{
    /// <summary>
    /// For a leveled dataset, a level's first object when it moves up whole, as it does when keys
    /// arrive in order, so that each level keeps one band of the key space, and otherwise
    /// <see cref="Largest"/>, which writes fewer bytes that way. <see cref="RoundRobin"/> for a tiered
    /// one, whose plan then descends the trees: a dataset ordered by arrival keeps each level's
    /// objects together, and the two take the same runs.
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

    /// <summary>
    /// An object's oldest mark reaches half of the dataset's erasure delay
    /// (<see cref="DatasetOptions.ErasureDelay"/>): the object is rewritten alone, in its level,
    /// without its deleted rows, before the delay passes.
    /// </summary>
    Erasure = 5,

    /// <summary>
    /// At a level's end, at least as many adjacent small objects of one size as the fan-out, which keys
    /// in order leave there: they are merged into one, in their level, so that objects grow with keys in
    /// order as merges make them grow with keys at random.
    /// </summary>
    Neighbours = 6,

    /// <summary>
    /// The late level of a dataset compacted by periods holds more objects than level 0's ceiling: they
    /// are merged into one object per period, in that level.
    /// </summary>
    LateCeiling = 7,

    /// <summary>
    /// A fresh level of a dataset compacted by periods holds <c>F</c> adjacent objects of one period:
    /// they are merged into one object of the next fresh level.
    /// </summary>
    FreshMerge = 8,

    /// <summary>
    /// A period of a dataset compacted by periods has ended, and the lateness horizon past it too: its
    /// fresh objects, its late rows and its earlier object if it has one are written into one object of
    /// the periods level, with its time slices. A sealed period is sealed again when its late rows
    /// crowd the late level.
    /// </summary>
    Seal = 9,

    /// <summary>
    /// The periods level of a dataset compacted by periods holds <c>F</c> adjacent small objects of one
    /// partition, each under an <c>F</c>-th of <see cref="CompactionOptions.JoinedPeriodBytes"/>: they are
    /// joined into one object of that level, which spans their periods.
    /// </summary>
    Join = 10,
}

/// <summary>
/// How a job of a dataset compacted by periods cuts what it writes: an object per period, and in
/// level 0's exit, the rows whose instant lies past <c>FreshAfter</c> into the first fresh level and
/// the others into the late level.
/// </summary>
/// <param name="Calendar">Where the periods start and end.</param>
/// <param name="FreshAfter">
/// In level 0's exit, the largest instant a fresh object or a period holds, past which a row is fresh;
/// null when there is none, every row being fresh. Ignored by the other jobs, whose rows all go to
/// their destination.
/// </param>
/// <param name="Exit">Whether the job is level 0's exit, which routes rows between two levels.</param>
/// <param name="Slices">Whether the job is a seal, whose object declares its time slices.</param>
internal sealed record PeriodCut(PeriodCalendar Calendar, long? FreshAfter, bool Exit, bool Slices = false)
{
    /// <summary>The unit the instant column counts in.</summary>
    internal TimeUnit Unit => Calendar.Unit;

    /// <summary>
    /// For a seal, the newest instant the dataset held when it was planned, in any partition: a change of
    /// length the period it writes asks for takes effect past it.
    /// </summary>
    internal long? Newest { get; init; }

    /// <summary>
    /// For a seal, the largest object the dataset's other partitions sealed of the period, its bytes and
    /// rows, (0, 0) for none or without partitions: the length is weighed on the largest object of the
    /// period, every one of which it bounds. Null while another partition still holds rows of the period
    /// below the level of periods, when the seal weighs nothing, the period not yet sealed whole.
    /// </summary>
    internal (long Bytes, long Rows)? Peers { get; init; }

    /// <summary>The lateness horizon, in the instant's unit: in level 0's exit, a row of a period it has closed comes later than it.</summary>
    internal long Horizon { get; init; }

    /// <summary>
    /// The periods, from the start of the first to the end of the last, of the one object a join writes, or a seal
    /// of the late rows of a period a joined object holds, which rewrites the whole object; null for a job that
    /// writes an object per period.
    /// </summary>
    internal (long Start, long End)? Span { get; init; }

    /// <summary>Whether level 0's exit takes a row of <paramref name="period"/> later than the horizon: its period closed by the newest instant held.</summary>
    internal bool PastHorizon(long period) =>
        Exit && FreshAfter is { } newest && newest != long.MaxValue && Calendar.Next(period) <= newest - Horizon;

    /// <summary>The level a row of <paramref name="instant"/> goes to in a job whose destination is <paramref name="to"/>.</summary>
    internal int LevelOf(long instant, int to) =>
        !Exit ? to : FreshAfter is { } after && instant <= after ? PeriodLevels.Late : PeriodLevels.FirstFresh;
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

    internal const long DefaultMaxObjectBytes = 64L << 20;

    internal const int DefaultMaxFragments = 4;

    internal const long DefaultJoinedPeriodBytes = 1L << 30;

    private readonly int _maxLevels;

    private readonly long _joinedPeriodBytes = DefaultJoinedPeriodBytes;

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
    /// The cap on an output object, so that one stays a reasonable unit of work: what a compaction reads
    /// at least, and what a change that lands while it runs makes it give up; 64 MiB by default.
    /// </summary>
    /// <remarks>
    /// A delete marks rows rather than rewrite the object, so the cap no longer bounds what a delete
    /// costs: it weighs an object's per-object costs, its opens, entries and requests, against the work
    /// a large object loses. On the churn bench, at ten million rows, caps of 16 and 64 MiB against
    /// 4 MiB took 5 % fewer requests a commit and a quarter fewer requests a filter or a scan, at the
    /// same commit latency, compaction taking 6 % less time for 5 % more bytes. The cap binds from level
    /// 4 up with the default level-one target, so a dataset under a gigabyte keeps its levels' own sizes.
    /// </remarks>
    public long MaxObjectBytes { get; init; } = DefaultMaxObjectBytes;

    /// <summary>The index fragments an object may carry before a fragment compaction is due; 4 by default.</summary>
    public int MaxFragments { get; init; } = DefaultMaxFragments;

    /// <summary>
    /// The size a dataset of periods joins its small sealed objects up to; 1 GiB by default, 0 to join none.
    /// <see cref="Fanout"/> adjacent objects of one partition in the periods level, each under an <c>F</c>-th of
    /// it, join into one object that spans their periods, so that a partition whose periods are small, a tenant
    /// among larger ones, keeps objects near this size rather than one a period.
    /// </summary>
    /// <remarks>
    /// The calendar of the periods is the dataset's, weighed on its largest partition: a partition that takes a
    /// hundredth of the rows seals objects a hundred times smaller. A joined object grows in tiers, as the fresh
    /// levels do, a row rewritten once each time its object grows <c>F</c>-fold. It never spans two months of the
    /// calendar, an expiry keeping it until the last period it holds lies before the cutoff, and under an erasure delay
    /// it stays what a purge rewrites within half of it.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public long JoinedPeriodBytes
    {
        get => _joinedPeriodBytes;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _joinedPeriodBytes = value;
        }
    }

    /// <summary>
    /// Leveled, tiered, or whichever the clustering key implies. A plan refuses tiered for a dataset
    /// with a clustering key, which compacts leveled only.
    /// </summary>
    public CompactionStyle Style { get; init; } = CompactionStyle.Auto;

    /// <summary>Which objects a job takes from a level over its size, or whichever the style implies.</summary>
    public CompactionPick Pick { get; init; } = CompactionPick.Auto;

    /// <summary>Whether an object whose marks reach half a delete's bounds is rewritten without them; true by default.</summary>
    internal bool PurgeMarks { get; init; } = true;

    /// <summary>
    /// Whether a job of one object that overlaps nothing in the level above moves the object's entry
    /// there instead of rewriting the object; true by default, false to measure what the moves save.
    /// </summary>
    internal bool TrivialMoves { get; init; } = true;

    /// <summary>
    /// Whether small neighbours at a level's end are merged (<see cref="CompactionTrigger.Neighbours"/>);
    /// true by default, false to measure what the merges cost and what they grow.
    /// </summary>
    internal bool MergeNeighbours { get; init; } = true;

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
    /// Whether a leveled level gives up its first object when that one moves up whole, before the pick
    /// chooses: under <see cref="CompactionPick.Auto"/>, which keeps the bands keys in order leave.
    /// </summary>
    internal bool MovesFirst(CompactionStyle style) => Pick == CompactionPick.Auto && style == CompactionStyle.Leveled;

    /// <summary>
    /// The style this dataset merges under, never <see cref="CompactionStyle.Auto"/>: by periods for a
    /// dataset that declares them, leveled with a clustering key, whatever was asked, since a plan
    /// refuses the others there first.
    /// </summary>
    internal CompactionStyle StyleFor(bool clustered, bool periods = false) => Style switch
    {
        _ when periods => CompactionStyle.Periods,
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

        ObjectKeys = objects.MoveToImmutable();
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
    public ImmutableArray<string> ObjectKeys { get; init; }

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

    /// <summary>
    /// The part of the destination level a leveled job takes over, which its commit checks no object
    /// it did not read has moved into; null for a job that rewrites objects inside their own level,
    /// or concatenates.
    /// </summary>
    internal LevelRange? Range { get; init; }

    /// <summary>
    /// Whether the job may move its one object into the level above whole, its entry taken out of one
    /// tree and put into the other byte for byte, with no row read or written. A merge of that object
    /// alone would write its rows as one object of the same bounds.
    /// </summary>
    internal bool Moves { get; init; }

    /// <summary>How a job of a dataset compacted by periods cuts its outputs; null for any other job.</summary>
    internal PeriodCut? Cut { get; init; }
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

    /// <summary>
    /// How many values of its partition columns the dataset holds rows of, counted from the objects' summaries
    /// the plan reads; 0 for a dataset without partition columns (<see cref="DatasetOptions.PartitionColumns"/>).
    /// </summary>
    /// <remarks>
    /// <see cref="DatasetOptions.MaxPartitionValues"/> bounds the values one append holds, which catches a column
    /// of identifiers declared as a partition. Nothing bounds the values a dataset gathers over its life: each one
    /// grows periods of its own, and this count, which the meter records too, is what shows them pile up.
    /// </remarks>
    public int Partitions { get; init; }

    /// <summary>Whether a compaction is due.</summary>
    public bool HasWork => Job is not null;
}
