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
/// A sum widens what could overflow on the way: a narrow integer sums as a long or an unsigned
/// long, a single as a double; and the engine keeps 64 bits a group where the file statistics prove
/// the rows times the column's largest magnitude fit them, 128 where they prove nothing, the same
/// answers either way.
/// </summary>
public sealed partial class WidenedSumTests
{
    private const int Rows = 40_000;

    [Fact]
    public async Task ANarrowColumnSumsAsAWiderType()
    {
        Sample[] rows = Samples();
        string path = await WriteAsync(rows);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);

            // Past int.MaxValue, which an int sum would have thrown at.
            long large = await file.Scan<Sample>().SumAsync(r => r.Large, Ct);
            Assert.Equal(rows.Sum(r => (long)r.Large), large);
            Assert.True(large > int.MaxValue);

            Assert.Equal(rows.Aggregate(0UL, (sum, r) => sum + r.Small), await file.Scan<Sample>().SumAsync(r => r.Small, Ct));
            Assert.Equal(rows.Sum(r => (long)(r.Maybe ?? 0)), await file.Scan<Sample>().SumAsync(r => r.Maybe, Ct));
            double singles = await file.Scan<Sample>().SumAsync(r => r.Single, Ct);
            Assert.Equal(rows.Sum(r => (double)r.Single), singles, 6);

            List<Totals> groups = await ListAsync(file.Scan<Sample>()
                .GroupBy(r => r.Group)
                .Select(g => (g.Key, g.Sum(x => x.Large), g.Sum(x => x.Small), g.Sum(x => x.Maybe), g.Sum(x => x.Single), g.Sum(x => x.Wide)))
                .As<Totals>());
            foreach (Totals group in groups)
            {
                Sample[] of = [.. rows.Where(r => r.Group == group.Group)];
                Assert.Equal(of.Sum(r => (long)r.Large), group.Large);
                Assert.Equal(of.Aggregate(0UL, (sum, r) => sum + r.Small), group.Small);
                Assert.Equal(of.Sum(r => (long)(r.Maybe ?? 0)), group.Maybe);
                Assert.Equal(of.Sum(r => (double)r.Single), group.Single, 6);
                Assert.Equal(of.Aggregate(Int128.Zero, (sum, r) => sum + r.Wide), (Int128)group.Wide);
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task TheStatisticsChooseTheWidthOfTheState()
    {
        Sample[] rows = Samples();
        string path = await WriteAsync(rows);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            FileScanSource source = new FileScanSource(file);
            RecordBinding binding = file.Scan<Sample>().Binding;

            // 40 000 rows of an int at most 2^30 fit 63 bits; of a long near 2^62 they do not.
            Assert.IsType<FixedSlot<int, SumState<long>, NarrowSignedSum<int>, long>>(Create<long, int>(binding, r => r.Large, source));
            Assert.IsType<FixedSlot<byte, SumState<ulong>, NarrowUnsignedSum<byte>, ulong>>(Create<ulong, byte>(binding, r => r.Small, source));
            Assert.IsType<FixedSlot<long, SumState<Int128>, SignedSum<long>, long>>(Create<long, long>(binding, r => r.Wide, source));

            // Without statistics, nothing is proven.
            Assert.IsType<FixedSlot<int, SumState<Int128>, SignedSum<int>, long>>(Create<long, int>(binding, r => r.Large, null));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AggregateSlot Create<TSum, TColumn>(RecordBinding binding, Func<Probe<Sample>, Sym<TColumn>> column, ScanSource? source)
        where TSum : System.Numerics.INumber<TSum> =>
        ((IAggregateNode)Aggregators.Sum<TSum>(Aggregators.Input(binding, column)).Node).Create(source);

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

    private static Sample[] Samples()
    {
        Random random = new Random(13);
        Sample[] rows = new Sample[Rows];
        for (int row = 0; row < Rows; row++)
        {
            rows[row] = new Sample(
                row % 9,
                random.Next(1 << 29, 1 << 30),
                (byte)random.Next(256),
                row % 5 == 0 ? null : random.Next(-1_000_000, 1_000_000),
                (float)(random.NextDouble() * 100),
                (row % 2 == 0 ? 1 : -1) * ((1L << 62) - random.Next(1_000)));
        }

        return rows;
    }

    private static async Task<string> WriteAsync(Sample[] rows)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "widened-sums");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"samples-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Sample>(path))
        {
            await writer.WriteAsync<Sample>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        return path;
    }

    [VortexRecord]
    public partial record struct Sample(int Group, int Large, byte Small, int? Maybe, float Single, long Wide);

    [VortexRecord]
    public partial record struct Totals(int Group, long Large, ulong Small, long Maybe, double Single, long Wide);
}
