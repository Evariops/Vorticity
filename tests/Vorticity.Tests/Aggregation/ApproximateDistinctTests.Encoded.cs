// A distinct sketch as a table of sketches holds it: the same bytes for the same content, whatever the form that held it,
// merged into a sketch as the sketch it encodes merges, and bytes that hold no sketch refused.
using System.Buffers;
using System.Collections.Generic;
using Vorticity.Aggregating;
using Xunit;

namespace Vorticity.Tests.Aggregation;

public sealed partial class ApproximateDistinctTests
{
    /// <summary>
    /// Sketches exact, sparse and dense, each encoded and merged into an empty sketch and into one of other values: the
    /// words and the estimate of the sketch fed every value; their bytes the same whichever form held the same content.
    /// </summary>
    [Theory]
    [InlineData(40)]
    [InlineData(900)]
    [InlineData(50_000)]
    public void AnEncodedSketchMergesAsTheSketchItEncodes(int distinct)
    {
        CardinalitySketch sketch = new CardinalitySketch();
        CardinalitySketch others = new CardinalitySketch();
        CardinalitySketch both = new CardinalitySketch();
        for (int i = 0; i < distinct; i++)
        {
            sketch.Add(Hash(i));
            both.Add(Hash(i));
            others.Add(Hash(distinct + (i % 37)));
            both.Add(Hash(distinct + (i % 37)));
        }

        byte[] bytes = Encoded(sketch);
        CardinalitySketch read = new CardinalitySketch();
        read.MergeEncoded(bytes);
        Assert.Equal(Words(sketch), Words(read));
        Assert.Equal(sketch.Estimate(), read.Estimate());
        Assert.Equal(bytes, Encoded(read));

        others.MergeEncoded(bytes);
        Assert.Equal(Words(both), Words(others));
        Assert.Equal(both.Estimate(), others.Estimate());

        // The same content, through a merge of another form: the same bytes.
        CardinalitySketch merged = new CardinalitySketch();
        merged.Merge(read);
        Assert.Equal(bytes, Encoded(merged));
    }

    /// <summary>A sketch cleared holds nothing, encodes as an empty one, and counts its next values alone.</summary>
    [Fact]
    public void AClearedSketchCountsItsNextValuesAlone()
    {
        CardinalitySketch sketch = new CardinalitySketch();
        for (int i = 0; i < 5_000; i++)
        {
            sketch.Add(Hash(i));
        }

        sketch.Clear();
        Assert.True(sketch.IsExact);
        Assert.Equal(0, sketch.Estimate());
        Assert.Equal(Encoded(new CardinalitySketch()), Encoded(sketch));
        sketch.Add(Hash(1));
        sketch.Add(Hash(2));
        Assert.Equal(2, sketch.Estimate());
    }

    /// <summary>Bytes cut short, of an unknown form, past their content, or naming a register out of the sketch do not merge.</summary>
    [Fact]
    public void BytesThatHoldNoSketchDoNotMerge()
    {
        CardinalitySketch exact = new CardinalitySketch();
        CardinalitySketch sparse = new CardinalitySketch();
        for (int i = 0; i < 300; i++)
        {
            exact.Add(Hash(i % 10));
            sparse.Add(Hash(i));
        }

        byte[] few = Encoded(exact);
        byte[] many = Encoded(sparse);
        List<byte[]> broken =
        [
            [],
            [9],
            few[..^1],
            [.. few, 0],
            many[..^1],
            [.. many, 1],
            [1, 1, 0x80, 0x80, 0x01, 1],
            [1, 1, 0, 0],
            [2, 0, 0],
        ];
        foreach (byte[] bytes in broken)
        {
            Assert.Throws<VortexFormatException>(() => new CardinalitySketch().MergeEncoded(bytes));
        }
    }

    private static byte[] Encoded(CardinalitySketch sketch)
    {
        ArrayBufferWriter<byte> bytes = new ArrayBufferWriter<byte>();
        sketch.Encode(bytes);
        return bytes.WrittenSpan.ToArray();
    }
}
