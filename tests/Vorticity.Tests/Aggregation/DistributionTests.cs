// Distributions: a sketch's quantiles hold their relative error against the exact ones, its buckets
// are the same counts whatever the order of the values and of the merges, and the group by delivers
// the same bits at every degree and every cut of the rows.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Xunit;

namespace Vorticity.Tests.Aggregation;

public sealed partial class DistributionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void TheGridTakesTheFewestBitsThatHoldTheError()
    {
        Assert.Equal(6, QuantileSketch.BitsFor(0.01));
        Assert.True(QuantileSketch.ErrorOf(6) <= 0.01);
        Assert.True(QuantileSketch.ErrorOf(5) > 0.01);
        Assert.Equal(7, QuantileSketch.BitsFor(0.005));
        Assert.Throws<ArgumentOutOfRangeException>(() => QuantileSketch.BitsFor(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => QuantileSketch.BitsFor(1));
    }

    /// <summary>
    /// Latencies spread over eight decades, negatives, zeros, an infinity and NaNs: every quantile lies
    /// within the error of the exact value at its rank, the histogram counts every value, and the
    /// counts are the same in any order and any cut of merges, written and read back.
    /// </summary>
    [Fact]
    public void QuantilesHoldTheirErrorAndMergesTheirCounts()
    {
        Random random = new Random(3);
        List<double> values = [.. Enumerable.Range(0, 50_000).Select(_ => Math.Exp((random.NextDouble() * 18) - 9))];
        values.AddRange(Enumerable.Range(0, 500).Select(_ => -random.NextDouble() * 100));
        values.AddRange(Enumerable.Repeat(0.0, 300));
        values.Add(double.PositiveInfinity);
        double[] exact = [.. values.Order()];
        values.Add(double.NaN);

        QuantileSketch whole = new QuantileSketch(QuantileSketch.BitsFor(0.01));
        foreach (double value in values)
        {
            whole.Add(value);
        }

        Distribution distribution = new Distribution(whole);
        Assert.Equal(exact.Length, distribution.Count);
        Assert.Equal(exact.Length, distribution.Histogram.Sum(b => b.Count));
        foreach (double q in (double[])[0, 0.001, 0.01, 0.25, 0.5, 0.9, 0.99, 0.999])
        {
            double truth = exact[(int)Math.Floor(q * (exact.Length - 1))];
            double answer = distribution.Quantile(q)!.Value;
            Assert.True(
                truth == 0 ? answer == 0 : Math.Abs(answer - truth) <= distribution.RelativeError * Math.Abs(truth) * 1.000001,
                $"q {q}: {answer} for {truth}");
        }

        Assert.Equal(double.PositiveInfinity, distribution.Quantile(1));

        // Cut in parts merged in a shuffled order, each written and read back: the same buckets.
        for (int trial = 0; trial < 4; trial++)
        {
            List<QuantileSketch> parts = [.. Enumerable.Range(0, 7).Select(_ => new QuantileSketch(whole.Bits))];
            foreach (double value in values)
            {
                parts[random.Next(parts.Count)].Add(value);
            }

            QuantileSketch merged = new QuantileSketch(whole.Bits);
            foreach (QuantileSketch part in parts.OrderBy(_ => random.Next()))
            {
                List<long> words = [];
                part.Write(words.Add);
                int at = 0;
                merged.Merge(QuantileSketch.Read(part.Bits, () => words[at++]));
            }

            Assert.Equal(whole.Buckets(), merged.Buckets());
            Assert.Equal(whole.Count, merged.Count);
        }

        Assert.Null(new Distribution(new QuantileSketch(6)).Quantile(0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => distribution.Quantile(1.5));
    }

    /// <summary>
    /// The quantiles of a group by over a file are the bits of a sketch fed the group's values one at a
    /// time, at every degree; a distribution over the whole scan counts every row.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(14)]
    public async Task AGroupsQuantilesAreTheSameAtEveryDegree(int degree)
    {
        (Latency[] rows, string path) = await Fixture.Async;
        await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
        await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);

        List<KeyQuantiles> found = [];
        await foreach (KeyQuantiles row in file.Scan<Latency>().GroupBy(r => r.Key)
            .Select(g => (g.Key, g.Quantile(r => r.Millis, 0.5), g.Quantile(r => r.Millis, 0.99), g.Quantile(r => r.Millis, 0.99, 0.001)))
            .As<KeyQuantiles>().ToRecordsAsync(Ct))
        {
            found.Add(row);
        }

        Dictionary<int, (QuantileSketch Coarse, QuantileSketch Fine)> expected = [];
        foreach (Latency row in rows)
        {
            if (!expected.TryGetValue(row.Key, out var sketches))
            {
                sketches = (new QuantileSketch(QuantileSketch.BitsFor(0.01)), new QuantileSketch(QuantileSketch.BitsFor(0.001)));
                expected[row.Key] = sketches;
            }

            sketches.Coarse.Add(row.Millis);
            sketches.Fine.Add(row.Millis);
        }

        Assert.Equal(
            expected.OrderBy(e => e.Key).Select(e => (e.Key, new Distribution(e.Value.Coarse).Quantile(0.5), new Distribution(e.Value.Coarse).Quantile(0.99), new Distribution(e.Value.Fine).Quantile(0.99))),
            found.OrderBy(f => f.Key).Select(f => (f.Key, f.Median, f.P99, f.FineP99)));

        Distribution? all = await file.Scan<Latency>().AggregateAsync(a => a.Distribution(r => r.Millis), Ct);
        Assert.Equal(rows.Length, all!.Count);
    }

    private static class Fixture
    {
        internal static readonly Task<(Latency[] Rows, string Path)> Async = WriteAsync();

        private static async Task<(Latency[], string)> WriteAsync()
        {
            Random random = new Random(11);
            Latency[] rows = [.. Enumerable.Range(0, 200_000).Select(i => new Latency(random.Next(200), Math.Exp(random.NextDouble() * 9)))];
            string path = Path.Combine(Path.GetTempPath(), $"vorticity-distribution-{Guid.NewGuid():N}.vortex");
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Latency>(path))
            {
                await writer.WriteAsync<Latency>(rows, default);
                await writer.CompleteAsync(default);
            }

            return (rows, path);
        }
    }

    [VortexRecord]
    public partial record struct Latency(int Key, double Millis);

    [VortexRecord]
    public partial record struct KeyQuantiles(int Key, double? Median, double? P99, double? FineP99);
}
