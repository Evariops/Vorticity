// The rollups a dataset keeps over its deepest level: written by RollUpAsync, taken away by the commits that change what
// they cover, kept and swept by the vacuum.
//
// WHAT IS HELD: after a call, the tree of rollups holds rollups of rank 1 over runs of one hour each, and rollups of rank 2
// over them, each summing exactly the objects between its first and last tree keys, as the version holds them now (its
// fingerprint the commit's), each value's rows the sum of its objects'; a second call puts none. A delete in one hour takes
// away the rollups over that hour's objects and no other; an expiry those over the hours it removes, a later call summing
// the runs that changed again. A vacuum deletes the objects of the rollups no version names and keeps the others, and the
// dataset verifies.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetRollupsTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] Services = ["api", "auth", "billing", "cart"];

    private const int Hours = 12;

    private const int RowsPerHour = 3_000;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ACallSumsTheDeepestLevelIntoRollupsOfTwoRanks()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await WriteAsync(store);
        int put = await dataset.RollUpAsync(Ct);
        List<RollupEntry> rollups = await RollupsAsync(dataset);
        Console.WriteLine($"ROLLUPS {dataset.Snapshot.Levels[rollups[0].Level].Entries} objects in level {rollups[0].Level}, {put} put: {string.Join(", ", rollups.GroupBy(r => r.Rank).Select(g => $"{g.Count()} of rank {g.Key}"))}");
        Assert.Equal(put, rollups.Count);
        Assert.Contains(rollups, r => r.Rank == 1);
        Assert.Contains(rollups, r => r.Rank == 2);
        Assert.True(dataset.Snapshot.Header.Features.HasFlag(DatasetFeatures.Rollups));

        // A rollup of rank 1 holds one hour; one of rank 2 keeps each of its hours apart.
        Assert.All(rollups.Where(r => r.Rank == 1), r => Assert.Single(r.Buckets));
        Assert.Contains(rollups, r => r.Rank == 2 && r.Buckets.Count > 1);
        await HoldsAsync(dataset, rollups);

        // Each rank covers the rollups of the rank below but those of its last run, which stays open until more objects
        // come: the rollups it leaves are the last ones.
        for (int rank = 2; rank <= rollups.Max(r => r.Rank); rank++)
        {
            List<RollupEntry> lower = [.. rollups.Where(r => r.Rank == rank - 1).OrderBy(r => Convert.ToHexString(r.FirstKey.Span), StringComparer.Ordinal)];
            List<RollupEntry> upper = [.. rollups.Where(r => r.Rank == rank)];
            bool[] covered = [.. lower.Select(child => upper.Any(parent =>
                parent.FirstKey.Span.SequenceCompareTo(child.FirstKey.Span) <= 0 && child.LastKey.Span.SequenceCompareTo(parent.LastKey.Span) <= 0))];
            int left = Array.IndexOf(covered, false);
            Assert.True(left < 0 || covered[left..].All(held => !held), $"rank {rank} leaves rollup {left} of rank {rank - 1} out, and covers one after it");
        }

        // A rollup of rank 1's totals of the service are its objects' counts, summed.
        RollupEntry one = rollups.First(r => r.Rank == 1 && r.Children > 1);
        Dictionary<string, long> expected = [];
        await foreach (TreeEntry entry in DatasetCommitter.RangeAsync(dataset.Snapshot.Levels[one.Level], one.FirstKey, one.LastKey, dataset.Snapshot.Pages, Ct))
        {
            ColumnCounts counts = (await dataset.ReadCountsAsync(ObjectEntry.FromBytes(entry.Value), 1, sketches: false, Ct)).Counts
                ?? throw new InvalidOperationException("An object keeps no counts of the service.");
            foreach (CountGroup group in counts.Groups)
            {
                string name = System.Text.Encoding.UTF8.GetString(group.Value ?? []);
                expected[name] = expected.GetValueOrDefault(name) + counts.WordsOf(group.FirstEntry)[0];
            }
        }

        ColumnCounts totals = await TotalsAsync(store, one, 1);
        Assert.Equal(expected.OrderBy(e => e.Key, StringComparer.Ordinal), totals.Groups.Select(g => KeyValuePair.Create(System.Text.Encoding.UTF8.GetString(g.Value ?? []), totals.WordsOf(g.FirstEntry)[0])));

        // Nothing changed: a second call, and another handle's, put none.
        Assert.Equal(0, await dataset.RollUpAsync(Ct));
        await using VortexDataset other = await VortexDataset.OpenAsync(store, Options(), Ct);
        Assert.Equal(0, await other.RollUpAsync(Ct));
        Assert.True((await dataset.VerifyAsync(cancellationToken: Ct)).Holds);
    }

    [Fact]
    public async Task ADeleteTakesAwayTheRollupsOverItsObjectsAndAVacuumTheirs()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await WriteAsync(store);
        await dataset.RollUpAsync(Ct);
        List<RollupEntry> before = await RollupsAsync(dataset);
        int level = before[0].Level;
        Dictionary<string, string> objects = await EntriesAsync(dataset, level);

        // Rows of the third hour deleted: the rollups over the objects that held them go, of every rank; the others stay.
        DateTimeOffset from = Noon.AddHours(2);
        RowChangeResult deleted = await dataset.DeleteAsync(VortexExpr.Parse(
            $"Service = 'api' and At >= '{from:O}' and At < '{from.AddMinutes(30):O}'"), Ct);
        Assert.True(deleted.Rows > 0);
        Dictionary<string, string> now = await EntriesAsync(dataset, level);
        byte[][] changed = [.. objects.Keys.Union(now.Keys)
            .Where(key => !objects.TryGetValue(key, out string? was) || !now.TryGetValue(key, out string? is_) || was != is_)
            .Select(Convert.FromHexString)];
        Assert.NotEmpty(changed);
        List<RollupEntry> after = await RollupsAsync(dataset);
        Console.WriteLine($"ROLLUPS {before.Count} before the delete, {after.Count} after; {changed.Length} objects changed");
        Assert.DoesNotContain(after, r => changed.Any(key => r.Covers(key)));
        Assert.Equal(before.Where(r => !changed.Any(key => r.Covers(key))).Select(r => r.Key).Order(), after.Select(r => r.Key).Order());
        Assert.True(after.Count < before.Count);
        await HoldsAsync(dataset, after);

        // The objects of the rollups taken away are swept; those of the others stay; the dataset verifies.
        VacuumResult vacuum = await dataset.VacuumAsync(new VacuumOptions { TimeProvider = new Later(TimeSpan.FromHours(1)) }, Ct);
        string[] gone = [.. before.Select(r => r.Key).Except(after.Select(r => r.Key))];
        Assert.NotEmpty(gone);
        Assert.All(gone, key => Assert.Contains(key, vacuum.Deleted));
        foreach (RollupEntry rollup in after)
        {
            Assert.NotNull(await store.HeadAsync(rollup.Key, Ct));
        }

        Assert.True((await dataset.VerifyAsync(cancellationToken: Ct)).Holds);
    }

    [Fact]
    public async Task AnExpiryTakesAwayTheRollupsOfTheHoursItRemoves()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await WriteAsync(store);
        await dataset.RollUpAsync(Ct);

        // The first hour expired: no rollup over it is left, and a later call sums the runs its going changed.
        long first = Image(Noon);
        await dataset.ExpireAsync(VortexExpr.Parse($"At < '{Noon.AddHours(1):O}'"), Ct);
        List<RollupEntry> after = await RollupsAsync(dataset);
        Assert.DoesNotContain(after, r => r.Buckets.Any(b => b.Image.SignedValue == first));
        await HoldsAsync(dataset, after);
        int again = await dataset.RollUpAsync(Ct);
        List<RollupEntry> summed = await RollupsAsync(dataset);
        Console.WriteLine($"ROLLUPS after the expiry: {after.Count}, then {again} put");
        Assert.DoesNotContain(summed, r => r.Buckets.Any(b => b.Image.SignedValue == first));
        await HoldsAsync(dataset, summed);
    }

    public static TheoryData<string> Questions() =>
    [
        "count by service", "count by service and hour", "measures by service, hours two to eight", "count by status, one service",
        "median bytes by service", "count by service, half an hour cut",
    ];

    [Theory]
    [MemberData(nameof(Questions))]
    public async Task AQuestionTheRollupsAnswerAnswersAsTheRowsDo(string question)
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await WriteAsync(store);
        await dataset.RollUpAsync(Ct);
        List<string> rows = await AskAsync(dataset, question, synopses: false);
        List<string> states = await AskAsync(dataset, question, synopses: true);
        ScanPlan plan = await Ask(dataset, question, synopses: true).ExplainAsync(Ct);
        int rollups = plan.States.Count(step => step.Read == "rollup");
        int objects = plan.States.Count(step => step.Read == "states");
        int byBucket = plan.States.Count(step => step.Read == "rollup" && step.Detail.Contains("by bucket", StringComparison.Ordinal));
        Console.WriteLine($"ROLLUPS {question}: {rollups} rollups, {byBucket} of them by bucket, and {objects} objects' counts answer, {plan.States.Length - rollups - objects} other steps");
        Assert.NotEmpty(rows);
        Assert.Equal(rows, states);
        if (question is "count by service" or "measures by service, hours two to eight" or "median bytes by service" or "count by service and hour")
        {
            Assert.True(rollups > 0, $"{question}: no rollup answered");
        }

        // A rollup of rank 2 or more keeps its hours apart, which answer a question by the hour bucket by bucket.
        if (question == "count by service and hour")
        {
            Assert.True(byBucket > 0, $"{question}: no rollup answered by bucket");
        }
    }

    [Fact]
    public async Task TheRollupsReadFewerObjectsThanTheCounts()
    {
        // The same question asked of a fresh handle before the rollups are written and after: the counts of every object,
        // then the totals of a few rollups.
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using (VortexDataset written = await WriteAsync(memory))
        {
        }

        (long requests, long bytes) before = await CostAsync(memory, "count by service");
        await using (VortexDataset dataset = await VortexDataset.OpenAsync(memory, Options(), Ct))
        {
            Assert.True(await dataset.RollUpAsync(Ct) > 0);
        }

        (long requests, long bytes) after = await CostAsync(memory, "count by service");
        Console.WriteLine($"ROLLUPS count by service: {before.requests} requests and {before.bytes} bytes with the objects' counts, {after.requests} and {after.bytes} with the rollups");
        Assert.True(after.requests < before.requests, $"{after.requests} requests with the rollups, {before.requests} without");
    }

    /// <summary>The requests and bytes a fresh handle's answer to <paramref name="question"/> costs, its opening apart.</summary>
    private static async Task<(long Requests, long Bytes)> CostAsync(MemoryObjectStore memory, string question)
    {
        await using CountingObjectStore store = new CountingObjectStore(memory);
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, Options(), Ct);
        store.Reset();
        await AskAsync(dataset, question, synopses: true);
        return (store.Requests, store.BytesRead);
    }

    /// <summary>The groups <paramref name="question"/> answers, each a line of its values, sorted.</summary>
    private static async Task<List<string>> AskAsync(VortexDataset dataset, string question, bool synopses)
    {
        List<string> rows = [];
        await using (IAsyncEnumerator<RecordBatch> batches = Ask(dataset, question, synopses).Query.Batches(Ct))
        {
            while (await batches.MoveNextAsync())
            {
                Vorticity.Tests.Writing.Values.DescribeRows(batches.Current, rows);
            }
        }

        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    private static Vorticity.Aggregation Ask(VortexDataset dataset, string question, bool synopses)
    {
        Scan<Hit> scan = dataset.Scan<Hit>().With(new ScanOptions { UseSynopses = synopses });
        DateTimeOffset from = Noon.AddHours(2);
        DateTimeOffset to = Noon.AddHours(9);
        DateTimeOffset cut = Noon.AddMinutes(150);
        return question switch
        {
            "count by service" => scan.GroupBy(h => h.Service).Select(g => (g.Key, g.Count())),
            "count by service and hour" => scan.GroupBy(h => (h.Service, h.At.Truncate(CalendarUnit.Hour))).Select(g => (g.Key.Service, g.Key.Item2, g.Count())),
            "measures by service, hours two to eight" => scan.Where(h => h.At >= from && h.At < to).GroupBy(h => h.Service)
                .Select(g => (g.Key, g.Count(), g.Sum(h => h.Bytes), g.Min(h => h.Bytes), g.Max(h => h.Bytes))),
            "count by status, one service" => scan.Where(h => h.Service == "api").GroupBy(h => h.Status).Select(g => (g.Key, g.Count())),
            "median bytes by service" => scan.GroupBy(h => h.Service).Select(g => (g.Key, g.Quantile(h => h.Bytes, 0.5))),
            "count by service, half an hour cut" => scan.Where(h => h.At >= cut).GroupBy(h => h.Service).Select(g => (g.Key, g.Count())),
            _ => throw new ArgumentException($"No question named '{question}'.", nameof(question)),
        };
    }

    [Fact]
    public async Task ASchemaChangeTakesEveryRollupAway()
    {
        // A rollup names its columns by their places in the schema: a change of it leaves none to answer for other columns.
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await WriteAsync(store);
        await dataset.RollUpAsync(Ct);
        Assert.NotEmpty(await RollupsAsync(dataset));
        VortexField[] fields = [.. Enumerable.Range(0, Hit.Schema.Count).Select(i => Hit.Schema[i])];
        await dataset.EvolveSchemaAsync(VortexSchema.Create([.. fields, new VortexField("Region", VortexType.Utf8.Nullable)]), cancellationToken: Ct);
        Assert.Empty(await RollupsAsync(dataset));
        Assert.True((await dataset.VerifyAsync(cancellationToken: Ct)).Holds);
    }

    /// <summary>The entries of level <paramref name="level"/>, by the hex of their tree keys, each the hex of its value.</summary>
    private static async Task<Dictionary<string, string>> EntriesAsync(VortexDataset dataset, int level)
    {
        Dictionary<string, string> entries = [];
        await foreach (TreeEntry entry in dataset.Snapshot.Levels[level].EnumerateAsync(dataset.Snapshot.Pages, Ct))
        {
            entries[Convert.ToHexString(entry.Key.Span)] = Convert.ToHexString(entry.Value.Span);
        }

        return entries;
    }

    /// <summary>Every rollup the version holds sums what it covers as the version holds it now.</summary>
    private static async Task HoldsAsync(VortexDataset dataset, List<RollupEntry> rollups)
    {
        foreach (RollupEntry rollup in rollups)
        {
            (UInt128 fingerprint, int children) = await DatasetCommitter.SummedAsync(dataset.Snapshot.Levels, rollup, dataset.Snapshot.Pages, Ct);
            Assert.Equal((rollup.Fingerprint, rollup.Children), (fingerprint, children));
        }
    }

    private static async Task<List<RollupEntry>> RollupsAsync(VortexDataset dataset)
    {
        List<RollupEntry> rollups = [];
        if (dataset.Snapshot.Levels.Rollups.IsEmpty)
        {
            return rollups;
        }

        await foreach (TreeEntry entry in dataset.Snapshot.Levels.Rollups.EnumerateAsync(dataset.Snapshot.Pages, Ct))
        {
            rollups.Add(RollupEntry.FromBytes(entry.Key.Span, entry.Value.Span));
        }

        return rollups;
    }

    /// <summary>The totals of column <paramref name="field"/> a rollup's object holds.</summary>
    private static async Task<ColumnCounts> TotalsAsync(MemoryObjectStore store, RollupEntry rollup, int field)
    {
        byte[] bytes = new byte[rollup.Bytes];
        await store.ReadAsync(rollup.Key, 0, bytes, null, Ct);
        IndexSegment region = rollup.ColumnOf(field)!.Value.Counts;
        ReadOnlySpan<byte> counts = bytes.AsSpan((int)region.Offset, (int)region.Length);
        Assert.True(region.Holds(counts));
        return ColumnCounts.From(counts);
    }

    /// <summary>The hour of <paramref name="at"/>, as the clustering value stores it: microseconds.</summary>
    private static long Image(DateTimeOffset at) => (at - DateTimeOffset.UnixEpoch).Ticks / 10;

    private static DatasetOptions Options() => new DatasetOptions
    {
        Seed = 0x5EED_0F_A11,
        SheetObjectBytes = 0,
        ContentsObjectBytes = 0,
        RollupFanout = 2,
        RetentionWindow = TimeSpan.FromSeconds(1),
    };

    /// <summary>The clock of a vacuum run past the retention window.</summary>
    private sealed class Later(TimeSpan by) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + by;
    }

    /// <summary>
    /// Four hours of hits appended in parts of ten minutes and compacted to the end, cut by the hour and at 16 KiB over
    /// blocks of a few kilobytes: a dozen objects or more an hour in the deepest level, each keeping its counts.
    /// </summary>
    private static async Task<VortexDataset> WriteAsync(MemoryObjectStore store)
    {
        VortexDataset dataset = await VortexDataset.CreateAsync(store, Hit.Schema, Options() with
        {
            ClusteringKey = [nameof(Hit.At)],
            Clustering = new ClusteringOptions { Value = ClusteringValue.Truncate(CalendarUnit.Hour), ObjectBytes = 12L << 10 },
            Write = new VortexWriteOptions { RowBlockSize = 256, DataBlockTargetBytes = 2 << 10 },
        }, Ct);
        Random random = new Random(29);
        int part = RowsPerHour / 6;
        for (int row = 0; row < Hours * RowsPerHour; row += part)
        {
            Hit[] hits = new Hit[part];
            for (int i = 0; i < part; i++)
            {
                hits[i] = new Hit(
                    Noon.AddSeconds((row + i) * 3_600.0 / RowsPerHour),
                    Services[random.Next(Services.Length)],
                    (short)(random.Next(10) < 8 ? 200 : 500),
                    random.NextInt64(100, 100_000));
            }

            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Hit>(hits, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 1 }, Ct) is not null)
        {
        }

        return dataset;
    }

    [VortexRecord]
    public partial record struct Hit(DateTimeOffset At, string Service, short Status, long Bytes);
}
