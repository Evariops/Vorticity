// Two kernels against oracles that answer one row at a time in a type wide enough for any side:
// `column IN (...)` with its candidates hashed once, and two columns of one type compared.
//
// Where AVX-512 is, whole blocks of 64 rows are compared as vectors -- a few candidates against
// each vector of rows, or a vector of one column against the other -- and the verdicts spread
// back to a byte a row; the rows past the last block, and a set of many candidates, go through the
// scalar loops. So each case runs at lengths either side of a block, with validity bitmaps
// starting at several bits of a byte, and with the values that make a narrowing wrong: candidates
// the column's type cannot hold, zero, the type's extremes, a null candidate, and NaN.
using System;
using System.Collections.Generic;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Compute;

public sealed class MembershipAndPairTests
{
    private static readonly int[] Lengths = [0, 1, 63, 64, 65, 130, 257];

    private static readonly PType[] Integers =
        [PType.I8, PType.I16, PType.I32, PType.I64, PType.U8, PType.U16, PType.U32, PType.U64];

    private static readonly ComparisonOp[] Ops =
    [
        ComparisonOp.Equal, ComparisonOp.NotEqual, ComparisonOp.Less,
        ComparisonOp.LessOrEqual, ComparisonOp.Greater, ComparisonOp.GreaterOrEqual,
    ];

