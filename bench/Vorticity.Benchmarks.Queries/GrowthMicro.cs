using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// A growth over points in order, alone, in memory (<c>--micro growth</c>): a column of instants and a column of counter
/// values folded batch after batch into each row's group, by the slot <c>OrderedBy(…).Increase(…)</c> takes, beside its twin,
/// the least instant and the greatest value of each group, which read the same two columns and compare each row once. No
/// file, no decode, no key: what a change to the growth's step does, in seconds. The checksum of the growths tells two
/// builds apart.
/// </summary>
/// <remarks>
/// The shapes are a counter read every fifteen seconds: by series, a group's points side by side as a dataset sorted by its
/// series then its instant keeps them; by instant, every group's points interleaved, as rows sorted by their instant are. The
/// same groups at 24 points and at 240: the growth's state is the group's, whatever its points. Run it with
/// <c>DOTNET_TieredCompilation=0</c>, so that every pass runs the code at its last tier.
/// </remarks>
internal static class GrowthMicro
{
    /// <summary>The rows of a batch.</summary>
    private const int BatchRows = 65_536;

    internal static int Run(int rounds)
    {
        Console.WriteLine($"{"growth",-34} {"rows",11} {"groups",9} {"growth ms",10} {"twin ms",9} {"ratio",6} {"ns/row",7} {"B/group",8} {"checksum",18}");
        foreach (bool bySeries in (bool[])[true, false])
        {
            foreach (int points in (int[])[24, 240])
            {
                Measure($"{(bySeries ? "by series" : "by instant")}, {points} points a group", 20_000, points, bySeries, rounds);
            }
        }

        return 0;
    }

    private static void Measure(string name, int groups, int points, bool bySeries, int rounds)
    {
        int rows = groups * points;
        long[] instants = new long[rows];
        double[] values = new double[rows];
        int[] rowGroups = new int[rows];
        double[] counters = new double[groups];
        for (int row = 0; row < rows; row++)
        {
            // By series: a group's points one after the other; by instant: a point of every group, then the next.
            int group = bySeries ? row / points : row % groups;
            int step = bySeries ? row % points : row / groups;
            ulong mix = Mix((ulong)row);
            counters[group] = (mix % 400) == 0 ? (mix >> 20) % 50 : counters[group] + ((mix >> 32) % 1_000 * 0.01);
            rowGroups[row] = group;
            instants[row] = step * 15_000_000L;
            values[row] = counters[group];
        }

        double best = double.MaxValue;
        double twin = double.MaxValue;
        long checksum = 0;
        long footprint = 0;
        for (int round = 0; round < rounds; round++)
        {
            GrowthSlot<double, WideIndexedSum, FloatGrowth, double?> growth = new GrowthSlot<double, WideIndexedSum, FloatGrowth, double?>(PType.F64, PType.I64, static s => s.Value);
            (double ms, _) = Pass(instants, values, rowGroups, groups, [growth], bySeries);
            best = Math.Min(best, ms);
            footprint = growth.Footprint;
            checksum = 0;
            for (int group = 0; group < groups; group++)
            {
                if (!growth.TrySettle(group))
                {
                    throw new InvalidOperationException($"group {group}'s runs cross.");
                }

                checksum = (checksum * 31) ^ BitConverter.DoubleToInt64Bits(growth.Result(group) ?? double.NaN);
            }

            growth.Release();
            AggregateSlot first = new FixedSlot<long, ExtremeState<long>, MinOp<long>, long?>(StorageKind.Primitive, static s => s.Has ? s.Value : null);
            AggregateSlot most = new FixedSlot<double, ExtremeState<double>, MaxOp<double>, double?>(StorageKind.Primitive, static s => s.Has ? s.Value : null);
            (_, double twinMs) = Pass(instants, values, rowGroups, groups, [first, most], bySeries);
            twin = Math.Min(twin, twinMs);
        }

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{name,-34} {rows,11:N0} {groups,9:N0} {best,10:F1} {twin,9:F1} {best / twin,6:F2} {best * 1e6 / rows,7:F2} {footprint / groups,8:N0} {checksum,18:X16}"));
    }

    /// <summary>
    /// One pass of <paramref name="slots"/> over every batch: a growth reads the values with the instants as its order; the
    /// twin's first slot reads the instants and its second the values. A group's rows side by side, <paramref name="ranged"/>,
    /// are folded a range at a time, as the pass folds a key in runs. The time of the pass.
    /// </summary>
    private static (double Ms, double TwinMs) Pass(long[] instants, double[] values, int[] rowGroups, int groups, AggregateSlot[] slots, bool ranged)
    {
        GC.Collect();
        GroupRanges ranges = new GroupRanges();
        RetainingArena arena = new RetainingArena();
        DTypeArena types = new DTypeArena();
        DType longs = types.Primitive(PType.I64, Nullability.NonNullable);
        DType doubles = types.Primitive(PType.F64, Nullability.NonNullable);
        foreach (AggregateSlot slot in slots)
        {
            slot.EnsureGroups(groups);
        }

        long start = Stopwatch.GetTimestamp();
        long batch = 0;
        for (int at = 0; at < values.Length; at += BatchRows)
        {
            int count = Math.Min(BatchRows, values.Length - at);
            arena.ResetKeepingBlocks();
            VortexBuffer orderBuffer = arena.Allocate(count * sizeof(long), sizeof(long), out Span<byte> orderBytes);
            instants.AsSpan(at, count).CopyTo(MemoryMarshal.Cast<byte, long>(orderBytes));
            int order = arena.AddPrimitive(longs, count, Validity.NonNullable, PType.I64, orderBuffer);
            VortexBuffer valueBuffer = arena.Allocate(count * sizeof(double), sizeof(double), out Span<byte> valueBytes);
            values.AsSpan(at, count).CopyTo(MemoryMarshal.Cast<byte, double>(valueBytes));
            int value = arena.AddPrimitive(doubles, count, Validity.NonNullable, PType.F64, valueBuffer);
            batch++;
            ReadOnlySpan<int> batchGroups = rowGroups.AsSpan(at, count);
            if (ranged)
            {
                ranges.Clear();
                int runStart = 0;
                for (int row = 1; row <= count; row++)
                {
                    if (row == count || batchGroups[row] != batchGroups[runStart])
                    {
                        ranges.Add(runStart, row, batchGroups[runStart]);
                        runStart = row;
                    }
                }
            }

            for (int s = 0; s < slots.Length; s++)
            {
                BatchInput input = slots.Length == 1
                    ? new BatchInput(batch, arena, value, count, [], at, orderNode: order)
                    : new BatchInput(batch, arena, s == 0 ? order : value, count, [], at);
                if (ranged)
                {
                    slots[s].StepRanges(input, ranges);
                }
                else
                {
                    slots[s].StepRows(input, batchGroups);
                }
            }
        }

        double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        arena.Reset();
        return (ms, ms);
    }

    private static ulong Mix(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }
}
