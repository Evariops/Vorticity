using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// The lanes' spill: a query whose budget cannot hold what its lanes hold, and whose core cannot take it,
/// writes it to the scratch in runs and reads them back at the end, part by part, exact, every
/// reservation and every file given back. Budgets are set against the result: what one lane holds of
/// it once it has met every row.
/// </summary>
[Collection(nameof(CorePressureCollection))]
public sealed partial class LaneSpillTests
{
    private const int Rows = 2_000_000;

    [Theory]
    [InlineData(1, 200)]
    [InlineData(1, 50)]
    [InlineData(1, 10)]
    [InlineData(4, 50)]
    [InlineData(4, 10)]
    [InlineData(14, 200)]
    [InlineData(14, 50)]
    [InlineData(14, 10)]
    public async Task ADistinctCountOverTheScanItsBudgetCannotHoldSpillsAndEndsExact(int degree, int percent)
    {
        // A million and a half values, nulls, and the value of zero bits, which no slot of a set holds.
        (string path, Row[] rows) = await WriteAsync();
        string scratch = Directory.CreateTempSubdirectory("vorticity-lane-spill-").FullName;
        try
        {
            long result = await PeakAsync(path, degree: 1);
            QueryMemoryBudget budget = new QueryMemoryBudget(result / 100 * percent);
            await using VortexSession session = VortexSession.Create(options =>
            {
                options.MaxDegreeOfParallelism = degree;
                options.MemoryBudget = budget;
                options.ScratchDirectory = scratch;
            });

            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            long count = await file.Scan<Row>().CountDistinctAsync(r => r.User, Ct);
            Assert.Equal(rows.Where(r => r.User is not null).Select(r => r.User).Distinct().LongCount(), count);
            Assert.Equal(0, budget.ReservedBytes);
            Assert.True(budget.PeakBytes <= budget.CeilingBytes * 106 / 100, $"peak {budget.PeakBytes:N0} of {budget.CeilingBytes:N0}");
            Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task ADistinctCountOverTheScanWhoseScratchBudgetItsRunPassesFailsCleanly()
    {
        // A tenth of the result, and 64 KiB of scratch, which the first run passes: the typed refusal, no
        // file left, nothing reserved of either budget. That it fails shows the spill was taken.
        (string path, _) = await WriteAsync();
        string scratch = Directory.CreateTempSubdirectory("vorticity-lane-spill-").FullName;
        try
        {
            long result = await PeakAsync(path, degree: 1);
            QueryMemoryBudget budget = new QueryMemoryBudget(result / 10);
            ScratchBudget room = new ScratchBudget(64 * 1024);
            await using VortexSession session = VortexSession.Create(options =>
            {
                options.MaxDegreeOfParallelism = 4;
                options.MemoryBudget = budget;
                options.ScratchDirectory = scratch;
                options.ScratchBudget = room;
            });

            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            VortexMemoryException refused = await Assert.ThrowsAsync<VortexMemoryException>(async () => await file.Scan<Row>().CountDistinctAsync(r => r.User, Ct));
            Assert.Contains("scratch budget", refused.Message, StringComparison.Ordinal);
            Assert.Equal(0, budget.ReservedBytes);
            Assert.Equal(0, room.ReservedBytes);
            Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
            System.IO.File.Delete(path);
        }
    }

    /// <summary>The most the query holds of a budget that grants it all, at <paramref name="degree"/>.</summary>
    private static async Task<long> PeakAsync(string path, int degree)
    {
        QueryMemoryBudget large = new QueryMemoryBudget(1L << 30);
        await using VortexSession session = VortexSession.Create(options =>
        {
            options.MaxDegreeOfParallelism = degree;
            options.MemoryBudget = large;
        });

        await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
        await file.Scan<Row>().CountDistinctAsync(r => r.User, Ct);
        return large.PeakBytes;
    }

    private static async Task<(string Path, Row[] Rows)> WriteAsync()
    {
        Row[] rows = new Row[Rows];
        for (int row = 0; row < Rows; row++)
        {
            ulong mix = Mix((ulong)row);
            int? user = row % 97 == 0 ? null : row % 101 == 0 ? 0 : (int)(mix % 1_500_000);
            rows[row] = new Row(user, (long)((mix >> 40) % 1_000));
        }

        string directory = Path.Combine(AppContext.BaseDirectory, "lane-spill");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"rows-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Row>(path))
        {
            await writer.WriteAsync<Row>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        return (path, rows);
    }

    private static ulong Mix(ulong x)
    {
        x += 0x9E37_79B9_7F4A_7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58_476D_1CE4_E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D0_49BB_1331_11EBUL;
        return x ^ (x >> 31);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [VortexRecord]
    public partial record struct Row(int? User, long Value);
}
