// Audit VW-04 and VW-05: what a column's counts cost as the writer feeds them, each row of the counted column folding every
// measure into its value's entry and adding to its sketches; and what a measure's granule statistics cost, its values
// folded into the granule's record and its histogram. 327,680 rows (an object of a compaction) fed by blocks of 8,192, a
// granule each: an instant, the counted key (20 values), an amount (a long), a size (an int, one row in ten null) and a
// price (a double).
using System;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A column's counts fed and finished.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditCountsAccumulateBenchmarks
{
    private const int Rows = 327_680;
    private const int Block = 8_192;

    private CountsPlan _plan = null!;
    private DType _amount;
    private DType _size;
    private CanonicalArena[] _arenas = [];
    private int[][] _nodes = [];

    /// <summary>Draws the blocks.</summary>
    [GlobalSetup]
    public void Setup()
    {
        VortexSchema schema = VortexSchema.Create([
            new VortexField("At", VortexType.Int64),
            new VortexField("Key", VortexType.Int64),
            new VortexField("Amount", VortexType.Int64),
            new VortexField("Size", VortexType.Int32.Nullable),
            new VortexField("Price", VortexType.Float64)]);
        DTypeArena types = new DTypeArena();
        DType dtype = VortexTypes.ToDType(schema, types);
        _plan = CountsPlan.For(dtype)!;
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType i32 = types.Primitive(PType.I32, Nullability.Nullable);
        (_amount, _size) = (i64, i32);
        DType f64 = types.Primitive(PType.F64, Nullability.NonNullable);
        Random random = new Random(43);
        _arenas = new CanonicalArena[Rows / Block];
        _nodes = new int[Rows / Block][];
        for (int b = 0; b < _arenas.Length; b++)
        {
            CanonicalArena arena = _arenas[b] = new CanonicalArena();
            _nodes[b] =
            [
                Longs(arena, i64, i => (b * Block) + i),
                Longs(arena, i64, _ => random.Next(20)),
                Longs(arena, i64, _ => random.Next(1, 100_000)),
                Ints(arena, i32, types, random),
                Doubles(arena, f64, random),
            ];
        }

        if (Accumulate() == 0)
        {
            throw new InvalidOperationException("The counts hold nothing.");
        }
    }

    /// <summary>The key's counts fed every block, then finished; their bytes.</summary>
    [Benchmark]
    public int Accumulate()
    {
        using ColumnSheetBuilder sheet = new ColumnSheetBuilder();
        using ColumnCountsBuilder counts = new ColumnCountsBuilder(sheet, _plan, field: 1);
        for (int b = 0; b < _arenas.Length; b++)
        {
            _plan.Prepare(_arenas[b], _nodes[b], Block);
            sheet.Accumulate(_arenas[b], _nodes[b][1], 0, Block);
            counts.Accumulate(_arenas[b], _nodes[b][1], 0, Block);
        }

        counts.Settle();
        return counts.Finish() is { } finished ? finished.Counts.Length : 0;
    }

    /// <summary>The granule statistics of the amount, every row valid, or of the size, one in ten null; their bytes.</summary>
    /// <param name="field">2 for the amount, 3 for the size.</param>
    [Benchmark]
    [Arguments(2)]
    [Arguments(3)]
    public int Stats(int field)
    {
        using ColumnSheetBuilder sheet = new ColumnSheetBuilder();
        using GranuleStatsBuilder stats = new GranuleStatsBuilder(sheet, _plan, field, field == 2 ? _amount : _size);
        for (int b = 0; b < _arenas.Length; b++)
        {
            _plan.Prepare(_arenas[b], _nodes[b], Block);
            sheet.Accumulate(_arenas[b], _nodes[b][field], 0, Block);
            stats.Accumulate(_arenas[b], _nodes[b][field], 0, Block);
            stats.CloseBlock();
        }

        return stats.Finish(Rows) is { } finished ? finished.Statistics.Length : 0;
    }

    /// <summary>The sheet alone the statistics are fed after, which <see cref="Stats"/> pays as well; its distinct values.</summary>
    /// <param name="field">2 for the amount, 3 for the size.</param>
    [Benchmark]
    [Arguments(2)]
    [Arguments(3)]
    public int Sheet(int field)
    {
        using ColumnSheetBuilder sheet = new ColumnSheetBuilder();
        sheet.NumberRows();
        for (int b = 0; b < _arenas.Length; b++)
        {
            _plan.Prepare(_arenas[b], _nodes[b], Block);
            sheet.Accumulate(_arenas[b], _nodes[b][field], 0, Block);
        }

        return sheet.Distinct;
    }

    private static int Longs(CanonicalArena arena, DType dtype, Func<int, long> value)
    {
        VortexBuffer buffer = arena.Allocate(Block * sizeof(long), sizeof(long), out Span<byte> bytes);
        Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
        for (int i = 0; i < Block; i++)
        {
            values[i] = value(i);
        }

        return arena.AddPrimitive(dtype, Block, Validity.NonNullable, PType.I64, buffer);
    }

    private static int Ints(CanonicalArena arena, DType dtype, DTypeArena types, Random random)
    {
        VortexBuffer buffer = arena.Allocate(Block * sizeof(int), sizeof(int), out Span<byte> bytes);
        Span<int> values = MemoryMarshal.Cast<byte, int>(bytes);
        VortexBuffer bits = arena.Allocate(Block / 8, 8, out Span<byte> valid);
        valid.Clear();
        for (int i = 0; i < Block; i++)
        {
            values[i] = random.Next(1, 4_096);
            valid[i / 8] |= (byte)(random.Next(10) == 0 ? 0 : 1 << (i % 8));
        }

        int mask = arena.AddBool(types.Bool(Nullability.NonNullable), Block, Validity.NonNullable, bits, 0);
        return arena.AddPrimitive(dtype, Block, Validity.Bitmap(mask), PType.I32, buffer);
    }

    private static int Doubles(CanonicalArena arena, DType dtype, Random random)
    {
        VortexBuffer buffer = arena.Allocate(Block * sizeof(double), sizeof(double), out Span<byte> bytes);
        Span<double> values = MemoryMarshal.Cast<byte, double>(bytes);
        for (int i = 0; i < Block; i++)
        {
            values[i] = random.NextDouble() * 1_000;
        }

        return arena.AddPrimitive(dtype, Block, Validity.NonNullable, PType.F64, buffer);
    }
}
