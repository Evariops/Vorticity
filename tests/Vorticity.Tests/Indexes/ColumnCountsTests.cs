// A column's counts over an object's rows, built as the writer builds them.
//
// WHAT IS HELD: past its budget of entries, a column keeps the values whose rows are most, each exact, and counts the
// others together, exact too, so that every row is counted once; and what the counts write reads back word for word:
// signed and unsigned extremes, sums past 64 bits, float sums and the extremes of a measure that met no value; and their
// sketches byte for byte, whatever the order the rows of a value came in.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Indexes;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Indexes;

public sealed class ColumnCountsTests
{
    private const long MicrosPerHour = 3_600_000_000L;

    [Fact]
    public void AColumnPastItsBudgetKeepsItsMostFrequentValuesAndTheOthersTogether()
    {
        // Value v of forty takes 8 × (40 - v)² rows, each value's spread evenly over four hours, with an amount and a price
        // each: the rows that come first hold every value in its share, the most frequent apart by a few rows a thousand.
        List<(long At, long Key, long Amount, double Price)> rows = [];
        Random random = new Random(5);
        for (int v = 0; v < 40; v++)
        {
            int count = 8 * (40 - v) * (40 - v);
            for (int i = 0; i < count; i++)
            {
                rows.Add(((long)((i + 0.5) / count * 4 * MicrosPerHour), v, random.NextInt64(-1_000, 1_000_000), Math.Round(random.NextDouble() * 100, 2)));
            }
        }

        rows.Sort((left, right) => left.At.CompareTo(right.At));

        // A budget of 16 entries for forty values: past it, the values of fewest rows so far go together, and stay together.
        (ColumnCounts counts, ColumnSketches? sketches) = Build(rows, entryLimit: 16);
        Assert.Equal([2, 3], counts.MeasureFields);
        Assert.True(counts.Sketched);
        Assert.Single(counts.Groups, g => g.Kind == CountedValue.Others);
        long[] kept = [.. counts.Groups.Where(g => g.Kind == CountedValue.Value).Select(g => BitConverter.ToInt64(g.Value))];
        Assert.True(kept.Length < 16, $"{kept.Length} values kept");

        // The most frequent values are kept, each exact; the others hold the rest, exact.
        Assert.Subset(kept.ToHashSet(), Enumerable.Range(0, 8).Select(v => (long)v).ToHashSet());
        foreach (CountGroup group in counts.Groups)
        {
            Assert.Equal(1, group.Entries);
            int e = group.FirstEntry;
            List<(long At, long Key, long Amount, double Price)> held = [.. rows.Where(r =>
                group.Kind == CountedValue.Others ? !kept.Contains(r.Key) : r.Key == BitConverter.ToInt64(group.Value))];
            ReadOnlySpan<long> words = counts.WordsOf(e);
            Assert.Equal(held.Count, words[0]);
            Assert.Equal(held.Count, words[1 + MeasureLayout.Values]);
            Assert.Equal(held.Aggregate(Int128.Zero, (s, r) => s + r.Amount), StateWords.IntegerOf(words, 1 + MeasureLayout.Sum));
            Assert.Equal(held.Min(r => r.Amount), words[1 + MeasureLayout.IntegerLeast]);
            Assert.Equal(held.Max(r => r.Amount), words[1 + MeasureLayout.IntegerGreatest]);
            int price = 1 + MeasureLayout.IntegerWords;
            WideIndexedSum sum = default;
            held.ForEach(r => sum.Add(r.Price));
            Assert.Equal(sum.Value, StateWords.SumOf(words, price + MeasureLayout.Sum).Value);
            Assert.Equal(held.Min(r => r.Price), BitConverter.Int64BitsToDouble(words[price + MeasureLayout.FloatLeast]));
            Assert.Equal(held.Max(r => r.Price), BitConverter.Int64BitsToDouble(words[price + MeasureLayout.FloatGreatest]));

            // Each entry's sketches, the values left out's merged from theirs, are those of its rows' values.
            Assert.Equal(Sketch(held.Select(r => (double)r.Amount)), sketches!.SketchOf(e, 0));
            Assert.Equal(Sketch(held.Select(r => r.Price)), sketches.SketchOf(e, 1));
        }

        Assert.Equal(rows.Count, Enumerable.Range(0, counts.Entries).Sum(e => counts.WordsOf(e)[0]));
    }

