using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// A result of one value per group, read from the batches of the result's column: every key form
/// and aggregate type the same values as the rows hold, past one batch of groups, through each way
/// of reading it, and a custom state as a record or a number.
/// </summary>
public sealed class ResultBatchTests
{
    // More groups than a batch of the result holds, keyed by Units.
    private const int Rows = 100_000;

    private static readonly string[] Shops = ["Arles", "Brest", "Caen", "Dax", "Évry"];

    [Fact]
    public async Task EachKeyFormComesAsItsValues()
    {
        (Sale[] rows, string path) = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(Sorted(rows.Select(r => r.Shop).Distinct()), Sorted(await file.Scan<Sale>().GroupBy(r => r.Shop).Select(g => g.Key).ToListAsync(Ct)));
            Assert.Equal(Sorted(rows.Select(r => r.Day).Distinct()), Sorted(await file.Scan<Sale>().GroupBy(r => r.Day).Select(g => g.Key).ToListAsync(Ct)));
            Assert.Equal(Sorted(rows.Select(r => r.At).Distinct()), Sorted(await file.Scan<Sale>().GroupBy(r => r.At).Select(g => g.Key).ToListAsync(Ct)));
            Assert.Equal(Sorted(rows.Select(r => r.Amount).Distinct()), Sorted(await file.Scan<Sale>().GroupBy(r => r.Amount).Select(g => g.Key).ToListAsync(Ct)));
            Assert.Equal(Sorted(rows.Select(r => r.Paid).Distinct()), Sorted(await file.Scan<Sale>().GroupBy(r => r.Paid).Select(g => g.Key).ToListAsync(Ct)));
            Assert.Equal(Sorted(rows.Select(r => r.Customer).Distinct()), Sorted(await file.Scan<Sale>().GroupBy(r => r.Customer).Select(g => g.Key).ToListAsync(Ct)));
            Assert.Equal(Sorted(rows.Select(r => r.Price).Distinct()), Sorted(await file.Scan<Sale>().GroupBy(r => r.Price).Select(g => g.Key).ToListAsync(Ct)));
            Assert.Equal(
                Sorted(rows.Select(r => r.Shop).Distinct()),
                Sorted((await file.Scan<Sale>().GroupBy(r => (r.Day, r.Shop, r.Paid)).Select(g => g.Key.Shop).ToListAsync(Ct)).Distinct()));
            Assert.Equal(
                Sorted(rows.Select(r => (r.Day, r.Shop, r.Paid)).Distinct().Select(k => k.Day)),
                Sorted(await file.Scan<Sale>().GroupBy(r => (r.Day, r.Shop, r.Paid)).Select(g => g.Key.Day).ToListAsync(Ct)));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task EachAggregateComesAsItsValues()
    {
        (Sale[] rows, string path) = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, TestContext.Current.CancellationToken);
            IEnumerable<IGrouping<string, Sale>> byShop = rows.GroupBy(r => r.Shop);
            Assert.Equal(Sorted(byShop.Select(g => (long)g.Count())), Sorted(await file.Scan<Sale>().GroupBy(r => r.Shop).Select(g => g.Count()).ToListAsync(Ct)));
            Assert.Equal(Sorted(byShop.Select(g => g.Sum(r => r.Units))), Sorted(await file.Scan<Sale>().GroupBy(r => r.Shop).Select(g => g.Sum(r => r.Units)).ToListAsync(Ct)));
            Assert.Equal(Sorted(byShop.Select(g => g.Sum(r => r.Amount))), Sorted(await file.Scan<Sale>().GroupBy(r => r.Shop).Select(g => g.Sum(r => r.Amount)).ToListAsync(Ct)));
            Assert.Equal(Sorted(byShop.Select(g => g.Max(r => r.At))), Sorted(await file.Scan<Sale>().GroupBy(r => r.Shop).Select(g => g.Max(r => r.At)).ToListAsync(Ct)));
            Assert.Equal(Sorted(byShop.Select(g => (long)g.Select(r => r.Day).Distinct().Count())), Sorted(await file.Scan<Sale>().GroupBy(r => r.Shop).Select(g => g.CountDistinct(r => r.Day)).ToListAsync(Ct)));
            Assert.Equal(
                Sorted(rows.GroupBy(r => r.Day).Select(g => g.Max(r => r.Shop))),
                Sorted(await file.Scan<Sale>().GroupBy(r => r.Day).Select(g => g.Max(r => r.Shop)).ToListAsync(Ct)));

            double?[] means = [.. await file.Scan<Sale>().GroupBy(r => r.Shop).Select(g => g.Average(r => r.Price)).ToListAsync(Ct)];
            double?[] expected = [.. byShop.Select(g => g.Average(r => r.Price))];
            Assert.Equal(expected.Length, means.Length);
            foreach ((double? want, double? got) in Sorted(expected).Zip(Sorted(means)))
            {
                Assert.Equal(want!.Value, got!.Value, 9);
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task GroupsPastOneBatchComeWhicheverWayTheyAreRead()
    {
        (Sale[] rows, string path) = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, TestContext.Current.CancellationToken);
            List<long> expected = Sorted(rows.Select(r => r.Units).Distinct());
            Assert.True(expected.Count > 65_536);

            List<long> enumerated = [];
            await foreach (long units in file.Scan<Sale>().GroupBy(r => r.Units).Select(g => g.Key).WithCancellation(Ct))
            {
                enumerated.Add(units);
            }

            Assert.Equal(expected, Sorted(enumerated));
            Assert.Equal(expected, Sorted(await file.Scan<Sale>().GroupBy(r => r.Units).Select(g => g.Key).ToListAsync(Ct)));
            Assert.Equal(expected, Sorted(await file.Scan<Sale>().GroupBy(r => r.Units).Select(g => g.Key).ToArrayAsync(Ct)));
            Assert.Equal(expected, Sorted(await file.Scan<Sale>().GroupBy(r => r.Units).Select(g => g.Key).ToValuesAsync(Ct).ToListAsync(Ct)));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AStateComesAsARecordOrAsANumber()
    {
        (Sale[] rows, string path) = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, TestContext.Current.CancellationToken);
            List<MeanState> states = await file.Scan<Sale>()
                .GroupBy(r => r.Shop)
                .Select(g => g.Aggregate<long, MeanOfUnits, MeanState>(r => r.Units))
                .ToListAsync(Ct);
            Assert.Equal(
                Sorted(rows.GroupBy(r => r.Shop).Select(g => (Count: (long)g.Count(), Sum: (double)g.Sum(r => r.Units)))),
                Sorted(states.Select(s => (s.Count, s.Sum))));

            List<long> totals = await file.Scan<Sale>()
                .GroupBy(r => r.Shop)
                .Select(g => g.Aggregate<long, UnitsTotal, long>(r => r.Units))
                .ToListAsync(Ct);
            Assert.Equal(Sorted(rows.GroupBy(r => r.Shop).Select(g => g.Sum(r => r.Units))), Sorted(totals));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AStateNoColumnHoldsIsRefusedBySelect()
    {
        (_, string path) = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, TestContext.Current.CancellationToken);
            VortexSchemaException refused = Assert.Throws<VortexSchemaException>(
                () => file.Scan<Sale>().GroupBy(r => r.Shop).Select(g => g.Aggregate<long, LooseTotal, Loose>(r => r.Units)));
            Assert.Contains("[VortexRecord]", refused.Message, StringComparison.Ordinal);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static System.Threading.CancellationToken Ct => TestContext.Current.CancellationToken;

    private static List<T> Sorted<T>(IEnumerable<T> values)
    {
        List<T> sorted = [.. values];
        sorted.Sort();
        return sorted;
    }

    private static async Task<(Sale[] Rows, string Path)> WriteAsync()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "result-batches");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"sales-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        Sale[] rows = new Sale[Rows];
        DateTime origin = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        for (int row = 0; row < Rows; row++)
        {
            rows[row] = new Sale(
                Shops[row / 7 % Shops.Length],
                row / 1_000,
                origin.AddMinutes(row % 977),
                (row % 300) / 4m,
                row % 11 == 0 ? null : row % 50 / 8.0,
                row % 3 == 0,
                new Guid(row % 37, 0, 0, [0, 0, 0, 0, 0, 0, 0, (byte)(row % 5)]),
                row);
        }

        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Sale>(path))
        {
            await writer.WriteAsync<Sale>(rows, TestContext.Current.CancellationToken);
            await writer.CompleteAsync(TestContext.Current.CancellationToken);
        }

