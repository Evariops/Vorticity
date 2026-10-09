// What a group by allocates once its process is warm, held against a ratchet: a whole query, from its
// plan to its last result, on every thread.
//
// WHY EVERY THREAD. A group by folds on lanes the pool schedules and applies the core's parts on any
// of them: the bytes the caller's thread allocates are a fraction of what the query costs. And what
// the rest allocates is what stops every lane: a collection of the workstation collector suspends
// every thread and collects on one. At fourteen lanes the stops took a fifth of the time of s7 and
// q10 on 4·10⁶ rows (PLAN-DUCKDB.md, D9, 2026-10-09). So the figure is the process's, measured in
// AllocationCollection, which runs alone; D9 drives every axis to zero.
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
    /// At one lane a ceiling is the floor rounded up to 512 bytes; at four, the lanes' schedule moves
    /// the core's splits and batches by a few hundred kilobytes from one run to the next, so a ceiling
    /// is the floor and 2 % (1 % for six keys at one lane), rounded up to 64 KiB.
    /// </remarks>
    private static readonly (string Axis, int Lanes, GroupCoreReason Path, long Ceiling, Func<VortexFile, int, Task<(long Rows, GroupCoreReason Path)>> Query)[] Axes =
    [
        // A thousand integers numbered by their value, a table a lane.
        ("small integers", 1, GroupCoreReason.None, 360_448, SmallAsync),
        ("small integers", 4, GroupCoreReason.None, 561_152, SmallAsync),

        // A thousand short texts, hashed as their words.
        ("short texts", 1, GroupCoreReason.None, 521_728, NamesAsync),
        ("short texts", 4, GroupCoreReason.None, 1_159_168, NamesAsync),

        // A long integer a row, hashed: at four lanes, a lane's first rows are all new groups.
        ("hashed integers", 1, GroupCoreReason.None, 82_157_056, WideAsync),
        ("hashed integers", 4, GroupCoreReason.FirstRows, 51_249_152, WideAsync),

        // Integers in no order over a span of 2·10⁶, the one the statistics bound them to: the core by pages.
        ("spread integers", 1, GroupCoreReason.None, 58_423_808, DenseAsync),
        ("spread integers", 4, GroupCoreReason.Spread, 34_275_328, DenseAsync),

        // Six keys, three texts and three integers, nearly a group a row: tuples of their values.
        ("six keys", 1, GroupCoreReason.None, 165_806_080, SixAsync),
        ("six keys", 4, GroupCoreReason.FirstRows, 120_389_632, SixAsync),
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
