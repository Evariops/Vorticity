// Audit IN-21: what summing objects' counts of a column costs, as a rollup or a deletion's counts are built: 24 objects (an
// hour each of a day), each counting 2,048 values drawn among 4,096 integers, an entry a value of one integer measure whose
// quantile sketch holds a few rows, and a wide column's registers, 24 of the 2,048 set; every fourth object counted 3,968
// values and left the others out, so the merge checks every value against the values those objects keep and moves some
// 700 values into the entry of the values left out. Then the merged counts and sketches written, some 3,400 entries.
using System;
using System.Buffers;
using System.Collections.Generic;

using BenchmarkDotNet.Attributes;

using Vorticity.Aggregating;
using Vorticity.Indexes;
using Vorticity.Writing;

namespace Vorticity.Benchmarks.Audit;

/// <summary>Objects' counts summed, then written.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditCountsMergeBenchmarks
{
    private const int Objects = 24;
    private const int Values = 2_048;

    private readonly List<(ColumnCounts Counts, ColumnSketches Sketches)> _objects = [];
    private (ColumnCounts Counts, ColumnSketches? Sketches) _merged;

    /// <summary>Draws the objects.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(1021);
        MeasureKind[] kinds = [MeasureKind.Signed];
        int width = StateWords.Count(kinds);
        for (int o = 0; o < Objects; o++)
        {
            bool leftOut = o % 4 == 3;
            SortedSet<long> chosen = [];
            while (chosen.Count < (leftOut ? 3_968 : Values))
            {
                chosen.Add(random.Next(4_096));
            }

            int entries = chosen.Count + (leftOut ? 1 : 0);
            CountGroup[] groups = new CountGroup[entries];
            long[] words = new long[entries * width];
            byte[]?[] sketches = new byte[]?[entries];
            byte[]?[] registers = new byte[]?[entries];
            ArrayBufferWriter<byte> writer = new ArrayBufferWriter<byte>();
            byte[] ranks = new byte[ColumnSheet.Registers];
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

            for (int e = 0; e < entries; e++)
            {
                StateWords.Clear(words.AsSpan(e * width, width), kinds);
                words[e * width] = random.Next(1, 100);
                QuantileSketch sketch = new QuantileSketch(ColumnCountsBuilder.SketchBits);
                for (int r = 0; r < 4; r++)
                {
                    sketch.Add(random.Next(1, 10_000));
                }

                writer.ResetWrittenCount();
                sketch.Encode(writer);
                sketches[e] = writer.WrittenSpan.ToArray();
                Array.Clear(ranks);
                for (int set = 0; set < 24; set++)
                {
                    ranks[random.Next(ranks.Length)] = (byte)random.Next(1, 20);
                }

                registers[e] = HyperLogLog.Encode(ranks);
            }

            _objects.Add((
                new ColumnCounts([1], kinds, groups, words) { Sketched = true, AttachedFields = [2] },
                new ColumnSketches(entries, 1, sketches, 1, registers)));
        }

        _merged = Summed();
        if (_merged.Counts.Entries == 0 || Written() == 0)
        {
            throw new InvalidOperationException("The merge holds nothing.");
        }
    }

    /// <summary>The objects' counts summed; the entries kept.</summary>
    [Benchmark]
    public int Merge() => Summed().Counts.Entries;

    /// <summary>The merged counts and sketches written; their bytes.</summary>
    [Benchmark]
    public int Write() => Written();

    private (ColumnCounts Counts, ColumnSketches? Sketches) Summed()
    {
        CountsMerge merge = new CountsMerge();
        foreach ((ColumnCounts counts, ColumnSketches sketches) in _objects)
        {
            merge.Add(counts, sketches);
        }

        return merge.Finish()!.Value;
    }

    private int Written() => _merged.Counts.ToBytes().Length + _merged.Sketches!.ToBytes().Length;
}
