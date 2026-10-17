// Values at any address. The arena puts a column's buffer on a multiple of its values' width, but a
// copy can leave it off that boundary, and so can a segment read in place from a file that laid it
// out that way: the bytes a row encodes to must not depend on where its values lie.
//
// THE ORACLE IS THE SAME COLUMNS AT AN ALIGNED ADDRESS: every column is built twice, once where the
// arena puts it and once shifted past that boundary, and the two encodings must agree byte for byte.
using System;
using Vorticity.RowEncoding;
using Vorticity.Types;
using Vorticity.Types.Numerics;
using Xunit;

namespace Vorticity.Tests.RowEncoding;

public sealed class RowAlignmentTests
{
    private static readonly RowSortField Asc = RowSortField.Ascending;
    private static readonly RowSortField Desc = new RowSortField(descending: true, nullsFirst: false);

    /// <summary>
    /// Shifts past a 16-byte boundary: an odd one moves every width, 2 the 4- and 8-byte ones, 4 and
    /// 12 the 8-byte ones, and 8 none of them but a 16-byte decimal.
    /// </summary>
    public static TheoryData<int> Shifts => [1, 2, 3, 4, 7, 8, 12];

    [Theory]
    [MemberData(nameof(Shifts))]
    public void EveryFixedWidthTypeEncodesTheSameAtAnyAddress(int shift) =>
        AssertSameAtShift(shift, static f =>
        {
            bool[] valid = [true, false, true, true, true];
            int[] columns =
            [
                f.Primitive<byte>(PType.U8, [0, 1, 0x7F, 0x80, 0xFF]),
                f.Primitive<ushort>(PType.U16, [0, 1, 0x7FFF, 0x8000, 0xFFFF], valid),
                f.Primitive<uint>(PType.U32, [0, 1, int.MaxValue, 0x8000_0000, uint.MaxValue]),
                f.Primitive<ulong>(PType.U64, [0, 1, long.MaxValue, 0x8000_0000_0000_0000, ulong.MaxValue], valid),
                f.Primitive<sbyte>(PType.I8, [0, -1, sbyte.MaxValue, sbyte.MinValue, 1]),
                f.Primitive<short>(PType.I16, [0, -1, short.MaxValue, short.MinValue, 1], valid),
                f.Primitive<int>(PType.I32, [0, -1, int.MaxValue, int.MinValue, 1]),
                f.Primitive<long>(PType.I64, [0, -1, long.MaxValue, long.MinValue, 1], valid),
                f.Primitive<Half>(PType.F16, [Half.Zero, Half.NegativeOne, Half.MaxValue, Half.MinValue, Half.Epsilon]),
                f.Primitive<float>(PType.F32, [0f, -1.5f, float.MaxValue, float.NegativeInfinity, float.Epsilon], valid),
                f.Primitive<double>(PType.F64, [0d, -1.5d, double.MaxValue, double.NegativeInfinity, double.Epsilon]),
            ];

            RowSortField[] fields = new RowSortField[columns.Length];
            for (int i = 0; i < fields.Length; i++)
            {
                fields[i] = i % 2 == 0 ? Asc : Desc;
            }

            return (columns, fields);
        });

    [Theory]
    [MemberData(nameof(Shifts))]
    public void ValuesUnderAStructOrAFixedSizeListEncodeTheSameAtAnyAddress(int shift) =>
        AssertSameAtShift(shift, static f =>
        {
            int key = f.Primitive<ulong>(PType.U64, [3, 1, 4, 1, 5]);
            int count = f.Primitive<uint>(PType.U32, [9, 2, 6, 5, 3], [true, true, false, true, true]);
            int pair = f.Struct(["key", "count"], [key, count], rows: 5, valid: [true, true, true, false, true]);
            int wide = f.FixedSizeList(
                f.Primitive<long>(PType.I64, [1, -2, 3, -4, 5, -6, 7, -8, 9, -10]), size: 2, rows: 5, valid: [true, false, true, true, true]);
            int narrow = f.FixedSizeList(f.Primitive<short>(PType.I16, [1, -1, 2, -2, 3, -3, 4, -4, 5, -5]), size: 2, rows: 5);
            return ([pair, wide, narrow], [Asc, Desc, Asc]);
        });

    [Theory]
    [MemberData(nameof(Shifts))]
    public void DecimalsTextAndBooleansEncodeTheSameAtAnyAddress(int shift) =>
        AssertSameAtShift(shift, static f =>
        {
            int[] columns =
            [
                f.Decimal([1, -2, 3, -4, 5], precision: 2, scale: 0, DecimalStorageType.I8),
                f.Decimal([100, -200, 300, -400, 500], precision: 4, scale: 1, DecimalStorageType.I16),
                f.Decimal([1, -2, 3, -4, 5], precision: 9, scale: 2, DecimalStorageType.I32, [true, false, true, true, true]),
                f.Decimal([1, -2, 3, -4, 5], precision: 18, scale: 2, DecimalStorageType.I64),
                f.Decimal([(Int128)long.MaxValue * 1_000, -2, 3, -4, (Int128)long.MinValue * 1_000], precision: 38, scale: 2, DecimalStorageType.I128),
                f.VarBin(
                [
                    RowTestHelp.Utf8("a value long enough to leave its view"),
                    null,
                    [],
                    RowTestHelp.Utf8("short"),
                    RowTestHelp.Utf8("another value too long to sit inline"),
                ]),
                f.Bool([true, false, true, true, false], [true, true, false, true, true], bitOffset: 3),
            ];

            return (columns, [Asc, Desc, Asc, Desc, Asc, Desc, Asc]);
        });

    /// <summary>Builds the columns at an aligned address and <paramref name="shift"/> bytes past one, and compares their rows.</summary>
    private static void AssertSameAtShift(int shift, Func<RowFixture, (int[] Columns, RowSortField[] Fields)> build)
    {
        using RowFixture aligned = new RowFixture();
        using RowFixture shifted = new RowFixture { Shift = shift };
        (int[] columns, RowSortField[] fields) = build(aligned);
        (int[] moved, _) = build(shifted);
        Assert.Equal(RowTestHelp.Rows(aligned, columns, fields), RowTestHelp.Rows(shifted, moved, fields));
    }
}
