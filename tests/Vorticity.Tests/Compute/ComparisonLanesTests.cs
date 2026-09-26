// The comparison kernels against an oracle that compares one row at a time in the widest type.
//
// Where AVX-512 is, whole blocks of 64 rows are compared in the column's own type and the verdicts
// spread back to a byte a row; the rows past the last block, and every literal the column's type
// cannot hold exactly, go through the scalar loops. So every case here runs at lengths either side
// of a block, with validity bitmaps starting at every bit of a byte, and with literals in the
// column's range, at its ends, outside it, fractional and NaN: the vector and scalar arms must give
// the same answer row for row, the IEEE 754 one for floats and the arithmetic one for integers.
using System;
using System.Buffers.Binary;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Compute;

public sealed class ComparisonLanesTests
{
    private static readonly int[] Lengths = [0, 1, 63, 64, 65, 130, 257];

    private static readonly ComparisonOp[] Ops =
    [
        ComparisonOp.Equal, ComparisonOp.NotEqual, ComparisonOp.Less,
        ComparisonOp.LessOrEqual, ComparisonOp.Greater, ComparisonOp.GreaterOrEqual,
    ];

    [Theory]
    [InlineData(PType.I8)]
    [InlineData(PType.I16)]
    [InlineData(PType.I32)]
    [InlineData(PType.I64)]
    [InlineData(PType.U8)]
    [InlineData(PType.U16)]
    [InlineData(PType.U32)]
    [InlineData(PType.U64)]
    [InlineData(PType.F16)]
    [InlineData(PType.F32)]
    [InlineData(PType.F64)]
    internal void EveryRowIsAnsweredAsTheWidestTypeAnswersIt(PType ptype)
    {
        Random random = new Random((int)ptype * 7919);
        foreach (int rows in Lengths)
        {
            foreach (int bitOffset in new[] { -1, 0, 3, 7 })
            {
                CanonicalArena arena = new CanonicalArena();
                DTypeArena types = new DTypeArena();
                byte[] values = Values(ptype, rows, random);
                bool[] valid = new bool[rows];
                for (int i = 0; i < rows; i++)
                {
                    valid[i] = bitOffset < 0 || random.Next(4) != 0;
                }

                int node = Primitive(arena, types, ptype, values, valid, bitOffset);
                foreach (FilterLiteral literal in Literals(ptype, values, rows))
                {
                    foreach (ComparisonOp op in Ops)
                    {
                        byte[] answer = new byte[rows];
                        ComparisonKernels.Compare(arena, node, op, literal, answer);
                        for (int i = 0; i < rows; i++)
                        {
                            byte expected = valid[i]
                                ? (Holds(ptype, values, i, op, literal) ? Trilean.True : Trilean.False)
                                : Trilean.Unknown;
                            if (expected != answer[i])
                            {
                                Assert.Fail(
                                    $"{ptype} rows={rows} offset={bitOffset} {op} {Describe(literal)}: row {i} " +
                                    $"({Describe(ptype, values, i)}, valid={valid[i]}) answered {answer[i]}, expected {expected}.");
                            }
                        }
                    }
                }
            }
        }
    }

    [Fact]
    public void ANullCheckIsTheValidityRowForRow()
    {
        Random random = new Random(42);
        foreach (int rows in Lengths)
        {
            foreach (int bitOffset in new[] { 0, 1, 5 })
            {
                CanonicalArena arena = new CanonicalArena();
                DTypeArena types = new DTypeArena();
                bool[] valid = new bool[rows];
                for (int i = 0; i < rows; i++)
                {
                    valid[i] = random.Next(3) != 0;
                }

                int node = Primitive(arena, types, PType.I32, new byte[rows * 4], valid, bitOffset);
                foreach (bool isNull in new[] { true, false })
                {
                    byte[] answer = new byte[rows];
                    ComparisonKernels.NullCheck(arena, node, isNull, answer);
                    for (int i = 0; i < rows; i++)
                    {
                        Assert.Equal(valid[i] != isNull ? Trilean.True : Trilean.False, answer[i]);
                    }
                }
            }
        }
    }

    [Fact]
    public void ABoolColumnIsAnsweredRowForRowWithAndWithoutNulls()
    {
        Random random = new Random(43);
        foreach (int rows in Lengths)
        {
            foreach (int bitOffset in new[] { -1, 0, 6 })
            {
                CanonicalArena arena = new CanonicalArena();
                DTypeArena types = new DTypeArena();
                bool[] value = new bool[rows];
                bool[] valid = new bool[rows];
                for (int i = 0; i < rows; i++)
                {
                    value[i] = random.Next(2) == 0;
                    valid[i] = bitOffset < 0 || random.Next(4) != 0;
                }

                int valueOffset = Math.Max(bitOffset, 0) ^ 5;
                VortexBuffer bits = Bitmap(arena, value, valueOffset);
                Vorticity.Arrays.Validity validity = ValidityOf(arena, types, valid, bitOffset);
                int node = arena.AddBool(
                    types.Bool(bitOffset < 0 ? Nullability.NonNullable : Nullability.Nullable),
                    rows, validity, bits, valueOffset);
                foreach (bool literal in new[] { false, true })
                {
                    foreach (ComparisonOp op in Ops)
                    {
                        byte[] answer = new byte[rows];
                        ComparisonKernels.Compare(arena, node, op, FilterLiteral.From(literal), answer);
                        for (int i = 0; i < rows; i++)
                        {
                            byte expected = valid[i]
                                ? (Apply(op, value[i].CompareTo(literal)) ? Trilean.True : Trilean.False)
                                : Trilean.Unknown;
                            Assert.Equal(expected, answer[i]);
                        }
                    }
                }
            }
        }
    }

