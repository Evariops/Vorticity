// What a group by allocates once its process is warm, held against a ratchet: a whole query, from its
// plan to its last result, on every thread.
//
// WHY EVERY THREAD. A group by folds on lanes the pool schedules and applies the core's parts on any
// of them: the bytes the caller's thread allocates are a fraction of what the query costs. And what
// the rest allocates is what stops every lane: a collection of the workstation collector suspends
// every thread and collects on one. At fourteen lanes the stops took a fifth of the time of a group
// by 10⁷ hashed keys and of db-benchmark's q10 on 4·10⁶ rows, measured on 2026-10-09. So the figure
// is the process's, measured in AllocationCollection, which runs alone, and the aim is every axis at
// zero.
//
// WHY THE FLOOR OF SEVERAL QUERIES. The lanes take ranges from a queue in the order the pool runs
// them: the tables each grows, the sub-tables the core splits and the batches its lanes fill move a
// little from one query to the next. The floor of several queries is what a query cannot avoid; a
// cost paid on every one still raises it.
//
// THE CEILINGS sit just above the floor measured. LOWER ONE BY HAND in the commit that earns it, and
// never raise one without saying in the commit what grew and why.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Tests.Scan;
using Xunit;

namespace Vorticity.Tests.Aggregation;

[Collection(nameof(AllocationCollection))]
public sealed partial class GroupAllocationTests
{
    /// <summary>The rows of the file: enough for four lanes to judge their first rows (<see cref="AggregationPartition.LaneRows"/>).</summary>
    private const int Rows = 1_000_000;

    /// <summary>Queries before any measurement: the JIT, its tiers, the pools' first rents and the process's shelf.</summary>
    private const int WarmUp = 4;

    /// <summary>Queries measured, of which the floor is the answer.</summary>
    private const int Runs = 5;

    private static readonly string[] Names = Texts("name", 1_000);

    private static readonly string[] Labels = Texts("label", 13);

    private static readonly Lazy<Task<string>> Written = new Lazy<Task<string>>(WriteAsync);

    /// <summary>
    /// The axes: a path of the engine each, at one lane and at four, the core asked from four lanes
    /// (<see cref="AggregationPlan.CoreLanes"/>) where a key makes it turn. The ceiling is a warm
    /// query's bytes on every thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// At one lane a ceiling is the highest floor seen and 512 bytes, rounded up to 512 bytes: what the
    /// process's shelf holds moves it by a hundred bytes or so; at four, the lanes' schedule moves
    /// the core's splits and batches by a few hundred kilobytes from one run to the next, and the
    /// process's shelf holds what the suite's other tests left, so a ceiling of the core is the highest
    /// floor seen and 256 KiB, rounded up to 64 KiB; four lanes without the core move by a few
    /// kilobytes: the highest floor seen and 16 KiB, rounded up to 4 KiB.
    /// </para>
    /// <para>
    /// Measured without the shuffle of every result's order the tests make: 3 to 4 MB less on the
    /// core's axes. A lane's arrays come from the process's shelf and go back to it, as they grow and
    /// when its tables die, its cache's and the partition it bypasses it with, where they were left to
    /// the next collection: the core's axes 8 to 17 times lower; a lane alone's a quarter to two fifths
    /// lower, the rest its table, which is the result; and the group of each row is no longer rented
    /// from the shared array pool, whose array kept by one thread the lane of the next query, on
    /// another, did not find.
    /// </para>
    /// <para>
    /// A sub-table makes no array before its first group, and lays its records out as its core does; the
    /// scratch of keys and of the core's appliers comes from the process's shelf and goes back to it:
    /// the core's axes a half to a third lower again.
    /// </para>
    /// <para>
    /// What each query made once goes back to a shelf too: a merge's map of numbers is borrowed, a
    /// partition gives the group of each row back once it folds no more, a key's page slabs and a
    /// result column's values are given back, a key's ranges and the core's spread map are made at
    /// their first use or on the stack: a lane alone's small axes three to five times lower, the core's
    /// axes a sixth to a third.
    /// </para>
    /// <para>
    /// The table a lane alone holds as the result, or the largest a merge in series kept, goes back to
    /// the process's shelf once delivered, and its order is lent by it: a lane alone no longer grows a
    /// table of its own at every query, its axes 2 to 1 000 times lower (six keys 105 MB to 108 KB).
    /// </para>
    /// </remarks>
    private static readonly (string Axis, int Lanes, GroupCoreReason Path, long Ceiling, Func<VortexFile, int, Task<(long Rows, GroupCoreReason Path)>> Query)[] Axes =
    [
        // A thousand integers numbered by their value, a table a lane.
        ("small integers", 1, GroupCoreReason.None, 35_840, SmallAsync),
        ("small integers", 4, GroupCoreReason.None, 126_976, SmallAsync),

        // A thousand short texts, hashed as their words.
        ("short texts", 1, GroupCoreReason.None, 37_376, NamesAsync),
        ("short texts", 4, GroupCoreReason.None, 143_360, NamesAsync),

        // A long integer a row, hashed: at four lanes, a lane's first rows are all new groups.
        ("hashed integers", 1, GroupCoreReason.None, 100_352, WideAsync),
        //
        // On this axis and the core's two others, each part is built in an order its shelf lends: 3 to
        // 4 MB less, four bytes a group.
        ("hashed integers", 4, GroupCoreReason.FirstRows, 1_245_184, WideAsync),

        // Integers in no order over a span of 2·10⁶, the one the statistics bound them to: the core by pages.
        ("spread integers", 1, GroupCoreReason.None, 106_496, DenseAsync),
        ("spread integers", 4, GroupCoreReason.Spread, 1_245_184, DenseAsync),

        // Six keys, three texts and three integers, nearly a group a row: tuples of their values.
        ("six keys", 1, GroupCoreReason.None, 109_056, SixAsync),
        ("six keys", 4, GroupCoreReason.FirstRows, 1_638_400, SixAsync),
    ];

