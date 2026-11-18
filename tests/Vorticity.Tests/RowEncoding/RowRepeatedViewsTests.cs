// A text column whose rows repeat a value view that value's bytes once: a run's or a dictionary's decoded rows, a
// constant's, a block gathered from a merge. The encoder takes a row of the view of the last value written as that
// value's key, its bytes copied as they lie. Nothing here knows that rule: each row's key is compared with the key of
// the same value encoded alone, a column of one row, which has no row before it to take anything from.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.RowEncoding;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.RowEncoding;

public sealed class RowRepeatedViewsTests
{
    /// <summary>The values the rows view: inline, a block exactly, past a block, two blocks exactly, and two of one length and one prefix.</summary>
    private static readonly byte[][] Values =
    [
        RowTestHelp.Utf8("short"),
        RowTestHelp.Utf8("twelve bytes"),
        RowTestHelp.Utf8("checkout-service-eu-west"),
        RowTestHelp.Utf8("checkout-service-eu-east"),
        RowTestHelp.Utf8("a value of thirty-two bytes long"),
        RowTestHelp.Utf8("a value long enough to cross the thirty-two byte block boundary"),
        RowTestHelp.Utf8("sixty-four bytes: two whole blocks and nothing left of the last."),
        [],
    ];

    /// <summary>A row's value: an index into <see cref="Values"/>, or -1 for a null row.</summary>
    private const int Null = -1;