    private static int Primitive(
        CanonicalArena arena, DTypeArena types, PType ptype, byte[] values, bool[] valid, int bitOffset)
    {
        VortexBuffer buffer = arena.Allocate(values.Length, Math.Max(ptype.ByteWidth(), 1), out Span<byte> bytes);
        values.CopyTo(bytes);
        Vorticity.Arrays.Validity validity = ValidityOf(arena, types, valid, bitOffset);
        return arena.AddPrimitive(
            types.Primitive(ptype, bitOffset < 0 ? Nullability.NonNullable : Nullability.Nullable),
            valid.Length, validity, ptype, buffer);
    }

    /// <summary>A validity bitmap starting <paramref name="bitOffset"/> bits in, or none when it is negative.</summary>
    private static Vorticity.Arrays.Validity ValidityOf(CanonicalArena arena, DTypeArena types, bool[] valid, int bitOffset)
    {
        if (bitOffset < 0)
        {
            return Vorticity.Arrays.Validity.NonNullable;
        }

        VortexBuffer bits = Bitmap(arena, valid, bitOffset);
        return Vorticity.Arrays.Validity.Bitmap(arena.AddBool(
            types.Bool(Nullability.NonNullable), valid.Length, Vorticity.Arrays.Validity.NonNullable, bits, bitOffset));
    }

    private static VortexBuffer Bitmap(CanonicalArena arena, bool[] set, int bitOffset)
    {
        int bytes = Math.Max((set.Length + bitOffset + 7) / 8, 1);
        VortexBuffer buffer = arena.Allocate(bytes, 1, out Span<byte> bits);

        // Bits outside the rows are set, so a kernel that reads one it should not shows.
        bits.Fill(0xFF);
        for (int i = 0; i < set.Length; i++)
        {
            int at = bitOffset + i;
            if (!set[i])
            {
                bits[at >> 3] &= (byte)~(1 << (at & 7));
            }
        }

        return buffer;
    }

    private static byte[] Values(PType ptype, int rows, Random random)
    {
        int width = ptype.ByteWidth();
        byte[] values = new byte[rows * width];
        random.NextBytes(values);

        // Small values too, so that the literals drawn from the column meet their neighbours, and
        // the special values of floats.
        for (int i = 0; i < rows; i += 3)
        {
            Span<byte> slot = values.AsSpan(i * width, width);
            switch (ptype)
            {
                case PType.F16: BinaryPrimitives.WriteHalfLittleEndian(slot, (Half)(random.Next(-8, 8) / 2.0)); break;
                case PType.F32: BinaryPrimitives.WriteSingleLittleEndian(slot, i % 9 == 0 ? float.NaN : random.Next(-8, 8) / 2.0f); break;
                case PType.F64: BinaryPrimitives.WriteDoubleLittleEndian(slot, i % 9 == 0 ? double.NaN : i % 6 == 0 ? -0.0 : random.Next(-8, 8) / 2.0); break;
                default: slot.Clear(); slot[0] = (byte)random.Next(-4, 4); if (random.Next(2) == 0) { slot.Fill(0xFF); slot[0] = (byte)random.Next(250, 256); } break;
            }
        }

        return values;
    }

    private static FilterLiteral[] Literals(PType ptype, byte[] values, int rows)
    {
        FilterLiteral fromColumn = rows == 0 ? FilterLiteral.From(0L) : Literal(ptype, values, rows / 2);
        return ptype switch
        {
            PType.F16 or PType.F32 or PType.F64 =>
            [
                fromColumn, FilterLiteral.From(0.0), FilterLiteral.From(-0.0), FilterLiteral.From(1.5),
                FilterLiteral.From(0.1), FilterLiteral.From(double.NaN), FilterLiteral.From(double.PositiveInfinity),
                FilterLiteral.From(-3L), FilterLiteral.From(1e300),
            ],
            PType.I8 or PType.I16 or PType.I32 or PType.I64 =>
            [
                fromColumn, FilterLiteral.From(0L), FilterLiteral.From(-1L), FilterLiteral.From(-4L),
                FilterLiteral.From(Min(ptype)), FilterLiteral.From(Max(ptype)),
                FilterLiteral.From(1_000L), FilterLiteral.From(-1_000L), FilterLiteral.From(100_000L), FilterLiteral.From(-5_000_000_000L),
                FilterLiteral.From(ulong.MaxValue), FilterLiteral.From(3.5), FilterLiteral.From(-2.0), FilterLiteral.From(double.NaN),
            ],
            _ =>
            [
                fromColumn, FilterLiteral.From(0L), FilterLiteral.From(3UL), FilterLiteral.From(-1L),
                FilterLiteral.From((ulong)Max(ptype)), FilterLiteral.From(300UL), FilterLiteral.From(70_000UL), FilterLiteral.From(5_000_000_000UL),
                FilterLiteral.From(ulong.MaxValue), FilterLiteral.From(ulong.MaxValue - 5), FilterLiteral.From(2.5), FilterLiteral.From(double.NaN),
            ],
        };
    }