    [Fact]
    public void WhatTheCountsWriteReadsBackWordForWord()
    {
        MeasureKind[] kinds = [MeasureKind.Signed, MeasureKind.Unsigned, MeasureKind.Float];
        int width = StateWords.Count(kinds);
        long[] words = new long[3 * width];
        StateWords.Clear(words.AsSpan(0, width), kinds);
        StateWords.Clear(words.AsSpan(width, width), kinds);
        StateWords.Clear(words.AsSpan(2 * width, width), kinds);

        // An entry whose measures hold extremes and a sum past 64 bits; two whose measures met no value.
        Span<long> first = words.AsSpan(0, width);
        first[0] = 5;
        first[1 + MeasureLayout.Values] = 5;
        StateWords.Write((Int128)long.MinValue * 5, first, 1 + MeasureLayout.Sum);
        first[1 + MeasureLayout.IntegerLeast] = long.MinValue;
        first[1 + MeasureLayout.IntegerGreatest] = long.MaxValue;
        int unsigned = 1 + MeasureLayout.IntegerWords;
        first[unsigned + MeasureLayout.Values] = 2;
        StateWords.Write((Int128)ulong.MaxValue * 2, first, unsigned + MeasureLayout.Sum);
        first[unsigned + MeasureLayout.IntegerLeast] = -1;
        first[unsigned + MeasureLayout.IntegerGreatest] = -1;
        int floats = unsigned + MeasureLayout.IntegerWords;
        WideIndexedSum sum = default;
        foreach (double value in (double[])[1e300, -2.5, 1e-300])
        {
            sum.Add(value);
        }

        first[floats + MeasureLayout.Values] = 3;
        StateWords.Write(in sum, first, floats + MeasureLayout.Sum);
        first[floats + MeasureLayout.FloatLeast] = BitConverter.DoubleToInt64Bits(-2.5);
        first[floats + MeasureLayout.FloatGreatest] = BitConverter.DoubleToInt64Bits(1e300);
        words[width] = 7;
        words[2 * width] = 1;

        ColumnCounts counts = new ColumnCounts(
            [4, 7, 9],
            kinds,
            [new CountGroup(CountedValue.Null, null, 0, 1), new CountGroup(CountedValue.Value, [1, 2, 3], 1, 1), new CountGroup(CountedValue.Value, [1, 2, 4], 2, 1)],
            words)
        {
            Sketched = true,
            AttachedFields = [5],
        };
        ColumnCounts back = ColumnCounts.From(counts.ToBytes());

        Assert.Equal(counts.MeasureFields, back.MeasureFields);
        Assert.Equal(counts.Kinds, back.Kinds);
        Assert.Equal(counts.Groups.Select(g => (g.Kind, g.Value is null ? "" : Convert.ToHexString(g.Value), g.FirstEntry, g.Entries)),
            back.Groups.Select(g => (g.Kind, g.Value is null ? "" : Convert.ToHexString(g.Value), g.FirstEntry, g.Entries)));
        Assert.Equal(counts.Words, back.Words);
        Assert.True(back.Sketched);
        Assert.Equal([5], back.AttachedFields);
        Assert.Throws<VortexFormatException>(() => ColumnCounts.From(counts.ToBytes()[..^1]));

        // Counts of another version, the hours an earlier writer kept, are none this library reads.
        byte[] earlier = counts.ToBytes();
        earlier[0] = 1;
        Assert.Throws<VortexFormatException>(() => ColumnCounts.From(earlier));

        // Their sketches, each entry's of each measure, none for a measure that met no value, and its registers.
        ColumnSketches sketches = new ColumnSketches(3, 3, [[1], null, [2, 3], [4], [5], null, null, null, [6]], 1, [[7, 8], null, [9]]);
        ColumnSketches sketchesBack = ColumnSketches.From(sketches.ToBytes());
        Assert.Equal((3, 3, 1), (sketchesBack.Entries, sketchesBack.Measures, sketchesBack.Attached));
        Assert.Equal(sketches.Sketches, sketchesBack.Sketches);
        Assert.Equal(sketches.Distinct, sketchesBack.Distinct);
        Assert.Throws<VortexFormatException>(() => ColumnSketches.From(sketches.ToBytes()[..^1]));
    }

