// Columns' counts by hour summed into days and months, with no row read.
//
// WHAT IS HELD: the counts by hour of several objects, merged into days, hold for each value and day what the rows of
// every object give, word for word and sketch for sketch, and a wide column's registers the most of theirs; merged
// again into their month, what the hours merged into it give; a value one object left out goes with the values left
// out in each day that object's others reach, every count staying exact; the merge carries the measures and the
// sketches every object carries; and past its budget, the values of fewest rows go with the others.
using System;
using System.Collections.Generic;
using System.Linq;
using Vorticity.Aggregating;
using Vorticity.Indexes;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Indexes;

public sealed class CountsMergeTests
{
    private const long MicrosPerHour = 3_600_000_000L;

    [Fact]
    public void TheHoursOfSeveralObjectsMergeIntoDaysAsTheirRowsDo()
    {
        Random random = new Random(3);
        List<(long At, long Key, long Amount, double Price)>[] objects = [Rows(random, 0, 30), Rows(random, 5, 48), Rows(random, 40, 70)];
        CountsMerge days = new CountsMerge();
        foreach (List<(long At, long Key, long Amount, double Price)> rows in objects)
        {
            (ColumnCounts counts, ColumnSketches? sketches) = ColumnCountsTests.Build(rows, ColumnCountsBuilder.EntryLimit);
            days.Add(counts, sketches, hour => Day(hour));
        }

        (ColumnCounts merged, ColumnSketches? sketched) = days.Finish()!.Value;
        Assert.Equal([2, 3], merged.MeasureFields);
        Assert.True(merged.Sketched);
        Assert.DoesNotContain(merged.Groups, group => group.Kind == CountedValue.Others);
        Check(merged, sketched, [.. objects.SelectMany(rows => rows)], at => Day(at / MicrosPerHour));

        // The days into their month: what the hours merged into it would give.
        CountsMerge month = new CountsMerge();
        month.Add(merged, sketched, _ => 0);
        (ColumnCounts monthly, ColumnSketches? monthSketches) = month.Finish()!.Value;
        Check(monthly, monthSketches, [.. objects.SelectMany(rows => rows)], _ => 0);
    }

    [Fact]
    public void AValueAnObjectLeftOutGoesWithTheOthersWhereItsOthersReach()
    {
        Random random = new Random(5);
        List<(long At, long Key, long Amount, double Price)> crowded = Rows(random, 0, 24, keys: 40, skewed: true);
        List<(long At, long Key, long Amount, double Price)> plain = Rows(random, 12, 48, keys: 40);
        (ColumnCounts left, ColumnSketches? leftSketches) = ColumnCountsTests.Build(crowded, entryLimit: 64);
        (ColumnCounts kept, ColumnSketches? keptSketches) = ColumnCountsTests.Build(plain, ColumnCountsBuilder.EntryLimit);
        Assert.Contains(left.Groups, group => group.Kind == CountedValue.Others);
        CountsMerge days = new CountsMerge();
        days.Add(left, leftSketches, hour => Day(hour));
        days.Add(kept, keptSketches, hour => Day(hour));
        (ColumnCounts merged, ColumnSketches? sketched) = days.Finish()!.Value;

        // The values the crowded object keeps are exact in every day; in the day its others reach, every other value went
        // with them; past it, every value is its own.
        HashSet<long> keptByLeft = [.. left.Groups.Where(g => g.Kind == CountedValue.Value).Select(g => BitConverter.ToInt64(g.Value))];
        List<(long At, long Key, long Amount, double Price)> all = [.. crowded, .. plain];
        foreach (CountGroup group in merged.Groups.Where(g => g.Kind == CountedValue.Value))
        {
            long key = BitConverter.ToInt64(group.Value);
            for (int e = group.FirstEntry; e < group.FirstEntry + group.Entries; e++)
            {
                Assert.True(keptByLeft.Contains(key) || merged.Hours[e] != 0, $"value {key} stands alone in the day the others reach");
                Assert.Equal(all.Count(r => r.Key == key && Day(r.At / MicrosPerHour) == merged.Hours[e]), merged.WordsOf(e)[0]);
            }
        }

        Assert.Equal(all.Count, Enumerable.Range(0, merged.Entries).Sum(e => merged.WordsOf(e)[0]));
        Assert.Equal(sketched!.Entries, merged.Entries);
    }

