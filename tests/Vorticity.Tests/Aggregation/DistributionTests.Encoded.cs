// A sketch as a table of sketches holds it: its bytes merged back give its buckets, on its grid and on every coarser
// one, the buckets a sketch of that grid counts the same values in.
using System;
using System.Buffers;
using System.Linq;
using Vorticity.Aggregating;
using Xunit;

namespace Vorticity.Tests.Aggregation;

public sealed partial class DistributionTests
{
    /// <summary>
    /// Values of every sign and spread, zeros, NaNs and infinities among them, a few and many, sketched on grids of 1 to
    /// 12 bits: the bytes merged into a sketch of the same grid or a coarser one give the buckets, the count and the
    /// zeros of a sketch of that grid fed the values, and merged twice those of the values twice.
    /// </summary>
    [Fact]
    public void AnEncodedSketchMergesIntoItsGridAndEveryCoarserOne()
    {
        Random random = new Random(71);
        ArrayBufferWriter<byte> bytes = new ArrayBufferWriter<byte>();
        for (int trial = 0; trial < 200; trial++)
        {
            int count = trial % 4 == 0 ? random.Next(0, 4) : random.Next(1, 3_000);
            double[] values = [.. Enumerable.Range(0, count).Select(_ => random.Next(12) switch
            {
                0 => 0.0,
                1 => -Math.Exp(random.NextDouble() * 30),
                2 => trial % 7 == 0 ? double.NaN : 1.0,
                3 => trial % 11 == 0 ? double.PositiveInfinity : 2.0,
                _ => Math.Exp((random.NextDouble() * 40) - 20),
            })];
            int bits = 1 + random.Next(12);
            QuantileSketch stored = new QuantileSketch(bits);
            stored.AddAll<double>(values);
            bytes.ResetWrittenCount();
            stored.Encode(bytes);

            for (int coarser = 1; coarser <= bits; coarser++)
            {
                QuantileSketch expected = new QuantileSketch(coarser);
                expected.AddAll<double>(values);
                QuantileSketch merged = new QuantileSketch(coarser);
                merged.MergeEncoded(bytes.WrittenSpan);
                Assert.Equal(expected.Count, merged.Count);
                Assert.Equal(expected.Zeros, merged.Zeros);
                Assert.Equal(expected.Buckets(), merged.Buckets());

                expected.AddAll<double>(values);
                merged.MergeEncoded(bytes.WrittenSpan);
                Assert.Equal(expected.Buckets(), merged.Buckets());
                Assert.Equal(expected.Count, merged.Count);
            }

            // A finer grid cannot be read off a coarser one.
            if (bits < QuantileSketch.MaxBits)
            {
                Assert.Throws<VortexFormatException>(() => new QuantileSketch(bits + 1).MergeEncoded(bytes.WrittenSpan));
            }
        }
    }

    /// <summary>The bytes of a sketch cut short, or with bytes past its buckets, do not merge.</summary>
    [Fact]
    public void BytesThatHoldNoSketchDoNotMerge()
    {
        QuantileSketch sketch = new QuantileSketch(6);
        sketch.AddAll<double>([1.5, 2.5, 1e6, -3, 0]);
        ArrayBufferWriter<byte> bytes = new ArrayBufferWriter<byte>();
        sketch.Encode(bytes);
        byte[] whole = bytes.WrittenSpan.ToArray();
        for (int cut = 0; cut < whole.Length; cut++)
        {
            Assert.Throws<VortexFormatException>(() => new QuantileSketch(6).MergeEncoded(whole.AsSpan(0, cut)));
        }

        Assert.Throws<VortexFormatException>(() => new QuantileSketch(6).MergeEncoded([.. whole, 0]));
        QuantileSketch read = new QuantileSketch(6);
        read.MergeEncoded(whole);
        Assert.Equal(sketch.Buckets(), read.Buckets());
    }
}
