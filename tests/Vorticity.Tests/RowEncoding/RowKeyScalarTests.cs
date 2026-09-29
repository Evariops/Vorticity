using System;
using System.Collections.Generic;
using Vorticity.Expressions;
using Vorticity.RowEncoding;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.RowEncoding;

/// <summary>
/// The key of one tuple, written value by value without a column, is the row the column encoder
/// writes for the same values.
/// </summary>
public sealed class RowKeyScalarTests
{
    private const int Rows = 300;

    private static readonly RowSortField[] Fields =
    [
        new RowSortField(descending: false, nullsFirst: true),
        new RowSortField(descending: false, nullsFirst: false),
        new RowSortField(descending: true, nullsFirst: true),
        new RowSortField(descending: true, nullsFirst: false),
    ];

    private static readonly PType[] PTypes =
    [
        PType.U8, PType.U16, PType.U32, PType.U64, PType.I8, PType.I16, PType.I32, PType.I64,
        PType.F16, PType.F32, PType.F64,
    ];

    [Fact]
    public void AKeyOfOneValueIsItsRowInTheColumnsEncoding()
    {
        Random random = new Random(20260927);
        foreach (RowSortField field in Fields)
        {
            foreach (PType ptype in PTypes)
            {
                foreach (bool nullable in (bool[])[false, true])
                {
                    using RowFixture fixture = new RowFixture();
                    (int column, FilterLiteral[] values) = Primitives(fixture, random, ptype, nullable);
                    Check(fixture, [column], [values], field);
                }
            }

            foreach (bool nullable in (bool[])[false, true])
            {
                using RowFixture fixture = new RowFixture();
                (int column, FilterLiteral[] values) = Bools(fixture, random, nullable);
                Check(fixture, [column], [values], field);
            }

            foreach (DTypeKind kind in (DTypeKind[])[DTypeKind.Utf8, DTypeKind.Binary])
            {
                foreach (bool nullable in (bool[])[false, true])
                {
                    using RowFixture fixture = new RowFixture();
                    (int column, FilterLiteral[] values) = Bytes(fixture, random, kind, nullable);
                    Check(fixture, [column], [values], field);
                }
            }
        }
    }

    [Fact]
    public void AKeyOfATupleIsItsRowAcrossTheColumns()
    {
        Random random = new Random(20260928);
        using RowFixture fixture = new RowFixture();
        (int text, FilterLiteral[] texts) = Bytes(fixture, random, DTypeKind.Utf8, nullable: true);
        (int number, FilterLiteral[] numbers) = Primitives(fixture, random, PType.I32, nullable: true);
        (int flag, FilterLiteral[] flags) = Bools(fixture, random, nullable: false);
        Check(fixture, [text, number, flag], [texts, numbers, flags], Fields[0], Fields[3], Fields[1]);
    }

    /// <summary>Each row of the columns' encoding against the key written from the row's values.</summary>
    private static void Check(RowFixture fixture, int[] columns, FilterLiteral[][] values, params RowSortField[] fields)
    {
        RowSortField[] each = fields.Length == 1 ? [.. System.Linq.Enumerable.Repeat(fields[0], columns.Length)] : fields;
        byte[][] rows = RowTestHelp.Rows(fixture, columns, each);
        DType[] dtypes = new DType[columns.Length];
        for (int c = 0; c < columns.Length; c++)
        {
            dtypes[c] = fixture.Arena.GetNode(columns[c]).DType;
        }

        FilterLiteral[] tuple = new FilterLiteral[columns.Length];
        for (int row = 0; row < rows.Length; row++)
        {
            for (int c = 0; c < columns.Length; c++)
            {
                tuple[c] = values[c][row];
            }

            byte[] key = RowEncoder.EncodeKey(tuple, dtypes, each);
            Assert.True(rows[row].AsSpan().SequenceEqual(key), $"row {row}: {Convert.ToHexString(rows[row])} against {Convert.ToHexString(key)}");
            Assert.Equal(key.Length, RowEncoder.KeyLength(tuple, dtypes, each));
        }
    }

