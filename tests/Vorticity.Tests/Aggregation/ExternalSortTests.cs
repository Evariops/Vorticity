using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// The sort of a result (PLAN-HIGH-CARDINALITY, H10): under a budget a tenth of its rows, written to
/// the scratch in sorted runs and merged back, exact, ties in the result's order, a null last and
/// NaN after +∞, its peak within the budget, every run's file gone once it is read.
/// </summary>
public sealed partial class ExternalSortTests
{
    private const int Rows = 400_000;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AResultTenTimesItsBudgetSortsInRunsAndEndsExact(bool descending)
    {
        Row[] rows = Rows_();
        string path = await WriteAsync(rows);
        string scratch = Directory.CreateTempSubdirectory("vorticity-sort-").FullName;
        try
        {
            // What the result holds sorted in memory, under a large budget: the sort reserves it.
            long whole = await PeakAsync(path, descending, new QueryMemoryBudget(1L << 30), scratch);
            QueryMemoryBudget budget = new QueryMemoryBudget(whole / 10);
            await using VortexSession session = VortexSession.Create(options =>
            {
                options.MaxDegreeOfParallelism = 1;
                options.MemoryBudget = budget;
                options.ScratchDirectory = scratch;
            });

            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            List<NameValue> sorted = [];
            bool spilled = false;
            await foreach (NameValue row in Sorted(file, descending).ToRecordsAsync(Ct))
            {
                spilled |= sorted.Count == 0 && Directory.EnumerateFileSystemEntries(scratch).Any();
                sorted.Add(row);
            }

            Assert.True(spilled, "no run written");
            Assert.Equal(Expected(rows, descending), sorted);
            Assert.True(budget.PeakBytes <= budget.CeilingBytes, $"{budget.PeakBytes:N0} bytes at most under {budget.CeilingBytes:N0}");
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
    public async Task AConsumerThatStopsLeavesNoRunBehind()
    {
        Row[] rows = Rows_();
        string path = await WriteAsync(rows);
        string scratch = Directory.CreateTempSubdirectory("vorticity-sort-").FullName;
        try
        {
            long whole = await PeakAsync(path, descending: false, new QueryMemoryBudget(1L << 30), scratch);
            QueryMemoryBudget budget = new QueryMemoryBudget(whole / 10);
            await using VortexSession session = VortexSession.Create(options =>
            {
                options.MaxDegreeOfParallelism = 1;
                options.MemoryBudget = budget;
                options.ScratchDirectory = scratch;
            });

            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            await foreach (NameValue _ in Sorted(file, descending: false).ToRecordsAsync(Ct))
            {
                break;
            }

            Assert.Equal(0, budget.ReservedBytes);
            Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
            System.IO.File.Delete(path);
        }
    }

    /// <summary>The rows sorted by their value, ties in the order they came, a null last and NaN after +∞ whichever the direction of the others.</summary>
    private static List<NameValue> Expected(Row[] rows, bool descending)
    {
        IEnumerable<NameValue> values = rows.Select(r => new NameValue(r.Name, r.Value));
        Comparer<double?> floats = Comparer<double?>.Create((a, b) =>
            a is null || b is null ? (a is null).CompareTo(b is null)
            : double.IsNaN(a.Value) || double.IsNaN(b.Value) ? (descending ? -1 : 1) * double.IsNaN(a.Value).CompareTo(double.IsNaN(b.Value))
            : (descending ? -1 : 1) * (a.Value == b.Value ? 0 : a.Value.CompareTo(b.Value)));
        return [.. values.OrderBy(v => v.Value, floats)];
    }

    private static Scan<NameValue> Sorted(VortexFile file, bool descending)
    {
        Scan<NameValue> rows = file.Scan<Row>().Select(r => (r.Name, r.Value)).As<NameValue>();
        return descending ? rows.OrderByDescending(r => r.Value) : rows.OrderBy(r => r.Value);
    }

    /// <summary>What the sort reserves at most under <paramref name="budget"/>.</summary>
    private static async Task<long> PeakAsync(string path, bool descending, QueryMemoryBudget budget, string scratch)
    {
        await using VortexSession session = VortexSession.Create(options =>
        {
            options.MaxDegreeOfParallelism = 1;
            options.MemoryBudget = budget;
            options.ScratchDirectory = scratch;
        });

        await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
        await foreach (NameValue _ in Sorted(file, descending).ToRecordsAsync(Ct))
        {
        }

        return budget.PeakBytes;
    }

    /// <summary>Names of a few dozen bytes, values a few thousand share, NaN and null among them.</summary>
    private static Row[] Rows_()
    {
        Row[] rows = new Row[Rows];
        for (int row = 0; row < Rows; row++)
        {
            ulong mix = (ulong)row * 0x9E37_79B9_7F4A_7C15UL;
            double? value = row % 97 == 0 ? null : row % 89 == 0 ? double.NaN : row % 83 == 0 ? (row % 2 == 0 ? 0.0 : -0.0) : (double)((mix >> 20) % 5_000) / 4;
            rows[row] = new Row(row, $"name-{(mix >> 30) % 100_000:D6}-{row}", value);
        }

        return rows;
    }

    private static async Task<string> WriteAsync(Row[] rows)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "external-sort");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"rows-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Row>(path))
        {
            await writer.WriteAsync<Row>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        return path;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [VortexRecord]
    public partial record struct Row(long Id, string Name, double? Value);

    [VortexRecord]
    public partial record struct NameValue(string Name, double? Value);
}
