// What truncating an instant to a calendar unit, or in a zone, costs a value.
//
// 1,048,576 instants in microseconds, over a year in time order, or over fifty years in no order, truncated to a month
// as stored, to a month in Paris, or to an hour in Paris: the calendar's arithmetic for every value, and the zone's
// offset before and after it.
using System;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A truncation of instants to a calendar unit, or in a zone, over a block of a million values.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditTruncateBenchmarks
{
    private const int Rows = 1 << 20;

    private CanonicalArena _arena = null!;
    private CanonicalArena _work = null!;
    private InstantTruncate _function = null!;
    private VortexBuffer _values;
    private DType _dtype;

    /// <summary>The truncation: a month as stored, a month in Paris, an hour in Paris.</summary>
    [Params("month", "month-paris", "hour-paris")]
    public string Unit { get; set; } = "month";

    /// <summary>Whether the instants come in time order over a year, or in none over fifty.</summary>
    [Params(false, true)]
    public bool Sorted { get; set; }

    /// <summary>Draws the instants into a block.</summary>
    [GlobalSetup]
    public void Setup()
    {
        const long Start = 1_700_000_000_000_000;
        const long Year = 365L * 86_400 * 1_000_000;
        Random random = new Random(43);
        _arena = new CanonicalArena();
        _values = _arena.Allocate(Rows * sizeof(long), sizeof(long), out Span<byte> bytes);
        Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
        for (int i = 0; i < Rows; i++)
        {
            values[i] = Sorted ? Start + (Year / Rows * i) : Start + random.NextInt64(-25 * Year, 25 * Year);
        }

        TimeZoneInfo paris = TimeZoneInfo.TryFindSystemTimeZoneById("Europe/Paris", out TimeZoneInfo? iana)
            ? iana
            : TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time");
        _function = Unit switch
        {
            "month" => new InstantTruncate(CalendarUnit.Month, TimeUnit.Microseconds, null),
            "month-paris" => new InstantTruncate(CalendarUnit.Month, TimeUnit.Microseconds, paris),
            _ => new InstantTruncate(CalendarUnit.Hour, TimeUnit.Microseconds, paris),
        };

        _dtype = new DTypeArena().Primitive(PType.I64, Nullability.NonNullable);
        _work = new CanonicalArena();
    }

    /// <summary>The block's truncations, the middle one read.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public long Truncate()
    {
        _work.ResetKeepingBlocks();
        int column = _work.AddPrimitive(_dtype, Rows, Validity.NonNullable, PType.I64, _values);
        CanonicalNode node = _work.GetNode(_function.Apply(_work, column));
        return MemoryMarshal.Cast<byte, long>(node.Values.Span)[Rows / 2];
    }
}
