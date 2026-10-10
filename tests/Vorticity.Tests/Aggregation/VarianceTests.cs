using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// The sample variance and standard deviation, of a group and of a whole scan, against the two-pass
/// formula over the same rows: null below two values, the same bits at every degree and in every
/// order of the rows, and the digits kept on values far from zero, which the center keeps.
/// </summary>
public sealed partial class VarianceTests
{
    private const int Rows = 60_000;

    /// <summary>
    /// The center and what a stored value is worth are the run's, held by the slot's op: a group's
    /// state is its two sums, a line of cache.
    /// </summary>
    [Fact]
    public void AGroupsStateIsItsTwoSums()
    {
        Aggregating.AggregateSlot slot = new Aggregating.FixedSlot<long, Aggregating.VarianceState, Aggregating.VarianceOp<long>, double?>(
            Aggregating.StorageKind.Primitive, static s => s.Variance, new Aggregating.VarianceOp<long>(1_000, 1));
        Assert.Equal(64, slot.StateBytes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task AGroupsVarianceIsTheTwoPassOne(int degree)
    {
        Reading[] rows = Readings();
        string path = await WriteAsync(rows);
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            List<Spread> groups = await ListAsync(file.Scan<Reading>()
                .GroupBy(r => r.Station)
                .Select(g => (
                    g.Key,
                    g.Variance(x => x.Level),
                    g.StandardDeviation(x => x.Level),
                    g.Variance(x => x.Celsius),
                    g.StandardDeviation(x => x.Gain),
                    g.Where(x => x.Level > 500).Variance(x => x.Epoch),
                    g.Variance(x => x.Price)))
                .As<Spread>());

            Assert.Equal(rows.Select(r => r.Station).Distinct().Count(), groups.Count);
            foreach (Spread group in groups)
            {
                Reading[] of = [.. rows.Where(r => r.Station == group.Station)];
                Close(Sample(of.Select(r => (double)r.Level)), group.Level);
                Close(Deviation(of.Select(r => (double)r.Level)), group.LevelDeviation);
                Close(Sample(of.Where(r => r.Celsius is double c && !double.IsNaN(c)).Select(r => r.Celsius!.Value)), group.Celsius);
                Close(Deviation(of.Select(r => (double)r.Gain)), group.GainDeviation);
                Close(Sample(of.Where(r => r.Level > 500).Select(r => r.Epoch)), group.LateEpoch);
                Close(Sample(of.Select(r => (double)r.Price)), group.Price);
            }

            // One value has no spread to speak of.
            Assert.Null(groups.Single(g => g.Station == "lone").Level);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AVarianceIsTheSameBitsWhateverTheDegreeAndTheOrder()
    {
        Reading[] rows = Readings();
        Reading[] shuffled = (Reading[])rows.Clone();
        new Random(17).Shuffle(shuffled);
        string path = await WriteAsync(rows);
        string other = await WriteAsync(shuffled);
        try
        {
            HashSet<string> answers = [];
            foreach (int degree in new[] { 1, 2, 5 })
            {
                await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
                foreach (string file in new[] { path, other })
                {
                    await using VortexFile open = await session.OpenAsync(file, cancellationToken: Ct);
                    Whole whole = await open.Scan<Reading>().AggregateAsync<Whole>(a => (a.Variance(x => x.Celsius), a.StandardDeviation(x => x.Epoch)), Ct);
                    List<Spread> groups = await ListAsync(open.Scan<Reading>()
                        .GroupBy(r => r.Station)
                        .OrderBy(g => g.Key)
                        .Select(g => (g.Key, g.Variance(x => x.Level), g.StandardDeviation(x => x.Level), g.Variance(x => x.Celsius), g.StandardDeviation(x => x.Gain), g.Variance(x => x.Epoch), g.Variance(x => x.Price)))
                        .As<Spread>());
                    answers.Add($"{Bits(whole.Celsius)}/{Bits(whole.EpochDeviation)}/{string.Join(";", groups.Select(g => $"{Bits(g.Level)},{Bits(g.Celsius)},{Bits(g.GainDeviation)},{Bits(g.LateEpoch)},{Bits(g.Price)}"))}");
                }
            }

            Assert.Single(answers);
        }
        finally
        {
            System.IO.File.Delete(path);
            System.IO.File.Delete(other);
        }
    }

    [Fact]
    public async Task ValuesFarFromZeroKeepTheirSpread()
    {
        // Seconds since the epoch over one day: the center, the middle of the column's bounds, takes
        // the 1.7 · 10^9 away before anything is squared.
        Reading[] rows = Readings();
        string path = await WriteAsync(rows);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            Whole whole = await file.Scan<Reading>().AggregateAsync<Whole>(a => (a.Variance(x => x.Celsius), a.StandardDeviation(x => x.Epoch)), Ct);
            double expected = Deviation(rows.Select(r => r.Epoch))!.Value;
            Assert.True(Math.Abs(whole.EpochDeviation!.Value - expected) <= expected * 1e-12, $"{whole.EpochDeviation} against {expected}");
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Bits(double? value) => value is double v ? BitConverter.DoubleToInt64Bits(v).ToString("X16", System.Globalization.CultureInfo.InvariantCulture) : "null";

    private static void Close(double? expected, double? actual)
    {
        if (expected is null)
        {
            Assert.Null(actual);
            return;
        }

        Assert.NotNull(actual);
        Assert.True(Math.Abs(actual.Value - expected.Value) <= Math.Abs(expected.Value) * 1e-11 + 1e-12, $"{actual} against {expected}");
    }

    /// <summary>The sample variance by the two-pass formula, over the mean first.</summary>
    private static double? Sample(IEnumerable<double> values)
    {
        double[] all = [.. values];
        if (all.Length < 2)
        {
            return null;
        }

        double mean = all.Average();
        return all.Sum(v => (v - mean) * (v - mean)) / (all.Length - 1);
    }

    private static double? Deviation(IEnumerable<double> values) => Sample(values) is double v ? Math.Sqrt(v) : null;

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

    private static Reading[] Readings()
    {
        Random random = new Random(41);
        string[] stations = ["north", "south", "east", "west", "summit"];
        Reading[] rows = new Reading[Rows];
        for (int row = 0; row < Rows; row++)
        {
            rows[row] = new Reading(
                row == Rows - 1 ? "lone" : stations[row % stations.Length],
                random.Next(0, 1_000),
                row % 23 == 0 ? null : row % 61 == 0 ? double.NaN : 15 + (random.NextDouble() * 20) - (row % 5 * 3),
                (float)(random.NextDouble() * 2),
                1_700_000_000 + (row * 1.25) + random.NextDouble(),
                Math.Round((decimal)(random.NextDouble() * 1_000), 2));
        }

        return rows;
    }

    private static async Task<string> WriteAsync(Reading[] rows)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "variances");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"readings-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Reading>(path))
        {
            await writer.WriteAsync<Reading>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        return path;
    }

    [VortexRecord]
    public partial record struct Reading(string Station, int Level, double? Celsius, float Gain, double Epoch, decimal Price);

    [VortexRecord]
    public partial record struct Spread(string Station, double? Level, double? LevelDeviation, double? Celsius, double? GainDeviation, double? LateEpoch, double? Price);

    [VortexRecord]
    public partial record struct Whole(double? Celsius, double? EpochDeviation);
}