    [Fact]
    public void TheMergeCarriesWhatEveryObjectCarries()
    {
        // The second object carries no price, as a column of few values in it would not be.
        Random random = new Random(7);
        List<(long At, long Key, long Amount, double Price)> first = Rows(random, 0, 24);
        List<(long At, long Key, long Amount, double Price)> second = Rows(random, 24, 48);
        (ColumnCounts a, ColumnSketches? aSketches) = ColumnCountsTests.Build(first, ColumnCountsBuilder.EntryLimit);
        (ColumnCounts b, ColumnSketches? bSketches) = WithoutPrice(ColumnCountsTests.Build(second, ColumnCountsBuilder.EntryLimit));
        CountsMerge days = new CountsMerge();
        days.Add(a, aSketches, hour => Day(hour));
        days.Add(b, bSketches, hour => Day(hour));
        (ColumnCounts merged, ColumnSketches? sketched) = days.Finish()!.Value;
        Assert.Equal([2], merged.MeasureFields);
        foreach (CountGroup group in merged.Groups)
        {
            long key = BitConverter.ToInt64(group.Value);
            for (int e = group.FirstEntry; e < group.FirstEntry + group.Entries; e++)
            {
                List<(long At, long Key, long Amount, double Price)> held = [.. first.Concat(second).Where(r => r.Key == key && Day(r.At / MicrosPerHour) == merged.Hours[e])];
                Assert.Equal(held.Count, merged.WordsOf(e)[0]);
                Assert.Equal(held.Aggregate(Int128.Zero, (s, r) => s + r.Amount), StateWords.IntegerOf(merged.WordsOf(e), 1 + MeasureLayout.Sum));
                Assert.Equal(ColumnCountsTests.Sketch(held.Select(r => (double)r.Amount)), sketched!.SketchOf(e, 0));
            }
        }
    }

    [Fact]
    public void TheRegistersOfAWideColumnRiseToTheMostOfTheirHours()
    {
        // Two objects whose entries each keep registers of a wide column, drawn at random, a few set.
        Random random = new Random(13);
        List<(ColumnCounts Counts, ColumnSketches Sketches, byte[][] Ranks)> objects = [];
        foreach ((int from, int to) in ((int, int)[])[(0, 30), (20, 50)])
        {
            (ColumnCounts counts, ColumnSketches? sketches) = ColumnCountsTests.Build(Rows(random, from, to), ColumnCountsBuilder.EntryLimit);
            byte[][] ranks = new byte[counts.Entries][];
            byte[]?[] registers = new byte[]?[counts.Entries];
            for (int e = 0; e < counts.Entries; e++)
            {
                ranks[e] = new byte[ColumnSheet.Registers];
                for (int set = 0; set < 40; set++)
                {
                    ranks[e][random.Next(ranks[e].Length)] = (byte)random.Next(1, 20);
                }

                registers[e] = HyperLogLog.Encode(ranks[e]);
            }

            objects.Add((
                new ColumnCounts(counts.MeasureFields, counts.Kinds, counts.Groups, counts.Hours, counts.Words) { Sketched = true, AttachedFields = [9] },
                new ColumnSketches(counts.Entries, counts.MeasureFields.Length, sketches!.Sketches, 1, registers),
                ranks));
        }

        CountsMerge days = new CountsMerge();
        objects.ForEach(o => days.Add(o.Counts, o.Sketches, hour => Day(hour)));
        (ColumnCounts merged, ColumnSketches? sketched) = days.Finish()!.Value;
        Assert.Equal([9], merged.AttachedFields);
        foreach (CountGroup group in merged.Groups)
        {
            for (int e = group.FirstEntry; e < group.FirstEntry + group.Entries; e++)
            {
                byte[] expected = new byte[ColumnSheet.Registers];
                foreach ((ColumnCounts counts, _, byte[][] ranks) in objects)
                {
                    CountGroup? same = counts.Groups.Where(g => g.Value.AsSpan().SequenceEqual(group.Value)).Cast<CountGroup?>().FirstOrDefault();
                    for (int i = same?.FirstEntry ?? 0; same is { } held && i < held.FirstEntry + held.Entries; i++)
                    {
                        if (Day(counts.Hours[i]) == merged.Hours[e])
                        {
                            for (int r = 0; r < expected.Length; r++)
                            {
                                expected[r] = Math.Max(expected[r], ranks[i][r]);
                            }
                        }
                    }
                }

                Assert.Equal(HyperLogLog.Encode(expected), sketched!.DistinctOf(e, 0));
            }
        }
    }

