// A quantile read in place from its sketch, walking its buckets in the order of their values, gives what the sketch's
// distribution answers, whatever the stores hold: a few sorted buckets, a dense window, a map of buckets far apart,
// negative values, zeros.
using System;
using Vorticity.Aggregating;
using Xunit;

namespace Vorticity.Tests.Aggregation;

public sealed partial class DistributionTests
{
    [Theory]
    [InlineData(5, false, false, 7)]
    [InlineData(20, false, false, 7)]
    [InlineData(5_000, false, false, 7)]
    [InlineData(5_000, true, true, 7)]
    [InlineData(3_000, true, false, 20)]
    [InlineData(1, true, true, 7)]
    public void AQuantileReadInPlaceIsTheDistributionsOwn(int values, bool negatives, bool zeros, int bits)
    {
        Random random = new Random(values + bits);
        QuantileSketch sketch = new QuantileSketch(bits);
        for (int i = 0; i < values; i++)
        {
            double value = Math.Exp(random.NextDouble() * (bits > 10 ? 40 : 12));
            if (negatives && random.Next(3) == 0)
            {
                value = -value;
            }

            sketch.Add(zeros && random.Next(10) == 0 ? 0 : value);
        }

        Distribution distribution = new Distribution(sketch);
        foreach (double q in (double[])[0, 0.001, 0.01, 0.25, 0.5, 0.75, 0.9, 0.99, 0.999, 1])
        {
            Assert.Equal(distribution.Quantile(q), sketch.Quantile(q));
        }

        Assert.Null(new QuantileSketch(bits).Quantile(0.5));
    }
}