    // The rows of each value spread over the object, in no order: an entry a value, whose sketches hold every one of its
    // values, as one built from them all.
    [Fact]
    public void AnEntryHoldsEveryRowOfItsValueWhateverTheirOrder()
    {
        Random random = new Random(9);
        List<(long At, long Key, long Amount, double Price)> rows = [];
        foreach (int hour in (int[])[0, 0, 1, 0, 2, 1])
        {
            for (int i = 0; i < 1_000; i++)
            {
                rows.Add(((hour * MicrosPerHour) + random.NextInt64(MicrosPerHour), random.Next(5), random.NextInt64(1, 100_000), Math.Round(random.NextDouble() * 50, 3)));
            }
        }

        (ColumnCounts counts, ColumnSketches? sketches) = Build(rows, ColumnCountsBuilder.EntryLimit);
        Assert.Equal(5, counts.Entries);
        for (int e = 0; e < counts.Entries; e++)
        {
            long key = BitConverter.ToInt64(counts.Groups.Single(g => e >= g.FirstEntry && e < g.FirstEntry + g.Entries).Value);
            List<(long At, long Key, long Amount, double Price)> held = [.. rows.Where(r => r.Key == key)];
            Assert.Equal(held.Count, counts.WordsOf(e)[0]);
            Assert.Equal(Sketch(held.Select(r => (double)r.Amount)), sketches!.SketchOf(e, 0));
            Assert.Equal(Sketch(held.Select(r => r.Price)), sketches.SketchOf(e, 1));
        }
    }

    /// <summary>The encoded quantile sketch, at 1 % as the counts keep them, of <paramref name="values"/>.</summary>
    internal static byte[] Sketch(IEnumerable<double> values)
    {
        QuantileSketch sketch = new QuantileSketch(QuantileSketch.BitsFor(0.01));
        foreach (double value in values)
        {
            sketch.Add(value);
        }

        System.Buffers.ArrayBufferWriter<byte> writer = new System.Buffers.ArrayBufferWriter<byte>();
        sketch.Encode(writer);
        return writer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// The counts of column <c>Key</c> of <paramref name="rows"/>, and their sketches, under a schema of an instant, the
    /// key, an amount and a price, fed in parts as a writer feeds them.
    /// </summary>
    internal static (ColumnCounts Counts, ColumnSketches? Sketches) Build(List<(long At, long Key, long Amount, double Price)> rows, int entryLimit)
    {
        VortexSchema schema = VortexSchema.Create([
            new VortexField("At", VortexType.Timestamp(TimeUnit.Microseconds, "UTC")),
            new VortexField("Key", VortexType.Int64),
            new VortexField("Amount", VortexType.Int64),
            new VortexField("Price", VortexType.Float64)]);
        DTypeArena types = new DTypeArena();
        DType dtype = VortexTypes.ToDType(schema, types);
        CountsPlan plan = CountsPlan.For(dtype)!;
        ColumnSheetBuilder sheet = new ColumnSheetBuilder();
        ColumnCountsBuilder counts = new ColumnCountsBuilder(sheet, plan, field: 1, entryLimit);
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType f64 = types.Primitive(PType.F64, Nullability.NonNullable);
        const int Part = 1_000;
        for (int start = 0; start < rows.Count; start += Part)
        {
            int count = Math.Min(Part, rows.Count - start);
            CanonicalArena arena = new CanonicalArena();
            int[] nodes =
            [
                Column(arena, i64, PType.I64, rows, start, count, r => r.At),
                Column(arena, i64, PType.I64, rows, start, count, r => r.Key),
                Column(arena, i64, PType.I64, rows, start, count, r => r.Amount),
                Column(arena, f64, PType.F64, rows, start, count, r => BitConverter.DoubleToInt64Bits(r.Price)),
            ];
            plan.Prepare(arena, nodes, count);
            sheet.Accumulate(arena, nodes[1], 0, count);
            counts.Accumulate(arena, nodes[1], 0, count);
        }

        counts.Settle();
        (byte[] bytes, byte[]? sketches) = counts.Finish()!.Value;
        return (ColumnCounts.From(bytes), sketches is null ? null : ColumnSketches.From(sketches));
    }

    private static int Column(
        CanonicalArena arena, DType dtype, PType ptype, List<(long At, long Key, long Amount, double Price)> rows, int start, int count,
        Func<(long At, long Key, long Amount, double Price), long> bits)
    {
        VortexBuffer buffer = arena.Allocate(count * sizeof(long), sizeof(long), out Span<byte> bytes);
        Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
        for (int i = 0; i < count; i++)
        {
            values[i] = bits(rows[start + i]);
        }

        return arena.AddPrimitive(dtype, count, Validity.NonNullable, ptype, buffer);
    }
}
