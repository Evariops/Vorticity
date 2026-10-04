using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Vorticity.Tests.Scan;
using Xunit;

namespace Vorticity.Tests.Api;

/// <summary>
/// The allocation promises a scan makes: nothing per batch on any sink but rows, and so nothing on
/// the large object heap once it is under way.
/// </summary>
/// <remarks>
/// In <see cref="AllocationCollection"/>, which runs alone: two of these measurements count every
/// thread's allocations, and the scans of a neighbouring class would be counted with them.
/// </remarks>
[Trait("Category", "ApiContract")]
[Collection(nameof(AllocationCollection))]
public sealed class AllocationContractTests
{
    /// <summary>Full scans before any measurement: the JIT, its tiers and every lazy static settle.</summary>
    private const int WarmUp = 4;

    /// <summary>Measured scans whose floor is taken where other threads can add to the count.</summary>
    private const int Runs = 5;

    private static readonly ScanOptions Sequential = new ScanOptions { Prefetch = 0 };

    [Fact]
    public async Task ATypedScanAllocatesNothingPerBatchAfterTheFirst()
    {
        ReleaseOnlyCeilings.Require();
        await using VortexFile file = await VortexFile.OpenAsync(await ContractFile.PathAsync(), TestContext.Current.CancellationToken);

        for (int i = 0; i < WarmUp; i++)
        {
            await TypedAsync(file, Sequential);
        }

        (long batches, long allocated) = await TypedAsync(file, Sequential);
        Assert.True(batches > 8, $"the file gave {batches} batches, too few to say anything per batch");
        Assert.True(
            allocated == 0,
            string.Create(CultureInfo.InvariantCulture, $"a typed scan allocated {allocated} B over its {batches - 1} batches after the first"));
    }

    /// <summary>
    /// A filter adds the evaluation, the comparison an encoding answers in place, and either the
    /// compaction or the selection a batch delivered whole carries.
    /// </summary>
    [Theory]
    [InlineData("Celsius > 30", true)]
    [InlineData("Celsius > 30", false)]
    [InlineData("City = Lyon", true)]
    [InlineData("City = Lyon", false)]
    public async Task AFilteredScanAllocatesNothingPerBatchAfterTheFirst(string filter, bool compact)
    {
        ReleaseOnlyCeilings.Require();
        await using VortexFile file = await VortexFile.OpenAsync(await ContractFile.PathAsync(), TestContext.Current.CancellationToken);
        Func<Scan<Reading>> scan = () =>
        {
            Scan<Reading> all = file.Scan<Reading>().With(Sequential with { Compact = compact });
            return filter == "City = Lyon" ? all.Where(r => r.City == "Lyon") : all.Where(r => r.Celsius > 30.0);
        };

        for (int i = 0; i < WarmUp; i++)
        {
            await TypedAsync(scan());
        }

        (long batches, long allocated) = await TypedAsync(scan());
        Assert.True(batches > 8, $"the filter left {batches} batches, too few to say anything per batch");
        Assert.True(
            allocated == 0,
            string.Create(CultureInfo.InvariantCulture, $"a scan filtered on {filter} allocated {allocated} B over its {batches - 1} batches after the first"));
    }

    [Fact]
    public async Task AToolScanAllocatesNothingPerBatchAfterTheFirst()
    {
        ReleaseOnlyCeilings.Require();
        await using VortexFile file = await VortexFile.OpenAsync(await ContractFile.PathAsync(), TestContext.Current.CancellationToken);

        for (int i = 0; i < WarmUp; i++)
        {
            await ToolAsync(file);
        }

        (long batches, long allocated) = await ToolAsync(file);
        Assert.True(batches > 8, $"the file gave {batches} batches, too few to say anything per batch");
        Assert.True(
            allocated == 0,
            string.Create(CultureInfo.InvariantCulture, $"a tool scan allocated {allocated} B over its {batches - 1} batches after the first"));
    }

