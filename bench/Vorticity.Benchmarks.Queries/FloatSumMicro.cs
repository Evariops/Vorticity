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
/// A grouped exact float sum alone, in memory (<c>--micro floatsum</c>): a column of doubles folded batch
/// after batch into the <see cref="IndexedSum"/> of each row's group, by the slot a sum or a mean of
/// doubles takes, the rows' groups given. No file, no decode, no key: what a change to the sum does, in
/// seconds. The checksum, the bits of every group's sum folded together, tells two builds' sums apart.
/// </summary>
/// <remarks>
/// The shapes are db-benchmark's: q4's mean of <c>v3</c> by a hundred groups, q3's by a hundred thousand,
/// q10's sum where every row opens its group; and a hundred groups whose values lie in ranges of their
/// own. Run it with <c>DOTNET_TieredCompilation=0</c>, so that every pass runs the code at its last tier.
/// </remarks>
internal static class FloatSumMicro
{
    /// <summary>The rows of a batch: a chunk of the bench's db-benchmark file.</summary>
    private const int BatchRows = 65_536;

    internal static int Run(int rounds)
    {
        Console.WriteLine($"{"float sum",-32} {"rows",11} {"groups",10} {"best ms",10} {"ns/row",8} {"ns/group out",13} {"checksum",18}");
        Measure("1e2 groups, one range (q4)", 10_000_000, 100, mixed: false, rounds);
        Measure("1e5 groups, one range (q3)", 10_000_000, 100_000, mixed: false, rounds);
        Measure("1e2 groups, ranges of their own", 10_000_000, 100, mixed: true, rounds);
        Measure("a group a row (q10)", 2_000_000, 0, mixed: false, rounds);
        return 0;
    }

    /// <summary>The best of <paramref name="rounds"/> passes; <paramref name="groups"/> 0 for a group a row, each row its own in order.</summary>
    private static void Measure(string name, int rows, int groups, bool mixed, int rounds)
    {
        double[] values = new double[rows];
        int[] rowGroups = new int[rows];
        for (int row = 0; row < rows; row++)
        {
            ulong mix = Mix((ulong)row);
            int group = groups == 0 ? row : (int)((mix >> 40) % (ulong)groups);
            rowGroups[row] = group;
            double unit = (mix >> 11) * (1.0 / (1UL << 53));
            values[row] = mixed ? Math.ScaleB(unit + 0.5, (group % 16 * 9) - 70) * ((mix & 1) == 0 ? 1 : -1) : unit * 100;
        }

        double best = double.MaxValue;
        double bestOut = double.MaxValue;
        long checksum = 0;
        int count = groups == 0 ? rows : groups;
        for (int round = 0; round < rounds; round++)
        {
            (double ms, double outMs, long sum) = Pass(values, rowGroups, count);
            best = Math.Min(best, ms);
            bestOut = Math.Min(bestOut, outMs);
            checksum = sum;
        }

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{name,-32} {rows,11:N0} {count,10:N0} {best,10:F1} {best * 1e6 / rows,8:F2} {bestOut * 1e6 / count,13:F2} {checksum,18:X16}"));
    }

    /// <summary>One pass: every batch's values a canonical node of an arena, folded into their groups' sums; then every group's sum read out.</summary>
    private static (double Ms, double OutMs, long Checksum) Pass(double[] values, int[] rowGroups, int groups)
    {
        GC.Collect();
        RetainingArena arena = new RetainingArena();
        DType dtype = new DTypeArena().Primitive(PType.F64, Nullability.NonNullable);
        FixedSlot<double, IndexedSum, IndexedFloatSum<double>, double> slot =
            new FixedSlot<double, IndexedSum, IndexedFloatSum<double>, double>(StorageKind.Primitive, static s => s.Value);
        slot.EnsureGroups(groups);
        long start = Stopwatch.GetTimestamp();
        long batch = 0;
        for (int at = 0; at < values.Length; at += BatchRows)
        {
            int count = Math.Min(BatchRows, values.Length - at);
            arena.ResetKeepingBlocks();
            VortexBuffer buffer = arena.Allocate(count * sizeof(double), sizeof(double), out Span<byte> bytes);
            values.AsSpan(at, count).CopyTo(MemoryMarshal.Cast<byte, double>(bytes));
            int node = arena.AddPrimitive(dtype, count, Validity.NonNullable, PType.F64, buffer);
            slot.StepRows(new BatchInput(++batch, arena, node, count, []), rowGroups.AsSpan(at, count));
        }

        double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        // Every group's sum read out at once, as the result's batches read them.
        int[] order = new int[groups];
        for (int group = 0; group < groups; group++)
        {
            order[group] = group;
        }

        double[] sums = new double[groups];
        long reading = Stopwatch.GetTimestamp();
        slot.Results(order, sums);
        double outMs = Stopwatch.GetElapsedTime(reading).TotalMilliseconds;
        long checksum = 0;
        foreach (double sum in sums)
        {
            checksum = (checksum * 31) ^ BitConverter.DoubleToInt64Bits(sum);
        }

        arena.Reset();
        return (ms, outMs, checksum);
    }

    private static ulong Mix(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }
}
