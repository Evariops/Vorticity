// The compaction planner, which descends a dataset's trees rather than reading them.
//
// A PLAN BY DESCENT IS THE PLAN A FULL READ MAKES, or it is a different policy: the first tests hold
// the two to the same job on random shapes -- levels of every size, objects of equal sizes, key
// ranges with holes and ranges nobody states, objects over their fragments, marked rows, levels
// interleaved in the tree's order, pointers before, between, on and past a level's objects -- under
// random options, and count that every trigger was met. Others hold the descent to what it is for:
// a plan over 25 000 objects asks the store for a few pages, where a full read asks for every leaf.
// The objects are entries only, since a plan reads no row.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.Scanning;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class DatasetPlanTests
{
    private const ulong Seed = 0x91A5_5EED;

    [Fact]
    public async Task ADescentChoosesTheJobAFullReadChooses()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Dictionary<CompactionTrigger, int> met = [];
        int ranks = 0;
        for (int seed = 0; seed < 200; seed++)
        {
            Random random = new Random(seed);
            await using MemoryObjectStore store = new MemoryObjectStore();
            IBoundaryRule rule = new ProllyBoundaryRule(Seed, minBytes: 256, targetBytes: 512, maxBytes: 1_024);
            List<(int Level, ReadOnlyMemory<byte> Key, ObjectEntry Entry)> shape = Shape(random);
            await using VortexDataset dataset = await PointAsync(store, rule, await CreateAsync(store, rule, shape, ct), shape, random, clustered: true, ct);
            CompactionOptions options = new CompactionOptions
            {
                LevelZeroCeiling = random.Next(2, 9),
                Fanout = random.Next(2, 11),
                TargetBytesAtLevelOne = 256L << random.Next(0, 6),
                MaxFragments = random.Next(0, 4),
                Pick = (CompactionPick)random.Next(3),
            };

            ranks = Math.Max(ranks, await HoldAsync(dataset, options, seed, met, ct));
        }

        Met(met, ranks);
    }

    [Fact]
    public async Task ATieredDescentChoosesTheJobAFullReadChooses()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Dictionary<CompactionTrigger, int> met = [];
        int ranks = 0;
        int longest = 0;
        for (int seed = 0; seed < 300; seed++)
        {
            // Datasets ordered by arrival, whose levels interleave as compactions and rewrites leave
            // them, and clustered ones merged in tiers, whose levels overlap.
            Random random = new Random(seed);
            bool clustered = seed % 3 == 0;
            await using MemoryObjectStore store = new MemoryObjectStore();
            IBoundaryRule rule = new ProllyBoundaryRule(Seed, minBytes: 256, targetBytes: 512, maxBytes: 1_024);
            List<(int Level, ReadOnlyMemory<byte> Key, ObjectEntry Entry)> shape = clustered ? Shape(random) : Arrivals(random);
            await using VortexDataset dataset = await PointAsync(store, rule, await CreateAsync(store, rule, shape, ct, clustered), shape, random, clustered, ct);
            CompactionOptions options = new CompactionOptions
            {
                LevelZeroCeiling = random.Next(2, 9),
                Fanout = random.Next(2, 11),
                TargetBytesAtLevelOne = 256L << random.Next(0, 6),
                MaxFragments = random.Next(0, 4),
                Style = CompactionStyle.Tiered,
                Pick = seed % 4 == 1 ? CompactionPick.Largest : seed % 4 == 2 ? CompactionPick.RoundRobin : CompactionPick.Auto,
            };

            // The longest run is planned by reading every leaf whichever way it is asked for.
            ranks = Math.Max(ranks, await HoldAsync(dataset, options, seed, met, ct));
            if (options.Pick != CompactionPick.Largest
                && (await dataset.PlanCompactionAsync(options, ct)).Job is { Trigger: CompactionTrigger.LevelZeroCeiling or CompactionTrigger.LevelSize } job)
            {
                longest = Math.Max(longest, job.Inputs.Count);
            }
        }

        Met(met, ranks);
        Assert.True(longest >= 4, $"no run was longer than {longest} objects, so a run's end is untested");
    }

    [Theory]
    [InlineData(CompactionStyle.Leveled)]
    [InlineData(CompactionStyle.Tiered)]
    public async Task ARoundRobinTakesALevelsObjectsInKeyOrderAndComesBackToTheFirst(CompactionStyle style)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();

        // Level 1 far over its size. Leveled, its six objects one after another, a level-2 object
        // under two of them; tiered, by arrival, with level-2 objects splitting them into the runs
        // [0, 1], [2] and [3, 4, 5].
        bool clustered = style == CompactionStyle.Leveled;
        List<(int Level, ReadOnlyMemory<byte> Key, ObjectEntry Entry)> objects = [];
        int[] levels = clustered ? [1, 1, 1, 1, 1, 1, 2, 2] : [1, 1, 2, 1, 2, 1, 1, 1];
        long position = 0;
        for (int i = 0; i < levels.Length; i++)
        {
            objects.Add(clustered
                ? Entry(levels[i], i < 6 ? i * 100L : ((i - 6) * 300L) + 40, i < 6 ? (i * 100L) + 50 : ((i - 6) * 300L) + 60, 1_000, 0, 0, keyed: true)
                : Arrival(levels[i], position, 100, 1_000, 0, 0));
            position += 100;
        }

        await using VortexDataset dataset = await CreateAsync(store, null, objects, ct, clustered);
        CompactionOptions options = new CompactionOptions { TargetBytesAtLevelOne = 256, Fanout = 2, Style = style, Pick = CompactionPick.RoundRobin };
        List<string> sources = [.. objects.Where(o => o.Level == 1).OrderBy(o => o.Key, KeyOrder.Instance).Select(o => o.Entry.Key)];
        int[][] runs = clustered ? [[0], [1], [2], [3], [4], [5]] : [[0, 1], [2], [3, 4, 5]];
        for (int job = 0; job < 2 * runs.Length; job++)
        {
            CompactionPlan plan = await dataset.PlanCompactionAsync(options, ct);
            Same(await CompactionPolicy.PlanByReadingEveryLeafAsync(dataset, options, ct), plan, job);
            CompactionJob due = Assert.IsType<CompactionJob>(plan.Job);
            Assert.Equal((CompactionTrigger.LevelSize, 1), (due.Trigger, due.FromLevel));
            List<CompactionInput> taken = [.. due.Inputs.Where(input => input.Level == 1)];
            Assert.Equal(runs[job % runs.Length].Select(at => sources[at]), taken.Select(input => input.Entry.Key));
            Assert.Equal(Convert.ToHexString(taken[^1].Key.Span), Convert.ToHexString(due.Stop.Span));

            // The job, as a rewrite that leaves its sources where they were: only the pointer moves.
            await DatasetCommitter.CommitAsync(
                store,
                [new DatasetOperation.ReplaceObjects([.. taken.Select(input => (1, input.Key))], [.. taken.Select(input => (1, input.Key, input.Entry))])
                {
                    Pointer = (1, due.Stop),
                }],
                new CommitOptions { Seed = Seed },
                ct);
            await dataset.RefreshAsync(ct);
            Assert.Equal(Convert.ToHexString(due.Stop.Span), Convert.ToHexString(dataset.Levels.PointerOf(1).Span));
        }
    }

    [Theory]
    [InlineData(25_000)]
    [InlineData(100_000)]
    public async Task ATieredPlanAsksForTheSameFewPagesWhateverTheObjectsALevelHolds(int deepest)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);

        // By arrival: level 1 far over its size, each of its objects between two of level 2's, so
        // that every run is one object long, and a few of level 0 after them all.
        List<(int Level, ReadOnlyMemory<byte> Key, ObjectEntry Entry)> objects = [];
        long position = 0;
        for (int i = 0; i < deepest; i++)
        {
            objects.Add(Arrival(i % 2 == 0 ? 2 : 1, position, 100, 1L << 20, 0, 0));
            position += 100;
        }

        for (int i = 0; i < 3; i++)
        {
            objects.Add(Arrival(0, position, 100, 1L << 20, 0, 0));
            position += 100;
        }

        await using (VortexDataset created = await CreateAsync(store, null, objects, ct, clustered: false))
        {
        }

        // The defaults: a dataset without a clustering key merges in tiers, by the run past the pointer.
        (CompactionPlan plan, long asked) = await ColdAsync(store, (dataset, token) => dataset.PlanCompactionAsync(null, token).AsTask(), ct, clustered: false);
        (CompactionPlan full, long read) = await ColdAsync(
            store, (dataset, token) => CompactionPolicy.PlanByReadingEveryLeafAsync(dataset, null, token).AsTask(), ct, clustered: false);
        Same(full, plan, seed: -1);
        CompactionJob job = Assert.IsType<CompactionJob>(plan.Job);
        Assert.Equal((CompactionTrigger.LevelSize, CompactionStyle.Tiered, 1, 2, 1), (job.Trigger, job.Style, job.FromLevel, job.ToLevel, job.Inputs.Count));

        Console.Out.Write(FormattableString.Invariant(
            $"TIERED PLAN: {objects.Count} objects over three levels: a descent asked the store for {asked} pages, a full read for {read}.\n"));

        // THE CONSTANT: the top pages the header inlines, one path in each level to the run's start
        // and to where it stops, and the run's leaf, whatever the levels hold.
        Assert.InRange(asked, 0, 8);
        Assert.True(read > asked, $"a full read asked for {read} pages and a descent for {asked}");
    }

    [Fact]
    public async Task AJobOverTheFragmentsOfTwoLevelsIsRankedOnceAndTakesThemBoth()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();

        // Nothing due but objects over their fragments, in levels 1 and 2: one job takes them all,
        // and no job ranked after it may take any of them again.
        List<(int Level, ReadOnlyMemory<byte> Key, ObjectEntry Entry)> objects =
        [
            Entry(1, 0, 10, 1_000, 3, 0, keyed: true),
            Entry(1, 20, 30, 1_000, 0, 0, keyed: true),
            Entry(2, 0, 10, 1_000, 3, 0, keyed: true),
            Entry(2, 40, 50, 1_000, 3, 0, keyed: true),
        ];
        await using VortexDataset dataset = await CreateAsync(store, null, objects, ct);
        CompactionOptions options = new CompactionOptions { MaxFragments = 1 };
        foreach (CompactionPlan plan in (CompactionPlan[])
            [
                await CompactionPolicy.PlanAsync(dataset, options, 4, ct),
                await CompactionPolicy.PlanByReadingEveryLeafAsync(dataset, options, ct, jobs: 4),
            ])
        {
            CompactionJob job = Assert.Single(plan.Jobs);
            Assert.Equal(CompactionTrigger.Fragments, job.Trigger);
            Assert.Equal([1, 2, 2], job.Inputs.Select(input => input.Level));
        }
    }

    [Theory]
    [InlineData(2_500)]
    [InlineData(25_000)]
    [InlineData(100_000)]
    public async Task APlanAsksForTheSameFewPagesWhateverTheObjectsALevelHolds(int deepest)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);

        // Level 2 far over its size, level 3 under it with four times the objects on the same keys:
        // the job is level 2's largest object, and the few objects of level 3 its range meets.
        List<(int Level, ReadOnlyMemory<byte> Key, ObjectEntry Entry)> objects = [Entry(1, 0, 10, 4L << 20, 0, 0, keyed: true)];
        for (int i = 0; i < deepest / 4; i++)
        {
            objects.Add(Entry(2, (i * 400L) + 1, (i * 400L) + 390, (4L << 20) + (i % 7 == 3 ? i : 0), 0, 0, keyed: true));
        }

        for (int i = 0; i < deepest; i++)
        {
            objects.Add(Entry(3, (i * 100L) + 5, (i * 100L) + 95, 1L << 20, 0, 0, keyed: true));
        }

        await using (VortexDataset created = await CreateAsync(store, null, objects, ct))
        {
        }

        (CompactionPlan plan, long asked) = await ColdAsync(store, (dataset, token) => dataset.PlanCompactionAsync(null, token).AsTask(), ct);
        (CompactionPlan full, long read) = await ColdAsync(store, (dataset, token) => CompactionPolicy.PlanByReadingEveryLeafAsync(dataset, null, token).AsTask(), ct);
        Same(full, plan, seed: -1);
        CompactionJob job = Assert.IsType<CompactionJob>(plan.Job);
        Assert.Equal((CompactionTrigger.LevelSize, 2, 3), (job.Trigger, job.FromLevel, job.ToLevel));
        Assert.Equal(objects.Where(o => o.Level == 2).Max(o => o.Entry.Bytes), job.Inputs[0].Entry.Bytes);
        Assert.InRange(job.Objects.Length, 2, 6);

        Console.Out.Write(FormattableString.Invariant(
            $"COMPACTION PLAN: {objects.Count} objects over three levels: a descent asked the store for {asked} pages, a full read for {read}.\n"));

        // THE CONSTANT: the top pages the header inlines, one leaf of level 2 to find its largest
        // object and the leaves of level 3 its range meets, whatever the levels hold.
        Assert.InRange(asked, 0, 6);
        Assert.True(read > asked, $"a full read asked for {read} pages and a descent for {asked}");
    }

    [Fact]
    public async Task EveryPageCarriesTheTallyOfTheObjectsUnderIt()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        for (int seed = 0; seed < 20; seed++)
        {
            await using MemoryObjectStore store = new MemoryObjectStore();
            await using VortexDataset dataset = await CreateAsync(
                store, new ProllyBoundaryRule(Seed, minBytes: 256, targetBytes: 512, maxBytes: 1_024), Shape(new Random(seed)), ct);
            for (int level = 0; level < dataset.Levels.Count; level++)
            {
                DatasetTree tree = dataset.Levels[level];
                if (!tree.IsEmpty)
                {
                    await TallyAsync(dataset, tree.Root, tree.Depth, ct);
                }
            }
        }
    }

    [Fact]
    public async Task ALevelWrittenBeforeTalliesIsPlannedByReadingItsLeaves()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        IBoundaryRule rule = new ProllyBoundaryRule(Seed, minBytes: 256, targetBytes: 512, maxBytes: 1_024);
        await using (VortexDataset created = await CreateAsync(store, rule, [], ct))
        {
        }

        // Level 2 over its size and level 3 beneath it, their pages summarised as a build before
        // tallies summarised them: the descent has nothing to descend by, and reads.
        List<DatasetOperation> operations = [];
        for (int i = 0; i < 300; i++)
        {
            (int level, ReadOnlyMemory<byte> key, ObjectEntry entry) = i < 100
                ? Entry(2, (i * 40L) + 1, (i * 40L) + 30, 4_000 + (i % 3), 0, 0, keyed: true)
                : Entry(3, ((i - 100) * 20L) + 2, ((i - 100) * 20L) + 12, 1_000, 0, 0, keyed: true);
            operations.Add(new DatasetOperation.AddObject(key, entry) { Level = level });
        }

        await DatasetCommitter.CommitAsync(store, operations, new CommitOptions { Seed = Seed, Rule = rule, Fold = new Untallied() }, ct);
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, Options(rule), ct);
        Assert.True(dataset.Levels[2].Depth > 1, "the level should span pages, or no summary of it is read");
        CompactionOptions options = new CompactionOptions { TargetBytesAtLevelOne = 256 };
        CompactionPlan plan = await dataset.PlanCompactionAsync(options, ct);
        Same(await CompactionPolicy.PlanByReadingEveryLeafAsync(dataset, options, ct), plan, seed: -1);
        Assert.Equal(CompactionTrigger.LevelSize, plan.Job?.Trigger);
    }

    /// <summary>
    /// The tally under a page, counted here from its leaves -- sums and maxima written out, the share
    /// and the vector read off the decoded entry, not the tally's own arithmetic -- and held against
    /// the one every parent entry carries for it.
    /// </summary>
    private static async Task<ObjectTally> TallyAsync(VortexDataset dataset, PageReference reference, int depth, CancellationToken ct)
    {
        ReadOnlyMemory<byte> page = await dataset.Pages.ReadPageAsync(reference, ct);
        long bytes = 0;
        long largest = 0;
        long deleted = 0;
        long fragments = 0;
        long marked = 0;
        long vector = 0;
        if (depth == 1)
        {
            foreach (TreeEntry entry in TreePage.ReadLeaf(page))
            {
                ObjectEntry held = ObjectEntry.FromBytes(entry.Value);
                bytes += held.Bytes;
                largest = Math.Max(largest, held.Bytes);
                deleted += held.DeletedRows;
                fragments = Math.Max(fragments, held.Fragments.Count);
                marked = Math.Max(marked, held.DeletedRows * ObjectTally.Whole / held.PhysicalRows);
                vector = Math.Max(vector, held.HasDeletions ? held.Deletions.ToBytes().Length : 0);
            }

            return new ObjectTally(bytes, largest, deleted, fragments, marked, vector);
        }

        foreach (InternalEntry child in TreePage.ReadInternal(page))
        {
            ObjectTally below = await TallyAsync(dataset, child.Child, depth - 1, ct);
            ObjectSummaryFold.SummariesOf(child.Summary.Span, out ObjectTally? carried);
            Assert.Equal(below, carried);
            bytes += below.Bytes;
            largest = Math.Max(largest, below.Largest);
            deleted += below.DeletedRows;
            fragments = Math.Max(fragments, below.MostFragments);
            marked = Math.Max(marked, below.MostMarked);
            vector = Math.Max(vector, below.LargestVector);
        }

        return new ObjectTally(bytes, largest, deleted, fragments, marked, vector);
    }

    /// <summary>Tree keys in the order a tree holds them.</summary>
    private sealed class KeyOrder : IComparer<ReadOnlyMemory<byte>>
    {
        public static KeyOrder Instance { get; } = new KeyOrder();

        public int Compare(ReadOnlyMemory<byte> x, ReadOnlyMemory<byte> y) => x.Span.SequenceCompareTo(y.Span);
    }

    /// <summary>The dataset's fold, with the tally left out of every page, as a build before tallies wrote them.</summary>
    private sealed class Untallied : ISummaryFold
    {
        public ReadOnlyMemory<byte> OfLeaf(TreeEntry entry) => ObjectSummaryFold.Instance.OfLeaf(entry);

        public ReadOnlyMemory<byte> Union(IReadOnlyList<ReadOnlyMemory<byte>> parts, bool leaves) =>
            ObjectSummaryFold.SummariesOf(ObjectSummaryFold.Instance.Union(parts, leaves).Span, out _).ToArray();
    }

    /// <summary>
    /// Holds a descent to the plan a full read makes, alone and ranked four deep with no two jobs on
    /// one level, counts the trigger met, and returns how many jobs were ranked.
    /// </summary>
    private static async Task<int> HoldAsync(
        VortexDataset dataset, CompactionOptions options, int seed, Dictionary<CompactionTrigger, int> met, CancellationToken ct)
    {
        CompactionPlan read = await CompactionPolicy.PlanByReadingEveryLeafAsync(dataset, options, ct);
        CompactionPlan descended = await dataset.PlanCompactionAsync(options, ct);
        Same(read, descended, seed);
        CompactionTrigger trigger = read.Job?.Trigger ?? CompactionTrigger.None;
        met[trigger] = met.GetValueOrDefault(trigger) + 1;

        CompactionPlan readRanked = await CompactionPolicy.PlanByReadingEveryLeafAsync(dataset, options, ct, jobs: 4);
        CompactionPlan descendedRanked = await CompactionPolicy.PlanAsync(dataset, options, 4, ct);
        Assert.Equal(readRanked.Jobs.Length, descendedRanked.Jobs.Length);
        HashSet<int> taken = [];
        for (int rank = 0; rank < readRanked.Jobs.Length; rank++)
        {
            Same(readRanked with { Job = readRanked.Jobs[rank] }, descendedRanked with { Job = descendedRanked.Jobs[rank] }, seed);
            HashSet<int> levels = [readRanked.Jobs[rank].FromLevel, readRanked.Jobs[rank].ToLevel, .. readRanked.Jobs[rank].Inputs.Select(input => input.Level)];
            Assert.False(taken.Overlaps(levels), $"seed {seed}: job {rank} touches a level a job ranked before it took");
            taken.UnionWith(levels);
        }

        return readRanked.Jobs.Length;
    }

    /// <summary>Fails unless every trigger was met and some shape ranked three jobs.</summary>
    private static void Met(Dictionary<CompactionTrigger, int> met, int ranks)
    {
        foreach (CompactionTrigger trigger in Enum.GetValues<CompactionTrigger>())
        {
            Assert.True(met.GetValueOrDefault(trigger) > 0, $"no shape met {trigger}, so the test proves nothing of it");
        }

        Assert.True(ranks >= 3, $"no shape ranked more than {ranks} jobs, so the ranking is untested past them");
    }

    /// <summary>
    /// The dataset with a pointer on some of its levels, as a level's last job leaves one: on one of
    /// its objects, just past one, before the first or past the last.
    /// </summary>
    private static async Task<VortexDataset> PointAsync(
        IObjectStore store,
        IBoundaryRule? rule,
        VortexDataset dataset,
        List<(int Level, ReadOnlyMemory<byte> Key, ObjectEntry Entry)> shape,
        Random random,
        bool clustered,
        CancellationToken ct)
    {
        List<DatasetOperation> operations = [];
        for (int level = 0; level < dataset.Levels.Count; level++)
        {
            List<ReadOnlyMemory<byte>> keys = [.. shape.Where(o => o.Level == level).Select(o => o.Key)];
            if (keys.Count == 0 || random.Next(4) == 0)
            {
                continue;
            }

            ReadOnlyMemory<byte> on = keys[random.Next(keys.Count)];
            byte[] pointer = random.Next(4) switch
            {
                0 => on.ToArray(),
                1 => [.. on.Span, 0],
                2 => [0],
                _ => [.. Enumerable.Repeat((byte)0xFF, 32)],
            };
            operations.Add(new DatasetOperation.ReplaceObjects([], []) { Pointer = (level, pointer) });
        }

        if (operations.Count == 0)
        {
            return dataset;
        }

        await DatasetCommitter.CommitAsync(store, operations, new CommitOptions { Seed = Seed, Rule = rule }, ct);
        await dataset.DisposeAsync();
        return await VortexDataset.OpenAsync(store, Options(rule, clustered), ct);
    }

    /// <summary>A plan made on a handle opened afresh, and the reads it asked the store for beyond the open.</summary>
    private static async Task<(CompactionPlan Plan, long Requests)> ColdAsync(
        CountingObjectStore store, Func<VortexDataset, CancellationToken, Task<CompactionPlan>> plan, CancellationToken ct, bool clustered = true)
    {
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, Options(null, clustered), ct);
        store.Reset();
        CompactionPlan made = await plan(dataset, ct);
        return (made, store.Requests);
    }

    /// <summary>
    /// Random levels: a level 0 of overlapping ranges around its ceiling, levels above of disjoint
    /// ranges with holes, a few ranges nobody states, sizes drawn from a handful so that the largest
    /// ties, fragments up to six, and some objects with rows marked: a few, or runs enough that the
    /// vector alone makes them due.
    /// </summary>
    private static List<(int Level, ReadOnlyMemory<byte> Key, ObjectEntry Entry)> Shape(Random random)
    {
        List<(int, ReadOnlyMemory<byte>, ObjectEntry)> objects = [];
        int levels = random.Next(1, 6);
        for (int level = 0; level < levels; level++)
        {
            int count = level == 0 ? random.Next(0, 14) : random.Next(0, 90);
            long at = random.Next(0, 50);
            for (int i = 0; i < count; i++)
            {
                long low = level == 0 ? random.Next(0, 5_000) : at + random.Next(0, 3) * 17;
                long high = low + random.Next(0, 60);
                at = high + 1 + random.Next(0, 40);
                objects.Add(Entry(
                    level,
                    low,
                    high,
                    1_000L * random.Next(1, 5),
                    random.Next(10) == 0 ? random.Next(1, 7) : 0,
                    random.Next(8) == 0 ? random.Next(1, 20) : random.Next(30) == 0 ? random.Next(150, 400) : 0,
                    keyed: random.Next(25) != 0));
            }
        }

        return objects;
    }

    /// <summary>
    /// Random levels of a dataset ordered by arrival: runs of objects one after another, each run in
    /// one level, so that levels interleave as rewrites and compactions leave them; sizes,
    /// fragments and marks drawn as <see cref="Shape"/> draws them.
    /// </summary>
    private static List<(int Level, ReadOnlyMemory<byte> Key, ObjectEntry Entry)> Arrivals(Random random)
    {
        List<(int, ReadOnlyMemory<byte>, ObjectEntry)> objects = [];
        int levels = random.Next(1, 6);
        int count = random.Next(0, 140);
        long position = random.Next(0, 50);
        while (objects.Count < count)
        {
            int level = random.Next(levels);
            for (int run = random.Next(1, 7); run > 0 && objects.Count < count; run--)
            {
                int deleted = random.Next(8) == 0 ? random.Next(1, 20) : random.Next(30) == 0 ? random.Next(150, 400) : 0;
                long rows = Math.Max(random.Next(100, 160), (3L * deleted) + 1);
                objects.Add(Arrival(level, position, rows, 1_000L * random.Next(1, 5), random.Next(10) == 0 ? random.Next(1, 7) : 0, deleted));
                position += rows + (random.Next(10) == 0 ? random.Next(1, 500) : 0);
            }
        }

        return objects;
    }

    /// <summary>
    /// An object's entry at a level of a dataset ordered by arrival: its position, which its key
    /// starts with, its rows, its bytes, its fragments and its marked rows.
    /// </summary>
    private static (int Level, ReadOnlyMemory<byte> Key, ObjectEntry Entry) Arrival(
        int level, long position, long rows, long bytes, int fragments, int deleted)
    {
        UInt128 uid = ((UInt128)(ulong)level << 96) | (ulong)position;
        List<PageReference> references = [];
        for (int f = 0; f < fragments; f++)
        {
            references.Add(new PageReference((ulong)f + 1, f * 64L, 64, uid + (uint)f));
        }

        ObjectEntry entry = new ObjectEntry(
            CommitKey.ForData($"{level:x2}{position:x12}"), uid, rows, bytes, uid, references, ObjectSummaries.Empty);
        if (deleted > 0)
        {
            entry = entry.WithDeletions(DeletionVector.Of([.. Enumerable.Range(0, deleted).Select(row => (long)row * 3)]));
        }

        byte[] key = new byte[24];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(key, position);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(key.AsSpan(8), (ulong)(uid >> 64));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(key.AsSpan(16), (ulong)uid);
        return (level, key, entry);
    }

    /// <summary>An object's entry at a level: its key range, its bytes, its fragments and its marked rows.</summary>
    private static (int Level, ReadOnlyMemory<byte> Key, ObjectEntry Entry) Entry(
        int level, long low, long high, long bytes, int fragments, int deleted, bool keyed)
    {
        UInt128 uid = ((UInt128)(ulong)level << 96) | ((UInt128)(ulong)low << 32) | (uint)high;
        ObjectSummaries summaries = keyed
            ? ObjectSummaries.From([new ColumnSummary("key", FilterLiteral.From(low), true, FilterLiteral.From(high), true, IsExact: true, 0, true)])
            : ObjectSummaries.Empty;
        List<PageReference> references = [];
        for (int f = 0; f < fragments; f++)
        {
            references.Add(new PageReference((ulong)f + 1, f * 64L, 64, uid + (uint)f));
        }

        ObjectEntry entry = new ObjectEntry(
            CommitKey.ForData($"{level:x2}{low:x12}{high:x12}"), uid, Math.Max(high - low + 100, (3L * deleted) + 1), bytes, uid, references, summaries);
        if (deleted > 0)
        {
            entry = entry.WithDeletions(DeletionVector.Of([.. Enumerable.Range(0, deleted).Select(row => (long)row * 3)]));
        }

        // The key a clustered dataset gives an object: its smallest key, in an order bytes keep, then its uid.
        byte[] key = new byte[24];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(key, (ulong)low ^ (1UL << 63));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(key.AsSpan(8), (ulong)(uid >> 64));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(key.AsSpan(16), (ulong)uid);
        return (level, key, entry);
    }

    /// <summary>A dataset holding <paramref name="objects"/> at their levels, committed as entries.</summary>
    private static async Task<VortexDataset> CreateAsync(
        IObjectStore store,
        IBoundaryRule? rule,
        List<(int Level, ReadOnlyMemory<byte> Key, ObjectEntry Entry)> objects,
        CancellationToken ct,
        bool clustered = true)
    {
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = types.Struct(
            ["key", "measure"],
            [types.Primitive(PType.I64, Nullability.NonNullable), types.Primitive(PType.F64, Nullability.NonNullable)],
            Nullability.NonNullable);
        await using (VortexDataset created = await VortexDataset.CreateAsync(store, schema, Options(rule, clustered), ct))
        {
        }

        List<DatasetOperation> operations = [.. objects.Select(o => (DatasetOperation)new DatasetOperation.AddObject(o.Key, o.Entry) { Level = o.Level })];
        if (operations.Count > 0)
        {
            CommitResult committed = await DatasetCommitter.CommitAsync(store, operations, new CommitOptions { Seed = Seed, Rule = rule }, ct);
            Assert.All(committed.Outcomes, outcome => Assert.Equal(OperationOutcome.Applied, outcome));
        }

        return await VortexDataset.OpenAsync(store, Options(rule, clustered), ct);
    }

    private static DatasetOptions Options(IBoundaryRule? rule, bool clustered = true) => new DatasetOptions
    {
        Seed = Seed,
        ClusteringKey = clustered ? ["key"] : null,
        Rule = rule,
    };

    private static void Same(CompactionPlan expected, CompactionPlan actual, int seed)
    {
        string shape = $"seed {seed}";
        Assert.True(expected.ObjectsByLevel.SequenceEqual(actual.ObjectsByLevel), shape);
        Assert.True(expected.BytesByLevel.SequenceEqual(actual.BytesByLevel), shape);
        Assert.Equal((expected.FragmentedObjects, expected.Lag, expected.Style, expected.IsClustered, expected.Version), (actual.FragmentedObjects, actual.Lag, actual.Style, actual.IsClustered, actual.Version));
        if (expected.Job is not { } job)
        {
            Assert.Null(actual.Job);
            return;
        }

        CompactionJob other = Assert.IsType<CompactionJob>(actual.Job);
        Assert.Equal((job.Trigger, job.FromLevel, job.ToLevel, job.TargetBytes, job.Rows, job.Bytes), (other.Trigger, other.FromLevel, other.ToLevel, other.TargetBytes, other.Rows, other.Bytes));
        Assert.Equal((job.Style, job.FirstRow, Convert.ToHexString(job.Stop.Span)), (other.Style, other.FirstRow, Convert.ToHexString(other.Stop.Span)));
        Assert.True(job.Objects.SequenceEqual(other.Objects), $"{shape}: {string.Join(",", job.Objects)} against {string.Join(",", other.Objects)}");
        Assert.Equal(
            job.Inputs.Select(input => (input.Level, Convert.ToHexString(input.Key.Span))),
            other.Inputs.Select(input => (input.Level, Convert.ToHexString(input.Key.Span))));
    }
}
