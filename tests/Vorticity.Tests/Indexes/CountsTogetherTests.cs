// Buckets' counts of a column put together, against the buckets read one by one: a group a distinct value in the order
// of its bytes, after the nulls and the values left out, each group's entries those of the buckets that count the value,
// bucket by bucket, each entry's words and bucket the bucket's.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vorticity.Indexes;
using Xunit;

namespace Vorticity.Tests.Indexes;

public sealed class CountsTogetherTests
{
    [Theory]
    [InlineData(1, 10)]
    [InlineData(7, 50)]
    [InlineData(24, 300)]
    public void EachValuesEntriesAreItsBucketsInTheirOrder(int buckets, int values)
    {
        Random random = new Random(buckets * 31 + values);
        byte[][] universe = Enumerable.Range(0, values * 2).Select(i => Encoding.UTF8.GetBytes($"v{i % 7}-{i:D4}")).ToArray();
        (ColumnCounts Counts, ColumnSketches? Sketches)?[] held = new (ColumnCounts, ColumnSketches?)?[buckets];
        for (int b = 0; b < buckets; b++)
        {
            if (b % 5 == 3)
            {
                continue;
            }

            List<CountGroup> groups = [];
            List<long> words = [];
            if (b % 2 == 0)
            {
                groups.Add(new CountGroup(CountedValue.Null, null, words.Count, 1));
                words.Add(1000 + b);
            }

            if (b % 3 == 0)
            {
                groups.Add(new CountGroup(CountedValue.Others, null, words.Count, 1));
                words.Add(2000 + b);
            }

            foreach (byte[] value in universe.Where(_ => random.Next(2) == 0).OrderBy(v => v, Comparer<byte[]>.Create((x, y) => x.AsSpan().SequenceCompareTo(y))))
            {
                groups.Add(new CountGroup(CountedValue.Value, value, words.Count, 1));
                words.Add((b * 100_000) + Array.IndexOf(universe, value));
            }

            held[b] = (new ColumnCounts([], [], [.. groups], [.. words]), null);
        }

        ColumnCounts together = CountsBuckets.Together(held)!.Value.Counts;

        // The expected groups: the nulls', the values left out, then each value in byte order; entries bucket by bucket.
        List<(CountedValue Kind, string? Value, List<(int Bucket, long Word)> Entries)> expected = [];
        void Add(CountedValue kind, Func<CountGroup, bool> matches, string? value)
        {
            List<(int, long)> entries = [];
            for (int b = 0; b < buckets; b++)
            {
                if (held[b] is ({ } counts, _))
                {
                    foreach (CountGroup group in counts.Groups.Where(matches))
                    {
                        entries.Add((b, counts.WordsOf(group.FirstEntry)[0]));
                    }
                }
            }

            if (entries.Count > 0)
            {
                expected.Add((kind, value, entries));
            }
        }

        Add(CountedValue.Null, g => g.Kind == CountedValue.Null, null);
        Add(CountedValue.Others, g => g.Kind == CountedValue.Others, null);
        foreach (string value in universe.Select(v => Encoding.UTF8.GetString(v)).Distinct().OrderBy(v => v, StringComparer.Ordinal))
        {
            Add(CountedValue.Value, g => g.Kind == CountedValue.Value && Encoding.UTF8.GetString(g.Value!) == value, value);
        }

        Assert.Equal(expected.Count, together.Groups.Length);
        for (int i = 0; i < expected.Count; i++)
        {
            CountGroup group = together.Groups[i];
            Assert.Equal(expected[i].Kind, group.Kind);
            Assert.Equal(expected[i].Value, group.Value is null ? null : Encoding.UTF8.GetString(group.Value));
            Assert.Equal(expected[i].Entries.Count, group.Entries);
            for (int e = 0; e < group.Entries; e++)
            {
                Assert.Equal(expected[i].Entries[e].Bucket, together.EntryBuckets![group.FirstEntry + e]);
                Assert.Equal(expected[i].Entries[e].Word, together.WordsOf(group.FirstEntry + e)[0]);
            }
        }
    }
}