    /// <summary>
    /// A text column of rows that view <see cref="Values"/>, each value's bytes held once: two rows of one value hold the
    /// same view. A null row keeps the view of the row before it, which the encoder must not read as a value. With
    /// <paramref name="twice"/>, the values lie twice in the buffer and odd rows view the second copy, so that two rows of
    /// one value hold views that differ.
    /// </summary>
    private static int Shared(RowFixture fixture, ReadOnlySpan<int> rows, bool twice = false)
    {
        List<byte> heap = [];
        int copies = twice ? 2 : 1;
        int[,] at = new int[copies, Values.Length];
        for (int copy = 0; copy < copies; copy++)
        {
            for (int v = 0; v < Values.Length; v++)
            {
                at[copy, v] = heap.Count;
                heap.AddRange(Values[v]);
            }
        }

        byte[] views = new byte[rows.Length * 16];
        bool[] valid = new bool[rows.Length];
        bool anyNull = false;
        for (int i = 0; i < rows.Length; i++)
        {
            Span<byte> view = views.AsSpan(i * 16, 16);
            if (rows[i] == Null)
            {
                anyNull = true;
                if (i > 0)
                {
                    views.AsSpan((i - 1) * 16, 16).CopyTo(view);
                }

                continue;
            }

            valid[i] = true;
            byte[] value = Values[rows[i]];
            BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)value.Length);
            if (value.Length <= 12)
            {
                value.CopyTo(view[4..]);
            }
            else
            {
                value.AsSpan(0, 4).CopyTo(view[4..8]);
                BinaryPrimitives.WriteUInt32LittleEndian(view[8..12], 0);
                BinaryPrimitives.WriteUInt32LittleEndian(view[12..16], (uint)at[twice ? i & 1 : 0, rows[i]]);
            }
        }

        Validity validity = anyNull ? fixture.Validity(valid) : Validity.NonNullable;
        DType dtype = fixture.Types.Utf8(anyNull ? Nullability.Nullable : Nullability.NonNullable);
        Span<VortexBuffer> buffers = [fixture.Bytes(heap.ToArray())];
        return fixture.Arena.AddVarBinView(dtype, rows.Length, validity, fixture.Bytes(views), buffers);
    }

    /// <summary>The key of each row's value encoded alone, a column of one row: no row before it.</summary>
    private static byte[][] Alone(ReadOnlySpan<int> rows, bool nullable, RowSortField field)
    {
        byte[][] keys = new byte[rows.Length][];
        for (int i = 0; i < rows.Length; i++)
        {
            using RowFixture fixture = new RowFixture();
            int column = fixture.VarBin([rows[i] == Null ? null : Values[rows[i]]], forceNullable: nullable);
            keys[i] = RowTestHelp.Rows(fixture, column, field)[0];
        }

        return keys;
    }

    private static readonly int[] Repeats =
    [
        2, 2, 2, 3, 3, 2, 0, 0, 0, 1, 1, 7, 7, 4, 4, 4, 5, 5, 6, 6, 6, 5, 2, 2,
    ];

    private static readonly int[] RepeatsAndNulls =
    [
        2, 2, Null, 2, 2, Null, Null, 3, 0, Null, 0, 7, Null, 7, 5, 5, Null, 5, 6, 4, Null, 4, 4, 1,
    ];

    public static TheoryData<bool, bool> Directions => new()
    {
        { false, true },
        { false, false },
        { true, true },
        { true, false },
    };

    [Theory]
    [MemberData(nameof(Directions))]
    public void ARowThatViewsTheValueBeforeItHoldsTheKeyOfItsValue(bool descending, bool nullsFirst)
    {
        RowSortField field = new RowSortField(descending, nullsFirst);
        using RowFixture fixture = new RowFixture();
        byte[][] keys = RowTestHelp.Rows(fixture, Shared(fixture, Repeats), field);
        Assert.Equal(Alone(Repeats, nullable: false, field), keys);
    }

    [Theory]
    [MemberData(nameof(Directions))]
    public void ANullRowBetweenRepeatsIsNullAndTheRepeatAfterItHoldsItsKey(bool descending, bool nullsFirst)
    {
        RowSortField field = new RowSortField(descending, nullsFirst);
        using RowFixture fixture = new RowFixture();
        byte[][] keys = RowTestHelp.Rows(fixture, Shared(fixture, RepeatsAndNulls), field);
        Assert.Equal(Alone(RepeatsAndNulls, nullable: true, field), keys);
    }

    [Fact]
    public void RowsOfOneValueWhoseViewsDifferHoldTheSameKey()
    {
        using RowFixture fixture = new RowFixture();
        byte[][] keys = RowTestHelp.Rows(fixture, Shared(fixture, Repeats, twice: true), RowSortField.Ascending);
        Assert.Equal(Alone(Repeats, nullable: false, RowSortField.Ascending), keys);
    }

    [Fact]
    public void AColumnAfterRepeatedTextsStartsWhereEachRowsTextEnds()
    {
        // The repeats between two columns: each row's key is the three columns' keys end to end, so a repeat that moved its
        // row's cursor by another length would misplace the last column.
        using RowFixture fixture = new RowFixture();
        long[] numbers = new long[RepeatsAndNulls.Length];
        for (int i = 0; i < numbers.Length; i++)
        {
            numbers[i] = (i * 7919L) - 40_000;
        }

        int before = fixture.Primitive<long>(PType.I64, numbers);
        int text = Shared(fixture, RepeatsAndNulls);
        int after = fixture.Primitive<long>(PType.I64, numbers);
        RowSortField descending = new RowSortField(descending: true, nullsFirst: false);
        byte[][] keys = RowTestHelp.Rows(fixture, [before, text, after], RowSortField.Ascending, descending, RowSortField.Ascending);

        byte[][] number = RowTestHelp.Rows(fixture, before, RowSortField.Ascending);
        byte[][] alone = Alone(RepeatsAndNulls, nullable: true, descending);
        for (int i = 0; i < keys.Length; i++)
        {
            Assert.Equal([.. number[i], .. alone[i], .. number[i]], keys[i]);
        }
    }

    [Fact]
    public void RepeatedTextsUnderAStructHoldTheKeysOfTheirValues()
    {
        // A struct's text child is encoded apart and copied under the rows the struct holds: a null struct's row keeps one
        // byte, whatever its child repeats.
        bool[] held = new bool[Repeats.Length];
        for (int i = 0; i < held.Length; i++)
        {
            held[i] = i % 5 != 3;
        }

        using RowFixture fixture = new RowFixture();
        int shared = fixture.Struct(["t"], [Shared(fixture, Repeats)], Repeats.Length, held);
        byte[][] keys = RowTestHelp.Rows(fixture, shared, RowSortField.Ascending);

        using RowFixture apart = new RowFixture();
        byte[]?[] values = new byte[]?[Repeats.Length];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = Values[Repeats[i]];
        }

        int expected = apart.Struct(["t"], [apart.VarBin(values)], Repeats.Length, held);
        Assert.Equal(RowTestHelp.Rows(apart, expected, RowSortField.Ascending), keys);
    }
}
