// The compaction planner, which descends a leveled dataset's trees rather than reading them.
//
// A PLAN BY DESCENT IS THE PLAN A FULL READ MAKES, or it is a different policy: the first test holds
// the two to the same job on random shapes -- levels of every size, objects of equal sizes, key
// ranges with holes and ranges nobody states, objects over their fragments, marked rows -- under
// random options, and counts that every trigger was met. The second holds the descent to what it is
// for: a plan over 25 000 objects asks the store for a few pages, where a full read asks for every
// leaf. The objects are entries only, since a plan reads no row.
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
        for (int seed = 0; seed < 200; seed++)
        {
            Random random = new Random(seed);
            await using MemoryObjectStore store = new MemoryObjectStore();
            IBoundaryRule rule = new ProllyBoundaryRule(Seed, minBytes: 256, targetBytes: 512, maxBytes: 1_024);
            await using VortexDataset dataset = await CreateAsync(store, rule, Shape(random), ct);
            CompactionOptions options = new CompactionOptions
            {
                LevelZeroCeiling = random.Next(2, 9),
                Fanout = random.Next(2, 11),
                TargetBytesAtLevelOne = 256L << random.Next(0, 6),
                MaxFragments = random.Next(0, 4),
            };

            CompactionPlan read = await CompactionPolicy.PlanByReadingEveryLeafAsync(dataset, options, ct);
            CompactionPlan descended = await dataset.PlanCompactionAsync(options, ct);
            Same(read, descended, seed);
            CompactionTrigger trigger = read.Job?.Trigger ?? CompactionTrigger.None;
            met[trigger] = met.GetValueOrDefault(trigger) + 1;
        }

        foreach (CompactionTrigger trigger in Enum.GetValues<CompactionTrigger>())
        {
            Assert.True(met.GetValueOrDefault(trigger) > 0, $"no shape met {trigger}, so the test proves nothing of it");
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
    /// The tally under a page, counted here from its leaves -- sums and maxima written out, not the
    /// tally's own arithmetic -- and held against the one every parent entry carries for it.
    /// </summary>
    private static async Task<ObjectTally> TallyAsync(VortexDataset dataset, PageReference reference, int depth, CancellationToken ct)
    {
        ReadOnlyMemory<byte> page = await dataset.Pages.ReadPageAsync(reference, ct);
        long bytes = 0;
        long largest = 0;
        long deleted = 0;
        long fragments = 0;
        if (depth == 1)
        {
            foreach (TreeEntry entry in TreePage.ReadLeaf(page))
            {
                ObjectEntry held = ObjectEntry.FromBytes(entry.Value);
                bytes += held.Bytes;
                largest = Math.Max(largest, held.Bytes);
                deleted += held.DeletedRows;
                fragments = Math.Max(fragments, held.Fragments.Count);
            }

            return new ObjectTally(bytes, largest, deleted, fragments);
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
        }

        return new ObjectTally(bytes, largest, deleted, fragments);
    }

    /// <summary>The dataset's fold, with the tally left out of every page, as a build before tallies wrote them.</summary>
    private sealed class Untallied : ISummaryFold
    {
        public ReadOnlyMemory<byte> OfLeaf(TreeEntry entry) => ObjectSummaryFold.Instance.OfLeaf(entry);

        public ReadOnlyMemory<byte> Union(IReadOnlyList<ReadOnlyMemory<byte>> parts, bool leaves) =>
            ObjectSummaryFold.SummariesOf(ObjectSummaryFold.Instance.Union(parts, leaves).Span, out _).ToArray();
    }

    /// <summary>A plan made on a handle opened afresh, and the reads it asked the store for beyond the open.</summary>
    private static async Task<(CompactionPlan Plan, long Requests)> ColdAsync(
        CountingObjectStore store, Func<VortexDataset, CancellationToken, Task<CompactionPlan>> plan, CancellationToken ct)
    {
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, Options(null), ct);
        store.Reset();
        CompactionPlan made = await plan(dataset, ct);
        return (made, store.Requests);
    }

    /// <summary>
    /// Random levels: a level 0 of overlapping ranges around its ceiling, levels above of disjoint
    /// ranges with holes, a few ranges nobody states, sizes drawn from a handful so that the largest
    /// ties, fragments up to six, and some objects with rows marked.
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
                    random.Next(8) == 0 ? random.Next(1, 20) : 0,
                    keyed: random.Next(25) != 0));
            }
        }

        return objects;
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
            CommitKey.ForData($"{level:x2}{low:x12}{high:x12}"), uid, high - low + 100, bytes, uid, references, summaries);
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

    /// <summary>A clustered dataset holding <paramref name="objects"/> at their levels, committed as entries.</summary>
    private static async Task<VortexDataset> CreateAsync(
        IObjectStore store, IBoundaryRule? rule, List<(int Level, ReadOnlyMemory<byte> Key, ObjectEntry Entry)> objects, CancellationToken ct)
    {
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = types.Struct(
            ["key", "measure"],
            [types.Primitive(PType.I64, Nullability.NonNullable), types.Primitive(PType.F64, Nullability.NonNullable)],
            Nullability.NonNullable);
        await using (VortexDataset created = await VortexDataset.CreateAsync(store, schema, Options(rule), ct))
        {
        }

        List<DatasetOperation> operations = [.. objects.Select(o => (DatasetOperation)new DatasetOperation.AddObject(o.Key, o.Entry) { Level = o.Level })];
        if (operations.Count > 0)
        {
            CommitResult committed = await DatasetCommitter.CommitAsync(store, operations, new CommitOptions { Seed = Seed, Rule = rule }, ct);
            Assert.All(committed.Outcomes, outcome => Assert.Equal(OperationOutcome.Applied, outcome));
        }

        return await VortexDataset.OpenAsync(store, Options(rule), ct);
    }

    private static DatasetOptions Options(IBoundaryRule? rule) => new DatasetOptions
    {
        Seed = Seed,
        ClusteringKey = ["key"],
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
        Assert.True(job.Objects.SequenceEqual(other.Objects), $"{shape}: {string.Join(",", job.Objects)} against {string.Join(",", other.Objects)}");
        Assert.Equal(
            job.Inputs.Select(input => (input.Level, Convert.ToHexString(input.Key.Span))),
            other.Inputs.Select(input => (input.Level, Convert.ToHexString(input.Key.Span))));
    }
}
