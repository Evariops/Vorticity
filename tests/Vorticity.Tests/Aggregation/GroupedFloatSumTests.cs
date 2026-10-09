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
/// The float sums and means of a group by are the bits each group's values give an
/// <see cref="IndexedSum"/> one at a time, in the order of the rows, whatever the degree and the cut of
/// the rows into batches: groups of values of one range, which share a top, beside groups of tiny,
/// subnormal and huge values, of zeros, of values that grow, of NaN and infinities; and a key that opens
/// a group at every row.
/// </summary>
public sealed partial class GroupedFloatSumTests
{
    private const int Rows = 300_000;

    private const int Groups = 1_000;

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(14)]
    public async Task AGroupsFloatSumsAreTheBitsOfItsValuesOneAtATime(int degree)
    {
        (Reading[] rows, string path) = await Fixture.Async;
        await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
        await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);

        List<KeySums> sums = [];
        Scan<KeySums> grouped = file.Scan<Reading>().GroupBy(r => r.Key)
            .Select(g => (g.Key, g.Sum(r => r.Mixed), g.Sum(r => r.Uniform), g.Average(r => r.Uniform), g.Average(r => r.Mixed)))
            .As<KeySums>();
        await foreach (KeySums sum in grouped.ToRecordsAsync(Ct))
        {
            sums.Add(sum);
        }

        Dictionary<int, (IndexedSum Mixed, IndexedSum Uniform)> expected = [];
        foreach (Reading row in rows)
        {
            (IndexedSum mixed, IndexedSum uniform) = expected.GetValueOrDefault(row.Key);
            mixed.Add(row.Mixed);
            uniform.Add(row.Uniform);
            expected[row.Key] = (mixed, uniform);
        }

        Assert.Equal(expected.Count, sums.Count);
        Assert.Equal(
            expected.OrderBy(e => e.Key).Select(e => (e.Key, Bits(e.Value.Mixed.Value), Bits(e.Value.Uniform.Value), Bits(Mean(e.Value.Uniform)), Bits(Mean(e.Value.Mixed)))),
            sums.OrderBy(s => s.Key).Select(s => (s.Key, Bits(s.Mixed), Bits(s.Uniform), Bits(s.UniformMean), Bits(s.MixedMean))));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task AKeyThatOpensAGroupAtEveryRowSumsAsItsValuesDo(int degree)
    {
        (Reading[] rows, string path) = await Fixture.Async;
        await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
        await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);

        List<IdSum> sums = [];
        await foreach (IdSum sum in file.Scan<Reading>().GroupBy(r => r.Id).Select(g => (g.Key, g.Sum(r => r.Mixed))).As<IdSum>().ToRecordsAsync(Ct))
        {
            sums.Add(sum);
        }

        Assert.Equal(
            rows.Select(r => (r.Id, Bits(Single(r.Mixed)))),
            sums.OrderBy(s => s.Id).Select(s => (s.Id, Bits(s.Mixed))));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static long Bits(double value) => BitConverter.DoubleToInt64Bits(value);

    private static long Bits(double? value) => value is double some ? BitConverter.DoubleToInt64Bits(some) : long.MinValue;

    private static double? Mean(IndexedSum sum) => sum.Count == 0 ? null : sum.Value / sum.Count;

    private static double Single(double value)
    {
        IndexedSum sum = default;
        sum.Add(value);
        return sum.Value;
    }

    /// <summary>
    /// A value of the row's group's kind: of one range shared by most groups; tiny, subnormal or huge;
    /// zeros of both signs; growing from small to large along the rows; or of any range, a NaN and an
    /// infinity among them now and then.
    /// </summary>
    private static double MixedOf(int key, int row, Random random)
    {
        double unit = random.NextDouble();
        double sign = random.Next(4) == 0 ? -1 : 1;
        return (key % 10) switch
        {
            0 or 1 or 2 or 3 => sign * unit * 100,
            4 => sign * unit * 1e-200,
            5 => sign * unit * 1e-310,
            6 => sign * unit * 1e300,
            7 => random.Next(2) == 0 ? 0.0 : -0.0,
            8 => sign * Math.ScaleB(unit + 0.5, (int)((long)row * 120 / Rows) - 60),
            _ => row % 97 == 0 ? double.NaN : row % 1_013 == 0 ? (sign > 0 ? double.PositiveInfinity : double.NegativeInfinity)
                : sign * Math.ScaleB(unit + 0.5, random.Next(-80, 81)),
        };
    }

    private static class Fixture
    {
        private static readonly Lazy<Task<(Reading[] Rows, string Path)>> s_written = new Lazy<Task<(Reading[], string)>>(WriteAsync);

        internal static Task<(Reading[] Rows, string Path)> Async => s_written.Value;

        private static async Task<(Reading[], string)> WriteAsync()
        {
            Random random = new Random(23);
            Reading[] rows = new Reading[Rows];
            for (int row = 0; row < Rows; row++)
            {
                int key = (int)((uint)(row * 2_654_435_761u) % Groups);
                rows[row] = new Reading(row, key, MixedOf(key, row, random), random.NextDouble() * 1_000);
            }

            string directory = Path.Combine(AppContext.BaseDirectory, "grouped-float-sum");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"readings-{Environment.ProcessId}.vortex");
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Reading>(path, new VortexWriteOptions { RowBlockSize = 4_096, ChunkTargetBytes = 1 << 15 }))
            {
                await writer.WriteAsync<Reading>(rows, CancellationToken.None);
                await writer.CompleteAsync(CancellationToken.None);
            }

            return (rows, path);
        }
    }

    [VortexRecord]
    public partial record struct Reading(int Id, int Key, double Mixed, double Uniform);

    [VortexRecord]
    public partial record struct KeySums(int Key, double Mixed, double Uniform, double? UniformMean, double? MixedMean);

    [VortexRecord]
    public partial record struct IdSum(int Id, double Mixed);
}