    /// <summary>
    /// The default options decode ahead of the consumer on other threads, so the count is every
    /// thread's; its floor over a few scans is what the scan costs, the rest being the runtime's.
    /// </summary>
    [Fact]
    public async Task AScanThatDecodesAheadAllocatesNothingPerBatchAfterTheFirst()
    {
        ReleaseOnlyCeilings.Require();
        await using VortexFile file = await VortexFile.OpenAsync(await ContractFile.PathAsync(), TestContext.Current.CancellationToken);

        foreach (int prefetch in new[] { 1, 2 })
        {
            ScanOptions options = new ScanOptions { Prefetch = prefetch };
            for (int i = 0; i < WarmUp; i++)
            {
                await TypedAsync(file, options, everyThread: true);
            }

            long floor = long.MaxValue;
            for (int i = 0; i < Runs && floor > 0; i++)
            {
                floor = Math.Min(floor, (await TypedAsync(file, options, everyThread: true)).Allocated);
            }

            Assert.True(
                floor == 0,
                string.Create(CultureInfo.InvariantCulture, $"a scan decoding {prefetch} batches ahead allocated at least {floor} B after its first batch"));
        }
    }

    /// <summary>
    /// An aggregate and a count hand no batch to the caller, so the loop is reached through the
    /// source: what the scan's thread had allocated when it asked for the second split's segments,
    /// and when it asked for the last.
    /// </summary>
    [Fact]
    public async Task AggregatesAndCountsAllocateNothingBetweenTheirReads()
    {
        ReleaseOnlyCeilings.Require();
        SamplingSource source = new SamplingSource(await ContractFile.PathAsync());
        await using VortexFile file = await VortexSession.Default.OpenAsync(source, cancellationToken: TestContext.Current.CancellationToken);

        Func<Scan<Reading>> all = () => file.Scan<Reading>().With(Sequential);
        Func<Scan<Reading>> filtered = () => file.Scan<Reading>().With(Sequential).Where(r => r.Celsius > 30.0);
        (string Sink, Func<Scan<Reading>> Scan, Func<Scan<Reading>, Task> Run)[] sinks =
        [
            ("SumAsync", all, async scan => await scan.SumAsync(r => r.Day)),
            ("AverageAsync", all, async scan => await scan.AverageAsync(r => r.Celsius)),
            ("AggAsync", all, async scan => await scan.AggAsync<SumAndCount>(a => (a.Sum(r => r.Celsius), a.Count()))),
            ("GroupBy", all, async scan =>
            {
                await foreach (CityCount group in scan.GroupBy(r => r.City).Select(g => (g.Key, g.Count())).As<CityCount>().ToRecordsAsync())
                {
                    GC.KeepAlive(group.City);
                }
            }),
            ("CountAsync", filtered, async scan => await scan.CountAsync()),
            ("SumAsync, filtered", filtered, async scan => await scan.SumAsync(r => r.Day)),
        ];

        List<string> failures = [];
        foreach ((string sink, Func<Scan<Reading>> scan, Func<Scan<Reading>, Task> run) in sinks)
        {
            for (int i = 0; i < WarmUp; i++)
            {
                await run(scan());
            }

            // What the plan reads is what the sink reads before its first split, the structures
            // its filter consults; the split after the first one begins at the next read.
            source.Clear();
            await scan().ExplainAsync(TestContext.Current.CancellationToken);
            int second = source.Count + 1;

            source.Clear();
            await run(scan());
            int reads = source.Count;
            if (reads < second + 3)
            {
                failures.Add($"{sink} read {reads} times, too few to say anything per batch");
                continue;
            }

            int thread = source.ThreadAt(second);
            bool oneThread = true;
            for (int i = second; i < reads; i++)
            {
                oneThread &= source.ThreadAt(i) == thread;
            }

            if (!oneThread)
            {
                failures.Add($"{sink} read on more than one thread, so the per-thread counts do not add up");
                continue;
            }

            long allocated = source.AllocatedAt(reads - 1) - source.AllocatedAt(second);
            if (allocated != 0)
            {
                failures.Add(string.Create(CultureInfo.InvariantCulture, $"{sink} allocated {allocated} B from its read {second} to its last of {reads}"));
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public async Task NothingReachesTheLargeObjectHeapOnceAScanIsUnderWay()
    {
        ReleaseOnlyCeilings.Require();
        await using VortexFile file = await VortexFile.OpenAsync(await ContractFile.PathAsync(), TestContext.Current.CancellationToken);

        for (int i = 0; i < WarmUp; i++)
        {
            await TypedAsync(file, ScanOptions.Default, everyThread: true);
        }

        int collections = GC.CollectionCount(2);
        long allocated = 0;
        for (int i = 0; i < Runs; i++)
        {
            allocated += (await TypedAsync(file, ScanOptions.Default, everyThread: true)).Allocated;
        }

        Assert.Equal(collections, GC.CollectionCount(2));
        Assert.True(
            allocated < 85_000,
            string.Create(CultureInfo.InvariantCulture, $"{Runs} scans allocated {allocated} B after their first batches, room for an object on the large object heap"));
    }

    /// <summary>A typed scan of the whole file under <paramref name="options"/>.</summary>
    private static Task<(long Batches, long Allocated)> TypedAsync(VortexFile file, ScanOptions options, bool everyThread = false) =>
        TypedAsync(file.Scan<Reading>().With(options), everyThread);

    /// <summary>A typed scan, reading every column of every batch.</summary>
    /// <returns>The batches, and the bytes allocated after the first one was delivered.</returns>
    private static async Task<(long Batches, long Allocated)> TypedAsync(Scan<Reading> scan, bool everyThread = false)
    {
        Scan<Reading>.AsyncEnumerator batches = scan.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        try
        {
            if (!await batches.MoveNextAsync())
            {
                return (0, 0);
            }

            int thread = Environment.CurrentManagedThreadId;
            long start = Allocated(everyThread);
            long count = 1;
            double sink = 0;
            do
            {
                Columns<Reading> columns = batches.Current;
                sink += Touch(columns);
                count++;
            }
            while (await batches.MoveNextAsync());

            long allocated = Allocated(everyThread) - start;
            Assert.True(everyThread || thread == Environment.CurrentManagedThreadId, "the scan left the measuring thread, so its count is not the scan's");
            GC.KeepAlive(sink);
            return (count - 1, allocated);
        }
        finally
        {
            await batches.DisposeAsync();
        }
    }

    /// <summary>A tool scan with no read-ahead, reading every column of every batch.</summary>
    private static async Task<(long Batches, long Allocated)> ToolAsync(VortexFile file)
    {
        Vorticity.Scan scan = file.Scan().With(Sequential);
        Vorticity.Scan.AsyncEnumerator batches = scan.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        try
        {
            if (!await batches.MoveNextAsync())
            {
                return (0, 0);
            }

            int thread = Environment.CurrentManagedThreadId;
            long start = GC.GetAllocatedBytesForCurrentThread();
            long count = 1;
            double sink = 0;
            do
            {
                BatchView view = batches.Current;
                sink += Touch(view);
                count++;
            }
            while (await batches.MoveNextAsync());

            long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
            Assert.True(thread == Environment.CurrentManagedThreadId, "the scan left the measuring thread, so its count is not the scan's");
            GC.KeepAlive(sink);
            return (count - 1, allocated);
        }
        finally
        {
            await batches.DisposeAsync();
        }
    }

    private static long Allocated(bool everyThread) =>
        everyThread ? GC.GetTotalAllocatedBytes(precise: true) : GC.GetAllocatedBytesForCurrentThread();

    private static double Touch(Columns<Reading> columns)
    {
        ReadOnlySpan<int> days = columns.Column<int>(0).Values;
        ReadOnlySpan<double> celsius = columns.Column<double?>(1).Values;
        Column<string> city = columns.Column<string>(2);
        double sum = 0;
        for (int i = 0; i < days.Length; i++)
        {
            sum += days[i] + celsius[i] + city.GetLength(i);
        }

        return sum;
    }

    private static double Touch(BatchView view)
    {
        ReadOnlySpan<int> days = view.Column<int>(0).Values;
        ReadOnlySpan<double> celsius = view.Column<double?>(1).Values;
        Column<string> city = view.Column<string>(2);
        double sum = 0;
        for (int i = 0; i < days.Length; i++)
        {
            sum += days[i] + celsius[i] + city.GetLength(i);
        }

        return sum;
    }
}
