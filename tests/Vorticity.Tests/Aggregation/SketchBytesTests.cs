// The bytes a distinct sketch holds, kept as its arrays change, against the bytes counted from its arrays: through every
// form (exact hashes, a sparse table that grows, dense registers), merges of sketches in each form, a clear, words read
// back and encoded bytes merged in.
using System;
using System.Buffers;
using System.Collections.Generic;
using Vorticity.Aggregating;
using Xunit;

namespace Vorticity.Tests.Aggregation;

public sealed class SketchBytesTests
{
    [Fact]
    public void TheBytesKeptAreTheBytesOfTheArrays()
    {
        Random random = new Random(1017);
        foreach (int values in new[] { 0, 1, 3, 64, 65, 200, 900, 1_100, 5_000, 40_000 })
        {
            CardinalitySketch sketch = new CardinalitySketch();
            Check(sketch);
            for (int i = 0; i < values; i++)
            {
                sketch.Add((ulong)random.NextInt64() ^ ((ulong)random.Next() << 63));
                if ((i & 31) == 0)
                {
                    Check(sketch);
                }
            }

            Check(sketch);
            foreach (int others in new[] { 10, 300, 20_000 })
            {
                CardinalitySketch other = new CardinalitySketch();
                for (int i = 0; i < others; i++)
                {
                    other.Add((ulong)random.NextInt64());
                }

                CardinalitySketch merged = Copy(sketch);
                Check(merged);
                merged.Merge(other);
                Check(merged);

                ArrayBufferWriter<byte> encoded = new ArrayBufferWriter<byte>();
                other.Encode(encoded);
                CardinalitySketch decoded = Copy(sketch);
                decoded.MergeEncoded(encoded.WrittenSpan);
                Check(decoded);
            }

            sketch.Clear();
            Check(sketch);
            sketch.Add(12345);
            Check(sketch);
        }
    }

    private static CardinalitySketch Copy(CardinalitySketch sketch)
    {
        List<long> words = new List<long>();
        sketch.Write(words.Add);
        int at = 0;
        return CardinalitySketch.Read(() => words[at++]);
    }

    private static void Check(CardinalitySketch sketch) => Assert.Equal(sketch.CountedBytes, sketch.Bytes);
}
