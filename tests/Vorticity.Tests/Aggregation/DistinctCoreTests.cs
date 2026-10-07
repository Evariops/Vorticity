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
/// A <c>Distinct</c> on several lanes through the core (PLAN-HIGH-CARDINALITY, H13): each value told as
/// it enters its part's set, once, as the rows come, whatever the degree; the same values as on the
/// reader's thread alone; under a budget that spills, the values of a part evicted told once it comes
/// back, against the runs it wrote; every reservation and every file given back, read to its end or not.
/// </summary>
[Collection(nameof(CorePressureCollection))]
public sealed partial class DistinctCoreTests
{
    private const int Values = 150_000;

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(14)]
    public async Task EachValueComesOnceWhateverTheDegree(int degree)
    {
        (Row[] rows, string path) = await WriteAsync();
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            HashSet<long?> expected = [.. rows.Select(r => r.Key)];
            Aggregation<long?> keys = file.Scan<Row>().With(new ScanOptions { BatchRows = 4_096 }).Select(r => r.Key).Distinct();
            List<long?> read = await keys.ToListAsync(Ct);
            Assert.Equal(expected.Count, read.Count);
            Assert.True(expected.SetEquals(read));

            // On several lanes the core told them, its parts applied by bursts as the rows came.
            CoreRun? core = ((DistinctQuery)keys.Query).LastCore;
            Assert.Equal(degree > 1, core is not null);
            Assert.True(degree == 1 || core!.Bursts > 0, $"{core}");

            // A pair of columns travels as its word; a skip passes over the first values told.
            HashSet<(long?, int)> pairs = [.. rows.Select(r => (r.Key, r.Small))];
            List<KeySmall> pairsRead = await ListAsync(file.Scan<Row>().Select(r => (r.Key, r.Small)).Distinct().As<KeySmall>());
            Assert.Equal(pairs.Count, pairsRead.Count);
            Assert.True(pairs.SetEquals(pairsRead.Select(p => (p.Key, p.Small))));
            List<long?> skipped = await file.Scan<Row>().Select(r => r.Key).Distinct().Skip(1_000).ToListAsync(Ct);
            Assert.Equal(expected.Count - 1_000, skipped.Count);
            Assert.Equal(skipped.Count, skipped.Distinct().Count());
            Assert.True(skipped.All(expected.Contains));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Theory]
    [InlineData(4)]
    [InlineData(14)]
    public async Task ADistinctItsBudgetCannotHoldTellsEachValueOnceAfterItSpills(int degree)
    {
        (Row[] rows, string path) = await WriteAsync();
        string scratch = Directory.CreateTempSubdirectory("vorticity-distinct-").FullName;
        try
        {
            long peak = await PeakAsync(path, degree);
            QueryMemoryBudget budget = new QueryMemoryBudget(peak / 5);
            await using VortexSession session = VortexSession.Create(options =>
            {
                options.MaxDegreeOfParallelism = degree;
                options.MemoryBudget = budget;
                options.ScratchDirectory = scratch;
            });

            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Aggregation<long?> keys = file.Scan<Row>().With(new ScanOptions { BatchRows = 1_024 }).Select(r => r.Key).Distinct();
            List<long?> read = await keys.ToListAsync(Ct);
            HashSet<long?> expected = [.. rows.Select(r => r.Key)];
            Assert.Equal(expected.Count, read.Count);
            Assert.True(expected.SetEquals(read));
            Assert.True(((DistinctQuery)keys.Query).LastCore?.SpilledParts > 0, $"no part spilled under {budget.CeilingBytes:N0} bytes of {peak:N0}");
            Assert.Equal(0, budget.ReservedBytes);
            Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
            System.IO.File.Delete(path);
        }
    }

    // Half the values in the first half of the rows, the other half met for the first time after: a part
    // spilled in the first half makes the second's new values in memory, in silence, and they are told
    // once it comes back. Three passes, the spills falling where the lanes' timing puts them.
    [Theory]
    [InlineData(4)]
    [InlineData(14)]
    public async Task ValuesMetAfterTheirPartSpilledAreToldOnce(int degree)
    {
        Row[] rows = new Row[4 * Values];
        for (int row = 0; row < rows.Length; row++)
        {
            long value = (long)(((ulong)row * 0x9E37_79B9_7F4A_7C15UL >> 24) % (Values / 2));
            rows[row] = new Row((row < rows.Length / 2 ? value : value + (Values / 2)) * 1_000_003L, row % 7);
        }

        string path = Path.Combine(AppContext.BaseDirectory, "distinct-core", $"halves-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Row>(path))
        {
            await writer.WriteAsync<Row>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        string scratch = Directory.CreateTempSubdirectory("vorticity-distinct-").FullName;
        try
        {
            long peak = await PeakAsync(path, degree);
            HashSet<long?> expected = [.. rows.Select(r => r.Key)];
            for (int pass = 0; pass < 3; pass++)
            {
                QueryMemoryBudget budget = new QueryMemoryBudget(peak / 5);
                await using VortexSession session = VortexSession.Create(options =>
                {
                    options.MaxDegreeOfParallelism = degree;
                    options.MemoryBudget = budget;
                    options.ScratchDirectory = scratch;
                });

                await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
                Aggregation<long?> keys = file.Scan<Row>().With(new ScanOptions { BatchRows = 1_024 }).Select(r => r.Key).Distinct();
                List<long?> read = await keys.ToListAsync(Ct);
                Assert.Equal(expected.Count, read.Count);
                Assert.True(expected.SetEquals(read));
                Assert.True(((DistinctQuery)keys.Query).LastCore?.SpilledParts > 0, $"no part spilled under {budget.CeilingBytes:N0} bytes of {peak:N0}");
                Assert.Equal(0, budget.ReservedBytes);
            }
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AReaderThatStopsStopsThePass()
    {
        (_, string path) = await WriteAsync();
        try
        {
            QueryMemoryBudget budget = new QueryMemoryBudget(1L << 30);
            await using VortexSession session = VortexSession.Create(options =>
            {
                options.MaxDegreeOfParallelism = 14;
                options.MemoryBudget = budget;
            });

            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            await foreach (long? _ in file.Scan<Row>().With(new ScanOptions { BatchRows = 1_024 }).Select(r => r.Key).Distinct().WithCancellation(Ct))
            {
                break;
            }

            Assert.Equal(0, budget.ReservedBytes);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>What the values reserve at most on the reader's thread alone, its budget large.</summary>
    private static async Task<long> PeakAsync(string path, int degree)
    {
        QueryMemoryBudget large = new QueryMemoryBudget(1L << 30);
        await using VortexSession session = VortexSession.Create(options =>
        {
            options.MaxDegreeOfParallelism = degree;
            options.MemoryBudget = large;
        });

        await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
        await file.Scan<Row>().With(new ScanOptions { BatchRows = 1_024 }).Select(r => r.Key).Distinct().ToListAsync(Ct);
        return large.PeakBytes;
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

    /// <summary>
    /// 150 000 keys in no order, each four times, spread over eleven digits as identifiers are, so that
    /// no table by value bounds them; a null one row in 101; a small value of 7.
    /// </summary>
    private static async Task<(Row[] Rows, string Path)> WriteAsync()
    {
        Row[] rows = new Row[4 * Values];
        for (int row = 0; row < rows.Length; row++)
        {
            long? key = row % 101 == 0 ? null : (long)(((ulong)row * 0x9E37_79B9_7F4A_7C15UL >> 24) % Values) * 1_000_003L;
            rows[row] = new Row(key, row % 7);
        }

        string path = Path.Combine(AppContext.BaseDirectory, "distinct-core", $"rows-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Row>(path))
        {
            await writer.WriteAsync<Row>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        return (rows, path);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [VortexRecord]
    public partial record struct Row(long? Key, int Small);

    [VortexRecord]
    public partial record struct KeySmall(long? Key, int Small);
}
