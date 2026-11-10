// Audit VW-11 (found by VW-01's trace of a compaction) and VW-08: what a column's sheet costs as the writer feeds it, the
// exact counts of its distinct values kept by a table probed for every row, and then finished. A column of 327,680 longs
// (an object of a compaction), fed a block of 8,192 rows at a time, drawn among 1,000 values (a table that stays in the
// first caches), 80,000 (few enough, a quarter of the rows, that the sheet lists its frequent values), 100,000 (a table of
// some megabytes, as a column of sizes) or 600,000 (tens of megabytes). Then the sheet finished: its frequent values and
// its filter, for a column whose values repeat.
using System;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A column's sheet fed and finished.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditSheetAccumulateBenchmarks
{
    private const int Rows = 327_680;
    private const int Block = 8_192;

    private CanonicalArena _arena = null!;
    private int _node;

    /// <summary>The distinct values the rows are drawn among.</summary>
    [Params(1_000, 80_000, 100_000, 600_000)]
    public int Distinct { get; set; }

    /// <summary>Draws the column.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(Distinct);
        _arena = new CanonicalArena();
        DType i64 = new DTypeArena().Primitive(PType.I64, Nullability.NonNullable);
        VortexBuffer buffer = _arena.Allocate(Rows * sizeof(long), sizeof(long), out Span<byte> bytes);
        Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
        for (int i = 0; i < Rows; i++)
        {
            values[i] = 100 + random.Next(Distinct);
        }

        _node = _arena.AddPrimitive(i64, Rows, Validity.NonNullable, PType.I64, buffer);
        if (Accumulate() == 0 || Finish() == 0)
        {
            throw new InvalidOperationException("The sheet counted nothing.");
        }
    }

    /// <summary>The column fed a block at a time; the distinct values counted.</summary>
    [Benchmark]
    public int Accumulate()
    {
        using ColumnSheetBuilder sheet = new ColumnSheetBuilder();
        Feed(sheet);
        return sheet.Distinct;
    }

    /// <summary>The column fed, then the sheet finished; its bytes.</summary>
    [Benchmark]
    public int Finish()
    {
        using ColumnSheetBuilder sheet = new ColumnSheetBuilder();
        Feed(sheet);
        return sheet.Finish()?.Length ?? 0;
    }

    private void Feed(ColumnSheetBuilder sheet)
    {
        for (int start = 0; start < Rows; start += Block)
        {
            sheet.Accumulate(_arena, _node, start, Block);
        }
    }
}