    private static long Min(PType ptype) => ptype switch
    {
        PType.I8 => sbyte.MinValue,
        PType.I16 => short.MinValue,
        PType.I32 => int.MinValue,
        _ => long.MinValue,
    };

    private static long Max(PType ptype) => ptype switch
    {
        PType.I8 => sbyte.MaxValue,
        PType.I16 => short.MaxValue,
        PType.I32 => int.MaxValue,
        PType.U8 => byte.MaxValue,
        PType.U16 => ushort.MaxValue,
        PType.U32 => uint.MaxValue,
        _ => long.MaxValue,
    };

    private static FilterLiteral Literal(PType ptype, byte[] values, int row) => ptype switch
    {
        PType.F16 or PType.F32 or PType.F64 => FilterLiteral.From(Float(ptype, values, row)),
        PType.I8 or PType.I16 or PType.I32 or PType.I64 => FilterLiteral.From((long)Integer(ptype, values, row)),
        _ => FilterLiteral.From((ulong)Integer(ptype, values, row)),
    };

    private static bool Holds(PType ptype, byte[] values, int row, ComparisonOp op, FilterLiteral literal)
    {
        if (ptype is PType.F16 or PType.F32 or PType.F64)
        {
            return Compare(op, Float(ptype, values, row), AsDouble(literal));
        }

        Int128 value = Integer(ptype, values, row);
        return literal.Kind switch
        {
            FilterLiteralKind.Signed => Apply(op, value.CompareTo((Int128)literal.SignedValue)),
            FilterLiteralKind.Unsigned => Apply(op, value.CompareTo((Int128)literal.UnsignedValue)),

            // Exact for the literals used here: small, or NaN, against integers a double holds or
            // that are far from them.
            _ => Compare(op, (double)value, literal.FloatValue),
        };
    }

    private static double AsDouble(FilterLiteral literal) => literal.Kind switch
    {
        FilterLiteralKind.Float => literal.FloatValue,
        FilterLiteralKind.Signed => literal.SignedValue,
        _ => literal.UnsignedValue,
    };

    private static bool Compare(ComparisonOp op, double left, double right) => op switch
    {
        ComparisonOp.Equal => left == right,
        ComparisonOp.NotEqual => left != right,
        ComparisonOp.Less => left < right,
        ComparisonOp.LessOrEqual => left <= right,
        ComparisonOp.Greater => left > right,
        _ => left >= right,
    };

    private static bool Apply(ComparisonOp op, int order) => op switch
    {
        ComparisonOp.Equal => order == 0,
        ComparisonOp.NotEqual => order != 0,
        ComparisonOp.Less => order < 0,
        ComparisonOp.LessOrEqual => order <= 0,
        ComparisonOp.Greater => order > 0,
        _ => order >= 0,
    };

    private static Int128 Integer(PType ptype, byte[] values, int row)
    {
        ReadOnlySpan<byte> at = values.AsSpan(row * ptype.ByteWidth());
        return ptype switch
        {
            PType.I8 => (sbyte)at[0],
            PType.I16 => BinaryPrimitives.ReadInt16LittleEndian(at),
            PType.I32 => BinaryPrimitives.ReadInt32LittleEndian(at),
            PType.I64 => BinaryPrimitives.ReadInt64LittleEndian(at),
            PType.U8 => at[0],
            PType.U16 => BinaryPrimitives.ReadUInt16LittleEndian(at),
            PType.U32 => BinaryPrimitives.ReadUInt32LittleEndian(at),
            _ => BinaryPrimitives.ReadUInt64LittleEndian(at),
        };
    }

    private static double Float(PType ptype, byte[] values, int row)
    {
        ReadOnlySpan<byte> at = values.AsSpan(row * ptype.ByteWidth());
        return ptype switch
        {
            PType.F16 => (double)BinaryPrimitives.ReadHalfLittleEndian(at),
            PType.F32 => BinaryPrimitives.ReadSingleLittleEndian(at),
            _ => BinaryPrimitives.ReadDoubleLittleEndian(at),
        };
    }

    private static string Describe(PType ptype, byte[] values, int row) =>
        ptype is PType.F16 or PType.F32 or PType.F64 ? Float(ptype, values, row).ToString("R") : Integer(ptype, values, row).ToString();

    private static string Describe(FilterLiteral literal) => literal.Kind switch
    {
        FilterLiteralKind.Signed => literal.SignedValue + "L",
        FilterLiteralKind.Unsigned => literal.UnsignedValue + "UL",
        _ => literal.FloatValue.ToString("R"),
    };
}
