// A sketch on a grid of many bits over values far apart: its stores keep a map of the buckets that hold values past a
// window of QuantileSketch.MaxWindow buckets, so that its bytes follow its values and not their range, and its
// quantiles, its merges and its bytes encoded stay what they were.
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using Vorticity.Aggregating;
using Xunit;

namespace Vorticity.Tests.Aggregation;

public sealed partial class DistributionTests
{
    /// <summary>
    /// Ten thousand values over thirty powers of two on the finest grid, a million buckets a power of two: the sketch keeps
    /// them in a map of 32 bytes a bucket, about 320 KB, where a window over their range took some 230 MB; its quantiles
    /// hold their error, and the sketch written and read back, merged in parts or encoded and merged, gives the same
    /// buckets.
    /// </summary>
    [Fact]
    public void AFineGridOverAWideRangeKeepsItsBytesToItsValues()
    {
        Random random = new Random(29);
        double[] values = [.. Enumerable.Range(0, 10_000).Select(_ => Math.Exp((random.NextDouble() * 20) - 10) * (random.Next(8) == 0 ? -1 : 1))];
        double[] exact = [.. values.Order()];
        QuantileSketch whole = new QuantileSketch(QuantileSketch.MaxBits);
        long grown = 0;
        foreach (double value in values)
        {
            grown += whole.Add(value);
        }

        Assert.Equal(whole.Bytes, grown);
        Assert.InRange(whole.Bytes, 1, 2L * values.Length * 32);
        Console.Out.Write($"WIDE SKETCH: {values.Length} values in {whole.Bytes} bytes\n");

        Distribution distribution = new Distribution(whole);
        foreach (double q in (double[])[0, 0.01, 0.25, 0.5, 0.9, 0.99, 1])
        {
            double truth = exact[(int)Math.Floor(q * (exact.Length - 1))];
            double answer = distribution.Quantile(q)!.Value;
            Assert.True(Math.Abs(answer - truth) <= distribution.RelativeError * Math.Abs(truth) * 1.000001, $"q {q}: {answer} for {truth}");
        }

        // Cut in parts, each written and read back, merged in a shuffled order: the same buckets.
        List<QuantileSketch> parts = [.. Enumerable.Range(0, 5).Select(_ => new QuantileSketch(whole.Bits))];
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

        // Encoded as a table of sketches holds it, then merged back on its grid: the same buckets.
        ArrayBufferWriter<byte> bytes = new ArrayBufferWriter<byte>();
        whole.Encode(bytes);
        QuantileSketch decoded = new QuantileSketch(whole.Bits);
        decoded.MergeEncoded(bytes.WrittenSpan);
        Assert.Equal(whole.Buckets(), decoded.Buckets());
    }
}
