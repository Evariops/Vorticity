// Objects' counts merged where values move into the entry of the values left out, a value an object left out or one past
// the merge's budget: every entry of the merge, the values left out's included, holds the rows, the sketch and the
// registers of every input entry it took, against a merge of those entries one by one. Registers raised from their bytes
// are the most of both, in either form, at any length; their bytes read back as they were written.
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using Vorticity.Aggregating;
using Vorticity.Indexes;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Indexes;

public sealed class CountsMovedTests
{
    [Theory]
    [InlineData(ColumnCountsBuilder.EntryLimit)]
    [InlineData(40)]
    [InlineData(3)]
    public void EveryEntryHoldsWhatItTook(int entryLimit)
    {
        Random random = new Random(entryLimit);
        List<(ColumnCounts Counts, ColumnSketches Sketches)> objects = [];
        for (int o = 0; o < 6; o++)
        {
            objects.Add(Drawn(random, leftOut: o % 3 == 1, values: o % 3 == 1 ? 150 : 60));
        }

        CountsMerge merge = new CountsMerge(entryLimit);
        objects.ForEach(o => merge.Add(o.Counts, o.Sketches));
        (ColumnCounts merged, ColumnSketches? sketched) = merge.Finish()!.Value;
        HashSet<long> kept = [.. merged.Groups.Where(g => g.Kind == CountedValue.Value).Select(g => BitConverter.ToInt64(g.Value))];
        foreach (CountGroup group in merged.Groups)
        {
            // What the entry took: the input entries of its value, or for the values left out every input entry of a value
            // the merge does not keep, and every input's entry of the values it left out.
            long rows = 0;
            QuantileSketch sketch = new QuantileSketch(ColumnCountsBuilder.SketchBits);
            byte[] ranks = new byte[ColumnSheet.Registers];
            foreach ((ColumnCounts counts, ColumnSketches sketches) in objects)
            {
                foreach (CountGroup held in counts.Groups)
                {
                    bool took = group.Kind == CountedValue.Value
                        ? held.Kind == CountedValue.Value && held.Value.AsSpan().SequenceEqual(group.Value)
                        : held.Kind == CountedValue.Others || !kept.Contains(BitConverter.ToInt64(held.Value));
                    if (took)
                    {
                        rows += counts.WordsOf(held.FirstEntry)[0];
                        sketch.MergeEncoded(sketches.SketchOf(held.FirstEntry, 0));
                        byte[] other = new byte[ColumnSheet.Registers];
                        HyperLogLog.Decode(sketches.DistinctOf(held.FirstEntry, 0)!, other);
                        for (int r = 0; r < ranks.Length; r++)
                        {
                            ranks[r] = Math.Max(ranks[r], other[r]);
                        }
                    }
                }
            }

            ArrayBufferWriter<byte> writer = new ArrayBufferWriter<byte>();
            sketch.Encode(writer);
            Assert.Equal(rows, merged.WordsOf(group.FirstEntry)[0]);
            Assert.Equal(writer.WrittenSpan.ToArray(), sketched!.SketchOf(group.FirstEntry, 0));
            Assert.Equal(HyperLogLog.Encode(ranks), sketched.DistinctOf(group.FirstEntry, 0));
        }

        Assert.Contains(merged.Groups, g => g.Kind == CountedValue.Others);
        Assert.True(merged.Entries <= Math.Max(entryLimit, 1) + 1);
    }

    [Theory]
    [InlineData(2_048, 0)]
    [InlineData(2_048, 3)]
    [InlineData(2_048, 200)]
    [InlineData(2_048, 2_048)]
    [InlineData(100, 7)]
    [InlineData(100, 100)]
    public void RegistersRiseToTheMostOfBoth(int length, int set)
    {
        Random random = new Random((length * 7) + set);
        for (int round = 0; round < 20; round++)
        {
            byte[] held = Ranks(random, length, set);
            byte[] other = Ranks(random, length, round % 2 == 0 ? set : length - set);
            byte[] encoded = HyperLogLog.Encode(other);
            byte[] read = new byte[length];
            HyperLogLog.Decode(encoded, read);
            Assert.Equal(other, read);

            byte[] expected = [.. held.Zip(other, Math.Max)];
            HyperLogLog.Raise(encoded, held);
            Assert.Equal(expected, held);
        }
    }

    private static byte[] Ranks(Random random, int length, int set)
    {
        byte[] ranks = new byte[length];
        for (int i = 0; i < set; i++)
        {
            ranks[random.Next(length)] = (byte)random.Next(1, 54);
        }

        return ranks;
    }

    private static (ColumnCounts Counts, ColumnSketches Sketches) Drawn(Random random, bool leftOut, int values)
    {
        MeasureKind[] kinds = [MeasureKind.Signed];
        int width = StateWords.Count(kinds);
        SortedSet<long> chosen = [];
        while (chosen.Count < values)
        {
            chosen.Add(random.Next(200));
        }

        int entries = chosen.Count + (leftOut ? 1 : 0);
        CountGroup[] groups = new CountGroup[entries];
        long[] words = new long[entries * width];
        byte[]?[] sketches = new byte[]?[entries];
        byte[]?[] registers = new byte[]?[entries];
        int g = 0;
        if (leftOut)
        {
            groups[g++] = new CountGroup(CountedValue.Others, null, 0, 1);
        }

        foreach (long value in chosen)
        {
            groups[g] = new CountGroup(CountedValue.Value, BitConverter.GetBytes(value), g, 1);
            g++;
        }

        ArrayBufferWriter<byte> writer = new ArrayBufferWriter<byte>();
        for (int e = 0; e < entries; e++)
        {
            StateWords.Clear(words.AsSpan(e * width, width), kinds);
            words[e * width] = random.Next(1, 100);

            // Sketches of a few values, or of many spread wide, which a merge opens as a window.
            QuantileSketch sketch = new QuantileSketch(ColumnCountsBuilder.SketchBits);
            int spread = e % 4 == 0 ? 60 : 3;
            for (int r = 0; r < spread; r++)
            {
                sketch.Add(random.Next(1, 100_000) * (r % 5 == 0 ? -1 : 1));
            }

            writer.ResetWrittenCount();
            sketch.Encode(writer);
            sketches[e] = writer.WrittenSpan.ToArray();
            registers[e] = HyperLogLog.Encode(Ranks(random, ColumnSheet.Registers, e % 7 == 0 ? 900 : 12));
        }

        return (new ColumnCounts([1], kinds, groups, words) { Sketched = true, AttachedFields = [2] },
            new ColumnSketches(entries, 1, sketches, 1, registers));
    }
}
