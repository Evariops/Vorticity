// What a column sheet says of a column, built as the writer builds it.
//
// WHAT IS HELD: a column whose values repeat keeps its most frequent values with their exact rows and a filter that finds
// every value it holds and few it does not; one whose values hardly repeat keeps neither; nulls are counted and never
// hashed; past 2^20 distinct values the registers estimate them within 5 %; a sheet weighs about 4.5 KB plus 1.125
// bytes a distinct value, within 10 %; and what a sheet writes reads back the same.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Indexes;

public sealed class ColumnSheetTests
{
    [Fact]
    public void AColumnWhoseValuesRepeatKeepsItsFrequentValuesAndAFilter()
    {
        // Value v held 4 + v % 7 times: 10 000 distinct values over about 70 000 rows.
        List<long> values = [];
        for (long v = 0; v < 10_000; v++)
        {
            for (int i = 0; i < 4 + (v % 7); i++)
            {
                values.Add(v);
            }
        }

        new Random(23).Shuffle(CollectionsMarshal.AsSpan(values));
        ColumnSheet sheet = Sheet(values, nulls: 0);

        Assert.True(sheet.IsExact);
        Assert.Equal(10_000, sheet.Distinct);
        Assert.Equal(values.Count, sheet.Rows);
        Assert.Equal(0, sheet.Nulls);
        Assert.Equal(ColumnSheet.TopValues, sheet.Top.Count);

        // The most frequent are those held ten times, ties in the order of their bytes.
        IEnumerable<(long Value, long Rows)> expected = values.GroupBy(v => v)
            .Select(g => (g.Key, (long)g.Count()))
            .OrderByDescending(g => g.Item2).ThenBy(g => BitConverter.GetBytes(g.Key), Comparer<byte[]>.Create((l, r) => l.AsSpan().SequenceCompareTo(r)))
            .Take(ColumnSheet.TopValues);
        Assert.Equal(expected, sheet.Top.Select(t => (BitConverter.ToInt64(t.Value), t.Rows)));
        Assert.All(sheet.Top, t => Assert.Equal(0, t.Error));

        Assert.NotNull(sheet.Filter);
        DType i64 = new DTypeArena().Primitive(PType.I64, Nullability.NonNullable);
        for (long v = 0; v < 10_000; v++)
        {
            Assert.True(sheet.MayContain(FilterLiteral.From(v), i64), $"{v} is not found");
        }

        int claimed = 0;
        for (long v = 10_000; v < 110_000; v++)
        {
            claimed += sheet.MayContain(FilterLiteral.From(v), i64) ? 1 : 0;
        }

        Assert.True(claimed < 1_000, $"{claimed} of 100 000 values the column lacks are found");
    }

    [Fact]
    public void AColumnWhoseValuesHardlyRepeatKeepsNeitherFrequentValuesNorAFilter()
    {
        List<long> values = [.. Enumerable.Range(0, 30_000).Select(v => (long)v * 7_919)];
        ColumnSheet sheet = Sheet(values, nulls: 0);
        Assert.True(sheet.IsExact);
        Assert.Equal(30_000, sheet.Distinct);
        Assert.Empty(sheet.Top);
        Assert.Null(sheet.Filter);
        Assert.True(sheet.MayContain(FilterLiteral.From(-1L), new DTypeArena().Primitive(PType.I64, Nullability.NonNullable)));
    }

    [Fact]
    public void NullsAreCountedAndNeverHashed()
    {
        List<long> values = [.. Enumerable.Range(0, 4_000).Select(v => (long)(v % 10))];
        ColumnSheet sheet = Sheet(values, nulls: 1_000);
        Assert.Equal(5_000, sheet.Rows);
        Assert.Equal(1_000, sheet.Nulls);
        Assert.Equal(10, sheet.Distinct);
        Assert.Equal(400, sheet.Top[0].Rows);
    }

    [Fact]
    public void PastTwoToTheTwentyDistinctValuesTheRegistersEstimateThem()
    {
        const int Distinct = 1_200_000;
        List<long> values = [.. Enumerable.Range(0, Distinct).Select(v => (long)v)];
        ColumnSheet sheet = Sheet(values, nulls: 0);
        Assert.False(sheet.IsExact);
        Assert.InRange(sheet.Distinct, Distinct * 0.95, Distinct * 1.05);
        Assert.Empty(sheet.Top);
        Assert.Null(sheet.Filter);
    }

    [Fact]
    public void ASheetWeighsAboutFourAndAHalfKilobytesPlusAByteAndAnEighthADistinctValue()
    {
        const int Distinct = 1 << 17;
        List<long> values = [];
        for (long v = 0; v < Distinct; v++)
        {
            for (int i = 0; i < 4; i++)
            {
                values.Add(v * 104_729);
            }
        }

        new Random(29).Shuffle(CollectionsMarshal.AsSpan(values));
        byte[] bytes = Bytes(values, nulls: 0);
        double formula = 4_500 + (1.125 * Distinct);
        Assert.InRange(bytes.Length, formula * 0.9, formula * 1.1);
    }

    [Fact]
    public void WhatASheetWritesReadsBackTheSame()
    {
        List<long> values = [.. Enumerable.Range(0, 50_000).Select(v => (long)(v % 3_000))];
        byte[] bytes = Bytes(values, nulls: 17);
        ColumnSheet sheet = ColumnSheet.From(bytes);
        Assert.Equal(bytes, sheet.ToBytes());
        Assert.Equal(50_017, sheet.Rows);
        Assert.Equal(17, sheet.Nulls);
        Assert.Equal(3_000, sheet.Distinct);
        Assert.InRange(sheet.Estimate(), 3_000 * 0.95, 3_000 * 1.05);
        Assert.Throws<VortexFormatException>(() => ColumnSheet.From(bytes.AsSpan(0, 40)));
        bytes[0] = 9;
        Assert.Throws<VortexFormatException>(() => ColumnSheet.From(bytes));
    }

    private static ColumnSheet Sheet(List<long> values, int nulls) => ColumnSheet.From(Bytes(values, nulls));

    /// <summary>The sheet of an i64 column of <paramref name="values"/> and then <paramref name="nulls"/> nulls, fed in parts as a writer feeds it.</summary>
    private static byte[] Bytes(List<long> values, int nulls)
    {
        ColumnSheetBuilder builder = new ColumnSheetBuilder();
        DTypeArena types = new DTypeArena();
        DType i64 = types.Primitive(PType.I64, Nullability.Nullable);
        const int Part = 8_192;
        for (int start = 0; start < values.Count; start += Part)
        {
            int count = Math.Min(Part, values.Count - start);
            CanonicalArena arena = new CanonicalArena();
            VortexBuffer buffer = arena.Allocate(count * sizeof(long), sizeof(long), out Span<byte> bytes);
            CollectionsMarshal.AsSpan(values).Slice(start, count).CopyTo(MemoryMarshal.Cast<byte, long>(bytes));
            builder.Accumulate(arena, arena.AddPrimitive(i64, count, Validity.NonNullable, PType.I64, buffer), 0, count);
        }

        if (nulls > 0)
        {
            CanonicalArena arena = new CanonicalArena();
            VortexBuffer buffer = arena.Allocate(nulls * sizeof(long), sizeof(long), out _);
            builder.Accumulate(arena, arena.AddPrimitive(i64, nulls, Validity.AllInvalid, PType.I64, buffer), 0, nulls);
        }

        return builder.Finish()!;
    }
}
