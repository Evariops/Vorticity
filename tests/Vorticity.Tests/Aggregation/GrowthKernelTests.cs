using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// The kernels of a growth's step: the stretch of rising points found by vectors and by the scalar twin alike, and a group's
/// ranges folded a stretch at a time to the same bits as its rows folded one by one. In the scalar pass too
/// (<c>tests/scalar-pass.txt</c>), where the vectors are not accelerated.
/// </summary>
public sealed class GrowthKernelTests
{
    [Fact]
    public void TheVectorsAndTheScalarTwinFindTheSameStretch()
    {
        Random random = new Random(41);
        for (int round = 0; round < 2_000; round++)
        {
            int length = random.Next(2, 80);
            long[] order = new long[length];
            long[] signed = new long[length];
            ulong[] unsigned = new ulong[length];
            double[] floats = new double[length];
            for (int i = 0; i < length; i++)
            {
                // Mostly rising, with ties, falls of the order or of the value, and a float's NaN, infinities and −0.
                order[i] = i == 0 ? 0 : order[i - 1] + (random.Next(30) == 0 ? -1 : random.Next(3) == 0 ? 0 : 1);
                signed[i] = i == 0 ? -5 : signed[i - 1] + (random.Next(25) == 0 ? -3 : random.Next(3));
                unsigned[i] = i == 0 ? 7 : unchecked(unsigned[i - 1] + (ulong)(random.Next(25) == 0 ? -2 : random.Next(3)));
                floats[i] = random.Next(40) switch
                {
                    0 => double.NaN,
                    1 => double.PositiveInfinity,
                    2 => -0.0,
                    3 => -1,
                    _ => i == 0 ? 0 : Math.Abs(floats[i - 1]) is double f && double.IsFinite(f) ? f + random.Next(3) : i,
                };
            }

            int from = random.Next(1, length);
            int to = random.Next(from, length + 1);
            Assert.Equal(GrowthKernels.RisingScalar<long>(order, signed, from, to, floats: false), GrowthKernels.Rising<long>(order, signed, from, to, floats: false));
            Assert.Equal(GrowthKernels.RisingScalar<ulong>(order, unsigned, from, to, floats: false), GrowthKernels.Rising<ulong>(order, unsigned, from, to, floats: false));
            Assert.Equal(GrowthKernels.RisingScalar<double>(order, floats, from, to, floats: true), GrowthKernels.Rising<double>(order, floats, from, to, floats: true));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(64)]
    public void RangesFoldToTheBitsOfTheirRows(int groups)
    {
        // The rows of each group side by side, rising with ties, falls, NaNs and infinities, then folded by ranges and by rows.
        Random random = new Random(43 + groups);
        int rows = 20_000;
        long[] order = new long[rows];
        double[] values = new double[rows];
        int[] rowGroups = new int[rows];
        for (int row = 0; row < rows; row++)
        {
            int group = row * groups / rows;
            bool opens = row == 0 || rowGroups[row - 1] != group;
            rowGroups[row] = group;
            order[row] = opens ? 0 : order[row - 1] + (random.Next(50) == 0 ? -2 : random.Next(4) == 0 ? 0 : 1);
            double before = opens || !double.IsFinite(values[row - 1]) ? 100 : values[row - 1];
            values[row] = random.Next(60) switch
            {
                0 => double.NaN,
                1 => double.NegativeInfinity,
                2 => random.Next(10) * 0.5,
                3 => before - 0.25,
                _ => before + (random.Next(100) * 0.01),
            };
        }

        Assert.Equal(Fold(order, values, rowGroups, groups, ranged: false), Fold(order, values, rowGroups, groups, ranged: true));
    }

    /// <summary>Every group's growth, first and last points, as bits, folded a batch of 4 096 rows at a time.</summary>
    private static string Fold(long[] order, double[] values, int[] rowGroups, int groups, bool ranged)
    {
        GrowthSlot<double, WideIndexedSum, FloatGrowth, double?> growth = new GrowthSlot<double, WideIndexedSum, FloatGrowth, double?>(PType.F64, PType.I64, static s => s.Value);
        growth.EnsureGroups(groups);
        RetainingArena arena = new RetainingArena();
        DTypeArena types = new DTypeArena();
        DType longs = types.Primitive(PType.I64, Nullability.NonNullable);
        DType doubles = types.Primitive(PType.F64, Nullability.NonNullable);
        GroupRanges ranges = new GroupRanges();
        long batch = 0;
        for (int at = 0; at < values.Length; at += 4_096)
        {
            int count = Math.Min(4_096, values.Length - at);
            arena.ResetKeepingBlocks();
            VortexBuffer orderBuffer = arena.Allocate(count * sizeof(long), sizeof(long), out Span<byte> orderBytes);
            order.AsSpan(at, count).CopyTo(MemoryMarshal.Cast<byte, long>(orderBytes));
            int orderNode = arena.AddPrimitive(longs, count, Validity.NonNullable, PType.I64, orderBuffer);
            VortexBuffer valueBuffer = arena.Allocate(count * sizeof(double), sizeof(double), out Span<byte> valueBytes);
            values.AsSpan(at, count).CopyTo(MemoryMarshal.Cast<byte, double>(valueBytes));
            int valueNode = arena.AddPrimitive(doubles, count, Validity.NonNullable, PType.F64, valueBuffer);
            BatchInput input = new BatchInput(++batch, arena, valueNode, count, [], at, orderNode: orderNode);
            ReadOnlySpan<int> batchGroups = rowGroups.AsSpan(at, count);
            if (!ranged)
            {
                growth.StepRows(input, batchGroups);
                continue;
            }

            ranges.Clear();
            int start = 0;
            for (int row = 1; row <= count; row++)
            {
                if (row == count || batchGroups[row] != batchGroups[start])
                {
                    ranges.Add(start, row, batchGroups[start]);
                    start = row;
                }
            }

            growth.StepRanges(input, ranges);
        }

        string[] answers = new string[groups];
        for (int group = 0; group < groups; group++)
        {
            answers[group] = growth.TrySettle(group) ? $"{BitConverter.DoubleToInt64Bits(growth.Result(group) ?? double.NaN):X}" : "crossed";
        }

        arena.Reset();
        return string.Join(" ", answers);
    }
}
