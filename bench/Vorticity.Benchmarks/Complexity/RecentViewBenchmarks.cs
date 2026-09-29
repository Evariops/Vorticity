// What the writer's distinct table pays for short strings, by how often their views come back.
//
// A string column of 65,536 rows probed into the distinct table a block of 8,192 rows at a time, as
// the writer probes it, whose recent views let a row equal to a recent one take its code without the
// hash and the probe. `Fixture5` is the five values of
// the per-encoding corpus's dictionary files, "" to "dddd", row after row in turn; `Labels5`,
// `Labels16` and `Labels200` draw that many short labels at random for each row; `SortedRuns` is 256
// labels in runs of 256 rows; `Random12` is 65,536 distinct strings of twelve bytes, which no recent
// view spares. Run in a build of each version, alternately.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>A column of short strings probed into the writer's distinct table.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class RecentViewBenchmarks
{
    private const int Rows = 65_536;

    /// <summary>The rows of a block, the writer's default, which it probes at once.</summary>
    private const int BlockRows = 8_192;

    /// <summary>The column's values.</summary>
    [Params(Column.Fixture5, Column.Labels5, Column.Labels16, Column.Labels200, Column.SortedRuns, Column.Random12)]
    public Column Shape { get; set; }

    /// <summary>A column's values.</summary>
    public enum Column
    {
        /// <summary>"", "a", "bb", "ccc" and "dddd", row after row in turn.</summary>
        Fixture5,

        /// <summary>Five labels drawn at random for each row.</summary>
        Labels5,

        /// <summary>Sixteen labels drawn at random for each row.</summary>
        Labels16,

        /// <summary>Two hundred labels drawn at random for each row.</summary>
        Labels200,

        /// <summary>256 labels in runs of 256 rows.</summary>
        SortedRuns,

        /// <summary>Distinct strings of twelve bytes.</summary>
        Random12,
    }

    private CanonicalArena _arena = null!;
    private int _node;
    private DistinctTable _table = null!;

    /// <summary>Builds the column and probes it once, so the table is rented at its size.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _arena = new CanonicalArena();
        DTypeArena types = new DTypeArena();
        Random random = new Random(22);
        byte[][] values = Shape switch
        {
            Column.Fixture5 => Cycle(["", "a", "bb", "ccc", "dddd"]),
            Column.Labels5 => Draw(Labels(5, random), random),
            Column.Labels16 => Draw(Labels(16, random), random),
            Column.Labels200 => Draw(Labels(200, random), random),
            Column.SortedRuns => Runs(Labels(256, random)),
            _ => Distinct12(random),
        };
        _node = Strings(_arena, types.Utf8(Nullability.NonNullable), values);
        _table = DistinctTable.For(_arena.GetNode(_node))!;
        _ = Probe();
        if (_table.Abandoned)
        {
            throw new InvalidOperationException("The table gave the column up.");
        }
    }

    /// <summary>Gives the arena's blocks back.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _table.Reset();
        _arena.Reset();
    }

    /// <summary>The column probed into the library's distinct table a block at a time, as the writer probes it.</summary>
    [Benchmark]
    public int Probe()
    {
        _table.Reset();
        for (int start = 0; start < Rows; start += BlockRows)
        {
            _table.Probe(_arena, _arena.GetNode(_node), start, BlockRows);
        }

        return _table.Distinct;
    }

    private static byte[][] Cycle(string[] labels)
    {
        byte[][] values = new byte[Rows][];
        for (int i = 0; i < Rows; i++)
        {
            values[i] = Encoding.ASCII.GetBytes(labels[i % labels.Length]);
        }

        return values;
    }

    /// <summary><paramref name="count"/> distinct lowercase labels of two to twelve letters.</summary>
    private static byte[][] Labels(int count, Random random)
    {
        byte[][] labels = new byte[count][];
        HashSet<string> seen = [];
        for (int i = 0; i < count;)
        {
            byte[] label = new byte[random.Next(2, 13)];
            for (int k = 0; k < label.Length; k++)
            {
                label[k] = (byte)('a' + random.Next(26));
            }

            if (seen.Add(Encoding.ASCII.GetString(label)))
            {
                labels[i++] = label;
            }
        }

        return labels;
    }

    private static byte[][] Draw(byte[][] labels, Random random)
    {
        byte[][] values = new byte[Rows][];
        for (int i = 0; i < Rows; i++)
        {
            values[i] = labels[random.Next(labels.Length)];
        }

        return values;
    }

    private static byte[][] Runs(byte[][] labels)
    {
        byte[][] values = new byte[Rows][];
        int run = Rows / labels.Length;
        for (int i = 0; i < Rows; i++)
        {
            values[i] = labels[i / run];
        }

        return values;
    }

    private static byte[][] Distinct12(Random random)
    {
        byte[][] values = new byte[Rows][];
        for (int i = 0; i < Rows; i++)
        {
            values[i] = new byte[12];
            random.NextBytes(values[i]);
            BinaryPrimitives.WriteInt32LittleEndian(values[i], i);
        }

        return values;
    }

    /// <summary>A string column of <paramref name="values"/>, each at most twelve bytes and so inline in its view.</summary>
    private static int Strings(CanonicalArena arena, DType dtype, byte[][] values)
    {
        VortexBuffer heap = arena.Allocate(1, 1, out _);
        VortexBuffer views = arena.Allocate(values.Length * 16, 16, out Span<byte> viewBytes);
        for (int i = 0; i < values.Length; i++)
        {
            Span<byte> view = viewBytes.Slice(i * 16, 16);
            view.Clear();
            BinaryPrimitives.WriteInt32LittleEndian(view, values[i].Length);
            values[i].CopyTo(view[4..]);
        }

        return arena.AddVarBinView(dtype, values.Length, Validity.NonNullable, views, [heap]);
    }
}
