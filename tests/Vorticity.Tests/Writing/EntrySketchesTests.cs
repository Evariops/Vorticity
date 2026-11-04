// The sketches of a measure over a column's entries, held in one table: each entry's encodes to the bytes a sketch of its
// values writes, whatever its values (negatives, zeros of either sign, NaNs, infinities) and after its entries move.
using System;
using System.Buffers;
using System.Collections.Generic;
using Vorticity.Aggregating;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class EntrySketchesTests
{
    /// <summary>
    /// 300 entries fed values of every kind in no order, one fed NaNs alone and some none: each entry's bytes are those of a
    /// sketch of its values, null for an entry no value reached; and once the entries move, a third of them onto one, each
    /// new entry's bytes are those of the sketches of the entries that moved onto it, merged.
    /// </summary>
    [Fact]
    public void AnEntrysSketchEncodesAsASketchOfItsValuesDoes()
    {
        const int Entries = 300;
        Random random = new Random(17);
        List<double>[] values = new List<double>[Entries];
        for (int e = 0; e < Entries; e++)
        {
            values[e] = [];
        }

        using EntrySketches sketches = new EntrySketches(ColumnCountsBuilder.SketchBits);
        double[] odd = [0.0, -0.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.Epsilon, -double.MaxValue];
        for (int i = 0; i < 40_000; i++)
        {
            int entry = random.Next(Entries);
            if (entry % 7 == 3)
            {
                continue;
            }

            double value = entry == 5 ? double.NaN
                : random.Next(20) == 0 ? odd[random.Next(odd.Length)]
                : (random.NextDouble() - 0.3) * Math.Pow(10, random.Next(-5, 9));
            values[entry].Add(value);
            sketches.Add(entry, value);
        }

        // The entries moved as the values left out move them: every third onto the first of them, the others in order.
        int[] to = new int[Entries];
        List<double>[] moved = new List<double>[Entries];
        int next = 0;
        int others = -1;
        for (int e = 0; e < Entries; e++)
        {
            int into = e % 3 == 2 ? others >= 0 ? others : others = next++ : next++;
            to[e] = into;
            moved[into] ??= [];
            moved[into].AddRange(values[e]);
        }

        using EntrySketches before = new EntrySketches(ColumnCountsBuilder.SketchBits);
        for (int e = 0; e < Entries; e++)
        {
            foreach (double value in values[e])
            {
                before.Add(e, value);
            }
        }

        before.Seal(Entries);
        for (int e = 0; e < Entries; e++)
        {
            Assert.Equal(Encoded(values[e]), before.Encoded(e));
        }

        sketches.Remap(to);
        sketches.Seal(next);
        for (int e = 0; e < next; e++)
        {
            Assert.Equal(Encoded(moved[e]), sketches.Encoded(e));
        }
    }

    /// <summary>The bytes of a sketch of <paramref name="values"/>; null for none, an entry no value reached.</summary>
    private static byte[]? Encoded(List<double> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        QuantileSketch sketch = new QuantileSketch(ColumnCountsBuilder.SketchBits);
        foreach (double value in values)
        {
            sketch.Add(value);
        }

        ArrayBufferWriter<byte> writer = new ArrayBufferWriter<byte>();
        sketch.Encode(writer);
        return writer.WrittenSpan.ToArray();
    }
}