        return (rows, path);
    }
}

[VortexRecord]
public partial record struct Sale(string Shop, int Day, DateTime At, decimal Amount, double? Price, bool Paid, Guid Customer, long Units);

/// <summary>A count and a sum, a state a column holds as a struct of two.</summary>
[VortexRecord]
public partial struct MeanState
{
    public long Count;

    public double Sum;
}

/// <summary>A count and a sum, not a record: no column holds it.</summary>
public struct Loose
{
    public long Total;
}

public readonly struct MeanOfUnits : IAggregator<long, MeanState>
{
    public static MeanState Seed() => default;

    public static void Step(ref MeanState state, ReadOnlySpan<long> values, ReadOnlySpan<ulong> validity, Selection rows)
    {
        foreach (int row in rows)
        {
            state.Count++;
            state.Sum += values[row];
        }
    }

    public static void Merge(ref MeanState into, in MeanState other)
    {
        into.Count += other.Count;
        into.Sum += other.Sum;
    }
}

public readonly struct UnitsTotal : IAggregator<long, long>
{
    public static long Seed() => 0;

    public static void Step(ref long state, ReadOnlySpan<long> values, ReadOnlySpan<ulong> validity, Selection rows)
    {
        foreach (int row in rows)
        {
            state += values[row];
        }
    }

    public static void Merge(ref long into, in long other) => into += other;
}

public readonly struct LooseTotal : IAggregator<long, Loose>
{
    public static Loose Seed() => default;

    public static void Step(ref Loose state, ReadOnlySpan<long> values, ReadOnlySpan<ulong> validity, Selection rows)
    {
        foreach (int row in rows)
        {
            state.Total += values[row];
        }
    }

    public static void Merge(ref Loose into, in Loose other) => into.Total += other.Total;
}
