// The ingest's distinct table probing a fixed-width column: each row's value hashed and looked up,
// a code handed out per distinct value, so the writer can price a dictionary without a pass of its
// own. Every primitive column of three bytes or more of every batch is probed.
//
// 65 536 values among a thousand: in runs of 1 to 16 rows, the shape of a sorted or slowly
// changing column, or in no order at all.
using System;
using System.Collections.Generic;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks;

/// <summary><c>DistinctTable.Probe</c> of 65 536 rows.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
public class DistinctProbeBenchmarks
{
    private const int Rows = 1 << 16;

    [Params("runs", "random")]
    public string Order { get; set; } = "runs";

    [Params("i64", "i32")]
    public string Type { get; set; } = "i64";

    private readonly CanonicalArena _arena = new CanonicalArena();
    private int _node;
    private DistinctTable? _table;

    /// <summary>What one invocation reads: every row's value.</summary>
    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters) =>
        (Rows, Rows * (parameters.TryGetValue(nameof(Type), out object? t) && t is "i32" ? 4L : 8L));

    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(20260927);
        PType ptype = Type == "i32" ? PType.I32 : PType.I64;
        int width = ptype.ByteWidth();
        VortexBuffer values = _arena.Allocate(Rows * width, width, out Span<byte> bytes);
        int at = 0;
        while (at < Rows)
        {
            long value = random.Next(1000);
            int run = Order == "runs" ? random.Next(1, 17) : 1;
            for (int k = 0; k < run && at < Rows; k++, at++)
            {
                if (width == 4)
                {
                    BitConverter.TryWriteBytes(bytes.Slice(at * 4, 4), (int)value);
                }
                else
                {
                    BitConverter.TryWriteBytes(bytes.Slice(at * 8, 8), value);
                }
            }
        }

        _node = _arena.AddPrimitive(new DTypeArena().Primitive(ptype, Nullability.NonNullable), Rows, Validity.NonNullable, ptype, values);
        _table = DistinctTable.For(_arena.GetNode(_node));
    }

    [Benchmark(Description = "distinct probe")]
    public int Probe()
    {
        DistinctTable table = _table!;
        table.Probe(_arena, _arena.GetNode(_node), 0, Rows);
        int distinct = table.FirstRows.Length;
        table.Reset();
        return distinct;
    }
}
