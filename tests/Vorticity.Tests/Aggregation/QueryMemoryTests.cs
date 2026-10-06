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
/// The memory a query holds, under its session's budget (PLAN-HIGH-CARDINALITY, H2): a group by past it
/// fails with a <see cref="VortexMemoryException"/> that names it, and gives back everything it held,
/// at one lane and at fourteen; under it, it runs and gives its memory back when its groups are merged;
/// sessions that share a budget share its ceiling.
/// </summary>
public sealed partial class QueryMemoryTests
{
    private const int Rows = 400_000;

    [Theory]
    [InlineData(1)]
    [InlineData(14)]
    public async Task AGroupByPastItsBudgetFailsAndGivesEverythingBack(int degree)
    {
        string path = await WriteAsync();
        try
        {
            QueryMemoryBudget budget = new QueryMemoryBudget(2 << 20);
            await using VortexSession session = VortexSession.Create(options =>
            {
                options.MaxDegreeOfParallelism = degree;
                options.MemoryBudget = budget;
            });

            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            VortexMemoryException failure = await Assert.ThrowsAsync<VortexMemoryException>(async () =>
                await file.Scan<Row>().GroupBy(r => r.Key).Select(g => g.Sum(x => x.Value)).ToListAsync(Ct));

            Assert.Contains("group by", failure.Message, StringComparison.Ordinal);
            Assert.Contains("2,097,152", failure.Message, StringComparison.Ordinal);
            Assert.Equal(0, budget.ReservedBytes);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(14)]
    public async Task ADistinctCountPastItsBudgetFailsAndGivesEverythingBack(int degree)
    {
        string path = await WriteAsync();
        try
        {
            QueryMemoryBudget budget = new QueryMemoryBudget(1 << 20);
            await using VortexSession session = VortexSession.Create(options =>
            {
                options.MaxDegreeOfParallelism = degree;
                options.MemoryBudget = budget;
            });

            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            await Assert.ThrowsAsync<VortexMemoryException>(async () => await file.Scan<Row>().CountDistinctAsync(r => r.Key, Ct));
            Assert.Equal(0, budget.ReservedBytes);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(14)]
    public async Task AGroupByUnderItsBudgetRunsAndGivesItBack(int degree)
    {
        string path = await WriteAsync();
        try
        {
            QueryMemoryBudget budget = new QueryMemoryBudget(1L << 30);
            await using VortexSession session = VortexSession.Create(options =>
            {
                options.MaxDegreeOfParallelism = degree;
                options.MemoryBudget = budget;
            });

            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            List<long> counts = await file.Scan<Row>().GroupBy(r => r.Key).Select(g => g.Count()).ToListAsync(Ct);
            Assert.Equal(Keys, counts.Count);
            Assert.Equal(Rows, counts.Sum());
            Assert.Equal(Keys, await file.Scan<Row>().CountDistinctAsync(r => r.Key, Ct));
            Assert.Equal(0, budget.ReservedBytes);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(14)]
    public async Task AStreamAndADistinctGiveBackWhatTheyReserve(int degree)
    {
        // A key the statistics say is sorted: its group by streams, by ranges side by side on several lanes.
        Row[] sorted = new Row[Rows];
        for (int row = 0; row < Rows; row++)
        {
            sorted[row] = new Row(row / 2, row % 100);
        }

        string path = Path.Combine(AppContext.BaseDirectory, "query-memory", $"sorted-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Row>(path))
        {
            await writer.WriteAsync<Row>(sorted, Ct);
            await writer.CompleteAsync(Ct);
        }

        string random = await WriteAsync();
        try
        {
            QueryMemoryBudget budget = new QueryMemoryBudget(1L << 30);
            await using VortexSession session = VortexSession.Create(options =>
            {
                options.MaxDegreeOfParallelism = degree;
                options.MemoryBudget = budget;
            });

            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            List<long> sums = await file.Scan<Row>().GroupBy(r => r.Key).Select(g => g.Sum(x => x.Value)).ToListAsync(Ct);
            Assert.Equal(Rows / 2, sums.Count);
            Assert.Equal(0, budget.ReservedBytes);

            await using VortexFile shuffled = await session.OpenAsync(random, cancellationToken: Ct);
            Assert.Equal(Keys, (await shuffled.Scan<Row>().Select(r => r.Key).Distinct().ToListAsync(Ct)).Count);
            Assert.Equal(0, budget.ReservedBytes);

            // Past a budget too small for its index, the distinct fails and gives everything back.
            QueryMemoryBudget small = new QueryMemoryBudget(2 << 20);
            await using VortexSession tight = VortexSession.Create(options =>
            {
                options.MaxDegreeOfParallelism = degree;
                options.MemoryBudget = small;
            });

            await using VortexFile again = await tight.OpenAsync(random, cancellationToken: Ct);
            await Assert.ThrowsAsync<VortexMemoryException>(async () => await again.Scan<Row>().Select(r => r.Key).Distinct().ToListAsync(Ct));
            Assert.Equal(0, small.ReservedBytes);
        }
        finally
        {
            System.IO.File.Delete(path);
            System.IO.File.Delete(random);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(14)]
    public async Task EveryTableAShelfGrowsGivesBackWhatItReserved(int degree)
    {
        const int rows = 200_000;
        Wide[] data = new Wide[rows];
        for (int row = 0; row < rows; row++)
        {
            data[row] = new Wide(row % 50_000, row % 7, row % 100, $"n{row % 50_000}", row % 3);
        }

        string path = Path.Combine(AppContext.BaseDirectory, "query-memory", $"wide-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Wide>(path))
        {
            await writer.WriteAsync<Wide>(data, Ct);
            await writer.CompleteAsync(Ct);
        }

        try
        {
            QueryMemoryBudget budget = new QueryMemoryBudget(1L << 30);
            await using VortexSession session = VortexSession.Create(options =>
            {
                options.MaxDegreeOfParallelism = degree;
                options.MemoryBudget = budget;
            });

            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);

            // A text key; a key of two columns; one of five, encoded into bytes.
            Assert.Equal(50_000, (await file.Scan<Wide>().GroupBy(r => r.Name).Select(g => g.Count()).ToListAsync(Ct)).Count);
            Assert.Equal(0, budget.ReservedBytes);
            Assert.Equal(data.Select(r => (r.Key, r.Other)).Distinct().Count(), (await file.Scan<Wide>().GroupBy(r => (r.Key, r.Other)).Select(g => g.Count()).ToListAsync(Ct)).Count);
            Assert.Equal(0, budget.ReservedBytes);
            Assert.Equal(
                data.Select(r => (r.Key, r.Other, r.Value, r.Name, r.Extra)).Distinct().Count(),
                (await file.Scan<Wide>().GroupBy(r => (r.Key, r.Other, r.Value, r.Name, r.Extra)).Select(g => g.Count()).ToListAsync(Ct)).Count);
            Assert.Equal(0, budget.ReservedBytes);

            // A text's extreme, and distinct counts of a text and of an integer.
            List<string?> largest = await file.Scan<Wide>().GroupBy(r => r.Other).OrderBy(g => g.Key).Select(g => g.Max(x => x.Name)).ToListAsync(Ct);
            Assert.Equal(data.GroupBy(r => r.Other).OrderBy(g => g.Key).Select(g => g.Select(x => x.Name).Max(StringComparer.Ordinal)), largest);
            Assert.Equal(0, budget.ReservedBytes);
            List<long> names = await file.Scan<Wide>().GroupBy(r => r.Other).OrderBy(g => g.Key).Select(g => g.CountDistinct(x => x.Name)).ToListAsync(Ct);
            Assert.Equal(data.GroupBy(r => r.Other).OrderBy(g => g.Key).Select(g => (long)g.Select(x => x.Name).Distinct().Count()), names);
            Assert.Equal(0, budget.ReservedBytes);
            List<long> keys = await file.Scan<Wide>().GroupBy(r => r.Other).OrderBy(g => g.Key).Select(g => g.CountDistinct(x => x.Key)).ToListAsync(Ct);
            Assert.Equal(data.GroupBy(r => r.Other).OrderBy(g => g.Key).Select(g => (long)g.Select(x => x.Key).Distinct().Count()), keys);
            Assert.Equal(0, budget.ReservedBytes);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(14)]
    public async Task AnOrderATopAndAFetchGiveBackWhatTheyReserve(int degree)
    {
        string path = await WriteAsync();
        try
        {
            QueryMemoryBudget budget = new QueryMemoryBudget(1L << 30);
            await using VortexSession session = VortexSession.Create(options =>
            {
                options.MaxDegreeOfParallelism = degree;
                options.MemoryBudget = budget;
            });

            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);

            // The groups in order, all of them, then their top: the sort's scratch and the order delivered.
            List<int> ordered = await file.Scan<Row>().GroupBy(r => r.Key).OrderByDescending(g => g.Sum(x => x.Value)).ThenBy(g => g.Key).Select(g => g.Key).ToListAsync(Ct);
            Assert.Equal(Keys, ordered.Count);
            Assert.Equal(0, budget.ReservedBytes);
            List<int> top = await file.Scan<Row>().GroupBy(r => r.Key).OrderByDescending(g => g.Sum(x => x.Value)).ThenBy(g => g.Key).Take(10).Select(g => g.Key).ToListAsync(Ct);
            Assert.Equal(ordered.Take(10), top);
            Assert.Equal(0, budget.ReservedBytes);

            // A row each group chose, fetched once the groups are known.
            List<long> lasts = await file.Scan<Row>().GroupBy(r => r.Key).Select(g => g.Last().Value).ToListAsync(Ct);
            Assert.Equal(Keys, lasts.Count);
            Assert.Equal(0, budget.ReservedBytes);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AQueryItsBudgetCannotGiveEveryLaneStartsOnFewer()
    {
        string path = await WriteAsync();
        try
        {
            // A lane's working memory is a megabyte: eight grant seven lanes of fourteen, and their few tables.
            QueryMemoryBudget budget = new QueryMemoryBudget(8 << 20);
            await using VortexSession session = VortexSession.Create(options =>
            {
                options.MaxDegreeOfParallelism = 14;
                options.MemoryBudget = budget;
            });

            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Aggregation<long> counts = file.Scan<Row>().GroupBy(r => r.Value).Select(g => g.Count());
            List<long> answered = await counts.ToListAsync(Ct);
            Assert.Equal(100, answered.Count);
            Assert.Equal(Rows, answered.Sum());

            int lanes = ((AggregationQuery)counts.Query).Plan.LastRun!.Lanes.Length;
            Assert.True(lanes is > 1 and < 14, $"{lanes} lanes");
            Assert.Equal(0, budget.ReservedBytes);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task ATableReservesWhatItHoldsNotTwice()
    {
        string path = await WriteAsync();
        try
        {
            // What the groups hold, under a budget that grants it all.
            long state;
            await using (VortexSession wide = VortexSession.Create(options =>
            {
                options.MaxDegreeOfParallelism = 1;
                options.MemoryBudget = new QueryMemoryBudget(1L << 30);
            }))
            {
                await using VortexFile file = await wide.OpenAsync(path, cancellationToken: Ct);
                Aggregation<long> sums = file.Scan<Row>().GroupBy(r => r.Key).Select(g => g.Sum(x => x.Value));
                Assert.Equal(Keys, (await sums.ToListAsync(Ct)).Count);
                state = ((AggregationQuery)sums.Query).Plan.LastRun!.StateBytes;
            }

            // Its arrays reserved as they come, the old ones beside the new only for the copy: under the
            // twice its groups hold that a reading after each batch reserved.
            QueryMemoryBudget budget = new QueryMemoryBudget((state * 7 / 4) + (2 << 20));
            await using VortexSession session = VortexSession.Create(options =>
            {
                options.MaxDegreeOfParallelism = 1;
                options.MemoryBudget = budget;
            });

            await using VortexFile again = await session.OpenAsync(path, cancellationToken: Ct);
            Assert.Equal(Keys, (await again.Scan<Row>().GroupBy(r => r.Key).Select(g => g.Sum(x => x.Value)).ToListAsync(Ct)).Count);
            Assert.Equal(0, budget.ReservedBytes);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AResultHoldsItsMemoryUntilItIsDelivered()
    {
        string path = await WriteAsync();
        try
        {
            QueryMemoryBudget budget = new QueryMemoryBudget(1L << 30);
            await using VortexSession session = VortexSession.Create(options =>
            {
                options.MaxDegreeOfParallelism = 4;
                options.MemoryBudget = budget;
            });

            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Aggregation<long> counts = file.Scan<Row>().GroupBy(r => r.Key).Select(g => g.Count());
            await using (IAsyncEnumerator<long> read = counts.GetAsyncEnumerator(Ct))
            {
                // The first group read, the others are still held: the result counts until it is let go.
                Assert.True(await read.MoveNextAsync());
                Assert.True(budget.ReservedBytes > 0);
            }

            Assert.Equal(0, budget.ReservedBytes);
            Assert.Equal(Keys, (await file.Scan<Row>().GroupBy(r => r.Key).Select(g => g.Count()).ToListAsync(Ct)).Count);
            Assert.Equal(0, budget.ReservedBytes);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task SessionsThatShareABudgetShareItsCeiling()
    {
        string path = await WriteAsync();
        try
        {
            // Two sessions, one budget: their queries side by side, then a query each, sequentially.
            QueryMemoryBudget budget = new QueryMemoryBudget(1L << 30);
            await using VortexSession first = VortexSession.Create(options =>
            {
                options.MaxDegreeOfParallelism = 4;
                options.MemoryBudget = budget;
            });

            await using VortexSession second = VortexSession.Create(options =>
            {
                options.MaxDegreeOfParallelism = 4;
                options.MemoryBudget = budget;
            });

            await using VortexFile a = await first.OpenAsync(path, cancellationToken: Ct);
            await using VortexFile b = await second.OpenAsync(path, cancellationToken: Ct);
            long[] counts = await Task.WhenAll(
                Task.Run(async () => (long)(await a.Scan<Row>().GroupBy(r => r.Key).Select(g => g.Count()).ToListAsync(Ct)).Count, Ct),
                Task.Run(async () => (long)(await b.Scan<Row>().GroupBy(r => r.Key).Select(g => g.Count()).ToListAsync(Ct)).Count, Ct));
            Assert.Equal([Keys, Keys], counts);
            Assert.Equal(0, budget.ReservedBytes);

            // Too small for either query: each fails apart, and the budget is whole after both.
            QueryMemoryBudget small = new QueryMemoryBudget(1 << 20);
            await using VortexSession third = VortexSession.Create(options => options.MemoryBudget = small);
            await using VortexFile c = await third.OpenAsync(path, cancellationToken: Ct);
            await Assert.ThrowsAsync<VortexMemoryException>(async () => await c.Scan<Row>().GroupBy(r => r.Key).Select(g => g.Count()).ToListAsync(Ct));
            await Assert.ThrowsAsync<VortexMemoryException>(async () => await c.Scan<Row>().GroupBy(r => r.Key).Select(g => g.Count()).ToListAsync(Ct));
            Assert.Equal(0, small.ReservedBytes);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void ABudgetTakesAPositiveCeiling()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new QueryMemoryBudget(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new QueryMemoryBudget(-1));
        Assert.Equal(1L << 20, new QueryMemoryBudget(1 << 20).CeilingBytes);
    }

    /// <summary>The distinct keys of the rows: three hundred thousand, past a megabyte or two of any table.</summary>
    private const int Keys = 300_000;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<string> WriteAsync()
    {
        Row[] rows = new Row[Rows];
        for (int row = 0; row < Rows; row++)
        {
            // Every key once in the first rows, then keys at random among them.
            int key = row < Keys ? row : (int)(((ulong)row * 0x9E37_79B9_7F4A_7C15UL >> 24) % Keys);
            rows[row] = new Row(key, row % 100);
        }

        string directory = Path.Combine(AppContext.BaseDirectory, "query-memory");
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
    public partial record struct Row(int Key, long Value);

    [VortexRecord]
    public partial record struct Wide(int Key, int Other, long Value, string Name, int Extra);
}