    [Fact]
    public void AMembershipIsAnsweredAsTheIntegersAnswerIt()
    {
        Random random = new Random(20260926);
        foreach (PType ptype in Integers)
        {
            bool signed = IsSigned(ptype);
            foreach (int rows in Lengths)
            {
                foreach (int bitOffset in new[] { -1, 0, 5 })
                {
                    foreach (int count in new[] { 4, 7, 16, 17, 40 })
                    {
                        foreach (bool nullCandidate in new[] { false, true })
                        {
                            CanonicalArena arena = new CanonicalArena();
                            DTypeArena types = new DTypeArena();
                            Int128[] values = new Int128[rows];
                            bool[] valid = new bool[rows];
                            for (int i = 0; i < rows; i++)
                            {
                                values[i] = Draw(ptype, random);
                                valid[i] = bitOffset < 0 || random.Next(4) != 0;
                            }

                            List<FilterLiteral> literals = [];
                            List<Int128> candidates = [];
                            for (int k = 0; k < count; k++)
                            {
                                // Mostly values the column holds; now and then one it cannot.
                                Int128 candidate = random.Next(6) == 0 ? OutOfRange(ptype, random) : Draw(ptype, random);
                                candidates.Add(candidate);
                                literals.Add(candidate < 0 ? FilterLiteral.From((long)candidate) : FilterLiteral.From((ulong)candidate));
                            }

                            if (nullCandidate)
                            {
                                literals.Insert(random.Next(literals.Count), FilterLiteral.Null);
                            }

                            int node = Primitive(arena, types, ptype, values, valid, bitOffset);
                            InSet? set = InSet.TryBuild(literals.ToArray(), signed);
                            Assert.NotNull(set);
                            byte[] answer = new byte[rows];
                            ComparisonKernels.In(arena, node, literals.ToArray(), answer, new byte[rows], set);
                            for (int i = 0; i < rows; i++)
                            {
                                byte expected = !valid[i] ? Trilean.Unknown
                                    : candidates.Contains(values[i]) ? Trilean.True
                                    : nullCandidate ? Trilean.Unknown : Trilean.False;
                                if (expected != answer[i])
                                {
                                    Assert.Fail(
                                        $"{ptype} rows={rows} offset={bitOffset} candidates={count} null={nullCandidate}: row {i} " +
                                        $"({values[i]}, valid={valid[i]}) answered {answer[i]}, expected {expected}.");
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData(PType.I8)]
    [InlineData(PType.I16)]
    [InlineData(PType.I32)]
    [InlineData(PType.I64)]
    [InlineData(PType.U8)]
    [InlineData(PType.U16)]
    [InlineData(PType.U32)]
    [InlineData(PType.U64)]
    [InlineData(PType.F32)]
    [InlineData(PType.F64)]
    internal void TwoColumnsAreComparedAsTheirValuesCompare(PType ptype)
    {
        Random random = new Random((int)ptype * 131);
        foreach (int rows in Lengths)
        {
            foreach ((int leftOffset, int rightOffset) in new[] { (-1, -1), (-1, 3), (0, -1), (5, 2) })
            {
                CanonicalArena arena = new CanonicalArena();
                DTypeArena types = new DTypeArena();
                double[] left = new double[rows];
                double[] right = new double[rows];
                bool[] leftValid = new bool[rows];
                bool[] rightValid = new bool[rows];
                for (int i = 0; i < rows; i++)
                {
                    left[i] = Pick(ptype, random);
                    right[i] = random.Next(3) == 0 ? left[i] : Pick(ptype, random);
                    leftValid[i] = leftOffset < 0 || random.Next(4) != 0;
                    rightValid[i] = rightOffset < 0 || random.Next(4) != 0;
                }

                int a = Column(arena, types, ptype, left, leftValid, leftOffset);
                int b = Column(arena, types, ptype, right, rightValid, rightOffset);
                foreach (ComparisonOp op in Ops)
                {
                    byte[] answer = new byte[rows];
                    ComparisonKernels.CompareColumns(arena, a, op, b, answer);
                    for (int i = 0; i < rows; i++)
                    {
                        byte expected = leftValid[i] && rightValid[i]
                            ? (Holds(op, left[i], right[i]) ? Trilean.True : Trilean.False)
                            : Trilean.Unknown;
                        if (expected != answer[i])
                        {
                            Assert.Fail(
                                $"{ptype} rows={rows} offsets=({leftOffset},{rightOffset}) {op}: row {i} " +
                                $"({left[i]} vs {right[i]}) answered {answer[i]}, expected {expected}.");
                        }
                    }
                }
            }
        }
    }

    private static bool Holds(ComparisonOp op, double left, double right) => op switch
    {
        ComparisonOp.Equal => left == right,
        ComparisonOp.NotEqual => left != right,
        ComparisonOp.Less => left < right,
        ComparisonOp.LessOrEqual => left <= right,
        ComparisonOp.Greater => left > right,
        _ => left >= right,
    };

    private static bool IsSigned(PType ptype) => ptype is PType.I8 or PType.I16 or PType.I32 or PType.I64;

    private static (Int128 Min, Int128 Max) Range(PType ptype) => ptype switch
    {
        PType.I8 => (sbyte.MinValue, sbyte.MaxValue),
        PType.I16 => (short.MinValue, short.MaxValue),
        PType.I32 => (int.MinValue, int.MaxValue),
        PType.I64 => (long.MinValue, long.MaxValue),
        PType.U8 => (byte.MinValue, byte.MaxValue),
        PType.U16 => (ushort.MinValue, ushort.MaxValue),
        PType.U32 => (uint.MinValue, uint.MaxValue),
        _ => (ulong.MinValue, ulong.MaxValue),
    };

    /// <summary>A value of the column's type: near zero mostly, so rows and candidates meet, else an extreme.</summary>
    private static Int128 Draw(PType ptype, Random random)
    {
        (Int128 min, Int128 max) = Range(ptype);
        return random.Next(10) switch
        {
            0 => min,
            1 => max,
            _ => Int128.Clamp(random.Next(-12, 13), min, max),
        };
    }

    /// <summary>
    /// A candidate just past the column's type, whose truncation is one of its values: above it
    /// or below it, whichever a literal can still express.
    /// </summary>
    private static Int128 OutOfRange(PType ptype, Random random)
    {
        (Int128 min, Int128 max) = Range(ptype);
        Int128 high = max + 1 + random.Next(3);
        Int128 low = min - 1 - random.Next(3);
        bool highFits = high <= ulong.MaxValue;
        bool lowFits = low >= long.MinValue;
        return (random.Next(2) == 0 && highFits) || !lowFits ? high : low;
    }

    /// <summary>A value of a column of <paramref name="ptype"/>, as a double that holds it exactly where the test needs it to.</summary>
    private static double Pick(PType ptype, Random random)
    {
        if (ptype is PType.F32 or PType.F64)
        {
            return random.Next(12) switch
            {
                0 => double.NaN,
                1 => -0.0,
                2 => 0.0,
                3 => double.PositiveInfinity,
                _ => random.Next(-5, 6) / 2.0,
            };
        }

        // Small integers and the extremes that fit a double exactly enough to order them: the
        // order of the extremes against small values is all a comparison can get wrong.
        return (double)Draw(ptype, random);
    }

    private static int Primitive(
        CanonicalArena arena, DTypeArena types, PType ptype, Int128[] values, bool[] valid, int bitOffset)
    {
        int width = ptype.ByteWidth();
        VortexBuffer buffer = arena.Allocate(values.Length * width, width, out Span<byte> bytes);
        for (int i = 0; i < values.Length; i++)
        {
            UInt128 bits = (UInt128)values[i];
            for (int b = 0; b < width; b++)
            {
                bytes[(i * width) + b] = (byte)(bits >> (8 * b));
            }
        }

        return arena.AddPrimitive(
            types.Primitive(ptype, bitOffset < 0 ? Nullability.NonNullable : Nullability.Nullable),
            values.Length, ValidityOf(arena, types, valid, bitOffset), ptype, buffer);
    }

    /// <summary>
    /// A column whose rows read back as <paramref name="values"/>: exactly for the integers the
    /// test draws and the floats, whose values are all representable as singles.
    /// </summary>
    private static int Column(
        CanonicalArena arena, DTypeArena types, PType ptype, double[] values, bool[] valid, int bitOffset)
    {
        if (ptype is not (PType.F32 or PType.F64))
        {
            Int128[] integers = new Int128[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                (Int128 min, Int128 max) = Range(ptype);
                integers[i] = values[i] <= (double)min ? min : values[i] >= (double)max ? max : (Int128)values[i];
                values[i] = (double)integers[i];
            }

            return Primitive(arena, types, ptype, integers, valid, bitOffset);
        }

        int width = ptype.ByteWidth();
        VortexBuffer buffer = arena.Allocate(values.Length * width, width, out Span<byte> bytes);
        for (int i = 0; i < values.Length; i++)
        {
            if (ptype == PType.F32)
            {
                BitConverter.TryWriteBytes(bytes.Slice(i * 4, 4), (float)values[i]);
            }
            else
            {
                BitConverter.TryWriteBytes(bytes.Slice(i * 8, 8), values[i]);
            }
        }

        return arena.AddPrimitive(
            types.Primitive(ptype, bitOffset < 0 ? Nullability.NonNullable : Nullability.Nullable),
            values.Length, ValidityOf(arena, types, valid, bitOffset), ptype, buffer);
    }

    /// <summary>A validity bitmap starting <paramref name="bitOffset"/> bits in, or none when it is negative.</summary>
    private static Validity ValidityOf(CanonicalArena arena, DTypeArena types, bool[] valid, int bitOffset)
    {
        if (bitOffset < 0)
        {
            return Validity.NonNullable;
        }

        int bytes = Math.Max((valid.Length + bitOffset + 7) / 8, 1);
        VortexBuffer buffer = arena.Allocate(bytes, 1, out Span<byte> bits);

        // Bits outside the rows are set, so a kernel that reads one it should not shows.
        bits.Fill(0xFF);
        for (int i = 0; i < valid.Length; i++)
        {
            int at = bitOffset + i;
            if (!valid[i])
            {
                bits[at >> 3] &= (byte)~(1 << (at & 7));
            }
        }

        return Validity.Bitmap(arena.AddBool(
            types.Bool(Nullability.NonNullable), valid.Length, Validity.NonNullable, buffer, bitOffset));
    }
}
