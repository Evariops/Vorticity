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
/// The spill (PLAN-HIGH-CARDINALITY, H6): a group by whose budget cannot hold its groups even once each
/// writes the largest parts of its core to the scratch, and delivers them one at a time after the parts
/// it held, exact, every reservation and every file given back.
/// </summary>
[Collection(nameof(CorePressureCollection))]
public sealed partial class SpillTests
{
    private const int Keys = 200_000;

    [Theory]
    [InlineData(1, 20, true)]
    [InlineData(1, 10, true)]
    [InlineData(4, 20, true)]
    [InlineData(14, 20, true)]
    [InlineData(14, 10, true)]
    [InlineData(1, 20, false)]
    [InlineData(14, 10, false)]
    public async Task AGroupByItsBudgetCannotHoldSpillsAndEndsExact(int degree, int percent, bool parted)
    {
        // Part by part (H7), the parts held in memory come out as the workers apply them; whole, they come
        // out together, then the parts spilled one at a time.
        (string path, long[] expected) = await WriteAsync();
        string scratch = Directory.CreateTempSubdirectory("vorticity-spill-").FullName;
        try
        {
            long peak = await PeakAsync(path, degree);
            QueryMemoryBudget budget = new QueryMemoryBudget(peak / 100 * percent);
            await using VortexSession session = VortexSession.Create(options =>
            {
                options.MaxDegreeOfParallelism = degree;
                options.MemoryBudget = budget;
                options.ScratchDirectory = scratch;
            });

            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Vorticity.Aggregation sums = Sums(file);
            sums.Plan.CoreParted = parted;
            int read = 0;
            await foreach (KeySum sum in sums.As<KeySum>().ToRecordsAsync(Ct))
            {
                Assert.Equal(expected[sum.Key], sum.Sum);
                read++;
            }

            Assert.Equal(Keys, read);
            GroupStatistics grouping = sums.Statistics.Grouping!;
            Assert.True(grouping.SpilledParts > 0, $"no part spilled under {budget.CeilingBytes:N0} bytes of {peak:N0}");
            Assert.True(grouping.SpilledBytes > 0);
            Assert.Equal(0, budget.ReservedBytes);
            Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task TwoQueriesSpillAtOnceAndBothEndExact()
    {
        (string path, long[] expected) = await WriteAsync();
        string scratch = Directory.CreateTempSubdirectory("vorticity-spill-").FullName;
        try
        {
            long peak = await PeakAsync(path, 4);
            QueryMemoryBudget first = new QueryMemoryBudget(peak / 5);
            QueryMemoryBudget second = new QueryMemoryBudget(peak / 5);
            int[] read = await Task.WhenAll(ReadAsync(first), ReadAsync(second));
            Assert.Equal([Keys, Keys], read);
            Assert.Equal(0, first.ReservedBytes);
            Assert.Equal(0, second.ReservedBytes);
            Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
            System.IO.File.Delete(path);
        }

        async Task<int> ReadAsync(QueryMemoryBudget budget)
        {
            await using VortexSession session = VortexSession.Create(options =>
            {
                options.MaxDegreeOfParallelism = 4;
                options.MemoryBudget = budget;
                options.ScratchDirectory = scratch;
            });

            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Vorticity.Aggregation sums = Sums(file);
            int count = 0;
            await foreach (KeySum sum in sums.As<KeySum>().ToRecordsAsync(Ct))
            {
                Assert.Equal(expected[sum.Key], sum.Sum);
                count++;
            }

            Assert.True(sums.Statistics.Grouping!.SpilledParts > 0);
            return count;
        }
    }

    [Fact]
    public async Task ACancelledSpillLeavesNoFileAndNoReservation()
    {
        (string path, _) = await WriteAsync();
        string scratch = Directory.CreateTempSubdirectory("vorticity-spill-").FullName;
        try
        {
            long peak = await PeakAsync(path, 4);
            QueryMemoryBudget budget = new QueryMemoryBudget(peak / 10);
            await using VortexSession session = VortexSession.Create(options =>
            {
                options.MaxDegreeOfParallelism = 4;
                options.MemoryBudget = budget;
                options.ScratchDirectory = scratch;
            });

            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            for (int after = 1; after <= 64; after *= 2)
            {
                using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
                cancel.CancelAfter(TimeSpan.FromMilliseconds(after));
                try
                {
                    await foreach (KeySum _ in Sums(file).As<KeySum>().ToRecordsAsync(cancel.Token))
                    {
                    }
                }
                catch (OperationCanceledException)
                {
                }

                Assert.Equal(0, budget.ReservedBytes);
                Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
            }
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
            System.IO.File.Delete(path);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(14)]
    public async Task AConsumerThatStopsAtItsFirstGroupGivesEverythingBack(int degree)
    {
        // Part by part (H7), the workers still apply the other parts when the consumer leaves: they stop,
        // and the query's memory and scratch are given back once they are done. Under a tenth of its
        // peak, the lanes turn to the core, which spills; under a large budget, the core asked for.
        (string path, _) = await WriteAsync();
        string scratch = Directory.CreateTempSubdirectory("vorticity-spill-").FullName;
        try
        {
            long peak = await PeakAsync(path, degree);
            foreach ((long ceiling, bool asked) in ((long, bool)[])[(peak / 10, false), (1L << 30, true)])
            {
                QueryMemoryBudget budget = new QueryMemoryBudget(ceiling);
                await using VortexSession session = VortexSession.Create(options =>
                {
                    options.MaxDegreeOfParallelism = degree;
                    options.MemoryBudget = budget;
                    options.ScratchDirectory = scratch;
                });

                await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
                Vorticity.Aggregation sums = Sums(file);
                sums.Plan.Core = asked;
                sums.Plan.CoreLanes = 1;
                sums.Plan.CoreCapacity = asked ? 4_096 : null;
                await foreach (KeySum _ in sums.As<KeySum>().ToRecordsAsync(Ct))
                {
                    break;
                }

                Assert.NotNull(sums.Plan.LastRun?.Core);
                Assert.Equal(0, budget.ReservedBytes);
                Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
            }
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AScratchThatCannotBeWrittenFailsTheQueryCleanly()
    {
        (string path, _) = await WriteAsync();
        try
        {
            long peak = await PeakAsync(path, 4);
            QueryMemoryBudget budget = new QueryMemoryBudget(peak / 10);
            await using VortexSession session = VortexSession.Create(options =>
            {
                options.MaxDegreeOfParallelism = 4;
                options.MemoryBudget = budget;
                options.ScratchDirectory = Path.Combine(Path.GetTempPath(), $"vorticity-absent-{Guid.NewGuid():N}", "scratch");
            });

            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            await Assert.ThrowsAnyAsync<IOException>(async () =>
            {
                await foreach (KeySum _ in Sums(file).As<KeySum>().ToRecordsAsync(Ct))
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
    public async Task AnOrderOverASpilledGroupBySaysItWaitsForTheExternalSort()
    {
        (string path, _) = await WriteAsync();
        try
        {
            long peak = await PeakAsync(path, 4);
            QueryMemoryBudget budget = new QueryMemoryBudget(peak / 5);
            await using VortexSession session = VortexSession.Create(options =>
            {
                options.MaxDegreeOfParallelism = 4;
                options.MemoryBudget = budget;
            });

            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            VortexMemoryException refused = await Assert.ThrowsAsync<VortexMemoryException>(async () =>
                await file.Scan<Row>().With(new ScanOptions { BatchRows = 1_024 }).GroupBy(r => r.Key).OrderBy(g => g.Sum(x => x.Value)).Select(g => g.Key).ToListAsync(Ct));
            Assert.Contains("external sort", refused.Message, StringComparison.Ordinal);
            Assert.Equal(0, budget.ReservedBytes);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>What the query reserves at most at <paramref name="degree"/>, its budget large.</summary>
    private static async Task<long> PeakAsync(string path, int degree)
    {
        QueryMemoryBudget large = new QueryMemoryBudget(1L << 30);
        await using VortexSession session = VortexSession.Create(options =>
        {
            options.MaxDegreeOfParallelism = degree;
            options.MemoryBudget = large;
        });

        await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
        await foreach (KeySum _ in Sums(file).As<KeySum>().ToRecordsAsync(Ct))
        {
        }

        return large.PeakBytes;
    }

    private static Vorticity.Aggregation Sums(VortexFile file) =>
        file.Scan<Row>().With(new ScanOptions { BatchRows = 1_024 }).GroupBy(r => r.Key).Select(g => (g.Key, g.Sum(x => x.Value)));

    private static async Task<(string Path, long[] Expected)> WriteAsync()
    {
        const int rows = 1_200_000;
        Row[] data = new Row[rows];
        long[] expected = new long[Keys];
        for (int row = 0; row < rows; row++)
        {
            int key = row < Keys ? row : (int)(((ulong)row * 0x9E37_79B9_7F4A_7C15UL >> 24) % Keys);
            data[row] = new Row(key, row % 100);
            expected[key] += row % 100;
        }

        string path = Path.Combine(AppContext.BaseDirectory, "spill", $"spill-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Row>(path);
        await writer.WriteAsync<Row>(data, Ct);
        await writer.CompleteAsync(Ct);
        return (path, expected);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [VortexRecord]
    public partial record struct Row(int Key, long Value);

    [VortexRecord]
    public partial record struct KeySum(int Key, long Sum);
}
