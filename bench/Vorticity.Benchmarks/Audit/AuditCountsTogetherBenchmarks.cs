// Audit IN-20: what putting buckets' counts of a column together costs, as a rollup is built: 24 buckets (an hour each of
// a day), each counting 4,096 values drawn among 8,192 texts of 12 to 20 bytes, an entry a value.
using System;
using System.Collections.Generic;
using System.Text;

using BenchmarkDotNet.Attributes;

using Vorticity.Indexes;

namespace Vorticity.Benchmarks.Audit;

/// <summary>Buckets' counts put together.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditCountsTogetherBenchmarks
{
    private const int Buckets = 24;
    private const int Values = 4_096;

    private (ColumnCounts Counts, ColumnSketches? Sketches)?[] _buckets = [];

    /// <summary>Draws the buckets.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(1027);
        byte[][] universe = new byte[8_192][];
        for (int i = 0; i < universe.Length; i++)
        {
            universe[i] = Encoding.UTF8.GetBytes($"service-{i:D5}-{new string('x', random.Next(8))}");
        }

        _buckets = new (ColumnCounts, ColumnSketches?)?[Buckets];
        for (int b = 0; b < Buckets; b++)
        {
            SortedSet<byte[]> chosen = new SortedSet<byte[]>(Comparer<byte[]>.Create((x, y) => x.AsSpan().SequenceCompareTo(y)));
            while (chosen.Count < Values)
            {
                chosen.Add(universe[random.Next(universe.Length)]);
            }

            CountGroup[] groups = new CountGroup[Values];
            int g = 0;
            foreach (byte[] value in chosen)
            {
                groups[g] = new CountGroup(CountedValue.Value, value, g, 1);
                g++;
            }

            long[] words = new long[Values];
            for (int e = 0; e < Values; e++)
            {
                words[e] = random.Next(1, 100);
            }

            _buckets[b] = (new ColumnCounts([], [], groups, words), null);
        }

        if (Together() == 0)
        {
            throw new InvalidOperationException("The buckets put together hold nothing.");
        }
    }

    /// <summary>The buckets put together; the groups found.</summary>
    [Benchmark]
    public int Together() => CountsBuckets.Together(_buckets) is ({ } counts, _) ? counts.Groups.Length : 0;
}
