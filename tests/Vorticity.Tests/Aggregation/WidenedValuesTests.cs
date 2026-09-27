using System;
using System.Runtime.InteropServices;

using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Expressions;
using Vorticity.Layouts;
using Vorticity.Types;
using Vorticity.Types.Numerics;
using Xunit;

namespace Vorticity.Tests.Aggregation;

public sealed class WidenedValuesTests
{
    private const int Rows = 16;

    private const int Ranges = 4;

    private static readonly DTypeArena Types = new DTypeArena();

    private static readonly DType Decimal = Types.Decimal(18, 2, Nullability.NonNullable);

    // Every range of a batch reads the one widening of it, and the next batch widens its own values
    // even when they sit at the same node.
    [Fact]
    public void EachBatchSumsItsOwnDecimalsOverEveryRange()
    {
        long[] first = Values(start: 5, step: 3);
        long[] second = Values(start: -40, step: 7);
        AggregateSlot<Int128> slot = new FixedSlot<Int128, SumState<Int128>, DecimalSum, Int128>(
            StorageKind.Decimal, static s => s.Sum);
        slot.EnsureGroups(Ranges);
        CanonicalArena arena = new CanonicalArena();
        int node = Decimals(arena, first);
        Fold(slot, new BatchInput(1, arena, node, Rows, default));

        arena.Reset();
        Assert.Equal(node, Decimals(arena, second));
        Fold(slot, new BatchInput(2, arena, node, Rows, default));

        for (int g = 0; g < Ranges; g++)
        {
            Assert.Equal(RangeSum(first, g) + RangeSum(second, g), slot.Result(g));
        }
    }

    [Fact]
    public void EachBatchCountsItsOwnDistinctDecimalsOverEveryRange()
    {
        long[] first = Values(start: 5, step: 0);
        long[] second = Values(start: 5, step: 1);
        AggregateSlot<long> slot = new FixedDistinctSlot<Int128>(StorageKind.Decimal);
        slot.EnsureGroups(Ranges);
        CanonicalArena arena = new CanonicalArena();
        int node = Decimals(arena, first);
        Fold(slot, new BatchInput(1, arena, node, Rows, default));

        arena.Reset();
        Assert.Equal(node, Decimals(arena, second));
        Fold(slot, new BatchInput(2, arena, node, Rows, default));

        // The first batch holds one value; the second, four new ones in each range but the first,
        // whose values start at the one the first batch held.
        Assert.Equal(4, slot.Result(0));
        for (int g = 1; g < Ranges; g++)
        {
            Assert.Equal(Rows / Ranges + 1, slot.Result(g));
        }
    }

    // A dictionary of decimals retained for a chunk is widened once for its batches; the arena
    // refilled for the next chunk puts other values under the same codes at the same node, which
    // only the publication tells apart.
    [Fact]
    public void ADecimalDictionaryPublishedAgainGroupsByItsNewValues()
    {
        long[] first = [100, 200, 300, 400];
        long[] second = [400, 300, 100, 200];
        uint[] codes = [0, 1, 2, 3, 3, 2, 1, 0];
        GroupKeys keys = new FixedKeys<Int128>(
            new ColumnShape(new ColumnSym(Expr.Field("k"), VortexType.Decimal(18, 2), null, null, -1, [])),
            sorted: false);
        RetainingArena chunk = new RetainingArena();
        int dictionary = DecimalDictionary(chunk, first, codes);
        chunk.Seal();
        AssertGroupedAs(keys, chunk, dictionary, codes, first);
        AssertGroupedAs(keys, chunk, dictionary, codes, first);

        chunk.Reset();
        Assert.Equal(dictionary, DecimalDictionary(chunk, second, codes));
        chunk.Seal();

        AssertGroupedAs(keys, chunk, dictionary, codes, second);
    }

    private static void Fold<TResult>(AggregateSlot<TResult> slot, in BatchInput input)
    {
        int step = Rows / Ranges;
        for (int g = 0; g < Ranges; g++)
        {
            slot.StepRange(input, g * step, (g + 1) * step, g);
        }
    }

    private static void AssertGroupedAs(GroupKeys keys, CanonicalArena chunk, int dictionary, uint[] codes, long[] values)
    {
        RetainingArena batch = new RetainingArena();
        int node = CanonicalSlice.SliceAcross(chunk, batch, dictionary, 0, codes.Length);
        int[] rowGroups = new int[codes.Length];

        Assert.False(keys.Assign(batch, [node], codes.Length, [(1UL << codes.Length) - 1], rowGroups, new GroupRanges()));

        Func<int, Int128> key = keys.Reader<Int128>(0);
        for (int row = 0; row < codes.Length; row++)
        {
            Assert.Equal((Int128)values[codes[row]], key(rowGroups[row]));
        }
    }

    private static long[] Values(long start, long step)
    {
        long[] values = new long[Rows];
        for (int i = 0; i < Rows; i++)
        {
            values[i] = start + (i * step);
        }

        return values;
    }

    private static Int128 RangeSum(long[] values, int group)
    {
        Int128 sum = 0;
        int step = Rows / Ranges;
        for (int i = group * step; i < (group + 1) * step; i++)
        {
            sum += values[i];
        }

        return sum;
    }

    private static int Decimals(CanonicalArena arena, long[] values)
    {
        VortexBuffer buffer = arena.Allocate(values.Length * sizeof(long), sizeof(long), out Span<byte> bytes);
        values.AsSpan().CopyTo(MemoryMarshal.Cast<byte, long>(bytes));
        return arena.AddDecimal(Decimal, values.Length, Validity.NonNullable, DecimalStorageType.I64, 18, 2, buffer);
    }

    private static int DecimalDictionary(CanonicalArena arena, long[] values, uint[] codes)
    {
        int entries = Decimals(arena, values);
        VortexBuffer buffer = arena.Allocate(codes.Length * sizeof(uint), sizeof(uint), out Span<byte> bytes);
        codes.AsSpan().CopyTo(MemoryMarshal.Cast<byte, uint>(bytes));
        return arena.AddDictionary(Decimal, codes.Length, Validity.NonNullable, buffer, entries);
    }
}