    [Fact]
    public async Task EveryWarmGroupByAllocatesWithinItsCeiling()
    {
        ReleaseOnlyCeilings.Require();
        string path = await Written.Value;
        StringBuilder report = new StringBuilder("GROUP BY ALLOCATIONS: a warm query on every thread, floor of ")
            .Append(Runs.ToString(CultureInfo.InvariantCulture))
            .Append(" after ")
            .Append(WarmUp.ToString(CultureInfo.InvariantCulture))
            .Append('\n');

        // The tests shuffle every result's order (ShuffledOrder), a copy of it a part, which a query
        // outside them never makes: measured without, as alone in its collection nothing else runs.
        bool shuffled = AggregationPlan.ShuffledOrder;
        AggregationPlan.ShuffledOrder = false;
        try
        {
            await MeasureAsync(path, report);
        }
        finally
        {
            AggregationPlan.ShuffledOrder = shuffled;
        }
    }

    private static async Task MeasureAsync(string path, StringBuilder report)
    {
        List<string> failures = [];
        foreach ((string axis, int lanes, GroupCoreReason expected, long ceiling, Func<VortexFile, int, Task<(long Rows, GroupCoreReason Path)>> query) in Axes)
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = lanes);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            for (int i = 0; i < WarmUp; i++)
            {
                await query(file, lanes);
            }

            long floor = long.MaxValue;
            GroupCoreReason took = expected;
            for (int i = 0; i < Runs && floor > 0; i++)
            {
                long before = GC.GetTotalAllocatedBytes(precise: true);
                (long rows, took) = await query(file, lanes);
                long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
                Assert.Equal((long)Rows, rows);
                floor = Math.Min(floor, allocated);
            }

            string name = string.Create(CultureInfo.InvariantCulture, $"{axis}, {lanes} lane{(lanes > 1 ? "s" : "")}");
            report.Append("    ")
                .Append(name.PadRight(28))
                .Append(floor.ToString("N0", CultureInfo.InvariantCulture).PadLeft(14))
                .Append(" B   ceiling ")
                .Append(ceiling.ToString("N0", CultureInfo.InvariantCulture).PadLeft(14))
                .Append("   headroom ")
                .Append((ceiling - floor).ToString("N0", CultureInfo.InvariantCulture).PadLeft(12))
                .Append("   ")
                .Append(took)
                .Append('\n');
            if (floor > ceiling)
            {
                failures.Add(string.Create(CultureInfo.InvariantCulture, $"{name} allocated {floor:N0} B against a ceiling of {ceiling:N0} B"));
            }

