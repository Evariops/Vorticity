// THE property: byte order equals tuple order.
//
// These are the tests that would survive a rewrite of the encoder, and several of them are
// transcribed from vortex-row's own suite at 0.86.1 (tests.rs) so that passing here means passing
// the reference's acceptance criteria and not merely our reading of them. The golden-byte tests
// in RowGoldenBytesTests are the other half of the pair: they fail when a byte changes but the
// order does not, and these fail when the order changes but the bytes look plausible.
using System;
using System.Collections.Generic;
using Vorticity.Types;
using Vorticity.Types.Numerics;
using Vorticity.RowEncoding;
using Xunit;

namespace Vorticity.Tests.RowEncoding;

public sealed class RowOrderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SignedIntegersSortLikeTheirValues(bool descending)
    {
        long[] values = [-5, 0, 5, long.MinValue, long.MaxValue, 7, -7, 1];
        using RowFixture fixture = new RowFixture();
        int column = fixture.Primitive<long>(PType.I64, values);
        byte[][] rows = RowTestHelp.Rows(fixture, column, new RowSortField(descending, nullsFirst: true));

        int[] expected = RowTestHelp.ValueOrder(
            values, (a, b) => descending ? b.CompareTo(a) : a.CompareTo(b));
        Assert.Equal(expected, RowTestHelp.ByteOrder(rows));
    }

    [Fact]
    public void UnsignedIntegersSortLikeTheirValues()
    {
        uint[] values = [0, 1, 100, uint.MaxValue, 42, 17];
        using RowFixture fixture = new RowFixture();
        int column = fixture.Primitive<uint>(PType.U32, values);
        byte[][] rows = RowTestHelp.Rows(fixture, column, RowSortField.Ascending);

        Assert.Equal(
            RowTestHelp.ValueOrder(values, (a, b) => a.CompareTo(b)),
            RowTestHelp.ByteOrder(rows));
    }

    /// <summary>
    /// Every physical type, because the transform differs by signedness and by floatness and a
    /// sweep is the only thing that catches the one case a per-type test forgot.
    /// </summary>
    [Fact]
    public void EveryPrimitiveTypeSortsLikeItsValues()
    {
        AssertOrder<byte>(PType.U8, [0, 1, 127, 128, 255], (a, b) => a.CompareTo(b));
        AssertOrder<ushort>(PType.U16, [0, 1, 32767, 32768, 65535], (a, b) => a.CompareTo(b));
        AssertOrder<uint>(PType.U32, [0, 1, uint.MaxValue], (a, b) => a.CompareTo(b));
        AssertOrder<ulong>(PType.U64, [0, 1, ulong.MaxValue], (a, b) => a.CompareTo(b));
        AssertOrder<sbyte>(PType.I8, [-128, -1, 0, 1, 127], (a, b) => a.CompareTo(b));
        AssertOrder<short>(PType.I16, [-32768, -1, 0, 1, 32767], (a, b) => a.CompareTo(b));
        AssertOrder<int>(PType.I32, [int.MinValue, -1, 0, 1, int.MaxValue], (a, b) => a.CompareTo(b));
        AssertOrder<long>(PType.I64, [long.MinValue, -1, 0, 1, long.MaxValue], (a, b) => a.CompareTo(b));
        AssertOrder<Half>(
            PType.F16,
            [(Half)(-1.5f), (Half)0f, (Half)1.5f, Half.NegativeInfinity, Half.PositiveInfinity],
            (a, b) => a.CompareTo(b));
        AssertOrder<float>(
            PType.F32,
            [-1.5f, 0f, 1.5f, float.NegativeInfinity, float.PositiveInfinity, MathF.PI],
            (a, b) => a.CompareTo(b));
        AssertOrder<double>(
            PType.F64,
            [-1.5, 0.0, 1.5, double.NegativeInfinity, double.PositiveInfinity, Math.PI],
            (a, b) => a.CompareTo(b));
    }

    [Fact]
    public void BoolSortsFalseBeforeTrue()
    {
        bool[] values = [true, false, true, false];
        using RowFixture fixture = new RowFixture();
        int column = fixture.Bool(values);
        byte[][] rows = RowTestHelp.Rows(fixture, column, RowSortField.Ascending);

        Assert.Equal([1, 3, 0, 2], RowTestHelp.ByteOrder(rows));
    }

    /// <summary>
    /// Utf8 ordering, including the prefix case the 32-byte blocks exist for: "ban" must sort
    /// before "banana", and "banana" before "banana_loaf_for_test".
    /// </summary>
    [Fact]
    public void Utf8SortsLexicographically()
    {
        string[] values = ["banana", "apple", "", "cherry", "ban", "banana_loaf_for_test"];
        using RowFixture fixture = new RowFixture();
        byte[]?[] bytes = new byte[]?[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            bytes[i] = RowTestHelp.Utf8(values[i]);
        }

        int column = fixture.VarBin(bytes);
        byte[][] rows = RowTestHelp.Rows(fixture, column, RowSortField.Ascending);

        Assert.Equal(
            RowTestHelp.ValueOrder(values, string.CompareOrdinal),
            RowTestHelp.ByteOrder(rows));
    }

    /// <summary>
    /// Prefix ordering across block boundaries, which is exactly what the marker byte buys: a
    /// value ending on a block boundary must still sort below one that continues.
    /// </summary>
    [Fact]
    public void PrefixesSortBeforeTheirExtensionsAcrossBlockBoundaries()
    {
        string[] values =
        [
            new string('a', 31),
            new string('a', 32),
            new string('a', 33),
            new string('a', 64),
            new string('a', 65),
            new string('a', 31) + "b",
        ];

        using RowFixture fixture = new RowFixture();
        byte[]?[] bytes = new byte[]?[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            bytes[i] = RowTestHelp.Utf8(values[i]);
        }

        int column = fixture.VarBin(bytes);
        byte[][] rows = RowTestHelp.Rows(fixture, column, RowSortField.Ascending);

        Assert.Equal(
            RowTestHelp.ValueOrder(values, string.CompareOrdinal),
            RowTestHelp.ByteOrder(rows));
    }

    /// <summary>
    /// Descending inverts the empty and non-empty sentinels together, so a non-empty value sorts
    /// BEFORE an empty one - the inverse of ascending, which is the point.
    /// </summary>
    [Fact]
    public void DescendingPutsNonEmptyBeforeEmpty()
    {
        using RowFixture fixture = new RowFixture();
        int column = fixture.VarBin([RowTestHelp.Utf8("a"), [], RowTestHelp.Utf8("abc")]);
        byte[][] rows = RowTestHelp.Rows(
            fixture, column, new RowSortField(descending: true, nullsFirst: true));

        Assert.Equal([2, 0, 1], RowTestHelp.ByteOrder(rows));
    }

    /// <summary>
    /// The regression the three-sentinel scheme was introduced for: an empty value followed by a
    /// second column must not collide with a value that begins with a NUL byte.
    /// </summary>
    [Fact]
    public void EmptyAndNulByteStringStayDistinctAcrossColumns()
    {
        using RowFixture fixture = new RowFixture();
        int text = fixture.VarBin([[], [0], RowTestHelp.Utf8("a"), RowTestHelp.Utf8("ab")]);
        int tag = fixture.Primitive<int>(PType.I32, [1, 1, 1, 1]);
        byte[][] rows = RowTestHelp.Rows(
            fixture, [text, tag], RowSortField.Ascending, RowSortField.Ascending);

        Assert.Equal([0, 1, 2, 3], RowTestHelp.ByteOrder(rows));
    }

    /// <summary>Null, empty and non-empty are three distinct classes at byte 0.</summary>
    [Fact]
    public void NullEmptyAndNonEmptyAreThreeClasses()
    {
        using RowFixture fixture = new RowFixture();
        int text = fixture.VarBin([null, [], RowTestHelp.Utf8("a"), null, []]);
        int tag = fixture.Primitive<int>(PType.I32, [1, 1, 1, 1, 1]);
        byte[][] rows = RowTestHelp.Rows(
            fixture, [text, tag], RowSortField.Ascending, RowSortField.Ascending);

        Assert.Equal(rows[0], rows[3]);
        Assert.Equal(rows[1], rows[4]);
        int[] order = RowTestHelp.ByteOrder(rows);
        Assert.Equal(0x00, rows[order[0]][0]);
        Assert.Equal(0x00, rows[order[1]][0]);
        Assert.Equal(0x01, rows[order[2]][0]);
        Assert.Equal(0x01, rows[order[3]][0]);
        Assert.Equal(0x02, rows[order[4]][0]);
    }

    [Fact]
    public void MultipleColumnsSortAsATuple()
    {
        int[] ints = [1, 2, 1, 2, 1, 3];
        string[] texts = ["b", "a", "a", "b", "c", "z"];
        using RowFixture fixture = new RowFixture();
        byte[]?[] bytes = new byte[]?[texts.Length];
        for (int i = 0; i < texts.Length; i++)
        {
            bytes[i] = RowTestHelp.Utf8(texts[i]);
        }

        int first = fixture.Primitive<int>(PType.I32, ints);
        int second = fixture.VarBin(bytes);
        byte[][] rows = RowTestHelp.Rows(
            fixture, [first, second], RowSortField.Ascending, RowSortField.Ascending);

        int[] expected = RowTestHelp.ValueOrder(
            Indices(ints.Length),
            (a, b) =>
            {
                int c = ints[a].CompareTo(ints[b]);
                return c != 0 ? c : string.CompareOrdinal(texts[a], texts[b]);
            });

        Assert.Equal(expected, RowTestHelp.ByteOrder(rows));
    }

    /// <summary>
    /// Per-column directions are independent: column 0 ascending and column 1 descending gives
    /// the tuple order a SQL <c>ORDER BY a ASC, b DESC</c> would.
    /// </summary>
    [Fact]
    public void ColumnsCarryTheirOwnDirection()
    {
        int[] ints = [1, 1, 2, 2];
        int[] scores = [10, 20, 5, 7];
        using RowFixture fixture = new RowFixture();
        int first = fixture.Primitive<int>(PType.I32, ints);
        int second = fixture.Primitive<int>(PType.I32, scores);
        byte[][] rows = RowTestHelp.Rows(
            fixture, [first, second],
            RowSortField.Ascending,
            new RowSortField(descending: true, nullsFirst: true));

        int[] expected = RowTestHelp.ValueOrder(
            Indices(ints.Length),
            (a, b) =>
            {
                int c = ints[a].CompareTo(ints[b]);
                return c != 0 ? c : scores[b].CompareTo(scores[a]);
            });

        Assert.Equal(expected, RowTestHelp.ByteOrder(rows));
    }

    /// <summary>
    /// Null placement is independent of direction: all four combinations put the nulls where they
    /// were asked for, and flipping the direction never moves them.
    /// </summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void NullPlacementIsIndependentOfDirection(bool descending, bool nullsFirst)
    {
        using RowFixture fixture = new RowFixture();
        int column = fixture.Primitive<int>(PType.I32, [5, 0, 1, 0, 3], [true, false, true, false, true]);
        byte[][] rows = RowTestHelp.Rows(
            fixture, column, new RowSortField(descending, nullsFirst));

        int[] order = RowTestHelp.ByteOrder(rows);
        bool[] isNull = [false, true, false, true, false];
        if (nullsFirst)
        {
            Assert.True(isNull[order[0]]);
            Assert.True(isNull[order[1]]);
        }
        else
        {
            Assert.True(isNull[order[^1]]);
            Assert.True(isNull[order[^2]]);
        }

        // The non-null rows keep the requested direction whatever the null placement is.
        int[] values = [5, 0, 1, 0, 3];
        List<int> nonNull = [];
        foreach (int i in order)
        {
            if (!isNull[i])
            {
                nonNull.Add(values[i]);
            }
        }

        Assert.Equal(descending ? new[] { 5, 3, 1 } : [1, 3, 5], nonNull);
    }

    /// <summary>
    /// One logical decimal column whose chunks compressed to different physical widths must still
    /// produce comparable keys, because the key width comes from the declared precision.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DecimalKeysCompareAcrossPhysicalWidths(bool descending)
    {
        using RowFixture fixture = new RowFixture();
        RowSortField field = new RowSortField(descending, nullsFirst: true);
        int narrow = fixture.Decimal([91, -5], precision: 7, scale: 5, DecimalStorageType.I8);
        int wide = fixture.Decimal([484, 300], precision: 7, scale: 5, DecimalStorageType.I16);

        byte[][] first = RowTestHelp.Rows(fixture, narrow, field);
        byte[][] second = RowTestHelp.Rows(fixture, wide, field);
        byte[][] all = [first[0], first[1], second[0], second[1]];
        int[] values = [91, -5, 484, 300];

        int[] expected = RowTestHelp.ValueOrder(
            Indices(values.Length),
            (a, b) => descending ? values[b].CompareTo(values[a]) : values[a].CompareTo(values[b]));
        Assert.Equal(expected, RowTestHelp.ByteOrder(all));
    }

    /// <summary>A null decimal slot's backing bytes are unspecified and must not be validated.</summary>
    [Fact]
    public void ADecimalNullSlotMayHoldAValueTooWideForTheKey()
    {
        using RowFixture fixture = new RowFixture();
        int column = fixture.Decimal(
            [484, 10_000_000_000_000, 91], precision: 7, scale: 5, DecimalStorageType.I64,
            [true, false, true]);
        byte[][] rows = RowTestHelp.Rows(fixture, column, RowSortField.Ascending);

        int[] order = RowTestHelp.ByteOrder(rows);
        Assert.Equal([1, 2, 0], order);
    }

    private static int[] Indices(int count)
    {
        int[] indices = new int[count];
        for (int i = 0; i < count; i++)
        {
            indices[i] = i;
        }

        return indices;
    }

    private static void AssertOrder<T>(PType ptype, T[] values, Comparison<T> comparison)
        where T : unmanaged
    {
        using RowFixture fixture = new RowFixture();
        int column = fixture.Primitive<T>(ptype, values);
        byte[][] rows = RowTestHelp.Rows(fixture, column, RowSortField.Ascending);
        Assert.Equal(RowTestHelp.ValueOrder(values, comparison), RowTestHelp.ByteOrder(rows));
    }
}
