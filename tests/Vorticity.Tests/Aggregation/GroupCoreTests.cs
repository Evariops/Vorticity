using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// The core of PLAN-HIGH-CARDINALITY, H4 (milestone 1), against the lanes' tables it replaces: the same
/// answers, float sums to the bit, at one lane, two and fourteen, with caches, floors, sub-tables and
/// batches so small that every batch of rows copies a cache, every deposit bursts and every burst
/// splits; at every α; for a key of one column, null included, and a tuple of two.
/// </summary>
public sealed partial class GroupCoreTests
{
    private const int Rows = 60_000;

    [Theory]
    [InlineData(1, 0.0)]
    [InlineData(1, 1.0)]
    [InlineData(2, 1.0)]
    [InlineData(14, 0.0)]
    [InlineData(14, 1.0)]
    public async Task EveryStateInARecordComesOutTheSame(int degree, double bypass)
    {
        // Batches enough for a lane to fill its cache, measure it, then bypass it.
        Row[] rows = Rows_(keys: 9_000, count: 4 * Rows);
        string path = await WriteAsync(rows);
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Dictionary<int, KeyStats> reference = ByKey(await ListAsync(Reference(file)));
            Vorticity.Aggregation core = Query(file, plan =>
            {
                Tiny(plan);
                plan.CoreBypass = bypass;
            });

            Dictionary<int, KeyStats> held = ByKey(await ListAsync(core.As<KeyStats>()));
            Assert.Equal(reference, held);

            // The caches copied, the parts burst and split; bypassed, or never. At fourteen lanes a lane
            // may take a single batch, before its cache has filled, as the queue hands them out.
            CoreRun run = core.Plan.LastRun!.Core!;
            Assert.True(run.Flushes > 0 && run.Bursts > 0 && run.Splits > 0, run.ToString());
            Assert.True(bypass > 0 ? run.BypassedRows > 0 || degree > 2 : run.BypassedRows == 0, run.ToString());

            // Delivered part by part (H7), every group once, a part let go before the next; and whole, as
            // an order over the groups asks it, the same again.
            Assert.Equal(held.Count, core.Statistics.Grouping!.Groups);
            Assert.InRange(core.Statistics.Grouping.PeakGroups, 1, held.Count);
            Vorticity.Aggregation whole = Query(file, plan =>
            {
                Tiny(plan);
                plan.CoreBypass = bypass;
                plan.CoreParted = false;
            });

            Assert.Equal(reference, ByKey(await ListAsync(whole.As<KeyStats>())));

            // And what .NET says of a few of them.
            foreach (IGrouping<int?, Row> group in rows.GroupBy(r => r.Key).Take(50))
            {
                KeyStats got = held[group.Key ?? int.MinValue];
                Assert.Equal(group.Count(), got.Count);
                Assert.Equal(group.Sum(r => r.Value), got.Sum);
                Assert.Equal(group.Max(r => r.Value), got.Max);
                Assert.Equal(group.Any(r => r.Value > 90), got.High);
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(14)]
    public async Task ALaneThatTurnsToTheCoreMidPassLosesNoGroup(int degree)
    {
        // Every lane folds two batches into its own table, then turns to the core, as under pressure
        // (PLAN-HIGH-CARDINALITY, H4, milestone 2): its table, null group included, emptied into the
        // core's batches, the rest of its rows into a cache. Batches small enough for every lane to
        // fold several, however the queue hands the ranges out.
        Row[] rows = Rows_(keys: 9_000, count: 4 * Rows);
        string path = await WriteAsync(rows);
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Dictionary<int, KeyStats> reference = ByKey(await ListAsync(Reference(file)));
            Vorticity.Aggregation turned = Query(
                file,
                plan =>
                {
                    Tiny(plan);
                    plan.Core = false;
                    plan.CoreUnderPressure = true;
                    plan.CoreTurnAt = 2;
                },
                batchRows: 2_048);

            Assert.Equal(reference, ByKey(await ListAsync(turned.As<KeyStats>())));
            CoreRun run = turned.Plan.LastRun!.Core!;
            Assert.True(run.Flushes > 0 && run.Bursts > 0, run.ToString());
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public async Task EveryAlphaComesOutTheSame(int alpha)
    {
        Row[] rows = Rows_(keys: 5_000);
        string path = await WriteAsync(rows);
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = 4);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Dictionary<int, KeyStats> reference = ByKey(await ListAsync(Reference(file)));
            Vorticity.Aggregation core = Query(file, plan =>
            {
                Tiny(plan);
                plan.CoreAlpha = alpha;
            });

            Assert.Equal(reference, ByKey(await ListAsync(core.As<KeyStats>())));
            Assert.Equal(alpha, core.Plan.LastRun!.Core!.Alpha);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(14)]
    public async Task ATupleOfTwoColumnsTravelsAsItsWord(int degree)
    {
        Row[] rows = Rows_(keys: 9_000);
        string path = await WriteAsync(rows);
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            // Past 2^16 tuples by the statistics' bounds: no table of groups takes them.
            Vorticity.Aggregation reference = file.Scan<Row>().GroupBy(r => (r.Key, r.Value)).Select(g => (g.Key.Key, g.Key.Value, g.Count(), g.Sum(x => x.Real)));
            Vorticity.Aggregation core = file.Scan<Row>().GroupBy(r => (r.Key, r.Value)).Select(g => (g.Key.Key, g.Key.Value, g.Count(), g.Sum(x => x.Real)));
            Tiny(core.Plan);
            List<PairStats> expected = await ListAsync(reference.As<PairStats>());
            List<PairStats> got = await ListAsync(core.As<PairStats>());
            Assert.NotNull(core.Plan.LastRun!.Core);
            Assert.Equal(
                expected.OrderBy(p => p.Key ?? int.MinValue).ThenBy(p => p.Value),
                got.OrderBy(p => p.Key ?? int.MinValue).ThenBy(p => p.Value));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task CachesThatNeverFillMergeAsBefore()
    {
        Row[] rows = Rows_(keys: 300);
        string path = await WriteAsync(rows);
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = 4);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Dictionary<int, KeyStats> reference = ByKey(await ListAsync(Reference(file)));
            Vorticity.Aggregation core = Query(file, plan =>
            {
                plan.Core = true;
                plan.CoreLanes = 1;
            });

            Assert.Equal(reference, ByKey(await ListAsync(core.As<KeyStats>())));
            Assert.Null(core.Plan.LastRun!.Core);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task FewLanesKeepTheirTables()
    {
        Row[] rows = Rows_(keys: 9_000);
        string path = await WriteAsync(rows);
        try
        {
            // Four lanes, below the lanes the core holds the groups from: every lane's table, merged.
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = 4);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Vorticity.Aggregation core = Query(file, plan =>
            {
                Tiny(plan);
                plan.CoreLanes = null;
            });

            Assert.Equal(ByKey(await ListAsync(Reference(file))), ByKey(await ListAsync(core.As<KeyStats>())));
            Assert.Null(core.Plan.LastRun!.Core);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task ATextKeyStaysOnTheLanesTables()
    {
        Row[] rows = Rows_(keys: 9_000);
        string path = await WriteAsync(rows);
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = 4);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Vorticity.Aggregation byName = file.Scan<Row>().GroupBy(r => r.Name).Select(g => (g.Key, g.Count()));
            Tiny(byName.Plan);
            List<NameCount> names = await ListAsync(byName.As<NameCount>());
            Assert.Null(byName.Plan.LastRun!.Core);
            Assert.Equal(rows.Select(r => r.Name).Distinct().Count(), names.Count);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task TwoRunsAtOneLaneGiveTheSameOrder()
    {
        Row[] rows = Rows_(keys: 9_000);
        string path = await WriteAsync(rows);
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = 1);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Vorticity.Aggregation first = Query(file, Tiny);
            Vorticity.Aggregation second = Query(file, Tiny);
            List<KeyStats> once = await ListAsync(first.As<KeyStats>());
            List<KeyStats> again = await ListAsync(second.As<KeyStats>());
            Assert.NotNull(first.Plan.LastRun!.Core);
            Assert.Equal(once.Select(s => s.Key), again.Select(s => s.Key));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(14)]
    public async Task AHotKeyAndTheNullsComeOutTheSame(int degree)
    {
        // One row in three the same key, one in five null, the rest twenty thousand keys.
        Row[] rows = new Row[Rows];
        for (int row = 0; row < Rows; row++)
        {
            ulong mix = (ulong)row * 0x9E37_79B9_7F4A_7C15UL;
            int? key = row % 5 == 0 ? null : row % 3 == 0 ? 7 : (int)((mix >> 24) % 20_000);
            rows[row] = new Row(key, $"n{row % 50}", row % 7, (long)((mix >> 8) % 100), (double)((mix >> 12) % 1_000) / 7);
        }

        string path = await WriteAsync(rows);
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Dictionary<int, KeyStats> reference = ByKey(await ListAsync(Reference(file)));
            Vorticity.Aggregation core = Query(file, Tiny);
            Dictionary<int, KeyStats> held = ByKey(await ListAsync(core.As<KeyStats>()));
            Assert.Equal(reference, held);
            Assert.Equal(rows.Count(r => r.Key is null), held[int.MinValue].Count);
            Assert.Equal(rows.Count(r => r.Key == 7), held[7].Count);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(14)]
    public async Task AFilterAndAWindowApplyPartByPart(int degree)
    {
        // Delivered part by part (H7), each worker filters its part's groups, and the reader cuts the
        // window across the parts' batches: the same groups as the lanes' tables keep, the window as many.
        Row[] rows = Rows_(keys: 9_000, count: 4 * Rows);
        string path = await WriteAsync(rows);
        try
        {
            QueryMemoryBudget budget = new QueryMemoryBudget(1L << 30);
            await using VortexSession session = VortexSession.Create(options =>
            {
                options.MaxDegreeOfParallelism = degree;
                options.MemoryBudget = budget;
            });

            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Dictionary<int, KeyStats> reference = ByKey(await ListAsync(Reference(file))).Where(p => p.Value.Count > 26).ToDictionary();
            Vorticity.Aggregation filtered = Filtered(file);
            Tiny(filtered.Plan);
            filtered.Plan.CoreBatchEntries = 64;
            Dictionary<int, KeyStats> held = ByKey(await ListAsync(filtered.As<KeyStats>()));
            Assert.Equal(reference, held);
            Assert.True(filtered.Plan.LastRun?.Core?.Tables > 1);

            // A window of 700 groups from the 1 000th, over batches of 256 groups.
            Vorticity.Aggregation windowed = Filtered(file, batchRows: 256).Skip(1_000).Take(700);
            Tiny(windowed.Plan);
            windowed.Plan.CoreBatchEntries = 64;
            List<KeyStats> window = await ListAsync(windowed.As<KeyStats>());
            Assert.Equal(700, window.Count);
            Assert.Equal(700, window.Select(s => s.Key).Distinct().Count());
            Assert.All(window, s => Assert.Equal(reference[s.Key ?? int.MinValue], s));
            Assert.Equal(0, budget.ReservedBytes);
        }
        finally
        {
            System.IO.File.Delete(path);
        }

        static Vorticity.Aggregation Filtered(VortexFile file, int batchRows = 0) => file.Scan<Row>()
            .With(new ScanOptions { BatchRows = batchRows })
            .GroupBy(r => r.Key)
            .Where(g => g.Count() > 26L)
            .Select(g => (
                g.Key, g.Count(), g.Sum(x => x.Value), g.Average(x => x.Real), g.Variance(x => x.Real), g.Any(x => x.Value > 90),
                g.Max(x => x.Value), g.Min(x => x.Real), g.First().Small, g.Last().Value));
    }

    [Fact]
    public async Task ASlowedHolderLeavesNoBatchBehind()
    {
        // Every burst waits once it holds its part: the lanes deposit past it, on stacks the holder took
        // or not, and every batch is applied once, by a burst or at the end.
        Row[] rows = Rows_(20_000);
        string path = await WriteAsync(rows);
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = 14);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Dictionary<int, KeyStats> reference = ByKey(await ListAsync(Reference(file)));
            Vorticity.Aggregation slowed = Query(
                file,
                static plan =>
                {
                    Tiny(plan);
                    plan.CoreBurstSpin = 20_000;
                },
                batchRows: 1_024);
            Assert.Equal(reference, ByKey(await ListAsync(slowed.As<KeyStats>())));
            Assert.True(slowed.Plan.LastRun?.Core?.Bursts > 0);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task ACancelledPassInTheMiddleOfItsBurstsGivesEverythingBack()
    {
        Row[] rows = Rows_(20_000);
        string path = await WriteAsync(rows);
        try
        {
            QueryMemoryBudget budget = new QueryMemoryBudget(1L << 30);
            await using VortexSession session = VortexSession.Create(options =>
            {
                options.MaxDegreeOfParallelism = 14;
                options.MemoryBudget = budget;
            });

            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Vorticity.Aggregation slowed = Query(
                file,
                static plan =>
                {
                    Tiny(plan);
                    plan.CoreBurstSpin = 200_000;
                },
                batchRows: 1_024);
            using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            cancel.CancelAfter(TimeSpan.FromMilliseconds(20));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await foreach (KeyStats _ in slowed.As<KeyStats>().ToRecordsAsync(cancel.Token))
                {
                }
            });
            Assert.Equal(0, budget.ReservedBytes);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void ATableThatCannotDoubleSaysSo()
    {
        // Groups are numbered by 32-bit integers: an array of 2^30 elements cannot double.
        Assert.Equal(1 << 30, GroupKeys.Doubled(1 << 29));
        VortexUnsupportedException refused = Assert.Throws<VortexUnsupportedException>(() => GroupKeys.Doubled(1 << 30));
        Assert.Contains("536,870,912", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>The core at sizes that make every batch of rows copy a cache, every deposit burst, every burst split.</summary>
    // A key its zones say scattered over a span of a million values or more takes the core from the start
    // on the core's lanes, its answers the lanes' tables'; the same span in the order of the rows, whose
    // zones each cover a few values, keeps the lanes' tables (PLAN-HIGH-CARDINALITY, decision 14). The
    // core's lanes brought down to two, so that four lanes take the rule on a file of 300 000 rows.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AKeyTheZonesSayScatteredTakesTheCoreFromTheStart(bool scattered)
    {
        const int Count = 300_000;
        Row[] rows = new Row[Count];
        for (int row = 0; row < Count; row++)
        {
            ulong mix = (ulong)row * 0x9E37_79B9_7F4A_7C15UL;
            int key = scattered ? (int)((mix >> 20) % 1_100_000) : row * 4;
            rows[row] = new Row(key, $"name-{(mix >> 40) % 2_000:D4}", row % 7, (long)((mix >> 8) % 100), ((double)((mix >> 12) % 100_000) / 3) - 9_000);
        }

        string path = await WriteAsync(rows);
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = 4);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Dictionary<int, KeyStats> reference = ByKey(await ListAsync(Query(file, static plan => plan.CoreScattered = false).As<KeyStats>()));
            Vorticity.Aggregation chosen = Query(file, static plan => plan.CoreLanes = 2);
            Assert.Equal(reference, ByKey(await ListAsync(chosen.As<KeyStats>())));
            Assert.Equal(scattered, chosen.Plan.LastRun!.Core is not null);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static void Tiny(AggregationPlan plan)
    {
        plan.Core = true;
        plan.CoreLanes = 1;
        plan.CoreCapacity = 96;
        plan.CoreFloor = 16;
        plan.CoreTableGroups = 40;
        plan.CoreBatchEntries = 8;
    }

    /// <summary>The groups' answers as the reference gives them: the lanes' tables, merged.</summary>
    private static Scan<KeyStats> Reference(VortexFile file) => Query(file, static _ => { }).As<KeyStats>();

    private static Vorticity.Aggregation Query(VortexFile file, Action<AggregationPlan> set, int batchRows = 0)
    {
        Vorticity.Aggregation query = file.Scan<Row>()
            .With(new ScanOptions { BatchRows = batchRows })
            .GroupBy(r => r.Key)
            .Select(g => (
                g.Key, g.Count(), g.Sum(x => x.Value), g.Average(x => x.Real), g.Variance(x => x.Real), g.Any(x => x.Value > 90),
                g.Max(x => x.Value), g.Min(x => x.Real), g.First().Small, g.Last().Value));
        set(query.Plan);
        return query;
    }

    /// <summary>The groups by key, the null one as a number no key holds.</summary>
    private static Dictionary<int, KeyStats> ByKey(List<KeyStats> stats) => stats.ToDictionary(s => s.Key ?? int.MinValue);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary><paramref name="keys"/> keys in no order, null one row in ninety-seven; small values, reals and names.</summary>
    private static Row[] Rows_(int keys, int count = Rows)
    {
        Row[] rows = new Row[count];
        for (int row = 0; row < count; row++)
        {
            ulong mix = (ulong)row * 0x9E37_79B9_7F4A_7C15UL;
            int? key = row % 97 == 0 ? null : (int)((mix >> 24) % (ulong)keys);
            rows[row] = new Row(key, $"name-{(mix >> 40) % 2_000:D4}", row % 7, (long)((mix >> 8) % 100), ((double)((mix >> 12) % 100_000) / 3) - 9_000);
        }

        return rows;
    }

    private static async Task<List<T>> ListAsync<T>(Scan<T> scan)
        where T : IVortexRecord<T>
    {
        List<T> rows = [];
        await foreach (T row in scan.ToRecordsAsync(Ct))
        {
            rows.Add(row);
        }

        return rows;
    }

    private static async Task<string> WriteAsync(Row[] rows)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "group-core");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"rows-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Row>(path))
        {
            await writer.WriteAsync<Row>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        return path;
    }

    [VortexRecord]
    public partial record struct Row(int? Key, string Name, int Small, long Value, double Real);

    [VortexRecord]
    public partial record struct KeyStats(
        int? Key, long Count, long Sum, double? Mean, double? Variance, bool High, long Max, double Least, int FirstSmall, long LastValue);

    [VortexRecord]
    public partial record struct PairStats(int? Key, long Value, long Count, double Sum);

    [VortexRecord]
    public partial record struct NameCount(string Key, long Count);
}
