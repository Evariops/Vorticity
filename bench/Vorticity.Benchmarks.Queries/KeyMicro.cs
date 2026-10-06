using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Expressions;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// A key's groups found alone, in memory (<c>--micro keys</c>): the bench's key columns, as a scan
/// hands them, batch after batch, to <see cref="FixedKeys{TValue}"/>.Assign, with the statistics'
/// bounds, which number a key by its value when they allow, and without them, which hashes it. No
/// file, no decode, no aggregate: what a change to the keys does, in seconds.
/// </summary>
internal static class KeyMicro
{
    private const int BatchRows = 8_192;

    internal static int Run(int rounds)
    {
        Console.WriteLine($"{"keys",-28} {"rows",11} {"groups",10} {"bounds ms",10} {"hashed ms",10} {"ratio",7}");
        Measure("random 1e6", 20_000_000, row => (int)((Mix(Mix((ulong)row)) >> 32) % 1_000_000), rounds);
        Measure("random 1e7", 20_000_000, row => (int)(Mix(Mix(Mix((ulong)row))) % 10_000_000), rounds);
        Measure("tenfold", 20_000_000, row => (int)((Mix(Mix(Mix((ulong)row))) >> 32) % 2_000_000), rounds);
        Measure("ordered 1e7", 20_000_000, row => row % 10_000_000, rounds);
        Measure("drift", 8_000_000, Drift, rounds);
        Measure("unique (hashed both)", 20_000_000, row => unchecked((int)((uint)row * 2_654_435_761u)), rounds);
        return 0;
    }

    /// <summary>The best of <paramref name="rounds"/> passes over the keys, with the bounds and without.</summary>
    private static void Measure(string name, int rows, Func<int, int> key, int rounds)
    {
        int[] keys = new int[rows];
        int min = int.MaxValue;
        int max = int.MinValue;
        for (int row = 0; row < rows; row++)
        {
            keys[row] = key(row);
            min = Math.Min(min, keys[row]);
            max = Math.Max(max, keys[row]);
        }

        KeyBounds bounds = new KeyBounds(min, max);
        double bounded = double.MaxValue;
        double hashed = double.MaxValue;
        int groups = 0;
        for (int round = 0; round < rounds; round++)
        {
            (double ms, int count) = Pass(keys, bounds, rows);
            bounded = Math.Min(bounded, ms);
            groups = count;
            hashed = Math.Min(hashed, Pass(keys, null, rows).Ms);
        }

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{name,-28} {rows,11:N0} {groups,10:N0} {bounded,10:F1} {hashed,10:F1} {bounded / hashed,7:F3}"));
    }

    /// <summary>One pass: every batch's keys a canonical node of an arena, assigned to groups.</summary>
    private static (double Ms, int Groups) Pass(int[] keys, KeyBounds? bounds, int rows)
    {
        GC.Collect();
        RetainingArena arena = new RetainingArena();
        DType dtype = new DTypeArena().Primitive(PType.I32, Nullability.NonNullable);
        ColumnShape shape = new ColumnShape(new ColumnSym(Expr.Field("k"), VortexType.Int32, null, null, -1, []));
        FixedKeys<int> groups = new FixedKeys<int>(shape, sorted: false, bounds, rows: rows);
        ulong[] selection = [];
        int[] rowGroups = new int[BatchRows];
        GroupRanges ranges = new GroupRanges();
        long start = Stopwatch.GetTimestamp();
        for (int at = 0; at < keys.Length; at += BatchRows)
        {
            int count = Math.Min(BatchRows, keys.Length - at);
            arena.ResetKeepingBlocks();
            VortexBuffer buffer = arena.Allocate(count * sizeof(int), sizeof(int), out Span<byte> bytes);
            keys.AsSpan(at, count).CopyTo(MemoryMarshal.Cast<byte, int>(bytes));
            int node = arena.AddPrimitive(dtype, count, Validity.NonNullable, PType.I32, buffer);
            ranges.Clear();
            groups.Assign(arena, [node], count, selection, rowGroups, ranges);
        }

        double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        arena.Reset();
        return (ms, groups.Count);
    }

    /// <summary>The bench's hot keys that change every sixteenth of the rows, half the rows, and a million others.</summary>
    private static int Drift(int row)
    {
        int phase = 8_000_000 / 16;
        ulong b = Mix(Mix((ulong)row));
        return (b & 1) == 0 ? (row / phase * 1_000) + (int)((b >> 1) % 1_000) : 1_000_000 + (int)((b >> 32) % 1_000_000);
    }

    private static ulong Mix(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }
}