    [Fact]
    public void PastItsBudgetTheValuesOfFewestRowsGoWithTheOthers()
    {
        Random random = new Random(11);
        List<(long At, long Key, long Amount, double Price)> rows = Rows(random, 0, 72, keys: 30, skewed: true);
        (ColumnCounts counts, ColumnSketches? sketches) = ColumnCountsTests.Build(rows, ColumnCountsBuilder.EntryLimit);
        CountsMerge days = new CountsMerge(entryLimit: 40);
        days.Add(counts, sketches, hour => Day(hour));
        (ColumnCounts merged, ColumnSketches? sketched) = days.Finish()!.Value;
        Assert.InRange(merged.Entries, 1, 40);
        CountGroup others = Assert.Single(merged.Groups, g => g.Kind == CountedValue.Others);

        // The values kept are those of most rows, each exact; the others hold the rest.
        long[] kept = [.. merged.Groups.Where(g => g.Kind == CountedValue.Value).Select(g => BitConverter.ToInt64(g.Value))];
        long fewestKept = kept.Min(key => rows.Count(r => r.Key == key));
        Assert.All(rows.Select(r => r.Key).Distinct().Where(key => !kept.Contains(key)), key => Assert.True(rows.Count(r => r.Key == key) <= fewestKept));
        Check(merged, sketched, [.. rows.Where(r => kept.Contains(r.Key))], at => Day(at / MicrosPerHour), skipOthers: true);
        Assert.Equal(rows.Count(r => !kept.Contains(r.Key)), Enumerable.Range(others.FirstEntry, others.Entries).Sum(e => merged.WordsOf(e)[0]));
    }

    /// <summary>The counts and sketches <paramref name="built"/> with their first measure, the amount, alone.</summary>
    private static (ColumnCounts Counts, ColumnSketches? Sketches) WithoutPrice((ColumnCounts Counts, ColumnSketches? Sketches) built)
    {
        (ColumnCounts counts, ColumnSketches? sketches) = built;
        int width = StateWords.Count([MeasureKind.Signed]);
        long[] words = new long[counts.Entries * width];
        byte[]?[] amounts = new byte[]?[counts.Entries];
        for (int e = 0; e < counts.Entries; e++)
        {
            counts.WordsOf(e)[..width].CopyTo(words.AsSpan(e * width, width));
            amounts[e] = sketches!.SketchOf(e, 0);
        }

        return (new ColumnCounts([2], [MeasureKind.Signed], counts.Groups, counts.Hours, words) { Sketched = true },
            new ColumnSketches(counts.Entries, 1, amounts, 0, null));
    }

    /// <summary>The first hour of the day <paramref name="hour"/> falls in, both counted from the Unix epoch.</summary>
    private static long Day(long hour) => hour / 24 * 24;

    /// <summary>Holds <paramref name="counts"/> and their sketches to what the rows <paramref name="rows"/> give, each in the bucket of its instant.</summary>
    private static void Check(
        ColumnCounts counts, ColumnSketches? sketches, List<(long At, long Key, long Amount, double Price)> rows, Func<long, long> bucketOf,
        bool skipOthers = false)
    {
        Dictionary<(long Key, long Bucket), List<(long At, long Key, long Amount, double Price)>> expected = rows
            .GroupBy(r => (r.Key, bucketOf(r.At))).ToDictionary(g => g.Key, g => g.ToList());
        int entries = 0;
        foreach (CountGroup group in counts.Groups)
        {
            if (group.Kind == CountedValue.Others && skipOthers)
            {
                continue;
            }

            Assert.Equal(CountedValue.Value, group.Kind);
            long key = BitConverter.ToInt64(group.Value);
            for (int e = group.FirstEntry; e < group.FirstEntry + group.Entries; e++, entries++)
            {
                List<(long At, long Key, long Amount, double Price)> held = expected[(key, counts.Hours[e])];
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
                Assert.Equal(ColumnCountsTests.Sketch(held.Select(r => (double)r.Amount)), sketches!.SketchOf(e, 0));
                Assert.Equal(ColumnCountsTests.Sketch(held.Select(r => r.Price)), sketches.SketchOf(e, 1));
            }
        }

        Assert.Equal(expected.Count, entries);
    }

    /// <summary>
    /// Rows from hour <paramref name="from"/> to hour <paramref name="to"/>, in order, eighty an hour, each of one of
    /// <paramref name="keys"/> values, the low values far the most frequent when <paramref name="skewed"/>.
    /// </summary>
    private static List<(long At, long Key, long Amount, double Price)> Rows(Random random, int from, int to, int keys = 30, bool skewed = false)
    {
        List<(long At, long Key, long Amount, double Price)> rows = [];
        for (int hour = from; hour < to; hour++)
        {
            for (int i = 0; i < 80; i++)
            {
                long key = skewed ? (long)Math.Floor(keys * Math.Pow(random.NextDouble(), 3)) : random.Next(keys);
                rows.Add(((hour * MicrosPerHour) + (i * (MicrosPerHour / 80)), key, random.NextInt64(-1_000, 1_000_000), Math.Round(random.NextDouble() * 100, 2)));
            }
        }

        return rows;
    }
}
