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
/// An extreme keeps the value alone where its column leaves a value no row holds, which its state
/// starts from: NaN for a float, the end of 128 bits for a
/// decimal, for an integer the end of its range the statistics prove no row reaches. Where they prove
/// nothing the state keeps a flag beside the value; the answers are the same either way, a group with
/// no value, or only NaN, included.
/// </summary>
public sealed partial class SeededExtremeTests
{
    private const int Rows = 60_000;

    private const int Groups = 600;

    [Fact]
    public async Task TheColumnChoosesTheState()
    {
        string path = await WriteAsync(Samples());
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            FileScanSource source = new FileScanSource(file);
            RecordBinding binding = file.Scan<Sample>().Binding;

            // A long that reaches neither end of its range: both extremes from a seed.
            Assert.IsType<FixedSlot<long, long, SeededMinOp<long>, long?>>(Create(binding, r => r.Value, source, max: false));
            Assert.IsType<FixedSlot<long, long, SeededMaxOp<long>, long?>>(Create(binding, r => r.Value, source, max: true));

            // An unsigned int holding zero and its largest value leaves neither end.
            Assert.IsType<FixedSlot<uint, ExtremeState<uint>, MinOp<uint>, uint>>(Create(binding, r => r.Full, source, max: false));
            Assert.IsType<FixedSlot<uint, ExtremeState<uint>, MaxOp<uint>, uint>>(Create(binding, r => r.Full, source, max: true));

            // A float and a decimal need no statistics; an integer without them keeps its flag.
            Assert.IsType<FixedSlot<double, double, SeededMinOp<double>, double?>>(Create(binding, r => r.Real, null, max: false));
            Assert.IsType<FixedSlot<Int128, Int128, SeededMaxOp<Int128>, decimal>>(Create(binding, r => r.Price, null, max: true));
            Assert.IsType<FixedSlot<long, ExtremeState<long>, MinOp<long>, long?>>(Create(binding, r => r.Value, null, max: false));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task EveryStateGivesTheSameExtremes(int degree)
    {
        Sample[] rows = Samples();
        string path = await WriteAsync(rows);
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Dictionary<int, Extremes> read = [];
            await foreach (Extremes group in file.Scan<Sample>()
                .GroupBy(r => r.Group)
                .Select(g => (
                    g.Key, g.Min(r => r.Value), g.Max(r => r.Value), g.Min(r => r.Full), g.Max(r => r.Full),
                    g.Min(r => r.Real), g.Max(r => r.Real), g.Min(r => r.Price), g.Max(r => r.Price)))
                .As<Extremes>()
                .ToRecordsAsync(Ct))
            {
                read.Add(group.Group, group);
            }

            Assert.Equal(Groups, read.Count);
            foreach (IGrouping<int, Sample> group in rows.GroupBy(r => r.Group))
            {
                Extremes extremes = read[group.Key];
                long[] values = [.. group.Where(r => r.Value is not null).Select(r => r.Value!.Value)];
                double[] reals = [.. group.Where(r => r.Real is { } real && !double.IsNaN(real)).Select(r => r.Real!.Value)];
                Assert.Equal(values.Length == 0 ? (long?)null : values.Min(), extremes.LeastValue);
                Assert.Equal(values.Length == 0 ? (long?)null : values.Max(), extremes.MostValue);
                Assert.Equal(group.Min(r => r.Full), extremes.LeastFull);
                Assert.Equal(group.Max(r => r.Full), extremes.MostFull);
                Assert.Equal(reals.Length == 0 ? (double?)null : reals.Min(), extremes.LeastReal);
                Assert.Equal(reals.Length == 0 ? (double?)null : reals.Max(), extremes.MostReal);
                Assert.Equal(group.Min(r => r.Price), extremes.LeastPrice);
                Assert.Equal(group.Max(r => r.Price), extremes.MostPrice);
            }

            // The groups past 590 hold no value, those from 580 only NaN: their extremes are null.
            Assert.Null(read[595].LeastValue);
            Assert.Null(read[585].MostReal);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AggregateSlot Create<T>(RecordBinding binding, Func<Probe<Sample>, Sym<T>> column, ScanSource? source, bool max) =>
        ((IAggregateNode)Aggregators.Extreme<T>(Aggregators.Input(binding, column), max).Node).Create(source, meanRead: false);

    /// <summary>
    /// Six hundred groups of a hundred rows, in no order: a long that never reaches either end of its
    /// range, null in every row of the last ten groups; an unsigned int that holds both ends; a float
    /// with NaN, only NaN in ten groups and null in the last ten; a decimal.
    /// </summary>
    private static Sample[] Samples()
    {
        Sample[] rows = new Sample[Rows];
        for (int row = 0; row < Rows; row++)
        {
            int group = (int)((ulong)row * 0x9E37_79B9_7F4A_7C15UL >> 40) % Groups;
            rows[row] = new Sample(
                group,
                group >= 590 ? null : ((long)row * 7_919 % 100_003) - 50_000,
                row % 7 == 0 ? 0 : row % 11 == 0 ? uint.MaxValue : (uint)row * 2_654_435_761,
                group >= 590 ? null : group >= 580 || row % 13 == 0 ? double.NaN : (row % 997) / 8.0 - 60,
                (row % 1_000 / 100m) - 5m);
        }

        return rows;
    }

    private static async Task<string> WriteAsync(Sample[] rows)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "seeded-extremes");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"samples-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Sample>(path, new VortexWriteOptions { RowBlockSize = 8_192, ChunkTargetBytes = 1 << 16 }))
        {
            await writer.WriteAsync<Sample>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        return path;
    }

    [VortexRecord]
    public partial record struct Sample(int Group, long? Value, uint Full, double? Real, decimal Price);

    [VortexRecord]
    public partial record struct Extremes(
        int Group, long? LeastValue, long? MostValue, uint LeastFull, uint MostFull, double? LeastReal, double? MostReal, decimal LeastPrice, decimal MostPrice);
}