            // An axis measures one path: a key that no longer turns, or turns for another reason, measures another.
            if (took != expected)
            {
                failures.Add($"{name} took the path {took}, where the axis measures {expected}");
            }
        }

        Console.Out.Write(report.ToString());

        // Reported together, as the read paths are: a change that moves one axis usually moves several.
        Assert.True(failures.Count == 0, string.Join("\n", failures) + "\n" + report);
    }

    private static Task<(long, GroupCoreReason)> SmallAsync(VortexFile file, int lanes) =>
        CountAsync<IntTotal>(file.Scan<Row>().GroupBy(r => r.Small).Select(g => (g.Key, g.Count(), g.Sum(r => r.Value))), lanes, 1);

    private static Task<(long, GroupCoreReason)> NamesAsync(VortexFile file, int lanes) =>
        CountAsync<NameTotal>(file.Scan<Row>().GroupBy(r => r.Name).Select(g => (g.Key, g.Count(), g.Sum(r => r.Value))), lanes, 1);

    private static Task<(long, GroupCoreReason)> WideAsync(VortexFile file, int lanes) =>
        CountAsync<WideTotal>(file.Scan<Row>().GroupBy(r => r.Wide).Select(g => (g.Key, g.Count(), g.Sum(r => r.Value))), lanes, 1);

    private static Task<(long, GroupCoreReason)> DenseAsync(VortexFile file, int lanes) =>
        CountAsync<IntTotal>(file.Scan<Row>().GroupBy(r => r.Dense).Select(g => (g.Key, g.Count(), g.Sum(r => r.Value))), lanes, 1);

    private static Task<(long, GroupCoreReason)> SixAsync(VortexFile file, int lanes) =>
        CountAsync<SixTotal>(
            file.Scan<Row>().GroupBy(r => (r.Name, r.Label, r.City, r.Small, r.Dense, r.Other))
                .Select(g => (g.Key.Item1, g.Key.Item2, g.Key.Item3, g.Key.Item4, g.Key.Item5, g.Key.Item6, g.Count(), g.Sum(r => r.Value))),
            lanes,
            6);

    /// <summary>
    /// Runs <paramref name="query"/> and reads its result as the batches it is, its counts summed: the
    /// rows the groups hold, and the path the groups took.
    /// </summary>
    /// <param name="counted">The column of the counts.</param>
    private static async Task<(long, GroupCoreReason)> CountAsync<TRecord>(Vorticity.Aggregation query, int lanes, int counted)
        where TRecord : IVortexRecord<TRecord>
    {
        query.Plan.CoreLanes = lanes;
        long rows = 0;
        await foreach (Columns<TRecord> batch in query.As<TRecord>())
        {
            rows += Sum(batch.Column<long>(counted).Values);
        }

        return (rows, query.Statistics.Grouping!.CoreReason);
    }

    private static long Sum(ReadOnlySpan<long> values)
    {
        long sum = 0;
        foreach (long value in values)
        {
            sum += value;
        }

        return sum;
    }

    private static async Task<string> WriteAsync()
    {
        // One per run, named by the process, gone when the run ends.
        string directory = Path.Combine(AppContext.BaseDirectory, "group-allocations");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"rows-{Environment.ProcessId}.vortex");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => System.IO.File.Delete(path);

        Row[] rows = new Row[Rows];
        for (int row = 0; row < Rows; row++)
        {
            ulong mixed = (ulong)row * 0x9E37_79B9_7F4A_7C15UL;
            rows[row] = new Row(
                row % 1_000,
                Names[(int)((mixed >> 40) % 1_000)],
                (long)(mixed & long.MaxValue),
                (int)((mixed >> 24) % (2 * Rows)),
                row % 7,
                Labels[row % 13],
                Names[row % 97],
                row);
        }

        await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Row>(path);
        await writer.WriteAsync<Row>(rows, CancellationToken.None);
        await writer.CompleteAsync(CancellationToken.None);
        return path;
    }

    private static string[] Texts(string prefix, int count)
    {
        string[] texts = new string[count];
        for (int i = 0; i < count; i++)
        {
            texts[i] = string.Create(CultureInfo.InvariantCulture, $"{prefix}-{i}");
        }

        return texts;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [VortexRecord]
    public partial record struct Row(int Small, string Name, long Wide, int Dense, int Other, string Label, string City, long Value);

    [VortexRecord]
    public partial record struct IntTotal(int Key, long Count, long Sum);

    [VortexRecord]
    public partial record struct NameTotal(string Key, long Count, long Sum);

    [VortexRecord]
    public partial record struct WideTotal(long Key, long Count, long Sum);

    [VortexRecord]
    public partial record struct SixTotal(string Name, string Label, string City, int Small, int Dense, int Other, long Count, long Sum);
}