    private static (int Column, FilterLiteral[] Values) Primitives(RowFixture fixture, Random random, PType ptype, bool nullable)
    {
        bool[]? valid = nullable ? new bool[Rows] : null;
        FilterLiteral[] literals = new FilterLiteral[Rows];
        int column;
        switch (ptype)
        {
            case PType.F16:
            case PType.F32:
            case PType.F64:
            {
                double[] specials = [0.0, -0.0, double.PositiveInfinity, double.NegativeInfinity, double.NaN, 1.5, -1.5];
                double[] doubles = new double[Rows];
                for (int i = 0; i < Rows; i++)
                {
                    double d = i < specials.Length ? specials[i] : (random.NextDouble() - 0.5) * Math.Pow(10, random.Next(-5, 6));
                    doubles[i] = ptype switch
                    {
                        PType.F16 => (double)(Half)d,
                        PType.F32 => (float)d,
                        _ => d,
                    };
                }

                valid = Valid(valid, random);
                column = ptype switch
                {
                    PType.F16 => fixture.Primitive(ptype, Array.ConvertAll(doubles, d => (Half)d).AsSpan(), valid),
                    PType.F32 => fixture.Primitive(ptype, Array.ConvertAll(doubles, d => (float)d).AsSpan(), valid),
                    _ => fixture.Primitive(ptype, doubles.AsSpan(), valid),
                };
                for (int i = 0; i < Rows; i++)
                {
                    literals[i] = valid is null || valid[i] ? FilterLiteral.From(doubles[i]) : FilterLiteral.Null;
                }

                return (column, literals);
            }

            default:
            {
                int width = ptype.ByteWidth();
                bool signed = ptype.IsSignedInteger();
                long[] values = new long[Rows];
                for (int i = 0; i < Rows; i++)
                {
                    values[i] = i switch
                    {
                        0 => signed ? Min(width) : 0,
                        1 => signed ? Max(width) : (long)Math.Min((ulong)long.MaxValue, UnsignedMax(width)),
                        2 => 0,
                        3 => signed ? -1 : 1,
                        _ => signed
                            ? random.NextInt64(Min(width), Max(width))
                            : (long)((ulong)random.NextInt64() % Math.Min(UnsignedMax(width), (ulong)long.MaxValue)),
                    };
                }

                valid = Valid(valid, random);
                column = width switch
                {
                    1 => fixture.Primitive(ptype, Array.ConvertAll(values, v => unchecked((byte)v)).AsSpan(), valid),
                    2 => fixture.Primitive(ptype, Array.ConvertAll(values, v => unchecked((ushort)v)).AsSpan(), valid),
                    4 => fixture.Primitive(ptype, Array.ConvertAll(values, v => unchecked((uint)v)).AsSpan(), valid),
                    _ => fixture.Primitive(ptype, Array.ConvertAll(values, v => unchecked((ulong)v)).AsSpan(), valid),
                };
                for (int i = 0; i < Rows; i++)
                {
                    literals[i] = valid is not null && !valid[i] ? FilterLiteral.Null
                        : signed ? FilterLiteral.From(values[i]) : FilterLiteral.From((ulong)values[i]);
                }

                return (column, literals);
            }
        }
    }

    private static (int Column, FilterLiteral[] Values) Bools(RowFixture fixture, Random random, bool nullable)
    {
        bool[]? valid = nullable ? new bool[Rows] : null;
        bool[] values = new bool[Rows];
        for (int i = 0; i < Rows; i++)
        {
            values[i] = random.Next(2) == 0;
        }

        int column = fixture.Bool(values, Valid(valid, random));
        FilterLiteral[] literals = new FilterLiteral[Rows];
        for (int i = 0; i < Rows; i++)
        {
            literals[i] = valid is not null && !valid[i] ? FilterLiteral.Null : FilterLiteral.From(values[i]);
        }

        return (column, literals);
    }

    private static (int Column, FilterLiteral[] Values) Bytes(RowFixture fixture, Random random, DTypeKind kind, bool nullable)
    {
        // Lengths around the 32-byte blocks and the 12 bytes a view holds inline.
        int[] lengths = [0, 1, 11, 12, 13, 31, 32, 33, 63, 64, 65, 96, 100];
        byte[]?[] values = new byte[]?[Rows];
        for (int i = 0; i < Rows; i++)
        {
            if (nullable && random.Next(6) == 0)
            {
                continue;
            }

            byte[] value = new byte[lengths[random.Next(lengths.Length)]];
            random.NextBytes(value);
            values[i] = value;
        }

        int column = fixture.VarBin(values, kind, forceNullable: nullable);
        FilterLiteral[] literals = new FilterLiteral[Rows];
        for (int i = 0; i < Rows; i++)
        {
            literals[i] = values[i] is { } value ? FilterLiteral.From(value) : FilterLiteral.Null;
        }

        return (column, literals);
    }

    /// <summary>Fills <paramref name="valid"/>, one row in five null, and returns it.</summary>
    private static bool[]? Valid(bool[]? valid, Random random)
    {
        if (valid is not null)
        {
            for (int i = 0; i < valid.Length; i++)
            {
                valid[i] = random.Next(5) != 0;
            }
        }

        return valid;
    }

    private static long Min(int width) => width == 8 ? long.MinValue : -(1L << ((8 * width) - 1));

    private static long Max(int width) => width == 8 ? long.MaxValue : (1L << ((8 * width) - 1)) - 1;

    private static ulong UnsignedMax(int width) => width == 8 ? ulong.MaxValue : (1UL << (8 * width)) - 1;
}
