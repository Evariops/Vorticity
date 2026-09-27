// Answers spread over rows by their codes, against a row-at-a-time oracle.
//
// With AVX-512 VBMI a dictionary of up to 128 values is looked up 64 rows a permute, the codes
// narrowed to bytes; past 128, or past the last whole block, rows go one at a time. So the cases
// straddle both edges -- 64 and 128 values, 64-row blocks -- at every code width, signed and not,
// with codes past the dictionary on valid rows (the first of which must be reported) and on null
// rows (which must not be).
using System;
using System.Buffers.Binary;

using Vorticity.Compute;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Compute;

public sealed class CodeAnswersTests
{
    private static readonly PType[] CodeTypes =
    [
        PType.U8, PType.U16, PType.U32, PType.U64, PType.I8, PType.I16, PType.I32, PType.I64,
    ];

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(300)]
    public void EveryRowTakesTheAnswerItsCodeNames(int values)
    {
        Random random = new Random(values);
        foreach (PType ptype in CodeTypes)
        {
            int reach = ptype is PType.I8 ? Math.Min(values, 128) : ptype is PType.U8 ? Math.Min(values, 256) : values;
            foreach (int rows in new[] { 0, 1, 63, 64, 65, 200 })
            {
                foreach (int offset in new[] { -1, 0, 5 })
                {
                    byte[] answers = Answers(random, values);
                    byte[] codes = new byte[rows * ptype.ByteWidth()];
                    bool[] valid = Valid(random, rows, offset);
                    for (int i = 0; i < rows; i++)
                    {
                        // A null row's code may be anything, the dictionary's end included.
                        long code = valid[i] ? random.Next(reach) : random.Next(-3, values + 3);
                        Write(ptype, codes, i, code);
                    }

                    byte[] answer = new byte[rows];
                    int bad = CodeAnswers.Expand(
                        answers, codes, ptype, Bitmap(valid, offset), Math.Max(offset, 0), offset < 0, answer);

                    Assert.Equal(-1, bad);
                    for (int i = 0; i < rows; i++)
                    {
                        byte expected = valid[i] ? answers[(int)Read(ptype, codes, i)] : Trilean.Unknown;
                        Assert.True(expected == answer[i], $"{ptype} values={values} rows={rows} offset={offset}: row {i}");
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData(PType.U8, 40, 200)]
    [InlineData(PType.U16, 100, 100)]
    [InlineData(PType.U32, 64, 1_000_000)]
    [InlineData(PType.U64, 7, 7)]
    [InlineData(PType.I8, 100, -1)]
    [InlineData(PType.I16, 200, -32768)]
    [InlineData(PType.I32, 5, -1)]
    [InlineData(PType.I64, 300, long.MinValue)]
    internal void TheFirstValidRowPastTheDictionaryIsReported(PType ptype, int values, long outside)
    {
        foreach (int rows in new[] { 70, 130, 200 })
        {
            Random random = new Random(rows);
            byte[] codes = new byte[rows * ptype.ByteWidth()];
            bool[] valid = Valid(random, rows, 3);
            for (int i = 0; i < rows; i++)
            {
                Write(ptype, codes, i, random.Next(Math.Min(values, 100)));
            }

            // Past the dictionary on a null row first, which is passed, then on a valid one.
            int nullRow = Array.FindIndex(valid, v => !v);
            int badRow = Array.FindIndex(valid, nullRow + 1, v => v);
            Write(ptype, codes, nullRow, outside);
            Write(ptype, codes, badRow, outside);
            Write(ptype, codes, rows - 1, outside);
            valid[rows - 1] = true;

            byte[] answer = new byte[rows];
            int bad = CodeAnswers.Expand(Answers(random, values), codes, ptype, Bitmap(valid, 3), 3, allValid: false, answer);
            Assert.Equal(badRow, bad);
        }
    }

    [Fact]
    public void AnEmptyDictionaryNamesNothingButANullRowIsStillUnknown()
    {
        byte[] codes = new byte[100 * 4];
        bool[] valid = new bool[100];
        byte[] answer = new byte[100];
        Assert.Equal(-1, CodeAnswers.Expand([], codes, PType.U32, Bitmap(valid, 0), 0, allValid: false, answer));
        Assert.All(answer, state => Assert.Equal(Trilean.Unknown, state));

        valid[99] = true;
        Assert.Equal(99, CodeAnswers.Expand([], codes, PType.U32, Bitmap(valid, 0), 0, allValid: false, answer));
    }

    private static byte[] Answers(Random random, int values)
    {
        byte[] answers = new byte[values];
        for (int i = 0; i < values; i++)
        {
            answers[i] = (byte)random.Next(3);
        }

        return answers;
    }

    private static bool[] Valid(Random random, int rows, int offset)
    {
        bool[] valid = new bool[rows];
        for (int i = 0; i < rows; i++)
        {
            valid[i] = offset < 0 || random.Next(4) != 0;
        }

        return valid;
    }

    private static byte[] Bitmap(bool[] valid, int offset)
    {
        offset = Math.Max(offset, 0);
        byte[] bits = new byte[(valid.Length + offset + 7) / 8 + 1];
        bits.AsSpan().Fill(0xFF);
        for (int i = 0; i < valid.Length; i++)
        {
            if (!valid[i])
            {
                int at = offset + i;
                bits[at >> 3] &= (byte)~(1 << (at & 7));
            }
        }

        return bits;
    }

    private static void Write(PType ptype, byte[] codes, int row, long code)
    {
        Span<byte> at = codes.AsSpan(row * ptype.ByteWidth());
        switch (ptype.ByteWidth())
        {
            case 1: at[0] = (byte)code; break;
            case 2: BinaryPrimitives.WriteInt16LittleEndian(at, (short)code); break;
            case 4: BinaryPrimitives.WriteInt32LittleEndian(at, (int)code); break;
            default: BinaryPrimitives.WriteInt64LittleEndian(at, code); break;
        }
    }

    private static long Read(PType ptype, byte[] codes, int row)
    {
        ReadOnlySpan<byte> at = codes.AsSpan(row * ptype.ByteWidth());
        return ptype switch
        {
            PType.U8 => at[0],
            PType.U16 => BinaryPrimitives.ReadUInt16LittleEndian(at),
            PType.U32 => BinaryPrimitives.ReadUInt32LittleEndian(at),
            PType.U64 => (long)BinaryPrimitives.ReadUInt64LittleEndian(at),
            PType.I8 => (sbyte)at[0],
            PType.I16 => BinaryPrimitives.ReadInt16LittleEndian(at),
            PType.I32 => BinaryPrimitives.ReadInt32LittleEndian(at),
            _ => BinaryPrimitives.ReadInt64LittleEndian(at),
        };
    }
}
